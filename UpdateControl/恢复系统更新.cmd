@echo off
chcp 65001 >nul
net session >nul 2>&1
if errorlevel 1 (
  powershell.exe -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
echo ================================================================
echo   Restore Windows Update settings from the latest backup
echo ================================================================
echo   1 = Dry run : preview only
echo   2 = Restore : import backup (policies / services / tasks / hosts)
echo   0 = Cancel
echo.
set /p MODE="Choose 1 / 2 / 0 : "
if "%MODE%"=="0" exit /b
if "%MODE%"=="1" powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0RestoreWindowsUpdate.ps1" -DryRun
if "%MODE%"=="2" powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0RestoreWindowsUpdate.ps1"
echo.
pause
