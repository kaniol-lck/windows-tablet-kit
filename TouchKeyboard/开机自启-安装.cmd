@echo off
chcp 65001 >nul
rem ---- self-elevate: writing the all-users Startup folder needs admin ----
net session >nul 2>&1
if errorlevel 1 (
  powershell.exe -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
echo ==========================================================
echo   Install TouchKeyboard autostart (all users + this user)
echo ==========================================================
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0SetAutostart.ps1" -Install
echo.
pause
