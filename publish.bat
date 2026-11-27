@echo off
rem Builds YAMEE for distribution: a single YAMEE.exe with the .NET runtime included (nothing has to be installed on the target PC)
rem plus the Languages and Export folders. Result: publish\YAMEE (or publish\YAMEE_Portable when arg 2 is "Portable").
rem The first time, .NET downloads the Windows runtime from nuget.org (internet required, about 70 MB), not again afterwards.
rem With arg 1 set (e.g. "nopause" from build_installer.bat) it does not "pause".
set EDITION=Release
set OUTDIR=%~dp0publish\YAMEE
if /i "%~2"=="Portable" (
  set EDITION=Portable
  set OUTDIR=%~dp0publish\YAMEE_Portable
)
cd /d "%~dp0app\MapEditor"
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:YameeEdition=%EDITION% --source https://api.nuget.org/v3/index.json -o "%OUTDIR%"
if errorlevel 1 (
  echo.
  echo Publishing failed - please check the messages above.
  if "%~1"=="" pause
  exit /b 1
)
echo.
echo Done: %OUTDIR%
if "%~1"=="" pause
