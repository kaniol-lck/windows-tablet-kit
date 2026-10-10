@echo off
chcp 65001 >nul
rem Experiment: blank all 17 system cursors for 10 seconds, then restore them.
rem While it runs, move the mouse with the touchpad and watch the pointer.
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0测试-隐藏光标10秒.ps1"
pause
