@echo off
chcp 65001 >nul
echo ============================================
echo   Check system On-Screen Keyboard (osk.exe)
echo ============================================
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0CheckOsk.ps1"
echo.
pause
