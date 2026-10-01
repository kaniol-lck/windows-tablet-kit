@echo off
chcp 65001 >nul
rem ---- self-elevate (writing HKLM\SYSTEM and disabling services needs admin) ----
net session >nul 2>&1
if errorlevel 1 (
  powershell.exe -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
echo ================================================================
echo   Disable Windows Update  (thorough + reversible)
echo ================================================================
echo   1 = Dry run : preview only, changes nothing
echo   2 = Disable : policies + services + scheduled tasks
echo   3 = Disable + also block update domains via hosts file
echo   0 = Cancel
echo.
set /p MODE="Choose 1 / 2 / 3 / 0 : "
if "%MODE%"=="0" exit /b
if "%MODE%"=="1" powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0DisableWindowsUpdate.ps1" -DryRun -BlockNetwork
if "%MODE%"=="2" powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0DisableWindowsUpdate.ps1"
if "%MODE%"=="3" powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0DisableWindowsUpdate.ps1" -BlockNetwork
echo.
pause
