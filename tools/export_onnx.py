"""Export the PP-OCRv6 detection/recognition models to ONNX for the native worker.

The native worker (OcrWorker/AnywhereOcrWorker.exe) runs the official PaddleOCR
models through ONNX Runtime, so the Paddle inference files that PaddleOCR
downloads into %USERPROFILE%\\.paddlex\\official_models must be converted once:

    python tools\\export_onnx.py                 # medium (default tier)
    python tools\\export_onnx.py --tier small    # or tiny

This writes, for the chosen tier, into the project's ``models`` folder:

    PP-OCRv6_<tier>_det.onnx
    PP-OCRv6_<tier>_rec.onnx
    PP-OCRv6_<tier>_dict.txt    (the recognizer's character list, one per line)

Requirements: ``pip install paddlepaddle paddle2onnx pyyaml`` (CPU PaddlePaddle is
enough) and the model directories, which ``PaddleOCR_worker.py --download-only``
or any PaddleOCR 3.x run creates.

Known problem on Windows: the paddle2onnx 2.x wheels for Windows import C++
symbols (``pir::...``) that the stable Windows PaddlePaddle wheels (3.2.2 and
3.4.0 were checked) do not export, so ``import paddle2onnx`` fails with
"DLL load failed ... The specified procedure could not be found" no matter which
Python is used.  The Linux wheels work, so the simplest route on Windows is WSL:

    wsl -d <distro>
    uv venv --python 3.10 ~/p2o
    uv pip install --python ~/p2o/bin/python paddlepaddle==3.2.2 paddle2onnx==2.1.0 \\
        onnx packaging pyyaml setuptools \\
        --index-url https://www.paddlepaddle.org.cn/packages/stable/cpu/ \\
        --index-strategy unsafe-best-match --extra-index-url https://pypi.org/simple
    ~/p2o/bin/python /mnt/<drive>/path/to/anywhere-ocr/tools/export_onnx.py \\
        --source /mnt/c/Users/<you>/.paddlex/official_models

The exported ONNX files are platform independent.
"""

from __future__ import annotations

import argparse
import os
import sys
from pathlib import Path


def default_models_dir() -> Path:
    return Path(os.path.expanduser("~")) / ".paddlex" / "official_models"


def write_dictionary(rec_dir: Path, target: Path) -> int:
    import yaml  # pyyaml, installed with PaddleOCR / PaddleX

    with open(rec_dir / "inference.yml", encoding="utf-8") as handle:
        config = yaml.safe_load(handle)
    chars = config["PostProcess"]["character_dict"]
    with open(target, "w", encoding="utf-8", newline="\n") as handle:
        for char in chars:
            text = str(char)
            if "\n" in text or "\r" in text:
                raise ValueError(f"unexpected newline in dictionary entry {text!r}")
            handle.write(text + "\n")
    return len(chars)


def export(model_dir: Path, target: Path, opset: int) -> None:
    import paddle2onnx

    model_file = model_dir / "inference.json"
    if not model_file.is_file():
        model_file = model_dir / "inference.pdmodel"
    params_file = model_dir / "inference.pdiparams"
    if not model_file.is_file() or not params_file.is_file():
        raise FileNotFoundError(f"no Paddle inference model in {model_dir}")
    paddle2onnx.export(
        model_filename=str(model_file),
        params_filename=str(params_file),
        save_file=str(target),
        opset_version=opset,
        auto_upgrade_opset=True,
        verbose=False,
        enable_onnx_checker=True,
    )


def add_ctc_argmax(path: Path) -> bool:
    """Append ArgMax/ReduceMax outputs to the recognizer graph.

    The recognizer emits one probability per character class (18710 of them) for
    every timestep, so a batch of wide crops produces hundreds of MB that would
    otherwise be copied from the GPU and scanned on the CPU.  CTC decoding only
    needs the best class and its probability, so the worker reads these two small
    outputs instead when they exist.  The original output is kept.
    """
    import onnx
    from onnx import TensorProto, helper

    model = onnx.load(str(path))
    graph = model.graph
    names = {o.name for o in graph.output}
    if "ctc_argmax" in names and "ctc_maxprob" in names:
        return False
    probs = graph.output[0].name
    opset = next(o.version for o in model.opset_import if o.domain in ("", "ai.onnx"))
    graph.node.append(helper.make_node("ArgMax", [probs], ["ctc_argmax"], axis=-1, keepdims=0))
    if opset >= 18:
        axes = helper.make_tensor("ctc_axes", TensorProto.INT64, [1], [-1])
        graph.initializer.append(axes)
        graph.node.append(helper.make_node("ReduceMax", [probs, "ctc_axes"], ["ctc_maxprob"], keepdims=0))
    else:
        graph.node.append(helper.make_node("ReduceMax", [probs], ["ctc_maxprob"], axes=[-1], keepdims=0))
    graph.output.append(helper.make_tensor_value_info("ctc_argmax", TensorProto.INT64, ["N", "T"]))
    graph.output.append(helper.make_tensor_value_info("ctc_maxprob", TensorProto.FLOAT, ["N", "T"]))
    onnx.checker.check_model(model)
    onnx.save(model, str(path))
    return True


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--tier", choices=("medium", "small", "tiny"), default="medium")
    parser.add_argument(
        "--augment-only",
        action="store_true",
        help="skip the Paddle export and only add the CTC argmax outputs to an existing "
        "<output>/PP-OCRv6_<tier>_rec.onnx (needs only the onnx package)",
    )
    parser.add_argument(
        "--source",
        type=Path,
        default=default_models_dir(),
        help="folder that contains PP-OCRv6_<tier>_det and PP-OCRv6_<tier>_rec",
    )
    parser.add_argument(
        "--output",
        type=Path,
        default=Path(__file__).resolve().parent.parent / "models",
        help="destination folder (default: <project>/models)",
    )
    parser.add_argument("--opset", type=int, default=17)
    args = parser.parse_args()

    name = f"PP-OCRv6_{args.tier}"
    if args.augment_only:
        rec_path = args.output / f"{name}_rec.onnx"
        if not rec_path.is_file():
            print(f"{rec_path} does not exist", file=sys.stderr)
            return 2
        changed = add_ctc_argmax(rec_path)
        print(f"{rec_path.name}: {'added' if changed else 'already has'} ctc_argmax/ctc_maxprob outputs")
        return 0

    det_dir = args.source / f"{name}_det"
    rec_dir = args.source / f"{name}_rec"
    for directory in (det_dir, rec_dir):
        if not directory.is_dir():
            print(
                f"{directory} does not exist. Download the models first with\n"
                f"  python PaddleOCR\\PaddleOCR_worker.py --model-tier {args.tier} "
                "--device cpu --download-only",
                file=sys.stderr,
            )
            return 2

    args.output.mkdir(parents=True, exist_ok=True)
    count = write_dictionary(rec_dir, args.output / f"{name}_dict.txt")
    print(f"wrote {name}_dict.txt ({count} characters)")
    for kind, directory in (("det", det_dir), ("rec", rec_dir)):
        target = args.output / f"{name}_{kind}.onnx"
        export(directory, target, args.opset)
        print(f"wrote {target.name} ({target.stat().st_size >> 20} MB)")
    add_ctc_argmax(args.output / f"{name}_rec.onnx")
    print(f"{name} is ready for OcrWorker\\dist\\AnywhereOcrWorker.exe --model-tier {args.tier}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
