@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0AutoRotate.ps1" -Once
echo.
pause
