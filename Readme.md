# Anywhere OCR — PP-OCRv6 (GPU-Accelerated)

Hold the `Win` key and drag your left mouse button to select an area on the screen. Upon release, the text within the selection is immediately recognized and copied to your clipboard.
This branch upgrades the original project’s PaddleOCR 2.x local DLLs to the official **PP-OCRv6_medium** detection and recognition model, with added support for NVIDIA GPU acceleration.

## Origins and Acknowledgments

This project is a fork and modification of [HKMV/anywhere-ocr](https://github.com/HKMV/anywhere-ocr).
Many thanks to the original author, **HKMV**, for open-sourcing this concise and practical tool for screen-selection OCR and translation.
The original project established the core functionality—hotkey-based screen selection, result display, clipboard integration, and a translation interface—upon which this branch builds by upgrading the OCR inference engine and deployment method.

Special thanks also go to the following projects and authors:

- [PaddlePaddle/PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR):
For the PP-OCRv6 models, training results, and Python inference framework.
- [@telppa/PaddleOCR-AutoHotkey](https://github.com/telppa/PaddleOCR-AutoHotkey):
For the original AutoHotkey PaddleOCR utility class.
- [@telppa/Translation-Terminator](https://github.com/telppa/Translation-Terminator):
For the translation utility class.
- [HKMV/Edge.ahk](https://github.com/HKMV/Edge.ahk):
For leveraging the Windows-native Edge engine to enhance translation capabilities.

Please retain these attributions and acknowledgments when redistributing, modifying, or publishing releases, and ensure compliance with the respective licenses of each upstream project.

## Features

- `Win + Left Mouse Drag`: Select any area on the screen to perform OCR.
- Automatically copies recognized text to the clipboard.
- Supports Simplified Chinese, Traditional Chinese, English, Japanese, and 46 other languages ​​that use the Latin script. - Supports Baidu Translate and Sogou Translate.
- Uses the Microsoft Edge engine to display translation pages.
- Defaults to using `PP-OCRv6_medium_det` + `PP-OCRv6_medium_rec`.
- Supports NVIDIA GPUs; can also run on a CPU if no NVIDIA GPU is available.
- Persistent OCR worker process: models are loaded only upon the first invocation, eliminating the need for re-initialization during subsequent selections.
- Two interchangeable workers: a native C# worker built on ONNX Runtime + DirectML that needs only a few hundred MB of RAM and works on any Windows GPU, and the original Python worker built on PaddleOCR 3.x. See [Native Worker (Low Memory)](#native-worker-low-memory).

## Why Upgrade to PP-OCRv6

PP-OCRv6 is the latest generation of general-purpose text recognition solutions from PaddleOCR. Its "medium," "small," and "tiny" model variants utilize the new PPLCNetV4 unified backbone network. Compared to the older PaddleOCR 2.x mobile models used in previous versions of this project, it offers several key advantages:

- **Higher Recognition Accuracy**: In internal multi-scenario testing, the "medium" model achieved an Hmean of `86.2%` for text detection and a weighted accuracy of `83.2%` for text recognition—improvements of `4.6` and `5.1` percentage points, respectively, over the PP-OCRv5_server model.
- **Unified Multilingual Support**: The "medium" and "small" models support 50 languages ​​within a single framework, removing the need to switch models when handling mixed-language text (e.g., Chinese, English, and Japanese).
- **Enhanced Reliability in Complex Scenarios**: Improved performance for handwritten text, blurred text, rotated text, stylized/artistic fonts, dot-matrix characters, digital displays, industrial markings, and irregular layouts.
- **Reduced "Hallucinations"**: As a detection-and-recognition-based OCR system, PP-OCRv6 focuses on faithfully transcribing the characters present in an image, rather than "rewriting" the original text as some general-purpose vision-language models might.
- **Fast GPU Inference**: The model consists of approximately 34.5 million parameters. The project has been validated through actual GPU inference on an NVIDIA GeForce RTX 4060 Ti (16 GB). Once the model is warmed up, it delivers highly responsive detection and recognition for user-selected screen areas; text recognition on a 4K screen takes approximately 2.5 seconds.
- **Model sizing based on hardware**: Use the `medium` model for servers or high-performance GPUs; the `small` model for standard desktops or laptops; and the `tiny` model for low-end devices.

For detailed metrics and supported languages, please refer to the [official PP-OCRv6 documentation](https://www.paddleocr.ai/latest/en/version3.x/algorithm/PP-OCRv6/PP-OCRv6.html).

## Model Tiers

| Tier | Detection Model | Recognition Model | Total Parameters | Intended Use | Recommendation |
| --- | --- | --- | ---: | --- | --- |
| medium | `PP-OCRv6_medium_det` | `PP-OCRv6_medium_rec` | ~34.5M | Servers / High Accuracy | Default tier for this project; a GPU with CUDA acceleration is recommended. |
| small | `PP-OCRv6_small_det` | `PP-OCRv6_small_rec` | ~7.7M | Mobile / Desktop | Balanced accuracy and speed. |
| tiny | `PP-OCRv6_tiny_det` | `PP-OCRv6_tiny_rec` | ~1.5M | Edge Devices / IoT | Lowest memory footprint and fastest speed. |

The `medium` tier offers the highest accuracy, while the `tiny` tier provides the fastest speed. Generally, smaller models exhibit lower accuracy when handling low-resolution text, handwriting, stylized fonts, or text against complex backgrounds. Official Model Pages:

- [PP-OCRv6 Hugging Face Model Collection](https://huggingface.co/collections/PaddlePaddle/pp-ocrv6)
- [Medium Detection](https://huggingface.co/PaddlePaddle/PP-OCRv6_medium_det) /
[Medium Recognition](https://huggingface.co/PaddlePaddle/PP-OCRv6_medium_rec)
- [Small Detection](https://huggingface.co/PaddlePaddle/PP-OCRv6_small_det) /
[Small Recognition](https://huggingface.co/PaddlePaddle/PP-OCRv6_small_rec)
- [Tiny Detection](https://huggingface.co/PaddlePaddle/PP-OCRv6_tiny_det) /
[Tiny Recognition](https://huggingface.co/PaddlePaddle/PP-OCRv6_tiny_rec)

## How It Works

```text
Win + Mouse Selection (Drag-to-select)
│
▼
AutoHotkey captures the selected area and saves it as a temporary PNG
│
▼
Local OCR Worker at 127.0.0.1:48626
│
├── PP-OCRv6_medium_det: Locates text
└── PP-OCRv6_medium_rec: Recognizes text
│
▼
Results returned to AutoHotkey → Displayed on screen → Copied to clipboard
```

The legacy `PaddleOCR.dll` was based on PaddleOCR 2.x and cannot load PP-OCRv6 directly. `PaddleOCR/PaddleOCR.ahk` manages a resident worker process and communicates with it through a local-only HTTP interface, so the models do not need to be reloaded every time a selection is made. Two workers implement that interface:

| Worker | Implementation | Typical RAM | Notes |
| --- | --- | --- | --- |
| `OcrWorker\dist\AnywhereOcrWorker.exe` | C# (.NET 8) + ONNX Runtime + DirectML | a few hundred MB | Preferred whenever it has been built. Runs on NVIDIA, AMD and Intel GPUs through DirectML, or on the CPU. |
| `PaddleOCR\PaddleOCR_worker.py` | Python + PaddleOCR 3.x + PaddlePaddle | several GB | The original worker; also used for downloading the official models. |

`PaddleOCR.ahk` starts the native worker when `OcrWorker\dist\AnywhereOcrWorker.exe` exists and falls back to the Python worker otherwise. Both workers run the same PP-OCRv6 models with the same pre- and post-processing, so the recognized text is the same.

## Deployment Requirements

### Required Environment

- Windows 10 or Windows 11 (64-bit). - If using the AHK frontend, [AutoHotkey v1.1 Unicode x64](https://www.autohotkey.com/) is required; if using the Python frontend, AutoHotkey installation is not necessary. The AHK code in this project uses v1 syntax and cannot be run directly with AutoHotkey v2.
- 64-bit Python 3.9–3.13 (Python 3.10 is recommended).
- PaddleOCR 3.7.x.
- Microsoft Edge (required if using the translation feature).

### GPU Environment

- NVIDIA GPU with a compute capability of 7.5 or higher is recommended.
- The RTX 4060 Ti, based on the Ada architecture, supports PaddlePaddle with CUDA 12.6.
- The CUDA 12.6 package requires an NVIDIA driver version of at least `550.54.14`; the version verified for this setup is `560.94`.
- The PaddlePaddle pip package includes the necessary CUDA/cuDNN runtime libraries, so there is generally no need to configure a separate system-wide CUDA Toolkit. The key requirement is that your graphics driver version meets the specified criteria.

Official installation instructions:
[PaddlePaddle Windows pip installation](https://www.paddlepaddle.org.cn/documentation/docs/en/install/pip/windows-pip_en.html).

## Deployment Method 1: Conda + NVIDIA GPU

Execute the following commands in PowerShell or an Anaconda Prompt.

### 1. Clone the project

```powershell
git clone https://github.com/<your-username>/<your-repository-name>.git
cd <your-repository-name>
```

### 2. Check GPU and driver

```powershell
nvidia-smi
```

Ensure that your NVIDIA GPU is detected and that the "Driver Version" is at least `550.54.14`.

### 3. Create the `ult` environment

This project is configured to automatically look for the Python interpreter at `C:\ProgramData\miniconda3\envs\ult\python.exe`. If your Conda is installed in a different
directory, you can specify it later using an environment variable.

```powershell
conda create -n ult python=3.10 -y
conda activate ult
python -m pip install --upgrade pip
```

### 4. Install the GPU Version of PaddlePaddle

For an RTX 4060 Ti with compatible drivers, the CUDA 12.6 wheel is recommended:

```powershell
python -m pip install paddlepaddle-gpu==3.2.2 -i https://www.paddlepaddle.org.cn/packages/stable/cu126/
```

If you have older drivers that meet the requirements for CUDA 11.8, use this instead:

```powershell
python -m pip install paddlepaddle-gpu==3.2.2 -i https://www.paddlepaddle.org.cn/packages/stable/cu118/
```

Do not install both the `paddlepaddle` (CPU) package and the `paddlepaddle-gpu` (GPU) package simultaneously.

### 5. Install PaddleOCR

```powershell
python -m pip install paddleocr==3.7.0
```
### 6. Verify PaddlePaddle and GPU

```powershell
python -c "import paddle; print('Paddle:', paddle.__version__); print('CUDA build:', paddle.device.is_compiled_with_cuda()); print('GPU count:', paddle.device.cuda.device_count()); paddle.utils.run_check()"
```

You should see at least the following:

```text
Paddle: 3.2.2
CUDA build: True
GPU count: 1
PaddlePaddle is installed successfully!
```

### 7. Specify the Python Path

If the `ult` environment is not in the default location, set the user-level environment variables:

```powershell
[Environment]::SetEnvironmentVariable(
"ANYWHERE_OCR_PYTHON",
(conda run -n ult python -c "import sys; print(sys.executable)").Trim(),
"User"
)
[Environment]::SetEnvironmentVariable("ANYWHERE_OCR_USE_GPU", "1", "User")
[Environment]::SetEnvironmentVariable("ANYWHERE_OCR_GPU_ID", "0", "User")
```

After setting these, restart AutoHotkey, or log out and log back in for the new environment variables to take effect.

Alternatively, you can set them only for the current PowerShell session and launch the script from that same window:

```powershell
$env:ANYWHERE_OCR_PYTHON = (conda run -n ult python -c "import sys; print(sys.executable)").Trim()
$env:ANYWHERE_OCR_USE_GPU = "1"
$env:ANYWHERE_OCR_GPU_ID = "0"
```

### 8. Download the Default Medium Model

This step is optional; the model will be downloaded automatically the first time you run Anywhere OCR. To download the models in advance and verify their availability, run:

```powershell
python PaddleOCR\PaddleOCR_worker.py --model-tier medium --device gpu:0 --download-only
```

Upon success, the following message will appear:

```text
PP-OCRv6_medium_det and PP-OCRv6_medium_rec are ready.
```

### 9. Launching

Double-click `anywhere-ocr.ahk`, or execute the following command in PowerShell:

```powershell
& "C:\Program Files\AutoHotkey\AutoHotkeyU64.exe" ".\anywhere-ocr.ahk"
```

The first launch requires model initialization, which takes longer than subsequent recognition tasks. Once initialized, hold down the `Win` key and use the left mouse button to drag and select the area containing the text.

### Python Frontend

The repository also provides `anywhere-ocr.py`, which offers functionality equivalent to the AHK frontend. It utilizes the same PP-OCRv6 medium worker and supports features such as `Win` + mouse-drag selection, automatic copying of recognized text, displaying the main window via the `F3` key, and integration with Baidu/Sogou translation services. The Python frontend uses Windows APIs for screen capture and global key detection, requiring no additional installations of libraries like Pillow, pywin32, or keyboard/mouse listeners. It employs Windows low-level input hooks to intercept mouse events during selection, ensuring that actions such as desktop item selection, file dragging, or clicks in other windows are not triggered; these hooks are automatically removed upon exit.

Run the script in an environment where PaddleOCR is installed:

```powershell
python .\anywhere-ocr.py
```

You can also use the environment variables `ANYWHERE_OCR_PYTHON`, `ANYWHERE_OCR_USE_GPU`, and `ANYWHERE_OCR_GPU_ID` to specify the Python interpreter and device for the worker. Closing the main window only hides the application; press `F3` to bring it back. To fully terminate the Python frontend, click the "Exit" button within the main window. Unlike the AHK version, which displays translations in an embedded window, the Python version's "Translate" button opens the selected translation service in your default web browser with the OCR-extracted text pre-filled. ## Deployment Method 2: CPU-Only

You can run the software even without an NVIDIA GPU. The `medium` model has high resource requirements and latency when running on a CPU; for standard computers, consider using the `small` or `tiny` models instead.

```powershell
conda create -n anywhere-ocr python=3.10 -y
conda activate anywhere-ocr
python -m pip install --upgrade pip
python -m pip install paddlepaddle==3.2.2 -i https://www.paddlepaddle.org.cn/packages/stable/cpu/
python -m pip install paddleocr==3.7.0
```

Configure the interpreter and CPU mode:

```powershell
[Environment]::SetEnvironmentVariable(
"ANYWHERE_OCR_PYTHON",
(python -c "import sys; print(sys.executable)").Trim(),
"User"
)
[Environment]::SetEnvironmentVariable("ANYWHERE_OCR_USE_GPU", "0", "User")
```

Verification:

```powershell
python -c "import paddle; print(paddle.__version__); print(paddle.device.get_device()); paddle.utils.run_check()"
```

## Native Worker (Low Memory)

The Python worker keeps the whole PaddlePaddle runtime resident. On the reference machine it commits about 6 GB of memory (1.5 GB working set) plus 2.7 GB of GPU memory just to stay ready. The native worker in `OcrWorker\` replaces it with a small C# program that runs the same PP-OCRv6 models through ONNX Runtime:

- No Python, PaddlePaddle or CUDA at run time. Only the .NET 8 runtime is needed, which Windows 10/11 users usually already have, plus the DirectML DLL that ships with the build.
- GPU acceleration through DirectML, so it works on NVIDIA, AMD and Intel GPUs and needs no CUDA toolkit. Pass `--device cpu` to run on the CPU instead.
- The detection and recognition pre/post-processing (DB post-processing, box sorting, perspective crops, CTC decoding) is a line-by-line port of the PaddleX pipeline, so results match the Python worker.
- Same localhost protocol (`GET /health`, `POST /ocr`), so `PaddleOCR.ahk` and `anywhere-ocr.py` work unchanged and pick it automatically.

### 1. Build the worker

Install the [.NET 8 SDK](https://dotnet.microsoft.com/download) (no C++ compiler or Windows SDK is required), then run:

```powershell
OcrWorker\publish.cmd
```

This produces `OcrWorker\dist\AnywhereOcrWorker.exe` together with `onnxruntime.dll` and `DirectML.dll`.

### 2. Export the models to ONNX

The official Paddle inference files must be converted once. In an environment that has `paddlepaddle` and `paddle2onnx` (the `ult` environment from the Conda deployment works), run:

```powershell
python tools\export_onnx.py                # medium, the default
python tools\export_onnx.py --tier small   # or tiny
```

The script reads `%USERPROFILE%\.paddlex\official_models` (download the models first with `PaddleOCR_worker.py --download-only` if they are missing) and writes `models\PP-OCRv6_<tier>_det.onnx`, `models\PP-OCRv6_<tier>_rec.onnx` and `models\PP-OCRv6_<tier>_dict.txt`. The ONNX files are about 140 MB for `medium` and are ignored by git.

### 3. Run

Double-click `anywhere-ocr.ahk` as before. The worker can also be started by hand to see its log:

```powershell
OcrWorker\dist\AnywhereOcrWorker.exe --device gpu:0 --verbose
```

Useful options:

| Option | Default | Description |
| --- | --- | --- |
| `--device cpu` / `--device gpu:N` | `cpu` | `gpu:N` selects DirectML adapter `N`; falls back to CPU if DirectML is unavailable. |
| `--model-tier medium\|small\|tiny` | `medium` | Which exported model pair to load. |
| `--models DIR` | `models` next to the project | Folder with the exported ONNX files; `ANYWHERE_OCR_MODELS` does the same. |
| `--threads N` | all logical cores | Intra-op threads for CPU inference. |
| `--port N` | `48626` | Listening port on `127.0.0.1`. |
| `--idle-unload SECONDS` | `300` | Release the models (and all GPU buffers) after this long without requests; the next request reloads them in about 1 s. `0` keeps them loaded. `ANYWHERE_OCR_IDLE_UNLOAD` does the same. |
| `--test IMAGE [--all] [--repeat N]` | | Recognize one file, print the text (or JSON with `--all`) and exit. |

### Choosing the backend

| Environment Variable | Example | Description |
| --- | --- | --- |
| `ANYWHERE_OCR_WORKER` | `D:\tools\AnywhereOcrWorker.exe` | Use a native worker stored elsewhere. |
| `ANYWHERE_OCR_BACKEND` | `python` | Force the Python worker even if the native one exists. |
| `ANYWHERE_OCR_MODELS` | `D:\models` | Folder with the exported ONNX models. |
| `ANYWHERE_OCR_IDLE_UNLOAD` | `120` | Seconds without requests after which the native worker releases the models (`0` disables). |

`ANYWHERE_OCR_USE_GPU` and `ANYWHERE_OCR_GPU_ID` apply to both workers.

### Measured footprint

Both workers were measured on the same machine (Windows 10, RTX 4060 Ti 16 GB, driver 560.94) with the `medium` models, recognizing a 2560x1440 screenshot three times after warm-up.

| Worker | State | Working set | Private bytes | GPU memory | Time per screenshot |
| --- | --- | ---: | ---: | ---: | ---: |
| Python (PaddleOCR 3.7, CUDA) | loaded, idle or after OCR | 1537 MB | 5968 MB | 2728 MB | 1.2–1.4 s |
| Native (DirectML) | after start-up | 368 MB | 516 MB | 234 MB | |
| Native (DirectML) | after OCR | 471 MB | 4195 MB | 3808 MB | 1.9 s |
| Native (DirectML) | after idle unload (default: 300 s without requests) | 155 MB | 127 MB | 0 MB | next request takes about 1 s longer |
| Native (CPU, 20 threads) | after OCR | 442 MB | 447 MB | 0 MB | 40 s |

The DirectML private bytes after an OCR are the D3D12 buffers that ONNX Runtime's allocator keeps for reuse; they are backed by GPU memory rather than RAM, and the idle unload (`--idle-unload`, `ANYWHERE_OCR_IDLE_UNLOAD`) releases them together with the models. The recognized text was identical between the DirectML and CPU runs and matched the Python worker on 181 of 184 lines; the remaining lines differed by one character because of interpolation rounding. The `medium` models are heavy on a CPU, so use the `small` or `tiny` tier there.

## Downloading `medium`, `small`, and `tiny` Models

For each tier, you must download both a detection model (`*_det`) and a recognition model (`*_rec`).
Do not download only one of them.

### Using the Project's Worker to Download (Recommended)

Activate the environment where PaddleOCR is installed, then run the following commands from the project root directory:

```powershell
# High-precision: medium
python PaddleOCR\PaddleOCR_worker.py --model-tier medium --device cpu --download-only

# Balanced: small
python PaddleOCR\PaddleOCR_worker.py --model-tier small --device cpu --download-only

# Lightweight: tiny
python PaddleOCR\PaddleOCR_worker.py --model-tier tiny --device cpu --download-only
```

You can use `--device cpu` during the download phase; this will not affect the model files, even if you intend to perform inference on a GPU later. If you wish to test GPU initialization immediately after the download, you can change the setting to `--device gpu:0`.

### Downloading via the PaddleOCR Python API

The following script will download three sets of detection and recognition models:

```powershell
@'
from paddleocr import PaddleOCR

for tier in ("medium", "small", "tiny"):
name = f"PP-OCRv6_{tier}"
print(f"Preparing {name}...")
PaddleOCR(
text_detection_model_name=f"{name}_det",
text_recognition_model_name=f"{name}_rec",
use_doc_orientation_classify=False,
use_doc_unwarping=False,
use_textline_orientation=False,
device="cpu",
)
print(f"{name} is ready.")
'@ | python -
```

### Model Cache Location

By default, PaddleOCR/PaddleX caches official models at:

```text
%USERPROFILE%\.paddlex\official_models\
```

Once the download is complete, you should see the following:

```text
PP-OCRv6_medium_det
PP-OCRv6_medium_rec
PP-OCRv6_small_det
PP-OCRv6_small_rec
PP-OCRv6_tiny_det
PP-OCRv6_tiny_rec
```

Once cached, the models will not be downloaded again during subsequent launches. Do not copy these models to the legacy `PaddleOCR\Dll\inference` directory; that directory is intended for older PaddleOCR 2.x DLLs and is incompatible with v6.

### Offline Deployment

After completing the download on a machine with internet access, copy the entire `%USERPROFILE%\.paddlex\official_models` directory to the corresponding user directory on the offline machine. The offline machine must still have Python, the appropriate version of PaddlePaddle, PaddleOCR, and all necessary dependencies installed beforehand. After copying, execute the following command to verify; it should no longer attempt to access the network:

```powershell
python PaddleOCR\PaddleOCR_worker.py --model-tier medium --device cpu --download-only
```

## Selecting Models for This Project

Anywhere OCR defaults to—and is hardcoded to use—the `medium` model, which offers the highest accuracy:

```autohotkey
PaddleOCR(Vs, {"model":"PP-OCRv6_medium"})
```

Downloading the `small` or `tiny` models will not automatically change the model currently used by the application. These are primarily intended for testing, deployment on low-spec hardware, or further secondary development. If you wish to switch the entire application to a different model tier, you must update the Worker's startup parameters, the model name used for health checks, and the AHK configuration. You cannot simply mix and match components—such as using a `small` detection model with a `medium` recognition model—and expect the same level of performance.

To manually test other tiers, you can launch a separate Worker instance:

```powershell
python PaddleOCR\PaddleOCR_worker.py --model-tier small --device gpu:0
python PaddleOCR\PaddleOCR_worker.py --model-tier tiny --device gpu:0
```

The default port is `48626`. Since only one process can occupy this port at a time, please ensure any existing Worker instance is closed before testing, or specify a different port using the `--port` argument.

## Configuration Parameters

`PaddleOCR(Image, Configs)` supports the following:

| Parameter | Default Value | Description |
| --- | --- | --- |
| `model` | `PP-OCRv6_medium` | Currently, only the `medium` model is supported; legacy aliases are retained for backward compatibility. |
| `use_gpu` | `1` (if `ult` is detected) | `1` to use GPU, `0` to use CPU. |
| `gpu_id` | `0` | NVIDIA GPU index. |
| `python` | `ult` (default) or the Python executable found in the system PATH | Full path to the Python interpreter (Python worker only). |
| `worker` | `OcrWorker\dist\AnywhereOcrWorker.exe` | Full path to the native worker executable. |
| `backend` | `native` when the executable exists | `python` forces the Python worker. |
| `get_all_info` | `0` | `1` to return a JSON object containing data such as coordinates and confidence scores. |

Configuration can also be managed via environment variables:

| Environment Variable | Example | Description |
| --- | --- | --- |
| `ANYWHERE_OCR_PYTHON` | `C:\Miniconda3\envs\ult\python.exe` | Path to the Python executable. |
| `ANYWHERE_OCR_USE_GPU` | `1` | Whether to use the GPU. |
| `ANYWHERE_OCR_GPU_ID` | `0` | GPU index. |
| `ANYWHERE_OCR_WORKER` | `D:\tools\AnywhereOcrWorker.exe` | Path to the native worker executable. |
| `ANYWHERE_OCR_BACKEND` | `python` | Force the Python worker. |
| `ANYWHERE_OCR_MODELS` | `D:\models` | Folder with the exported ONNX models (native worker). |

Parameters passed during function calls take precedence over environment variables.

## Performance Notes

- The first execution includes model downloading, model construction, and VRAM allocation; therefore, it is not representative of typical, ongoing recognition speeds.
- Performance should be measured starting from the second request—after the worker process has fully initialized.
- Larger selected regions and higher text density result in longer detection times.
- The `medium` model runs smoothly on an RTX 4060 Ti (16 GB). This approach offers significantly better performance than methods that require restarting the Python interpreter or reloading the model for every call.
- GPU usage can be monitored using the following command:

```powershell
nvidia-smi
```

- For reproducible performance benchmarking, ensure that the input image, resolution, model variant, and number of "warm-up" iterations remain constant. Record both the initial execution time and the time taken for subsequent (warmed-up) requests. This project does not include millisecond-latency figures that haven't been verified through standardized benchmarking.

## FAQ

### 1. `PP-OCRv6_medium failed to start`

Run the Worker directly to view the full error message:

```powershell
python PaddleOCR\PaddleOCR_worker.py --model-tier medium --device cpu
```

Verify that both packages can be imported in your current Python environment:

```powershell
python -c "import paddleocr; import paddle; print(paddleocr.__version__, paddle.__version__)"
```

### 2. Displays `CUDA build: False`

You have installed the CPU-only version. Uninstall any potentially conflicting versions, then install the GPU-enabled package:

```powershell
python -m pip uninstall -y paddlepaddle paddlepaddle-gpu
python -m pip install paddlepaddle-gpu==3.2.2 -i https://www.paddlepaddle.org.cn/packages/stable/cu126/
```

### 3. Python not found

Set `ANYWHERE_OCR_PYTHON` to the full, absolute path of your `python.exe` file, then restart AHK.

```powershell
where.exe python
conda run -n ult python -c "import sys; print(sys.executable)"
```

### 4. Port `48626` is already in use

```powershell
Get-NetTCPConnection -LocalPort 48626
```

Terminate the existing Anywhere OCR Worker process, or change the port used by both `PaddleOCR.ahk` and the Worker.
The interface listens only on `127.0.0.1` and is not exposed to the local network or the internet.

### 5. No results after a long wait during the first run

The application is likely downloading the detection and recognition weights for the 'medium' model tier. Check your network connection and inspect the following directory:

```text
%USERPROFILE%\.paddlex\official_models\
```

Alternatively, you can run the application with the `--download-only` flag to monitor the download progress in your terminal.

### 6. Model is corrupted or the download is incomplete

Delete only the specific folder for the corrupted model, then re-run the download command. Do not delete the entire user directory or project
directory. For example, if there is an issue with the "medium" model, address only the following:

```text
%USERPROFILE%\.paddlex\official_models\PP-OCRv6_medium_det
%USERPROFILE%\.paddlex\official_models\PP-OCRv6_medium_rec
```

### 7. AHK v2 Syntax Errors

Please use AutoHotkey v1.1 Unicode x64. The current code has not been migrated to AHK v2 syntax.

### 8. Script Errors on Translation Pages

Go to "Control Panel → Internet Options → Security → Local Intranet → Sites" and uncheck the relevant options.

### 9. Translation Fails

Install or upgrade to the latest version of [Microsoft Edge](https://www.microsoft.com/en-us/edge).

## Security Notes

- The OCR service is bound to `127.0.0.1` and is accessible only from the local machine.
- Screenshots are saved temporarily in the system's temporary directory and are automatically deleted once the recognition request is complete.
- OCR models run locally; screenshots are not uploaded to any third-party OCR API by this project.
- Baidu/Sogou translation features are independent; when used, text is transmitted according to the operational protocols of the respective translation service.

## Development and Verification

Check Worker parameters:

```powershell
python PaddleOCR\PaddleOCR_worker.py --help
```

Check Python syntax:

```powershell
python -m py_compile PaddleOCR\PaddleOCR_worker.py
```

Check AHK v1 syntax:

```powershell
& "C:\Program Files\AutoHotkey\AutoHotkeyU64.exe" /ErrorStdOut PaddleOCR\PaddleOCR.ahk
```

Build and smoke-test the native worker:

```powershell
dotnet build OcrWorker\OcrWorker.csproj -c Release
OcrWorker\dist\AnywhereOcrWorker.exe --device gpu:0 --test screenshot.png --repeat 3
```

Check Worker health status:

```powershell
Invoke-WebRequest http://127.0.0.1:48626/health
```

If the "medium" Worker is functioning correctly, it will return:

```text
PP-OCRv6_medium
```

### Real-world testing

  Measured on this machine (RTX 4060 Ti, medium models, same screenshot):

  |         Worker         |              State               |  Working set  | Private |  GPU   |  Per screenshot  |
  | --- | --- | --- | --- | --- | --- |
  | Python (CUDA)          | idle or after OCR                |    1537 MB | 5968 MB |  2728 MB |        1.2–1.4 s |
  | Native DirectML        | after OCR                        |     471 MB | 4195 MB |  3808 MB |            1.9 s |
  | Native DirectML        | after idle unload (default 300s)   |     155 MB |  127 MB |      0 | next request +1s  |
  | Native CPU (20 threads)         | after OCR                        |     442 MB |  447 MB |      0 |             40 s |

  The DirectML private bytes after OCR are GPU buffers that ONNX Runtime caches, backed by VRAM rather than RAM. The
  idle unload releases them. The medium model is slow on CPU, so the small or tiny tier is the right choice without a
  GPU.