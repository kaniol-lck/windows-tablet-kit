@echo off
chcp 65001 >nul
echo ============================================
echo   Undo touch-keyboard redirect (needs admin)
echo ============================================
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath powershell.exe -Verb RunAs -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File','%~dp0Uninstall-OskRedirect.ps1'"
echo.
echo A UAC prompt should have appeared; the elevated window does the work.
echo.
pause
