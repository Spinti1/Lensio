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
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "..\scripts\Prepare-WebView2.ps1"
if errorlevel 1 (popd & exit /b 1)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "..\scripts\Prepare-BundledDependencies.ps1"
if errorlevel 1 (popd & exit /b 1)
"%CSC%" /nologo /codepage:65001 /utf8output /optimize /unsafe /platform:x64 /target:winexe /win32manifest:LensioStudio.manifest /r:System.dll,System.Windows.Forms.dll,System.Drawing.dll,System.Core.dll,System.Web.Extensions.dll /resource:..\web\index.html,LensioWeb.index.html /resource:..\web\app.js,LensioWeb.app.js /resource:..\web\auto-connect.js,LensioWeb.auto-connect.js /resource:..\web\config.json,LensioWeb.config.json /resource:..\web\protocol.js,LensioWeb.protocol.js /resource:..\web\style.css,LensioWeb.style.css /resource:..\web\manifest.webmanifest,LensioWeb.manifest.webmanifest /resource:..\web\icon.svg,LensioWeb.icon.svg /r:vendor\WebView2\Microsoft.Web.WebView2.Core.dll /r:vendor\WebView2\Microsoft.Web.WebView2.WinForms.dll /resource:vendor\WebView2\Microsoft.Web.WebView2.Core.dll,LensioRuntime.Microsoft.Web.WebView2.Core.dll /resource:vendor\WebView2\Microsoft.Web.WebView2.WinForms.dll,LensioRuntime.Microsoft.Web.WebView2.WinForms.dll /resource:vendor\WebView2\WebView2Loader.dll,LensioRuntime.WebView2Loader.dll /resource:..\web\engine.html,LensioWeb.engine.html /resource:..\web\native-receiver.js,LensioWeb.native-receiver.js /resource:..\web\track-receiver.js,LensioWeb.track-receiver.js /resource:..\web\qrcode.js,LensioWeb.qrcode.js /resource:..\docs\THIRD_PARTY_NOTICES.txt,LensioRuntime.ThirdPartyNotices.txt /resource:..\scripts\Bundled-Dependencies.json,LensioSetup.manifest.json /resource:vendor\VirtualCamera\obs-virtualcam-module64.dll.gz,LensioSetup.obs-virtualcam-module64.dll.gz /resource:vendor\VirtualCamera\obs-virtualcam-module32.dll.gz,LensioSetup.obs-virtualcam-module32.dll.gz /resource:vendor\VirtualCamera\placeholder.png.gz,LensioSetup.placeholder.png.gz /resource:vendor\Usb\adb.exe.gz,LensioSetup.adb.exe.gz /resource:vendor\Usb\AdbWinApi.dll.gz,LensioSetup.AdbWinApi.dll.gz /resource:vendor\Usb\AdbWinUsbApi.dll.gz,LensioSetup.AdbWinUsbApi.dll.gz /resource:vendor\WebView2\MicrosoftEdgeWebview2Setup.exe.gz,LensioSetup.MicrosoftEdgeWebview2Setup.exe.gz /resource:..\docs\OBS-GPL-2.0.txt,LensioSetup.OBS-GPL-2.0.txt /resource:..\docs\ANDROID-PLATFORM-TOOLS-NOTICES.txt,LensioSetup.AndroidNotices.txt /out:"%OUTPUT_DIR%\LensioStudio.exe" BundledSetup.cs DiscoveryBeacon.cs VirtualCameraBridge.cs AdbTunnel.cs VideoFilters.cs VideoReceiver.cs MultiVideoReceiver.cs MultiDeviceStudio.cs WebCameraBridge.cs EmbeddedWebReceiver.cs PhonePairingUi.cs PhoneNames.cs WorkspacePersistence.cs GitHubUpdater.cs CameraFrameScaler.cs MjpegReceiver.cs DiscoveryIdentityServer.cs UpdateSetup.cs WasapiCapture.cs UdpStreamer.cs MicReceiver.cs MainForm.cs StudioLayout.cs CameraPicker.cs Program.cs
if errorlevel 1 (
    popd
    exit /b 1
)
:success
popd
echo [SUCCESS] Built "%OUTPUT_DIR%\LensioStudio.exe"
exit /b 0
