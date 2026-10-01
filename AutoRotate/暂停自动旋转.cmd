@echo off
chcp 65001 >nul
echo paused > "%~dp0pause.flag"
echo [OK] Auto-rotate paused (manual rotation still works).
echo      To resume, run the resume .cmd in this folder.
timeout /t 3 >nul