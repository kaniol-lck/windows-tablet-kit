@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0AutoRotate.ps1" -SelfTest
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0AutoRotate.ps1" -Status
echo.
pause
