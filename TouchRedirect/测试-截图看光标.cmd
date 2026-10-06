@echo off
rem Experiment: capture 20 screenshots while you tap the game, to tell a software
rem cursor [drawn into the frame, appears in the capture] from a hardware cursor
rem [NOT included in GDI captures].
rem Open GodSoul first and tap the screen repeatedly while this runs.
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0测试-截图看光标.ps1"
pause
