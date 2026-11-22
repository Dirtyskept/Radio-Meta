@echo off
setlocal
cd /d "%~dp0"
echo Radio Meta offline metadata regression tests
echo.
where dotnet >nul 2>nul
if errorlevel 1 (
  echo ERROR: .NET 8 SDK was not found. Install the .NET 8 SDK, then run this file again.
  echo This test harness does not require Emby assemblies or a running Emby server.
  pause
  exit /b 2
)
dotnet run --project "%~dp0RadioMeta.MetadataTests\RadioMeta.MetadataTests.csproj" --configuration Release
set "RESULT=%ERRORLEVEL%"
echo.
if "%RESULT%"=="0" (
  echo All automated metadata regression tests passed.
) else (
  echo One or more tests failed. Send the console output back for review.
)
pause
exit /b %RESULT%
