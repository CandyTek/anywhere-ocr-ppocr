"""Windows frontend for Anywhere OCR.

Hold either Windows key and drag with the left mouse button.  The selected
screen rectangle is sent to the persistent PP-OCRv6 worker in this repository;
the recognized text is then displayed and copied to the clipboard.

This file intentionally uses only the Python standard library.  PaddleOCR and
PaddlePaddle are needed by ``PaddleOCR/PaddleOCR_worker.py``.
"""

from __future__ import annotations

import ctypes
import os
import queue
import signal
import struct
import subprocess
import sys
import tempfile
import threading
import time
import tkinter as tk
import urllib.error
import urllib.parse
import urllib.request
import webbrowser
from ctypes import wintypes
from pathlib import Path
from tkinter import messagebox, ttk
from typing import Callable

APP_DIR = Path(__file__).resolve().parent
WORKER = APP_DIR / "PaddleOCR" / "PaddleOCR_worker.py"
# The native worker (C# + ONNX Runtime, see OcrWorker/) is preferred when it has
# been built; it needs a few hundred MB instead of several GB of RAM.
NATIVE_WORKER = Path(
    os.environ.get("ANYWHERE_OCR_WORKER")
    or APP_DIR / "OcrWorker" / "dist" / "AnywhereOcrWorker.exe"
)
HOST = "127.0.0.1"
PORT = 48626
MODEL_NAME = "PP-OCRv6_medium"

VK_LWIN = 0x5B
VK_RWIN = 0x5C
VK_F3 = 0x72
WH_KEYBOARD_LL = 13
WH_MOUSE_LL = 14
WM_KEYDOWN = 0x0100
WM_KEYUP = 0x0101
WM_SYSKEYDOWN = 0x0104
WM_SYSKEYUP = 0x0105
WM_MOUSEMOVE = 0x0200
WM_LBUTTONDOWN = 0x0201
WM_LBUTTONUP = 0x0202
WM_APP = 0x8000
WM_HOOK_STOP = WM_APP + 1
PM_NOREMOVE = 0x0000
SRCCOPY = 0x00CC0020
CAPTUREBLT = 0x40000000
DIB_RGB_COLORS = 0

user32 = ctypes.windll.user32
gdi32 = ctypes.windll.gdi32
kernel32 = ctypes.windll.kernel32

user32.GetDC.argtypes = (wintypes.HWND,)
user32.GetDC.restype = wintypes.HDC
user32.ReleaseDC.argtypes = (wintypes.HWND, wintypes.HDC)
user32.ReleaseDC.restype = ctypes.c_int
gdi32.CreateCompatibleDC.argtypes = (wintypes.HDC,)
gdi32.CreateCompatibleDC.restype = wintypes.HDC
gdi32.CreateCompatibleBitmap.argtypes = (wintypes.HDC, ctypes.c_int, ctypes.c_int)
gdi32.CreateCompatibleBitmap.restype = wintypes.HBITMAP
gdi32.SelectObject.argtypes = (wintypes.HDC, wintypes.HGDIOBJ)
gdi32.SelectObject.restype = wintypes.HGDIOBJ
gdi32.DeleteObject.argtypes = (wintypes.HGDIOBJ,)
gdi32.DeleteObject.restype = wintypes.BOOL
gdi32.DeleteDC.argtypes = (wintypes.HDC,)
gdi32.DeleteDC.restype = wintypes.BOOL
gdi32.BitBlt.argtypes = (
    wintypes.HDC,
    ctypes.c_int,
    ctypes.c_int,
    ctypes.c_int,
    ctypes.c_int,
    wintypes.HDC,
    ctypes.c_int,
    ctypes.c_int,
    wintypes.DWORD,
)
gdi32.BitBlt.restype = wintypes.BOOL
gdi32.GetDIBits.argtypes = (
    wintypes.HDC,
    wintypes.HBITMAP,
    wintypes.UINT,
    wintypes.UINT,
    wintypes.LPVOID,
    wintypes.LPVOID,
    wintypes.UINT,
)
gdi32.GetDIBits.restype = ctypes.c_int


class POINT(ctypes.Structure):
    _fields_ = (("x", wintypes.LONG), ("y", wintypes.LONG))


