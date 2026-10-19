# 随处 OCR（Anywhere OCR）— PP-OCRv6 GPU 版

按住 `Win` 键并拖动鼠标左键框选屏幕区域，松开后立即识别文字并复制到剪贴板。
本分支将原项目的 PaddleOCR 2.x 本地 DLL 升级为官方
**PP-OCRv6_medium** 检测与识别模型，并支持 NVIDIA GPU 加速。

## 项目来源与致谢

本项目 fork 并改造自 [HKMV/anywhere-ocr](https://github.com/HKMV/anywhere-ocr)。
衷心感谢原作者 **HKMV** 开源这套简洁、实用的屏幕框选 OCR 与翻译工具；
原项目完成了热键框选、结果展示、剪贴板写入及翻译界面的主要工作，本分支是在其
基础上升级 OCR 推理引擎和部署方式。

同时感谢以下项目和作者：

- [PaddlePaddle/PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR)：
  PP-OCRv6 模型、训练成果和 Python 推理框架。
- [@telppa/PaddleOCR-AutoHotkey](https://github.com/telppa/PaddleOCR-AutoHotkey)：
  原始 AutoHotkey PaddleOCR 工具类。
- [@telppa/Translation-Terminator](https://github.com/telppa/Translation-Terminator)：
  翻译工具类。
- [HKMV/Edge.ahk](https://github.com/HKMV/Edge.ahk)：
  使用 Windows 自带的 Edge 内核改进翻译功能。

请在二次分发、修改或发布 Release 时保留上述来源和致谢，并分别遵守各上游项目的
许可证。

## 功能

- `Win + 鼠标左键拖动`：框选任意屏幕区域进行 OCR。
- 自动把识别结果复制到剪贴板。
- 支持简体中文、繁体中文、英文、日文和 46 种拉丁字母语言。
- 支持百度翻译和搜狗翻译。
- 使用 Microsoft Edge 内核显示翻译页面。
- 默认使用 `PP-OCRv6_medium_det` + `PP-OCRv6_medium_rec`。
- 支持 NVIDIA GPU；无 NVIDIA GPU 时也能使用 CPU。
- OCR Worker 进程常驻：模型仅在首次调用时加载，后续框选无需重复初始化。
- 两种可互换的 Worker：基于 ONNX Runtime + DirectML 的原生 C# Worker，只占用几百 MB 内存，任何 Windows 显卡都能加速；以及原有的 PaddleOCR 3.x Python Worker。详见[原生 Worker（低内存）](#原生-worker低内存)。

## 为什么升级到 PP-OCRv6

PP-OCRv6 是 PaddleOCR 新一代通用文字识别方案，medium、small、tiny 三档模型采用
新的 PPLCNetV4 统一骨干网络。相较原项目中年代较早的 PaddleOCR 2.x mobile 模型，
它的主要优势包括：

- **识别精度更高**：官方内部多场景测试中，medium 的文字检测 Hmean 为 `86.2%`，
  文字识别加权准确率为 `83.2%`；相比 PP-OCRv5_server 分别提升 `4.6` 和 `5.1`
  个百分点。
- **多语言模型统一**：medium 和 small 使用同一套模型支持 50 种语言，不必因为
  中、英、日文混排而切换识别模型。
- **复杂场景更可靠**：针对手写字、模糊文字、旋转文字、艺术字、点阵字符、
  数字显示屏、工业字符和不规则场景做了增强。
- **更少的语言幻觉**：PP-OCRv6 仍是检测加识别式 OCR，目标是忠实输出图片中的
  字符，不会像通用视觉语言模型一样主动“改写”原文。
- **GPU 推理速度快**：模型规模仅约 3450 万参数。项目已在 NVIDIA GeForce
  RTX 4060 Ti 16 GB 上完成实际 GPU 推理验证；模型预热完成后，日常屏幕框选的
  检测和识别响应非常快，4k屏幕文本识别耗时 2.5s。
- **可按设备选择规模**：服务器或高性能显卡使用 medium；普通桌面、笔记本可使用
  small；低配置设备可选择 tiny。

详细指标和支持语言请参阅
[PP-OCRv6 官方介绍](https://www.paddleocr.ai/latest/en/version3.x/algorithm/PP-OCRv6/PP-OCRv6.html)。

## 模型档位

| 档位 | 检测模型 | 识别模型 | 总参数量 | 官方定位 | 建议 |
| --- | --- | --- | ---: | --- | --- |
| medium | `PP-OCRv6_medium_det` | `PP-OCRv6_medium_rec` | 约 34.5M | 服务器/高精度 | 本项目默认档；建议使用支持CUDA加速显卡 |
| small | `PP-OCRv6_small_det` | `PP-OCRv6_small_rec` | 约 7.7M | 移动端/桌面端 | 精度与速度平衡 |
| tiny | `PP-OCRv6_tiny_det` | `PP-OCRv6_tiny_rec` | 约 1.5M | 边缘设备/IoT | 内存占用最低、速度最快 |

medium 精度最高；tiny 速度最快。模型越小，低清晰度、手写、艺术字和复杂背景下的
准确率通常越低。

官方模型页面：

- [PP-OCRv6 Hugging Face 模型集合](https://huggingface.co/collections/PaddlePaddle/pp-ocrv6)
- [medium 检测](https://huggingface.co/PaddlePaddle/PP-OCRv6_medium_det) /
  [medium 识别](https://huggingface.co/PaddlePaddle/PP-OCRv6_medium_rec)
- [small 检测](https://huggingface.co/PaddlePaddle/PP-OCRv6_small_det) /
  [small 识别](https://huggingface.co/PaddlePaddle/PP-OCRv6_small_rec)
- [tiny 检测](https://huggingface.co/PaddlePaddle/PP-OCRv6_tiny_det) /
  [tiny 识别](https://huggingface.co/PaddlePaddle/PP-OCRv6_tiny_rec)

## 工作原理

```text
Win + 鼠标框选
       │
       ▼
AutoHotkey 截取区域并保存临时 PNG
       │
       ▼
127.0.0.1:48626 本机 OCR Worker
       │
       ├── PP-OCRv6_medium_det：定位文字
       └── PP-OCRv6_medium_rec：识别文字
       │
       ▼
识别结果返回 AutoHotkey → 界面显示 → 写入剪贴板
```

旧版 `PaddleOCR.dll` 基于 PaddleOCR 2.x，不能直接加载 PP-OCRv6。
`PaddleOCR/PaddleOCR.ahk` 负责启动一个常驻的 Worker 进程，并通过仅监听本机的 HTTP 接口通信，
因此不会在每次框选时重新加载模型。实现这个接口的 Worker 有两个：

| Worker | 实现 | 常驻内存 | 说明 |
| --- | --- | --- | --- |
| `OcrWorker\dist\AnywhereOcrWorker.exe` | C#（.NET 8）+ ONNX Runtime + DirectML | 几百 MB | 只要编译过就优先使用。通过 DirectML 支持 NVIDIA、AMD、Intel 显卡，也可用 CPU。 |
| `PaddleOCR\PaddleOCR_worker.py` | Python + PaddleOCR 3.x + PaddlePaddle | 数 GB | 原有 Worker，同时用于下载官方模型。 |

`PaddleOCR.ahk` 检测到 `OcrWorker\dist\AnywhereOcrWorker.exe` 存在时启动原生 Worker，否则回退到 Python Worker。
两者运行同样的 PP-OCRv6 模型和同样的前后处理，识别结果一致。

## 部署要求

### 必需环境

- Windows 10 或 Windows 11，64 位。
- 使用 AHK 前端时需要
  [AutoHotkey v1.1 Unicode x64](https://www.autohotkey.com/)；使用 Python 前端时
  不需要安装 AutoHotkey。本项目的 AHK 代码是 v1 语法，不能直接使用
  AutoHotkey v2 运行。
- 64 位 Python 3.9–3.13；推荐 Python 3.10。
- PaddleOCR 3.7.x。
- Microsoft Edge（使用翻译功能时需要）。

### GPU 环境

- NVIDIA GPU，建议计算能力不低于 7.5。
- RTX 4060 Ti 属于 Ada 架构，可使用 CUDA 12.6 版 PaddlePaddle。
- CUDA 12.6 包要求 NVIDIA 驱动至少为 `550.54.14`；本机验证驱动为
  `560.94`。
- PaddlePaddle 的 pip 包会安装所需 CUDA/cuDNN 运行库，通常不需要另外配置
  系统 CUDA Toolkit。重点是显卡驱动版本满足要求。

官方安装说明：
[PaddlePaddle Windows pip 安装](https://www.paddlepaddle.org.cn/documentation/docs/en/install/pip/windows-pip_en.html)。

## 部署方式一：Conda + 英伟达显卡

以下命令在 PowerShell 或 Anaconda Prompt 中执行。

### 1. 克隆项目

```powershell
git clone https://github.com/<你的用户名>/<你的仓库名>.git
cd <你的仓库名>
```

### 2. 检查显卡和驱动

```powershell
nvidia-smi
```

确认能看到 NVIDIA GPU，并且 Driver Version 不低于 `550.54.14`。

### 3. 创建 `ult` 环境

本项目会自动优先查找
`C:\ProgramData\miniconda3\envs\ult\python.exe`。如果你的 Conda 安装在其他
目录，也可以在后文通过环境变量指定。

```powershell
conda create -n ult python=3.10 -y
conda activate ult
python -m pip install --upgrade pip
```

### 4. 安装 GPU 版 PaddlePaddle

RTX 4060 Ti 和兼容驱动推荐 CUDA 12.6 wheel：

```powershell
python -m pip install paddlepaddle-gpu==3.2.2 -i https://www.paddlepaddle.org.cn/packages/stable/cu126/
```

如果驱动较旧但符合 CUDA 11.8 要求，可改用：

```powershell
python -m pip install paddlepaddle-gpu==3.2.2 -i https://www.paddlepaddle.org.cn/packages/stable/cu118/
```

不要同时安装 `paddlepaddle` CPU 包和 `paddlepaddle-gpu` GPU 包。

### 5. 安装 PaddleOCR

```powershell
python -m pip install paddleocr==3.7.0
```

### 6. 验证 PaddlePaddle 和 GPU

```powershell
python -c "import paddle; print('Paddle:', paddle.__version__); print('CUDA build:', paddle.device.is_compiled_with_cuda()); print('GPU count:', paddle.device.cuda.device_count()); paddle.utils.run_check()"
```

期望至少看到：

```text
Paddle: 3.2.2
CUDA build: True
GPU count: 1
PaddlePaddle is installed successfully!
```

### 7. 指定 Python 路径

如果 `ult` 不在默认位置，设置用户级环境变量：

```powershell
[Environment]::SetEnvironmentVariable(
    "ANYWHERE_OCR_PYTHON",
    (conda run -n ult python -c "import sys; print(sys.executable)").Trim(),
    "User"
)
[Environment]::SetEnvironmentVariable("ANYWHERE_OCR_USE_GPU", "1", "User")
[Environment]::SetEnvironmentVariable("ANYWHERE_OCR_GPU_ID", "0", "User")
```

设置后重新启动 AutoHotkey 或注销并重新登录，使新环境变量生效。

也可以只在当前 PowerShell 会话中设置并从该窗口启动脚本：

```powershell
$env:ANYWHERE_OCR_PYTHON = (conda run -n ult python -c "import sys; print(sys.executable)").Trim()
$env:ANYWHERE_OCR_USE_GPU = "1"
$env:ANYWHERE_OCR_GPU_ID = "0"
```

### 8. 下载默认 medium 模型

可以跳过此步；第一次运行 Anywhere OCR 时会自动下载。若要提前下载并确认模型可用：

```powershell
python PaddleOCR\PaddleOCR_worker.py --model-tier medium --device gpu:0 --download-only
```

成功后会显示：

```text
PP-OCRv6_medium_det and PP-OCRv6_medium_rec are ready.
```

### 9. 启动

双击 `anywhere-ocr.ahk`，或在 PowerShell 中执行：

```powershell
& "C:\Program Files\AutoHotkey\AutoHotkeyU64.exe" ".\anywhere-ocr.ahk"
```

首次启动需要初始化模型，时间比后续识别长。启动完成后，按住 `Win` 并拖动鼠标左键
框选文字区域。

### Python 前端

仓库同时提供与 AHK 前端功能对应的 `anywhere-ocr.py`。它使用相同的
PP-OCRv6 medium Worker，并支持 Win+鼠标框选、自动复制识别结果、F3 显示主窗口，
以及百度/搜狗翻译入口。Python 前端使用 Windows API 完成截图和全局按键检测，不需要
额外安装 Pillow、pywin32 或键鼠监听库。它通过 Windows 低级输入钩子拦截框选期间的
鼠标事件，因此不会同时触发桌面选中、文件拖动或其他窗口中的点击操作；退出时会自动
卸载钩子。

在已安装 PaddleOCR 的环境中运行：

```powershell
python .\anywhere-ocr.py
```

也可以继续用 `ANYWHERE_OCR_PYTHON`、`ANYWHERE_OCR_USE_GPU` 和
`ANYWHERE_OCR_GPU_ID` 指定 Worker 的解释器和设备。关闭主窗口只会隐藏程序；按 F3
可以重新显示，点击窗口中的“退出”才会结束 Python 前端。与 AHK 版的内嵌翻译结果窗
不同，Python 版的“翻译”按钮会在默认浏览器中打开所选翻译服务并预填 OCR 文本。

## 部署方式二：纯 CPU

没有 NVIDIA GPU 时仍可运行。medium 在 CPU 上资源占用和延迟较高，普通电脑可考虑
small 或 tiny。

```powershell
conda create -n anywhere-ocr python=3.10 -y
conda activate anywhere-ocr
python -m pip install --upgrade pip
python -m pip install paddlepaddle==3.2.2 -i https://www.paddlepaddle.org.cn/packages/stable/cpu/
python -m pip install paddleocr==3.7.0
```

设置解释器和 CPU 模式：

```powershell
[Environment]::SetEnvironmentVariable(
    "ANYWHERE_OCR_PYTHON",
    (python -c "import sys; print(sys.executable)").Trim(),
    "User"
)
[Environment]::SetEnvironmentVariable("ANYWHERE_OCR_USE_GPU", "0", "User")
```

验证：

```powershell
python -c "import paddle; print(paddle.__version__); print(paddle.device.get_device()); paddle.utils.run_check()"
```

## 原生 Worker（低内存）

Python Worker 会把整个 PaddlePaddle 运行时常驻在内存里：在参考机器上仅仅处于待命状态就要提交约 6 GB 内存（工作集 1.5 GB）外加 2.7 GB 显存。
`OcrWorker\` 目录中的原生 Worker 用一个很小的 C# 程序通过 ONNX Runtime 运行同样的 PP-OCRv6 模型：

- 运行时不需要 Python、PaddlePaddle 或 CUDA，只需要 Windows 10/11 通常已经自带的 .NET 8 运行时，以及随编译产物一起发布的 DirectML DLL。
- 通过 DirectML 做 GPU 加速，NVIDIA、AMD、Intel 显卡都可以，不需要安装 CUDA；传 `--device cpu` 则用 CPU。
- 检测与识别的前后处理（DB 后处理、文本框排序、透视裁剪、CTC 解码）逐行移植自 PaddleX 流水线，结果与 Python Worker 一致。
- 本机协议完全相同（`GET /health`、`POST /ocr`），`PaddleOCR.ahk` 和 `anywhere-ocr.py` 无需改动即可自动选用。

### 1. 编译 Worker

安装 [.NET 8 SDK](https://dotnet.microsoft.com/download)（不需要 C++ 编译器或 Windows SDK），然后运行：

```powershell
OcrWorker\publish.cmd
```

生成 `OcrWorker\dist\AnywhereOcrWorker.exe`，以及 `onnxruntime.dll` 和 `DirectML.dll`。

### 2. 把模型导出为 ONNX

官方 Paddle 推理模型需要转换一次。在装有 `paddlepaddle` 和 `paddle2onnx` 的环境中运行（Conda 部署方式中的 `ult` 环境即可）：

```powershell
python tools\export_onnx.py                # 默认 medium
python tools\export_onnx.py --tier small   # 或 tiny
```

脚本读取 `%USERPROFILE%\.paddlex\official_models`（若模型不存在，先用 `PaddleOCR_worker.py --download-only` 下载），
写出 `models\PP-OCRv6_<tier>_det.onnx`、`models\PP-OCRv6_<tier>_rec.onnx` 和 `models\PP-OCRv6_<tier>_dict.txt`。
medium 的 ONNX 文件约 140 MB，已被 git 忽略。

### 3. 运行

像以前一样双击 `anywhere-ocr.ahk` 即可。也可以手动启动 Worker 查看日志：

```powershell
OcrWorker\dist\AnywhereOcrWorker.exe --device gpu:0 --verbose
```

常用参数：

| 参数 | 默认值 | 说明 |
| --- | --- | --- |
| `--device cpu` / `--device gpu:N` | `cpu` | `gpu:N` 选择第 N 个 DirectML 适配器；DirectML 不可用时自动回退到 CPU。 |
| `--model-tier medium\|small\|tiny` | `medium` | 加载哪一档导出的模型。 |
| `--models DIR` | 项目下的 `models` | 存放 ONNX 文件的目录；环境变量 `ANYWHERE_OCR_MODELS` 作用相同。 |
| `--threads N` | 全部逻辑核心 | CPU 推理的线程数。 |
| `--port N` | `48626` | 在 `127.0.0.1` 上监听的端口。 |
| `--idle-unload SECONDS` | `300` | 这么长时间没有请求后释放模型（连同全部 GPU 缓冲区），下一次请求约多花 1 s 重新加载；`0` 表示一直常驻。环境变量 `ANYWHERE_OCR_IDLE_UNLOAD` 作用相同。 |
| `--test IMAGE [--all] [--repeat N]` | | 识别一个文件，打印文本（`--all` 打印 JSON）后退出。 |

### 选择后端

| 环境变量 | 示例 | 说明 |
| --- | --- | --- |
| `ANYWHERE_OCR_WORKER` | `D:\tools\AnywhereOcrWorker.exe` | 使用存放在别处的原生 Worker。 |
| `ANYWHERE_OCR_BACKEND` | `python` | 即使原生 Worker 存在也强制使用 Python Worker。 |
| `ANYWHERE_OCR_MODELS` | `D:\models` | 导出的 ONNX 模型目录。 |
| `ANYWHERE_OCR_IDLE_UNLOAD` | `120` | 原生 Worker 空闲多少秒后释放模型（`0` 关闭）。 |

`ANYWHERE_OCR_USE_GPU` 和 `ANYWHERE_OCR_GPU_ID` 对两种 Worker 都有效。

### 实测占用

两种 Worker 在同一台机器（Windows 10，RTX 4060 Ti 16 GB，驱动 560.94）上使用 medium 模型，预热后对同一张 2560x1440 截图识别三次：

| Worker | 状态 | 工作集 | 私有字节 | 显存 | 每张截图耗时 |
| --- | --- | ---: | ---: | ---: | ---: |
| Python（PaddleOCR 3.7，CUDA） | 加载后，空闲或识别后 | 1537 MB | 5968 MB | 2728 MB | 1.2–1.4 s |
| 原生（DirectML） | 启动后 | 368 MB | 516 MB | 234 MB | |
| 原生（DirectML） | 识别后 | 471 MB | 4195 MB | 3808 MB | 1.9 s |
| 原生（DirectML） | 空闲卸载后（默认 300 s 无请求） | 155 MB | 127 MB | 0 MB | 下一次请求多花约 1 s |
| 原生（CPU，20 线程） | 识别后 | 442 MB | 447 MB | 0 MB | 40 s |

DirectML 识别后的私有字节主要是 ONNX Runtime 分配器为复用而保留的 D3D12 缓冲区，它们占用的是显存而不是内存；
空闲卸载（`--idle-unload`、`ANYWHERE_OCR_IDLE_UNLOAD`）会连同模型一起释放。DirectML 与 CPU 两次运行的识别文本完全一致，
与 Python Worker 相比 184 行中有 181 行逐字相同，其余几行因插值取整差了一个字符。medium 模型在 CPU 上很重，CPU 场景请改用 small 或 tiny 档位。

## 下载 medium、small 和 tiny模型

每个档位都必须同时下载一套检测模型（`*_det`）和一套识别模型（`*_rec`）。
不要只下载其中一个。

### 使用本项目 Worker 下载（推荐）

激活已经安装 PaddleOCR 的环境，在项目根目录执行：

```powershell
# 高精度 medium
python PaddleOCR\PaddleOCR_worker.py --model-tier medium --device cpu --download-only

# 平衡型 small
python PaddleOCR\PaddleOCR_worker.py --model-tier small --device cpu --download-only

# 迷你型 tiny
python PaddleOCR\PaddleOCR_worker.py --model-tier tiny --device cpu --download-only
```

下载阶段使用 `--device cpu` 即可，即使之后准备在 GPU 上推理也不影响模型文件。
若希望下载后立即测试 GPU 初始化，可以改为 `--device gpu:0`。

### 使用 PaddleOCR Python API 下载

下面的脚本会依次下载三组检测和识别模型：

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

### 模型缓存位置

PaddleOCR/PaddleX 默认把官方模型缓存到：

```text
%USERPROFILE%\.paddlex\official_models\
```

下载完成后通常可以看到：

```text
PP-OCRv6_medium_det
PP-OCRv6_medium_rec
PP-OCRv6_small_det
PP-OCRv6_small_rec
PP-OCRv6_tiny_det
PP-OCRv6_tiny_rec
```

模型一旦缓存成功，后续启动不会重复下载。不要把模型复制到旧的
`PaddleOCR\Dll\inference` 目录；该目录属于旧 PaddleOCR 2.x DLL，不能用于 v6。

### 离线部署

在联网电脑上完成上述下载后，将整个
`%USERPROFILE%\.paddlex\official_models` 目录复制到离线电脑相同用户目录下。
离线电脑仍需提前安装 Python、对应版本的 PaddlePaddle、PaddleOCR 及其依赖。

复制后执行以下命令验证，不应再访问网络：

```powershell
python PaddleOCR\PaddleOCR_worker.py --model-tier medium --device cpu --download-only
```

## 当前项目如何选择模型

Anywhere OCR 默认并固定使用精度最高的 medium：

```autohotkey
PaddleOCR(Vs, {"model":"PP-OCRv6_medium"})
```

下载 small 或 tiny 不会自动改变应用当前使用的模型。它们主要用于测试、低配置部署
或后续二次开发。若需要把整个应用切换到其他档位，需要同时修改 Worker 的启动参数、
健康检查模型名和 AHK 配置；不能把 small 检测模型与 medium 识别模型混搭后假设得到
相同效果。

手动测试其他档位可以启动独立 Worker：

```powershell
python PaddleOCR\PaddleOCR_worker.py --model-tier small --device gpu:0
python PaddleOCR\PaddleOCR_worker.py --model-tier tiny --device gpu:0
```

默认端口是 `48626`。一次只能有一个进程占用该端口，测试前请先退出已有 Worker，
或使用 `--port` 指定其他端口。

## 配置参数

`PaddleOCR(Image, Configs)` 支持：

| 参数 | 默认值 | 说明 |
| --- | --- | --- |
| `model` | `PP-OCRv6_medium` | 当前应用只运行 medium；保留旧别名以兼容原调用 |
| `use_gpu` | 检测到默认 `ult` 时为 `1` | `1` 使用 GPU，`0` 使用 CPU |
| `gpu_id` | `0` | NVIDIA GPU 编号 |
| `python` | 默认 `ult` 或 PATH 中的 Python | Python 解释器完整路径（仅 Python Worker） |
| `worker` | `OcrWorker\dist\AnywhereOcrWorker.exe` | 原生 Worker 可执行文件的完整路径 |
| `backend` | 可执行文件存在时为 `native` | `python` 强制使用 Python Worker |
| `get_all_info` | `0` | `1` 返回包含坐标、得分等数据的 JSON |

也可以通过环境变量配置：

| 环境变量 | 示例 | 说明 |
| --- | --- | --- |
| `ANYWHERE_OCR_PYTHON` | `C:\Miniconda3\envs\ult\python.exe` | Python 路径 |
| `ANYWHERE_OCR_USE_GPU` | `1` | 是否使用 GPU |
| `ANYWHERE_OCR_GPU_ID` | `0` | GPU 编号 |
| `ANYWHERE_OCR_WORKER` | `D:\tools\AnywhereOcrWorker.exe` | 原生 Worker 可执行文件路径 |
| `ANYWHERE_OCR_BACKEND` | `python` | 强制使用 Python Worker |
| `ANYWHERE_OCR_MODELS` | `D:\models` | 导出的 ONNX 模型目录（原生 Worker） |

函数调用参数的优先级高于环境变量。

## 性能说明

- 第一次执行包含模型下载、模型构建和显存分配，因此不能代表日常识别速度。
- Worker 启动完成后的第二次及后续请求才适合测量性能。
- 框选区域越大、文字越密集，检测时间越长。
- medium 在 RTX 4060 Ti 16 GB 上运行流畅；其速度体验明显优于每次重新启动 Python
  或每次重新加载模型的调用方式。
- GPU 使用情况可以通过以下命令观察：

```powershell
nvidia-smi
```

- 若要进行可复现的性能对比，应固定图片、分辨率、模型档位和预热次数，并分别记录
  首次耗时与预热后耗时。本项目不写入未经统一基准测试的毫秒数。

## 常见问题

### 1. `PP-OCRv6_medium failed to start`

先直接运行 Worker 查看完整错误：

```powershell
python PaddleOCR\PaddleOCR_worker.py --model-tier medium --device cpu
```

确认当前 Python 中能导入两个包：

```powershell
python -c "import paddleocr; import paddle; print(paddleocr.__version__, paddle.__version__)"
```

### 2. 显示 `CUDA build: False`

安装的是 CPU 包。先卸载两个可能冲突的版本，再安装 GPU 包：

```powershell
python -m pip uninstall -y paddlepaddle paddlepaddle-gpu
python -m pip install paddlepaddle-gpu==3.2.2 -i https://www.paddlepaddle.org.cn/packages/stable/cu126/
```

### 3. 找不到 Python

设置 `ANYWHERE_OCR_PYTHON` 为真实的 `python.exe` 完整路径，重新启动 AHK。

```powershell
where.exe python
conda run -n ult python -c "import sys; print(sys.executable)"
```

### 4. 端口 `48626` 被占用

```powershell
Get-NetTCPConnection -LocalPort 48626
```

结束旧的 Anywhere OCR Worker，或修改 `PaddleOCR.ahk` 与 Worker 使用的端口。
接口只监听 `127.0.0.1`，不会对局域网或互联网开放。

### 5. 首次运行长时间没有结果

通常正在下载 medium 的检测与识别权重。检查网络，并查看：

```text
%USERPROFILE%\.paddlex\official_models\
```

也可以先执行 `--download-only`，在终端中观察下载进度。

### 6. 模型已经损坏或下载不完整

仅删除损坏的具体模型文件夹，然后重新运行下载命令。不要删除整个用户目录或项目
目录。例如 medium 有问题时，只处理：

```text
%USERPROFILE%\.paddlex\official_models\PP-OCRv6_medium_det
%USERPROFILE%\.paddlex\official_models\PP-OCRv6_medium_rec
```

### 7. AHK v2 报语法错误

请使用 AutoHotkey v1.1 Unicode x64。当前代码没有迁移到 AHK v2 语法。

### 8. 翻译页面脚本错误

打开“控制面板 → Internet 选项 → 安全 → 本地 Intranet → 站点”，取消相关勾选。

### 9. 无法翻译

安装或升级到最新版 [Microsoft Edge](https://www.microsoft.com/zh-cn/edge)。

## 安全说明

- OCR 服务绑定在 `127.0.0.1`，只允许本机访问。
- 截图会临时保存到系统临时目录，识别请求结束后自动删除。
- OCR 模型在本机运行，截图不会由本项目上传到第三方 OCR API。
- 百度/搜狗翻译属于独立功能；使用翻译时，文字会按相应翻译服务的工作方式发送。

## 开发和验证

检查 Worker 参数：

```powershell
python PaddleOCR\PaddleOCR_worker.py --help
```

检查 Python 语法：

```powershell
python -m py_compile PaddleOCR\PaddleOCR_worker.py
```

检查 AHK v1 语法：

```powershell
& "C:\Program Files\AutoHotkey\AutoHotkeyU64.exe" /ErrorStdOut PaddleOCR\PaddleOCR.ahk
```

编译并冒烟测试原生 Worker：

```powershell
dotnet build OcrWorker\OcrWorker.csproj -c Release
OcrWorker\dist\AnywhereOcrWorker.exe --device gpu:0 --test screenshot.png --repeat 3
```

检查 Worker 健康状态：

```powershell
Invoke-WebRequest http://127.0.0.1:48626/health
```

medium Worker 正常时返回：

```text
PP-OCRv6_medium
```
