@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0AutoRotate.ps1" -Set 2
timeout /t 2 >nul