class MSG(ctypes.Structure):
    _fields_ = (
        ("hwnd", wintypes.HWND),
        ("message", wintypes.UINT),
        ("wParam", wintypes.WPARAM),
        ("lParam", wintypes.LPARAM),
        ("time", wintypes.DWORD),
        ("pt", POINT),
        ("lPrivate", wintypes.DWORD),
    )


class MSLLHOOKSTRUCT(ctypes.Structure):
    _fields_ = (
        ("pt", POINT),
        ("mouseData", wintypes.DWORD),
        ("flags", wintypes.DWORD),
        ("time", wintypes.DWORD),
        ("dwExtraInfo", wintypes.WPARAM),
    )


class KBDLLHOOKSTRUCT(ctypes.Structure):
    _fields_ = (
        ("vkCode", wintypes.DWORD),
        ("scanCode", wintypes.DWORD),
        ("flags", wintypes.DWORD),
        ("time", wintypes.DWORD),
        ("dwExtraInfo", wintypes.WPARAM),
    )


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = (
        ("biSize", wintypes.DWORD),
        ("biWidth", wintypes.LONG),
        ("biHeight", wintypes.LONG),
        ("biPlanes", wintypes.WORD),
        ("biBitCount", wintypes.WORD),
        ("biCompression", wintypes.DWORD),
        ("biSizeImage", wintypes.DWORD),
        ("biXPelsPerMeter", wintypes.LONG),
        ("biYPelsPerMeter", wintypes.LONG),
        ("biClrUsed", wintypes.DWORD),
        ("biClrImportant", wintypes.DWORD),
    )


class BITMAPINFO(ctypes.Structure):
    _fields_ = (("bmiHeader", BITMAPINFOHEADER), ("bmiColors", wintypes.DWORD * 3))


HOOKPROC = ctypes.WINFUNCTYPE(
    ctypes.c_ssize_t, ctypes.c_int, wintypes.WPARAM, wintypes.LPARAM
)
kernel32.GetModuleHandleW.argtypes = (wintypes.LPCWSTR,)
kernel32.GetModuleHandleW.restype = wintypes.HMODULE
user32.SetWindowsHookExW.argtypes = (
    ctypes.c_int,
    HOOKPROC,
    wintypes.HINSTANCE,
    wintypes.DWORD,
)
user32.SetWindowsHookExW.restype = wintypes.HANDLE
user32.UnhookWindowsHookEx.argtypes = (wintypes.HANDLE,)
user32.UnhookWindowsHookEx.restype = wintypes.BOOL
user32.CallNextHookEx.argtypes = (
    wintypes.HANDLE,
    ctypes.c_int,
    wintypes.WPARAM,
    wintypes.LPARAM,
)
user32.CallNextHookEx.restype = ctypes.c_ssize_t
user32.GetMessageW.argtypes = (
    ctypes.POINTER(MSG),
    wintypes.HWND,
    wintypes.UINT,
    wintypes.UINT,
)
user32.GetMessageW.restype = wintypes.BOOL
user32.TranslateMessage.argtypes = (ctypes.POINTER(MSG),)
user32.TranslateMessage.restype = wintypes.BOOL
user32.DispatchMessageW.argtypes = (ctypes.POINTER(MSG),)
user32.DispatchMessageW.restype = ctypes.c_ssize_t
user32.PeekMessageW.argtypes = (
    ctypes.POINTER(MSG),
    wintypes.HWND,
    wintypes.UINT,
    wintypes.UINT,
    wintypes.UINT,
)
user32.PeekMessageW.restype = wintypes.BOOL
user32.PostThreadMessageW.argtypes = (
    wintypes.DWORD,
    wintypes.UINT,
    wintypes.WPARAM,
    wintypes.LPARAM,
)
user32.PostThreadMessageW.restype = wintypes.BOOL
user32.GetAsyncKeyState.argtypes = (ctypes.c_int,)
user32.GetAsyncKeyState.restype = ctypes.c_short
kernel32.GetCurrentThreadId.argtypes = ()
kernel32.GetCurrentThreadId.restype = wintypes.DWORD


def set_dpi_awareness() -> None:
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)
    except (AttributeError, OSError):
        try:
            user32.SetProcessDPIAware()
        except (AttributeError, OSError):
            pass


def cursor_position() -> tuple[int, int]:
    point = POINT()
    if not user32.GetCursorPos(ctypes.byref(point)):
        raise ctypes.WinError()
    return point.x, point.y


