@echo off
rem Builds everything and runs the tests. Needs the .NET SDK 10 or later: https://dotnet.microsoft.com/download
setlocal
cd /d "%~dp0"
if /i "%~1"=="avatar-meshes" goto avatar_meshes
dotnet build VRMotionRecorder.slnx -c Release -nologo -v minimal || exit /b 1
dotnet test --solution VRMotionRecorder.slnx -c Release --no-build || exit /b 1
echo.
echo Built into artifacts\bin\
exit /b 0

:avatar_meshes
dotnet test --project tests/Vrmr.App.Tests/Vrmr.App.Tests.csproj -c Release -- --filter-class VRMR.App.Tests.SettingsTests --filter-method *HiddenMeshesOfARealAvatarFollowTheTicks --filter-method *RealAvatarsAndTheirMeshesDraw --fail-skips on
exit /b %errorlevel%
