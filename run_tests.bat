@echo off
rem Builds and runs the automatic tests of YAMEE (app\YAMEE.Tests).
rem They work on a small generated world in the temp folder, never on your projects.
rem Optional: set YAMEE_TEST_WORLD=<project folder with towns> also tests the road network (about 1 minute; the folder is only read).
cd /d "%~dp0app\YAMEE.Tests"
dotnet build YAMEE.Tests.csproj -c Release -nologo -v q
if errorlevel 1 (
  echo.
  echo Compilation error
  pause
  exit /b 1
)
"bin\Release\net9.0-windows\YAMEE.Tests.exe" %*
set result=%errorlevel%
echo.
pause
exit /b %result%
