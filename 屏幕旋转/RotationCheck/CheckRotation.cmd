@echo off
chcp 65001 >nul
echo ================================================================
echo   System auto-rotation monitor  (built-in Windows rotation)
echo.
echo   Detach the detachable keyboard and watch ConvertibleSlateMode
echo   change from 0 (laptop/clamshell) to 1 (slate/tablet).
echo   In slate mode Windows will auto-rotate the screen.
echo.
echo   Press Ctrl+C to quit.
echo ================================================================
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0RotationCheck.ps1"
pause