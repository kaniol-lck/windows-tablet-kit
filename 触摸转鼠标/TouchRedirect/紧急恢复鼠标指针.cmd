@echo off
rem Emergency: restore the system mouse cursor if it got stuck hidden.
rem Does both paths: SPI_SETCURSORS + rewrite cursors from the registry scheme.
setlocal
set "EXE=%ProgramFiles%\TouchRedirect\TouchRedirect.exe"
if not exist "%EXE%" (
  echo TouchRedirect is not installed at "%EXE%".
  echo You can also restore manually: Settings - Bluetooth ^& devices - Mouse -
  echo Additional mouse options - Pointers - change the scheme and change it back.
  echo.
  pause
  exit /b 1
)
"%EXE%" -restorecursor
echo Cursor restore requested (see %LOCALAPPDATA%\TouchRedirect\TouchRedirect.log).
echo If the pointer is still invisible, change the mouse pointer scheme and change it back.
echo.
pause
