@echo off
chcp 65001 >nul
rem ---- self-elevate (needed to remove the scheduled task / ProgramData file) ----
net session >nul 2>&1
if errorlevel 1 (
  powershell.exe -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
echo Removing scheduled task "AutoRotate" and stopping running instances...
schtasks /delete /tn "AutoRotate" /f >nul 2>&1
powershell.exe -NoProfile -Command "Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -like '*AutoRotate.ps1*' -and $_.ProcessId -ne $PID } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }"
del /q "C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup\AutoRotate.vbs" 2>nul
del /q "C:\Users\Default\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\AutoRotate.vbs" 2>nul
echo.
echo [OK] Auto-rotate removed (task + all-users startup entry).
echo      Desktop shortcuts for manual rotation still work.
pause
