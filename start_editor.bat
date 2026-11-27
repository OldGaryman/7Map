@echo off
rem Builds the map editor (if necessary) and starts it.
cd /d "%~dp0app\MapEditor"
rem A running YAMEE locks its exe: the build then repeats the copy 10 times and prints a pile of warnings, then fails.
tasklist /FI "IMAGENAME eq YAMEE.exe" 2>nul | find /I "YAMEE.exe" >nul
if not errorlevel 1 (
  echo.
  echo YAMEE is still running and blocks the build.
  echo Please close YAMEE first, then start this file again.
  pause
  exit /b 1
)
dotnet build MapEditor.csproj -c Release -nologo -v q
if errorlevel 1 (
  echo.
  echo Compilation error
  pause
  exit /b 1
)
start "" "bin\Release\net9.0-windows\YAMEE.exe"
