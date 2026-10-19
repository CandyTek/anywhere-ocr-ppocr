/*
author:    telppa（空）, HKMV
version:   2026.07.23

PP-OCRv6 requires the current PaddleOCR runtime and cannot be loaded by the
PaddleOCR 2.x DLL previously bundled with this project.  This wrapper keeps a
local OCR worker alive so that the medium models are loaded only once.

Two interchangeable workers speak the same localhost protocol:
  - OcrWorker\dist\AnywhereOcrWorker.exe  (C# + ONNX Runtime + DirectML, a few
    hundred MB of RAM; preferred whenever it has been built)
  - PaddleOCR_worker.py                   (PaddleOCR 3.x in Python, several GB)
Set ANYWHERE_OCR_WORKER to the exe path to use a copy stored elsewhere, or set
ANYWHERE_OCR_BACKEND=python to force the Python worker.
*/
PaddleOCR(Image, Configs:="")
{
    static WorkerPort := 48626
    static WorkerReady := 0
    static WorkerPID := 0
    static ModelName := "PP-OCRv6_medium"

    get_all_info := 0
    ultPython := "C:\ProgramData\miniconda3\envs\ult\python.exe"
    use_gpu := FileExist(ultPython) ? 1 : 0
    gpu_id := 0
    python := FileExist(ultPython) ? ultPython : "python"

    ; Environment variables make GitHub deployments independent of one
    ; developer's Conda installation path.
    EnvGet, envPython, ANYWHERE_OCR_PYTHON
    EnvGet, envUseGPU, ANYWHERE_OCR_USE_GPU
    EnvGet, envGpuID, ANYWHERE_OCR_GPU_ID
    EnvGet, envWorker, ANYWHERE_OCR_WORKER
    EnvGet, envBackend, ANYWHERE_OCR_BACKEND
    nativeWorker := A_LineFile "\..\..\OcrWorker\dist\AnywhereOcrWorker.exe"
    if (envWorker != "")
        nativeWorker := envWorker
    if (envPython != "")
        python := envPython
    if (envUseGPU != "")
        use_gpu := envUseGPU
    if (envGpuID != "")
        gpu_id := envGpuID
    if (IsObject(Configs))
    {
        if (Configs.HasKey("get_all_info"))
            get_all_info := Configs.get_all_info
        if (Configs.HasKey("use_gpu"))
            use_gpu := Configs.use_gpu
        if (Configs.HasKey("gpu_id"))
            gpu_id := Configs.gpu_id
        if (Configs.HasKey("python"))
            python := Configs.python
        if (Configs.HasKey("worker"))
            nativeWorker := Configs.worker
        if (Configs.HasKey("backend"))
            envBackend := Configs.backend
    }
    useNative := (envBackend != "python") && FileExist(nativeWorker)

    ; PP-OCRv6_medium is deliberately the only model used by this wrapper.
    ; Keep accepting the old model option so existing callers do not break.
    if (IsObject(Configs) && Configs.HasKey("model")
        && Configs.model != ModelName
        && Configs.model != "medium"
        && Configs.model != "fast"
        && Configs.model != "mobile"
        && Configs.model != "server")
    {
        requestedModel := Configs.model
        MsgBox, 0x40010, Anywhere OCR,
            (LTrim
            Unsupported OCR model: %requestedModel%

            This version uses %ModelName%.
            )
        return, ""
    }

    if (!WorkerReady)
    {
        WorkerReady := PaddleOCR_WorkerIsReady(WorkerPort)
        if (!WorkerReady)
        {
            device := use_gpu ? "gpu:" gpu_id : "cpu"
            if (useNative)
            {
                command := """" nativeWorker """ --port " WorkerPort
                    . " --device " device
                workDir := nativeWorker "\.."
            }
            else
            {
                worker := A_LineFile "\..\PaddleOCR_worker.py"
                command := """" python """ """ worker """ --port "
                    . WorkerPort " --device " device
                workDir := A_LineFile "\.."
            }
            Run, % command, % workDir, Hide UseErrorLevel, WorkerPID
            if (ErrorLevel)
            {
                if (useNative)
                    MsgBox, 0x40010, Anywhere OCR,
                        (LTrim
                        Unable to start the OCR worker:
                        %nativeWorker%

                        Rebuild it with OcrWorker\publish.cmd, or set
                        ANYWHERE_OCR_BACKEND=python to use the Python worker.
                        )
                else
                    MsgBox, 0x40010, Anywhere OCR,
                        (LTrim
                        Unable to start Python.

                        Install Python and run:
                        python -m pip install paddlepaddle paddleocr
                        )
                return, ""
            }

            ; The first launch can download and initialize both medium models.
            Loop, 600
            {
                Sleep, 250
                if (PaddleOCR_WorkerIsReady(WorkerPort))
                {
                    WorkerReady := 1
                    break
                }
                Process, Exist, %WorkerPID%
                if (!ErrorLevel)
                    break
            }
        }

        if (!WorkerReady)
        {
            if (useNative)
                MsgBox, 0x40010, Anywhere OCR,
                    (LTrim
                    PP-OCRv6_medium failed to start.

                    From a terminal in the project folder, run:
                    OcrWorker\dist\AnywhereOcrWorker.exe --device %device%
                    to see the error.  The ONNX models must exist in the
                    'models' folder (see tools\export_onnx.py).
                    )
            else
                MsgBox, 0x40010, Anywhere OCR,
                    (LTrim
                    PP-OCRv6_medium failed to start.

                    From a terminal in the project folder, run:
                    python -m pip install paddlepaddle paddleocr
                    python PaddleOCR\PaddleOCR_worker.py
                    )
            return, ""
        }
    }

    pid := DllCall("GetCurrentProcessId")
    imagePath := A_Temp "\anywhere-ocr-" pid "-" A_TickCount ".png"
    ImagePutFile(Image, imagePath)

    try
    {
        http := ComObjCreate("WinHttp.WinHttpRequest.5.1")
        endpoint := "http://127.0.0.1:" WorkerPort "/ocr"
            . (get_all_info ? "?all=1" : "")
        http.Open("POST", endpoint, false)
        http.SetRequestHeader("Content-Type", "text/plain; charset=utf-8")
        http.SetTimeouts(5000, 5000, 300000, 300000)
        http.Send(imagePath)
        if (http.Status != 200)
            throw Exception(http.ResponseText)
        result := http.ResponseText
    }
    catch e
    {
        WorkerReady := 0
        MsgBox, 0x40010, Anywhere OCR, % "OCR failed:`n`n" e.Message
        result := ""
    }

    FileDelete, %imagePath%
    return, result
}

PaddleOCR_WorkerIsReady(Port)
{
    try
    {
        http := ComObjCreate("WinHttp.WinHttpRequest.5.1")
        http.Open("GET", "http://127.0.0.1:" Port "/health", false)
        http.SetTimeouts(250, 250, 250, 250)
        http.Send()
        return, http.Status = 200
            && InStr(http.ResponseText, "PP-OCRv6_medium")
    }
    catch
        return, 0
}

#Include %A_LineFile%\..\Lib\ImagePut.ahk
