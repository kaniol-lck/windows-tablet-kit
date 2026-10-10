@echo off
rem Convert mode: rewrite touch-derived clicks into clean mouse clicks.
rem Whitelist comes from the command line, or from games.txt [one process name per line].
setlocal enabledelayedexpansion
cd /d "%~dp0"

set "LIST="

:ARGS
if "%~1"=="" goto READFILE
set "LIST=!LIST! %~1"
shift
goto ARGS

:READFILE
if defined LIST goto RUN
if not exist "%~dp0games.txt" goto NOLIST
for /f "usebackq tokens=* eol=;" %%a in ("%~dp0games.txt") do set "LIST=!LIST! %%a"
if not defined LIST goto NOLIST

:RUN
if /i "!LIST!"==" all" goto ALL
if /i "!LIST!"=="all" goto ALL
echo == CONVERT mode, touch is rewritten only while one of these is in the foreground:!LIST! ==
echo Press Ctrl+C to stop.
echo.
TouchToMouse.exe -convert -only !LIST!
echo.
pause
exit /b

:ALL
echo == CONVERT mode for ALL apps: touch is rewritten everywhere ==
echo Press Ctrl+C to stop.
echo.
TouchToMouse.exe -convert
echo.
pause
exit /b

:NOLIST
echo No process names given and games.txt has no entries.
echo.
echo Usage:   2-convert.cmd stacklands            (one or more names, .exe optional)
echo Or put names into games.txt, one per line.
echo To rewrite touch in every app:  2-convert.cmd all
echo.
pause
exit /b
