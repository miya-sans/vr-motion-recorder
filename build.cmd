@echo off
rem Builds the app into dist\VRMotionRecorder.exe. Pin it to the taskbar; after a git pull, run this again to update it.
rem Needs the .NET SDK 10 or later: https://dotnet.microsoft.com/download
rem   build.cmd                build the app
rem   build.cmd test           build everything and run the tests first
rem   build.cmd avatar-meshes  run the two Settings tests that load a real avatar's meshes
setlocal
cd /d "%~dp0"
if /i "%~1"=="avatar-meshes" goto avatar_meshes
if /i "%~1"=="test" (
  dotnet build VRMotionRecorder.slnx -c Release -nologo -v minimal || exit /b 1
  dotnet test --solution VRMotionRecorder.slnx -c Release --no-build || exit /b 1
)
dotnet publish src\Vrmr.App\Vrmr.App.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o artifacts\app -nologo -v minimal || exit /b 1
if not exist dist mkdir dist
copy /y artifacts\app\VRMotionRecorder.exe dist\VRMotionRecorder.exe >nul || (
  echo VRMotionRecorder.exe is in use: close the app and run build.cmd again.
  exit /b 1
)
echo.
echo Built %~dp0dist\VRMotionRecorder.exe
exit /b 0

:avatar_meshes
dotnet test --project tests/Vrmr.App.Tests/Vrmr.App.Tests.csproj -c Release -- --filter-class VRMR.App.Tests.SettingsTests --filter-method *HiddenMeshesOfARealAvatarFollowTheTicks --filter-method *RealAvatarsAndTheirMeshesDraw --fail-skips on
exit /b %errorlevel%
