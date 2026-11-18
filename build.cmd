@echo off
rem Builds everything and runs the tests. Needs the .NET SDK 10 or later: https://dotnet.microsoft.com/download
setlocal
cd /d "%~dp0"
dotnet build VRMotionRecorder.slnx -c Release -nologo -v minimal || exit /b 1
dotnet test --solution VRMotionRecorder.slnx -c Release --no-build || exit /b 1
echo.
echo Built into artifacts\bin\
