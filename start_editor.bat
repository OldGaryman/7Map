@echo off
rem Baut den Karteneditor (falls noetig) und startet ihn.
cd /d "%~dp0app\MapEditor"
dotnet build MapEditor.csproj -c Release -nologo -v q
if errorlevel 1 (
  echo.
  echo Fehler beim Bauen - bitte die Meldungen oben an Claude schicken.
  pause
  exit /b 1
)
start "" "bin\Release\net9.0-windows\YAMEE.exe"
