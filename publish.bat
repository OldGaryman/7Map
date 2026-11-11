@echo off
rem Baut YAMEE zum Weitergeben: eine einzige YAMEE.exe mit eingebautem .NET (auf dem Ziel-PC muss nichts installiert sein)
rem plus den Ordner Languages. Ergebnis: publish\YAMEE. Beim ersten Mal laedt .NET die Windows-Laufzeit von nuget.org
rem (Internet noetig, ca. 70 MB), danach nicht mehr. Mit einem Argument (z. B. von build_installer.bat) ohne "pause".
cd /d "%~dp0app\MapEditor"
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:YameeEdition=Release --source https://api.nuget.org/v3/index.json -o "%~dp0publish\YAMEE"
if errorlevel 1 (
  echo.
  echo Fehler beim Veroeffentlichen - bitte die Meldungen oben an Claude schicken.
  if "%~1"=="" pause
  exit /b 1
)
echo.
echo Fertig: %~dp0publish\YAMEE
if "%~1"=="" pause
