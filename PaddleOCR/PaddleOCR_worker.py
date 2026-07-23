"""Persistent localhost bridge between AutoHotkey v1 and PaddleOCR."""

import argparse
import json
import os
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any, Dict, List

from paddleocr import PaddleOCR


def result_to_dict(result: Any) -> Dict[str, Any]:
    """Normalize PaddleOCR/PaddleX result objects across 3.x releases."""
    value = getattr(result, "json", result)
    if callable(value):
        value = value()
    if isinstance(value, str):
        value = json.loads(value)
    if isinstance(value, dict) and isinstance(value.get("res"), dict):
        value = value["res"]
    if not isinstance(value, dict):
        value = dict(value)
    return value


class OCRHandler(BaseHTTPRequestHandler):
    server_version = "AnywhereOCR/1.0"

    def send_text(self, status: int, value: str, content_type: str) -> None:
        payload = value.encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", f"{content_type}; charset=utf-8")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def do_GET(self) -> None:
        if self.path == "/health":
            self.send_text(200, self.server.model_name, "text/plain")
        else:
            self.send_text(404, "Not found", "text/plain")

    def do_POST(self) -> None:
        if not self.path.startswith("/ocr"):
            self.send_text(404, "Not found", "text/plain")
            return

        image_path = self.rfile.read(
            int(self.headers.get("Content-Length", "0"))
        ).decode("utf-8")
        try:
            path = Path(image_path)
            if not path.is_file():
                raise FileNotFoundError(image_path)

            results: List[Dict[str, Any]] = [
                result_to_dict(item)
                for item in self.server.ocr.predict(str(path))
            ]
            if self.path.endswith("?all=1"):
                response = json.dumps(results, ensure_ascii=False)
                self.send_text(200, response, "application/json")
            else:
                lines = [
                    text
                    for result in results
                    for text in result.get("rec_texts", [])
                    if text
                ]
                self.send_text(200, "\n".join(lines), "text/plain")
        except Exception as error:
            self.send_text(500, f"{type(error).__name__}: {error}", "text/plain")
        finally:
            try:
                os.remove(image_path)
            except OSError:
                pass

    def log_message(self, _format: str, *args: Any) -> None:
        pass


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=48626)
    parser.add_argument("--device", default="cpu")
    parser.add_argument(
        "--model-tier",
        choices=("medium", "small", "tiny"),
        default="medium",
        help="PP-OCRv6 tier; PaddleOCR calls its mini model 'tiny'",
    )
    parser.add_argument(
        "--download-only",
        action="store_true",
        help="download and initialize the selected model pair, then exit",
    )
    args = parser.parse_args()

    model_name = f"PP-OCRv6_{args.model_tier}"
    ocr = PaddleOCR(
        text_detection_model_name=f"{model_name}_det",
        text_recognition_model_name=f"{model_name}_rec",
        use_doc_orientation_classify=False,
        use_doc_unwarping=False,
        use_textline_orientation=False,
        device=args.device,
    )
    if args.download_only:
        print(f"{model_name}_det and {model_name}_rec are ready.")
        return

    server = ThreadingHTTPServer(("127.0.0.1", args.port), OCRHandler)
    server.ocr = ocr
    server.model_name = model_name
    server.serve_forever()


if __name__ == "__main__":
    main()
