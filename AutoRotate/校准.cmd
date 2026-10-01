@echo off
chcp 65001 >nul
echo ================================================
echo  AutoRotate - calibrate 4 poses
echo  (hold the device still for 3 seconds each time)
echo ================================================
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0AutoRotate.ps1" -Calibrate
echo.
pause
