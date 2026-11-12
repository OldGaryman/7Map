@echo off
rem Builds the map editor (if necessary) and starts it.
cd /d "%~dp0app\MapEditor"
dotnet build MapEditor.csproj -c Release -nologo -v q
if errorlevel 1 (
  echo.
  echo Compilation error
  pause
  exit /b 1
)
start "" "bin\Release\net9.0-windows\YAMEE.exe"
