@echo off
chcp 65001 >nul
net session >nul 2>&1
if errorlevel 1 (
  powershell.exe -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
echo ==========================================================
echo   Remove TouchKeyboard autostart
echo ==========================================================
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0SetAutostart.ps1" -Uninstall
echo.
pause
