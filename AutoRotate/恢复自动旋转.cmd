@echo off
chcp 65001 >nul
del /q "%~dp0pause.flag" 2>nul
echo [OK] Auto-rotate resumed.
timeout /t 3 >nul
