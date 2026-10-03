@echo off
chcp 65001 >nul
rem ---- self-elevate (Register-ScheduledTask needs admin) ----
net session >nul 2>&1
if errorlevel 1 (
  powershell.exe -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
echo Installing / repairing AutoRotate autostart (logon task + startup folder fallback)...
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-AutoStart.ps1"
echo.
echo If "daemon started" above is not confirmed, read the log lines printed above.
pause
