@echo off
rem Repair the mouse pointer appearance after a tool replaced the system cursors.
rem Windows applies the accessibility pointer size and color when it loads the cursor scheme,
rem so the fix is to make Windows reload the scheme: open the dialog below and press Apply or OK.
echo Opening the mouse pointer settings. Press "Apply" or "OK" there - that reloads the
echo cursor scheme with your size and color settings.
echo.
echo Also opening the accessibility pointer page as a fallback. Moving the size slider
echo one step and back also forces a reload.
echo.
start "" rundll32.exe shell32.dll,Control_RunDLL main.cpl,,1
timeout /t 1 /nobreak >nul
start "" ms-settings:easeofaccess-cursor
echo.
pause
