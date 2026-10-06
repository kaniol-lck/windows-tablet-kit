@echo off
cd /d "%~dp0"
echo == TouchToMouse: SELF TEST ==
echo A small test window pops up for a few seconds.
echo Your mouse cursor is moved onto it and a few clicks are sent to that window.
echo.
echo PASS means: every touch-like click the hook saw was delivered to the window
echo exactly once (0 would mean the re-injection is lost, 2 would mean double clicks).
echo If the system drops injected events, the test retries by itself (up to 10 tries).
echo.
TouchToMouse.exe -selftest
echo ExitCode=%ERRORLEVEL%
echo.
pause
