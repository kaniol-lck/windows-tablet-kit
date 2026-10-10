@echo off
rem TouchRedirect - uninstall [will ask for UAC elevation]
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall.ps1"
echo.
pause
