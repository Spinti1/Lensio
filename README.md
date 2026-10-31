# Lensio

**Your Android cameras. One Windows studio.**

LensioCam turns Android phones into wireless webcams. LensioStudio brings them together in a compact Windows studio with movable sources, image overlays, live filters and automatic workspace saving.

- Use up to four phones over Wi-Fi or USB (ADB).
- Drag and resize camera sources, add images, text and clocks.
- Use **LensioCam** as a virtual camera in Discord, browsers and other video apps.
- Keep streaming with the phone screen off; stop explicitly from the app or notification.
- Get updates from GitHub Releases with SHA-256 integrity checks.

## Android / iPhone web preview

Use the [latest stable release](https://github.com/Spinti1/Lensio/releases/latest). LensioStudio shows a QR code inside its Camera and Audio panel. Scan that same QR with one or several Android phones or iPhones and press **Start camera & connect**. Each phone appears automatically as its own source. No PC browser window, Add phone button, copied pairing links or return codes are required.

On a small window, select **Scan QR** in the panel header; click the QR to enlarge it. The website runs only on the phones, with just a camera preview, Start camera & connect, and Stop. Keep the phones on the same Wi-Fi, with their camera page visible and screen on. Android's native LensioCam app still connects automatically over Wi-Fi or USB (ADB). Web-camera video is received by an internal Microsoft WebView2 engine; Windows 10/11 with the Evergreen WebView2 Runtime is required. The normal four-camera limit applies across native and web phones together.

The phone remembers the paired PC after a successful connection. Opening the normal [LensioCam Web](https://spinti1.github.io/Lensio/) page and pressing Start reconnects to that PC; scanning another PC's QR selects a different PC. This preview supports video; microphone and PC audio remain native Android features. See [setup, limitations and privacy](docs/WEB_CAMERA.md).

## Get started

1. Download **LensioStudio.exe** and **LensioCam.apk** from [Releases](https://github.com/Spinti1/Lensio/releases/latest). Install each file on its corresponding device; the PC does not need to keep a copy of the APK.
2. Install the APK on your Android phone, grant camera/microphone permissions and open LensioCam.
3. Open LensioStudio on Windows. Use the same Wi-Fi network, or enable USB debugging for ADB.
4. On first launch, accept LensioStudio's virtual-camera setup and the Windows administrator prompt. No OBS installation or external scripts are needed. Select **LensioCam** in your video app.

Windows 10/11 x64 and Android 8+ are required. The single Windows EXE embeds the 32/64-bit virtual camera, ADB USB tools and a signed Microsoft WebView2 bootstrapper. If WebView2 is missing, LensioStudio offers a per-user installation; this step needs internet access. USB still requires the phone owner to enable and authorize USB debugging, and some phones need their manufacturer's USB driver. Actual resolution and frame rate depend on the phone, camera driver, lighting and connection. Screen-off capture can be affected by vendor power management.

## Rotation and source alignment

The native Android camera follows the phone's physical orientation, including portrait, landscape and upside-down. Camera capture remains bound while orientation changes; the sensor is stopped when the camera service is released. The Windows camera source adjusts its aspect ratio around its current center while overlays stay on the scene canvas. Web-phone sources also follow the dimensions received from the browser.

Dragging a source near either scene-center axis snaps it exactly into alignment and shows cyan guides. Hold **Alt** to move freely, or disable **Image → Snap to center**. The setting is saved with your workspace.

Image/text additions and property edits save immediately; the periodic autosave still covers other settings. Imported images are managed independently of the original files. Saved scenes render on startup even without a connected phone. An atomic workspace backup retains the preceding valid layout and its image assets for recovery.

## Updates

LensioStudio checks GitHub on startup and offers to install a newer stable version. You can also check from the tray menu → **Check for app updates**. The header button shows **Checking for updates...** while checking GitHub over HTTPS. Only the Windows EXE is downloaded. Its length and SHA-256 must match GitHub release metadata; oversized, truncated or changed downloads are rejected. The layout must save successfully before the app closes safely and opens the verified **LensioStudio Setup** with the current installation folder selected. Choose **Install and open** to replace the EXE atomically and reopen the studio. Setup rechecks the hash and runs without elevation. Keep the app in a user-writable folder. These installers are not code-signed.

LensioCam checks GitHub independently when opened, including when its camera service is already running. Settings → **Check for updates** checks manually. APK downloads use HTTPS and must match the release size and SHA-256. Android asks before installing an APK, may ask you to allow updates from LensioCam, and enforces matching package signatures. The PC does not serve, download or remotely install phone updates. Android APKs retain the existing signing key; the unsigned-installer notice refers to Windows EXEs.

Preview releases are intentionally excluded from automatic updates. No USB installation script is needed: `scripts/Install-Android-Usb.bat` is an optional development helper for the first installation through ADB. Users can install the APK directly on their phone; subsequent updates are offered inside the app.

## Privacy

Video and audio travel directly between your devices. There is no hosted media relay or analytics service. The optional iPhone web client uses Cloudflare for temporary device discovery and WebRTC signaling; its video still travels directly between devices. Cloudflare receives normal connection metadata and the temporary signaling data. Update checks contact GitHub, which receives normal connection metadata. Workspace settings and imported images stay in `%LOCALAPPDATA%\LensioStudio`; they are not part of the repository or release downloads.

This repository contains a clean source snapshot, without private Git history, device logs, personal configuration, signing keys or local build caches. Compatibility identifiers such as the Android package `com.apexcam` remain unchanged for existing installations.

## Build

**Windows:** run `LensioStudio\build.bat`. This uses the Windows .NET Framework compiler and writes `LensioStudio.exe` next to the source folder.

**Android:** install JDK 21 and Android SDK 34, set `JAVA_HOME` and `ANDROID_HOME`, then run `LensioCam\build_android.bat`. The APK uses your local Android debug signing key for compatibility with the current development release. Keep that key private and preserve it for future updates. A different key requires uninstalling the previous app.

The public CI builds and tests both platforms; its APK is only a build artifact, not a signed update for installed releases. See [release publishing](docs/RELEASING.md).

## Development

The maintained clients are `LensioStudio/` (C# / WinForms), `LensioCam/android/` (Kotlin / CameraX), and `web/` (buildless Safari/WebRTC client). `scripts/Prepare-BundledDependencies.ps1` verifies dependency archive/component hashes and OBS/Microsoft signatures before embedding compressed components. `tests/` contains desktop composition, persistence, layout, DirectShow, web bridge, pairing-protocol and updater checks; Android unit tests cover streaming lifecycle, YUV packing and release metadata validation.

The DirectShow bridge uses the OBS virtual-camera interface. OBS Studio and other dependencies retain their respective licenses. The official OBS 32.1.0 camera modules are embedded in release EXEs; source checkouts fetch pinned official dependencies when building. Matching OBS source and license files accompany each release. See [dependency provenance](docs/BUNDLED_DEPENDENCIES.md).
