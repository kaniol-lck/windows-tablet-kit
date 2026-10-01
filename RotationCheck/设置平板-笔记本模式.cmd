@echo off
chcp 65001 >nul
rem ---- self-elevate: writing HKLM\SYSTEM requires admin ----
net session >nul 2>&1
if errorlevel 1 (
  powershell.exe -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
echo ============================================================
echo  Set ConvertibleSlateMode
echo    0 = Slate / Tablet   (physical keyboard absent, auto-rotate ON)
echo    1 = Desktop / Laptop (physical keyboard present, auto-rotate OFF)
echo ============================================================
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0SetSlateMode.ps1"
echo.
pause
