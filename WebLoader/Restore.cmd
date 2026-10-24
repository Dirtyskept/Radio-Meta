@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Restore-WebLoader.ps1"
if errorlevel 1 (
  echo.
  echo Restore stopped. Read the error above.
)
pause
