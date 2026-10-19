@echo off
cd /d "%~dp0.."

start "" anywhere-ocr.ahk

OcrWorker\dist\AnywhereOcrWorker.exe --device gpu:0

taskkill /f /im AnywhereOcrWorker.exe >nul 2>&1