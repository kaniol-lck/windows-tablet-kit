@echo off
chcp 65001 >nul
echo ============================================
echo   Redirect touch-keyboard entry (needs admin)
echo ============================================
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath powershell.exe -Verb RunAs -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File','%~dp0Install-OskRedirect.ps1'"
echo.
echo A UAC prompt should have appeared; the elevated window does the work.
echo To use a keyboard exe in another location, run instead:
echo   powershell -ExecutionPolicy Bypass -File "%~dp0Install-OskRedirect.ps1" -KeyboardPath "D:\path\TouchKeyboard.exe"
echo.
pause
