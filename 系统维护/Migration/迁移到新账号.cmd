@echo off
chcp 65001 >nul
rem ---- self-elevate: reading another user's profile needs the %USERNAME%s token ----
net session >nul 2>&1
if errorlevel 1 (
  powershell.exe -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
echo ==========================================================
echo   Migrate personal files to the new account (%USERNAME%)
echo   Source: %USERPROFILE%
echo   Target: auto-detected new user profile
echo ==========================================================
echo.
set /p MODE="1=Personal files only   2=+App data (games/browser/Rime)   3=Dry run   0=Cancel : "
if "%MODE%"=="0" exit /b
if "%MODE%"=="1" powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Migrate.ps1" -PersonalOnly
if "%MODE%"=="2" powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Migrate.ps1"
if "%MODE%"=="3" powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Migrate.ps1" -DryRun
echo.
echo Done. Log files are in this folder.
pause
