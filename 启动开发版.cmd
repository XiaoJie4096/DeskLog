@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\build-and-start-dev.ps1"
if not errorlevel 1 goto done
echo.
echo Development build did not start. Review the error above.
pause
:done
endlocal
