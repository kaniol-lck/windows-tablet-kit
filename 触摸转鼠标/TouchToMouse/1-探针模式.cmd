@echo off
cd /d "%~dp0"
echo == TouchToMouse: PROBE mode ==
echo Nothing is changed. It only records what touch input Windows generates.
echo.
echo Tap several times inside the game / app you want to test,
echo then come back to this window and press Ctrl+C to see the summary.
echo.
TouchToMouse.exe
echo.
pause
