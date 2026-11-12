@echo off
rem Builds YAMEE for distribution: a single YAMEE.exe with the .NET runtime included (nothing has to be installed on the target PC)
rem plus the Languages folder. Result: publish\YAMEE. The first time, .NET downloads the Windows runtime from nuget.org
rem (internet required, about 70 MB), not again afterwards. With an argument (e.g. from build_installer.bat) it does not "pause".
cd /d "%~dp0app\MapEditor"
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:YameeEdition=Release --source https://api.nuget.org/v3/index.json -o "%~dp0publish\YAMEE"
if errorlevel 1 (
  echo.
  echo Publishing failed - please check the messages above.
  if "%~1"=="" pause
  exit /b 1
)
echo.
echo Done: %~dp0publish\YAMEE
if "%~1"=="" pause
