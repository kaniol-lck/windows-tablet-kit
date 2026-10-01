@echo off
chcp 65001 >nul
rem ---- self-elevate (creating an elevated scheduled task needs admin) ----
net session >nul 2>&1
if errorlevel 1 (
  powershell.exe -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
echo Installing scheduled task "AutoRotate" (run at logon, elevated)...
schtasks /create /tn "AutoRotate" /tr "powershell.exe -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File %~dp0AutoRotate.ps1" /sc onlogon /rl highest /f
if errorlevel 1 (
  echo.
  echo [FAILED] Could not create the task. Run this file as %USERNAME%.
  pause
  exit /b 1
)
schtasks /run /tn "AutoRotate"
echo.
echo [OK] Task installed and started. Auto-rotation is on.
echo      To remove it later, run the uninstall .cmd in this folder.
pause