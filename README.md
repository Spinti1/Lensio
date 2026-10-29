# Lensio

**Your Android cameras. One Windows studio.**

LensioCam turns Android phones into wireless webcams. LensioStudio brings them together in a compact Windows studio with movable sources, image overlays, live filters and automatic workspace saving.

- Use up to four phones over Wi-Fi or USB (ADB).
- Drag and resize camera sources, add images, text and clocks.
- Use **LensioCam** as a virtual camera in Discord, browsers and other video apps.
- Keep streaming with the phone screen off; stop explicitly from the app or notification.
- Get updates from GitHub Releases with SHA-256 integrity checks.

## Get started

1. Download **LensioStudio.exe** and **LensioCam.apk** from [Releases](https://github.com/Spinti1/Lensio/releases/latest). Keep both files together on your PC.
2. Install the APK on your Android phone, grant camera/microphone permissions and open LensioCam.
3. Open LensioStudio on Windows. Use the same Wi-Fi network, or enable USB debugging for ADB.
4. Install OBS Studio's virtual camera module if needed, then run `register_virtual_cam.bat` from the source checkout as administrator. Select **LensioCam** in your video app.

Windows 10/11 x64 and Android 8+ are required. Actual resolution and frame rate depend on the phone, camera driver, lighting and connection. Screen-off capture can be affected by vendor power management.

## Updates

LensioStudio checks GitHub on startup. To install an available update, use the tray menu → **Check for app updates**. Downloads are verified, the workspace is saved and the app restarts. Keep the app in a user-writable folder.

LensioCam checks GitHub and can fall back to the local PC update server. Android asks before installing an APK and enforces matching package signatures. Install the first GitHub-enabled APK manually to enable this update path.

## Privacy

Video and audio travel directly between your devices. There is no hosted media relay or analytics service. Update checks contact GitHub, which receives normal connection metadata. Workspace settings and imported images stay in `%LOCALAPPDATA%\LensioStudio`; they are not part of the repository or release downloads.

This repository contains a clean source snapshot, without private Git history, device logs, personal configuration, signing keys or local build caches. Compatibility identifiers such as the Android package `com.apexcam` remain unchanged for existing installations.

## Build

**Windows:** run `apexcam_pc\build.bat`. This uses the Windows .NET Framework compiler and writes `LensioStudio.exe` next to the source folder.

**Android:** install JDK 21 and Android SDK 34, set `JAVA_HOME` and `ANDROID_HOME`, then run `apexcam\build_android.bat`. The APK uses your local Android debug signing key for compatibility with the current development release. Keep that key private and preserve it for future updates. A different key requires uninstalling the previous app.

The public CI builds and tests both platforms; its APK is only a build artifact, not a signed update for installed releases. See [release publishing](docs/RELEASING.md).

## Development

The maintained clients are `apexcam_pc/` (C# / WinForms) and `apexcam/android/` (Kotlin / CameraX). `tests/` contains desktop composition, persistence, layout, DirectShow and updater checks; Android unit tests cover streaming lifecycle, YUV packing and release metadata validation.

The DirectShow bridge uses the OBS virtual-camera interface. OBS Studio and other dependencies retain their respective licenses. No OBS driver DLLs are bundled in this source repository.
