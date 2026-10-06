@echo off
rem TouchRedirect - install [will ask for UAC elevation]
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
echo.
pause