def capture_bmp(rect: tuple[int, int, int, int], destination: Path) -> None:
    x, y, width, height = rect
    screen_dc = user32.GetDC(None)
    memory_dc = gdi32.CreateCompatibleDC(screen_dc)
    bitmap = gdi32.CreateCompatibleBitmap(screen_dc, width, height)
    old_bitmap = gdi32.SelectObject(memory_dc, bitmap)
    try:
        if not gdi32.BitBlt(
            memory_dc, 0, 0, width, height, screen_dc, x, y, SRCCOPY | CAPTUREBLT
        ):
            raise ctypes.WinError()

        stride = ((width * 3 + 3) // 4) * 4
        pixel_size = stride * height
        info = BITMAPINFO()
        info.bmiHeader = BITMAPINFOHEADER(
            ctypes.sizeof(BITMAPINFOHEADER),
            width,
            height,
            1,
            24,
            0,
            pixel_size,
            0,
            0,
            0,
            0,
        )
        pixels = ctypes.create_string_buffer(pixel_size)
        lines = gdi32.GetDIBits(
            memory_dc,
            bitmap,
            0,
            height,
            pixels,
            ctypes.byref(info),
            DIB_RGB_COLORS,
        )
        if lines != height:
            raise ctypes.WinError()

        file_header_size = 14
        pixel_offset = file_header_size + ctypes.sizeof(BITMAPINFOHEADER)
        file_header = struct.pack(
            "<2sIHHI", b"BM", pixel_offset + pixel_size, 0, 0, pixel_offset
        )
        with destination.open("wb") as output:
            output.write(file_header)
            output.write(bytes(info.bmiHeader))
            output.write(pixels.raw)
    finally:
        gdi32.SelectObject(memory_dc, old_bitmap)
        gdi32.DeleteObject(bitmap)
        gdi32.DeleteDC(memory_dc)
        user32.ReleaseDC(None, screen_dc)


def request(url: str, data: bytes | None = None, timeout: float = 2.0) -> str:
    req = urllib.request.Request(
        url,
        data=data,
        headers={"Content-Type": "text/plain; charset=utf-8"},
        method="POST" if data is not None else "GET",
    )
    with urllib.request.urlopen(req, timeout=timeout) as response:
        return response.read().decode("utf-8")


class WorkerClient:
    def __init__(self) -> None:
        self.process: subprocess.Popen[bytes] | None = None

    @property
    def base_url(self) -> str:
        return f"http://{HOST}:{PORT}"

    def healthy(self) -> bool:
        try:
            return MODEL_NAME in request(f"{self.base_url}/health", timeout=0.4)
        except (OSError, urllib.error.URLError):
            return False

    @staticmethod
    def python_executable() -> str:
        configured = os.environ.get("ANYWHERE_OCR_PYTHON")
        if configured:
            return configured
        default = Path(r"C:\ProgramData\miniconda3\envs\ult\python.exe")
        return str(default) if default.is_file() else sys.executable

    def start(self, progress: Callable[[str], None]) -> None:
        if self.healthy():
            return
        use_native = (
            os.environ.get("ANYWHERE_OCR_BACKEND", "").lower() != "python"
            and NATIVE_WORKER.is_file()
        )
        if not use_native and not WORKER.is_file():
            raise FileNotFoundError(f"OCR worker not found: {WORKER}")

        use_gpu = os.environ.get("ANYWHERE_OCR_USE_GPU")
        if use_gpu is None:
            use_gpu = "1" if use_native or Path(
                r"C:\ProgramData\miniconda3\envs\ult\python.exe"
            ).is_file() else "0"
        gpu_id = os.environ.get("ANYWHERE_OCR_GPU_ID", "0")
        device = f"gpu:{gpu_id}" if use_gpu not in ("", "0", "false", "False") else "cpu"
        if use_native:
            launcher = [str(NATIVE_WORKER)]
            workdir = NATIVE_WORKER.parent
        else:
            launcher = [self.python_executable(), str(WORKER)]
            workdir = WORKER.parent
        command = launcher + [
            "--port",
            str(PORT),
            "--device",
            device,
            "--model-tier",
            "medium",
        ]
        startup = subprocess.STARTUPINFO()
        startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW
        self.process = subprocess.Popen(
            command,
            cwd=workdir,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            startupinfo=startup,
            creationflags=subprocess.CREATE_NO_WINDOW,
        )

        progress("正在初始化 PP-OCRv6_medium…" if is_chinese_ui() else
                 "Initializing PP-OCRv6_medium…")
        deadline = time.monotonic() + 150
        while time.monotonic() < deadline:
            if self.healthy():
                return
            if self.process.poll() is not None:
                raise RuntimeError(
                    "The OCR worker exited during startup. Run it in a terminal "
                    "to see the detailed PaddleOCR error."
                )
            time.sleep(0.25)
        raise TimeoutError("Timed out while initializing PP-OCRv6_medium.")

    def recognize(self, image_path: Path) -> str:
        try:
            return request(
                f"{self.base_url}/ocr",
                str(image_path).encode("utf-8"),
                timeout=300,
            )
        except urllib.error.HTTPError as error:
            details = error.read().decode("utf-8", errors="replace")
            raise RuntimeError(details) from error

    def close(self) -> None:
        if self.process is not None and self.process.poll() is None:
            self.process.terminate()
            try:
                self.process.wait(timeout=3)
            except subprocess.TimeoutExpired:
                self.process.kill()


def is_chinese_ui() -> bool:
    return kernel32.GetUserDefaultUILanguage() == 0x0804


class InputBlocker:
    """Capture Win+left-drag using dedicated low-level hook message thread."""

    def __init__(self, app: "AnywhereOCR") -> None:
        self.app = app
        self.keyboard_hook: int | None = None
        self.mouse_hook: int | None = None
        self.win_pressed = False
        self.selection_active = False
        self.f3_pressed = False

        # Keep ctypes callback objects alive for the entire hook lifetime.
        self._keyboard_callback = HOOKPROC(self._keyboard_handler)
        self._mouse_callback = HOOKPROC(self._mouse_handler)

        self._thread: threading.Thread | None = None
        self._thread_id = 0
        self._ready = threading.Event()
        self._stopped = threading.Event()
        self._startup_error: BaseException | None = None

    def start(self) -> None:
        if self._thread is not None and self._thread.is_alive():
            return

        self._ready.clear()
        self._stopped.clear()
        self._startup_error = None
        self._thread = threading.Thread(
            target=self._hook_message_loop,
            name="AnywhereOCR-Hooks",
            daemon=True,
        )
        self._thread.start()

        if not self._ready.wait(timeout=5):
            raise RuntimeError("键鼠 Hook 启动超时。")
        if self._startup_error is not None:
            raise RuntimeError(f"键鼠 Hook 注册失败：{self._startup_error}")

        print("Hook 注册成功！请按住 Win 键并拖动鼠标左键测试。")

    def _hook_message_loop(self) -> None:
        self._thread_id = kernel32.GetCurrentThreadId()
        msg = MSG()

        # Force creation of this thread's message queue before start() returns.
        user32.PeekMessageW(
            ctypes.byref(msg), None, 0, 0, PM_NOREMOVE
        )

        try:
            module = kernel32.GetModuleHandleW(None)
            self.keyboard_hook = user32.SetWindowsHookExW(
                WH_KEYBOARD_LL, self._keyboard_callback, module, 0
            )
            if not self.keyboard_hook:
                raise ctypes.WinError()

            self.mouse_hook = user32.SetWindowsHookExW(
                WH_MOUSE_LL, self._mouse_callback, module, 0
            )
            if not self.mouse_hook:
                raise ctypes.WinError()

            self._ready.set()

            while True:
                result = user32.GetMessageW(
                    ctypes.byref(msg), None, 0, 0
                )
                if result == 0:
                    break
                if result == -1:
                    raise ctypes.WinError()
                if msg.message == WM_HOOK_STOP:
                    break
                user32.TranslateMessage(ctypes.byref(msg))
                user32.DispatchMessageW(ctypes.byref(msg))
        except BaseException as error:
            self._startup_error = error
            self._ready.set()
            if not isinstance(error, SystemExit):
                print(f"Hook 线程错误：{error}", file=sys.stderr)
        finally:
            self._unhook()
            self._thread_id = 0
            self._stopped.set()

    def _unhook(self) -> None:
        if self.mouse_hook:
            user32.UnhookWindowsHookEx(self.mouse_hook)
            self.mouse_hook = None
        if self.keyboard_hook:
            user32.UnhookWindowsHookEx(self.keyboard_hook)
            self.keyboard_hook = None
        self.win_pressed = False
        self.selection_active = False
        self.f3_pressed = False

    def stop(self) -> None:
        thread = self._thread
        if thread is None:
            self._unhook()
            return

        if thread.is_alive() and self._thread_id:
            user32.PostThreadMessageW(
                self._thread_id, WM_HOOK_STOP, 0, 0
            )
            if threading.current_thread() is not thread:
                self._stopped.wait(timeout=2)

        self._thread = None
        self._unhook()

    def _keyboard_handler(self, code: int, message: int, data: int) -> int:
        try:
            if code >= 0:
                event = ctypes.cast(
                    data, ctypes.POINTER(KBDLLHOOKSTRUCT)
                ).contents

                if event.vkCode in (VK_LWIN, VK_RWIN):
                    if message in (WM_KEYDOWN, WM_SYSKEYDOWN):
                        self.win_pressed = True
                    elif message in (WM_KEYUP, WM_SYSKEYUP):
                        self.win_pressed = False

                elif event.vkCode == VK_F3:
                    if message in (WM_KEYDOWN, WM_SYSKEYDOWN):
                        if not self.f3_pressed:
                            self.f3_pressed = True
                            self.app.post_event(self.app.show)
                        return 1
                    if message in (WM_KEYUP, WM_SYSKEYUP):
                        self.f3_pressed = False
                        return 1
        except Exception as error:
            print(f"键盘 Hook 回调错误：{error}", file=sys.stderr)

        return user32.CallNextHookEx(
            self.keyboard_hook, code, message, data
        )

    def _is_win_down(self) -> bool:
        return bool(
            (user32.GetAsyncKeyState(VK_LWIN) & 0x8000)
            or (user32.GetAsyncKeyState(VK_RWIN) & 0x8000)
        )

    def _mouse_handler(self, code: int, message: int, data: int) -> int:
        try:
            if code >= 0:
                event = ctypes.cast(
                    data, ctypes.POINTER(MSLLHOOKSTRUCT)
                ).contents
                x, y = int(event.pt.x), int(event.pt.y)
                win_down = self.win_pressed or self._is_win_down()

                if (
                    message == WM_LBUTTONDOWN
                    and win_down
                    and not self.selection_active
                    and not self.app.busy
                    and self.app.selection is None
                ):
                    self.selection_active = True
                    self.app.post_event(
                        self.app.begin_selection, x, y
                    )
                    # Suppress the click so the underlying application
                    # does not receive the beginning of a drag.
                    return 1

                if self.selection_active:
                    if message == WM_MOUSEMOVE:
                        self.app.post_drag_update(x, y)
                        # Do NOT suppress movement. Blocking WM_MOUSEMOVE
                        # can freeze the cursor, making dragging appear broken.
                        return user32.CallNextHookEx(
                            self.mouse_hook, code, message, data
                        )

                    if message == WM_LBUTTONUP:
                        self.selection_active = False
                        self.app.post_event(
                            self.app.finish_selection, x, y
                        )
                        # Match the suppressed button-down.
                        return 1
        except Exception as error:
            print(f"鼠标 Hook 回调错误：{error}", file=sys.stderr)

        return user32.CallNextHookEx(
            self.mouse_hook, code, message, data
        )


class AnywhereOCR:
    def __init__(self) -> None:
        self.zh = is_chinese_ui()
        self.root = tk.Tk()
        self.root.title("随处识别" if self.zh else "Anywhere OCR")
        self.root.geometry("500x285")
        self.root.minsize(420, 230)
        self.root.protocol("WM_DELETE_WINDOW", self.hide)

        self.msg_queue: queue.Queue[tuple[Callable[..., None], tuple[object, ...]]] = queue.Queue()
        self.latest_drag_pos: tuple[int, int] | None = None

        self.worker = WorkerClient()
        self.worker_ready = False
        self.worker_error: str | None = None
        self.selection: tk.Toplevel | None = None
        self.selection_start = (0, 0)
        self.busy = False
        self._closing = False

        self.status = tk.StringVar(
            value="正在初始化…" if self.zh else "Initializing…"
        )
        self.engine = tk.StringVar(value="百度翻译" if self.zh else "Baidu Translate")
        self._build_ui()
        self.root.withdraw()

        self._poll_queue()

        self.blocker = InputBlocker(self)
        self.blocker.start()
        threading.Thread(target=self.initialize_worker, daemon=True).start()

    def post_event(self, callback: Callable[..., None], *args: object) -> None:
        self.msg_queue.put((callback, args))

    def post_drag_update(self, x: int, y: int) -> None:
        self.latest_drag_pos = (x, y)

    def _poll_queue(self) -> None:
        # 1. 优先处理常规 UI 事件
        while not self.msg_queue.empty():
            try:
                func, args = self.msg_queue.get_nowait()
                func(*args)
            except queue.Empty:
                break
            except Exception as e:
                print(f"Error handling UI event: {e}", file=sys.stderr)

        # 2. 实时刷新选区拖拽位置
        if self.latest_drag_pos is not None:
            x, y = self.latest_drag_pos
            self.latest_drag_pos = None
            self.update_selection(x, y)

        self.root.after(10, self._poll_queue)

    def ui_call(self, callback: Callable[..., None], *args: object) -> None:
        self.post_event(callback, *args)

    def begin_selection(self, x: int | None = None, y: int | None = None) -> None:
        self.selection_start = cursor_position() if x is None or y is None else (x, y)
        if self.selection is not None:
            try:
                self.selection.destroy()
            except Exception:
                pass
        
        self.selection = tk.Toplevel(self.root)
        self.selection.overrideredirect(True)
        self.selection.attributes("-topmost", True)
        self.selection.attributes("-alpha", 0.3)
        self.selection.configure(background="#1677ff")
        
        x0, y0 = self.selection_start
        self.selection.geometry(f"2x2+{x0}+{y0}")
        self.selection.update_idletasks()

    def update_selection(
        self, x: int | None = None, y: int | None = None
    ) -> None:
        if self.selection is None:
            return
        x0, y0 = self.selection_start
        x1, y1 = cursor_position() if x is None or y is None else (x, y)
        left, top = min(x0, x1), min(y0, y1)
        w, h = max(2, abs(x1 - x0)), max(2, abs(y1 - y0))
        self.selection.geometry(f"{w}x{h}+{left}+{top}")

    def finish_selection(
        self, x: int | None = None, y: int | None = None
    ) -> None:
        if self.selection is None:
            return
        x0, y0 = self.selection_start
        x1, y1 = cursor_position() if x is None or y is None else (x, y)
        
        try:
            self.selection.destroy()
        except Exception:
            pass
        self.selection = None
        self.latest_drag_pos = None
        
        rect = (min(x0, x1), min(y0, y1), abs(x1 - x0), abs(y1 - y0))
        if rect[2] < 10 or rect[3] < 10:
            return
        self.busy = True
        self.root.after(80, lambda: self.start_ocr(rect))

    def _build_ui(self) -> None:
        frame = ttk.Frame(self.root, padding=10)
        frame.pack(fill="both", expand=True)

        text_frame = ttk.Frame(frame)
        text_frame.pack(fill="both", expand=True)

        scrollbar = ttk.Scrollbar(text_frame, orient="vertical")
        scrollbar.pack(side="right", fill="y")

        self.text = tk.Text(
            text_frame,
            wrap="word",
            font=("Microsoft YaHei UI", 11),
            yscrollcommand=scrollbar.set,  # 绑定滚动条
        )
        self.text.pack(side="left", fill="both", expand=True)
        scrollbar.config(command=self.text.yview)  # 绑定文本视图

        controls = ttk.Frame(frame)
        controls.pack(fill="x", pady=(8, 0))
        engines = (
            ("百度翻译", "搜狗翻译")
            if self.zh
            else ("Baidu Translate", "Sogou Translate")
        )
        ttk.Combobox(
            controls,
            textvariable=self.engine,
            values=engines,
            state="readonly",
            width=18,
        ).pack(side="left")
        ttk.Button(
            controls,
            text="翻译" if self.zh else "Translate",
            command=self.translate,
        ).pack(side="left", padx=6)
        ttk.Button(
            controls,
            text="退出" if self.zh else "Exit",
            command=self.exit,
        ).pack(side="right")
        ttk.Label(frame, textvariable=self.status).pack(anchor="w", pady=(7, 0))

    def initialize_worker(self) -> None:
        try:
            self.worker.start(lambda value: self.ui_call(self.status.set, value))
            self.worker_ready = True
            ready = (
                "就绪：按住 Win 并拖动鼠标左键"
                if self.zh
                else "Ready: hold Win and drag the left mouse button"
            )
            self.ui_call(self.status.set, ready)
        except Exception as error:
            self.worker_error = str(error)
            self.ui_call(
                self.status.set,
                ("初始化失败" if self.zh else "Initialization failed"),
            )
            self.ui_call(self.show_error, self.worker_error)

    def start_ocr(self, rect: tuple[int, int, int, int]) -> None:
        threading.Thread(target=self.ocr_task, args=(rect,), daemon=True).start()

    def ocr_task(self, rect: tuple[int, int, int, int]) -> None:
        path = Path(tempfile.gettempdir()) / (
            f"anywhere-ocr-{os.getpid()}-{time.monotonic_ns()}.bmp"
        )
        try:
            if not self.worker_ready:
                if self.worker_error:
                    raise RuntimeError(self.worker_error)
                raise RuntimeError("The OCR model is still initializing.")
            capture_bmp(rect, path)
            self.ui_call(self.show_recognizing)
            result = self.worker.recognize(path)
            self.ui_call(self.ocr_complete, result)
        except Exception as error:
            self.ui_call(self.ocr_failed, str(error))
        finally:
            try:
                path.unlink()
            except OSError:
                pass

    def show_recognizing(self) -> None:
        self.show()
        self.set_text(
            "正在处理…\n\n识别中…" if self.zh else "Processing…\n\nRecognizing…"
        )
        self.status.set("识别中…" if self.zh else "Recognizing…")

    def ocr_complete(self, result: str) -> None:
        self.set_text(result)
        self.root.clipboard_clear()
        self.root.clipboard_append(result)
        self.root.update()
        self.status.set(
            "识别完成，内容已复制到剪贴板"
            if self.zh
            else "Recognition complete; copied to the clipboard"
        )
        self.busy = False

    def ocr_failed(self, details: str) -> None:
        self.busy = False
        self.status.set("识别失败" if self.zh else "Recognition failed")
        self.show_error(details)

    def set_text(self, value: str) -> None:
        self.text.delete("1.0", "end")
        self.text.insert("1.0", value)

    def translate(self) -> None:
        value = self.text.get("1.0", "end-1c").strip()
        if not value:
            self.show_error("待翻译文字为空。" if self.zh else
                            "There is no text to translate.")
            return
        encoded = urllib.parse.quote(value, safe="")
        if self.engine.get().startswith(("百度", "Baidu")):
            url = f"https://fanyi.baidu.com/#auto/zh/{encoded}"
        else:
            url = (
                "https://fanyi.sogou.com/text?"
                f"keyword={encoded}&transfrom=auto&transto=zh-CHS&model=general"
            )
        webbrowser.open(url)

    def show(self) -> None:
        self.root.deiconify()
        self.root.lift()
        self.root.attributes("-topmost", True)
        self.root.after(100, lambda: self.root.attributes("-topmost", False))

    def hide(self) -> None:
        self.root.withdraw()

    def show_error(self, details: str) -> None:
        messagebox.showerror(self.root.title(), details, parent=self.root)

    def exit(self) -> None:
        if self._closing:
            return
        self._closing = True

        try:
            self.blocker.stop()
        except Exception as error:
            print(f"停止 Hook 失败：{error}", file=sys.stderr)

        try:
            self.worker.close()
        except Exception as error:
            print(f"停止 OCR worker 失败：{error}", file=sys.stderr)

        try:
            self.root.quit()
        except tk.TclError:
            pass
        try:
            if self.root.winfo_exists():
                self.root.destroy()
        except tk.TclError:
            pass

    def run(self) -> None:
        # Signal handlers should not destroy Tk recursively from inside
        # Tcl/Tk's message dispatch. Queue a normal UI-thread shutdown.
        def sigint_handler(sig, frame):
            print("\n正在退出...")
            self.post_event(self.exit)

        signal.signal(signal.SIGINT, sigint_handler)

        try:
            self.root.mainloop()
        except KeyboardInterrupt:
            self.exit()
        finally:
            self.exit()


def main() -> None:
    if sys.platform != "win32":
        raise SystemExit("Anywhere OCR requires Windows 10 or Windows 11.")
    set_dpi_awareness()
    AnywhereOCR().run()


if __name__ == "__main__":
    main()
