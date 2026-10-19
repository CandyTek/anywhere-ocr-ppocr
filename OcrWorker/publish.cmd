@echo off
rem Builds the native OCR worker into OcrWorker\dist\AnywhereOcrWorker.exe.
rem Requires the .NET 8 SDK (https://dotnet.microsoft.com/download). No C++ toolchain is needed.
setlocal
cd /d "%~dp0"
dotnet publish OcrWorker.csproj -c Release -r win-x64 --self-contained false -o dist %*
if errorlevel 1 (
    echo.
    echo Build failed. Make sure the .NET 8 SDK is installed: dotnet --list-sdks
    exit /b 1
)
rem Debug builds of DirectML and symbol files are not needed at run time.
del /q dist\DirectML.Debug.* dist\*.pdb dist\onnxruntime.lib >nul 2>&1
echo.
echo Built dist\AnywhereOcrWorker.exe
echo Export the ONNX models next:  python tools\export_onnx.py
endlocal
