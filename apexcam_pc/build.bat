@echo off
setlocal
title Build LensioStudio
set "CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" exit /b 1
set "OUTPUT_DIR=%~dp0..\.."
if not "%~1"=="" set "OUTPUT_DIR=%~f1"
if not exist "%OUTPUT_DIR%" mkdir "%OUTPUT_DIR%"
pushd "%~dp0"
"%CSC%" /nologo /codepage:65001 /utf8output /optimize /unsafe /platform:x64 /target:winexe /r:System.dll,System.Windows.Forms.dll,System.Drawing.dll,System.Core.dll,System.Web.Extensions.dll /out:"%OUTPUT_DIR%\LensioStudio.exe" DiscoveryBeacon.cs VirtualCameraBridge.cs AdbTunnel.cs VideoFilters.cs VideoReceiver.cs MultiVideoReceiver.cs MultiDeviceStudio.cs PhoneNames.cs WorkspacePersistence.cs GitHubUpdater.cs CameraFrameScaler.cs MjpegReceiver.cs UpdateServer.cs WasapiCapture.cs UdpStreamer.cs MicReceiver.cs MainForm.cs StudioLayout.cs CameraPicker.cs Program.cs
if errorlevel 1 (
    popd
    exit /b 1
)
:success
popd
echo [SUCCESS] Built "%OUTPUT_DIR%\LensioStudio.exe"
exit /b 0
