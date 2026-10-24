@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-WebLoader.ps1"
if errorlevel 1 (
  echo.
  echo Installation stopped. Read the error above.
)
pause
