# Lensio

**Your cameras. One Windows studio.**

**2.11.4:** native Android clients can negotiate H.264 hardware encoding with LensioStudio. CameraX feeds one GPU texture stream to the MediaCodec encoder and local preview. Windows uses its inbox Media Foundation H.264 decoder, with D3D11 acceleration when available and software decoding otherwise. JPEG remains the compatibility fallback; web/iPhone clients retain their existing WebRTC path. No new permissions or external codec installer are required.

Validated using generated 720p/1080p H.264 streams, TCP reconnect/fallback tests, Android unit tests, and synthetic 1080p compositions with image overlays. A connected Galaxy A21s delivered 29–30 FPS using hardware H.264 and Windows D3D11 decoding, with corrected rotation confirmed in a phone test. The latest exposure and reconnect-recovery changes still need on-device confirmation; screen-off operation and JPEG/AVC quality comparisons remain unverified. Update both applications through their GitHub update checks to enable the new protocol. Older Android clients continue using JPEG.

The Android update card keeps its version, download size and installation action visible without displaying lengthy release notes. Desktop preview scaling retains a reusable surface without holding up scene publication; resetting filters saves the neutral settings immediately. A bounded two-frame camera buffer absorbs closely spaced arrivals between scene ticks, dropping older frames on overload instead of building up delay. Neutral camera frames use a direct pixel copy without another GDI+ drawing pass.

Hardware capture negotiates an advertised 30–30 FPS camera range through CameraX's public frame-rate API while keeping automatic exposure and white balance. A stalled encoder retries an adaptive camera range, then falls back to JPEG if output still stops. JPEG fallback retains its variable exposure range. Low light, driver and encoder limits can still affect brightness or capture rate. H.264 targets 12 Mbps at 720p and 24 Mbps at 1080p; quality equivalence to the former JPEG path needs visual verification. The status bar shows JPEG, H.264 GPU or H.264 CPU for the active native camera. Scene rendering uses paced high-resolution Windows timers.


LensioCam turns Android phones into wireless webcams. LensioStudio brings phones and PC webcams together in a compact Windows studio with movable sources, image overlays, live filters and automatic workspace saving.

Lensio shares its blue/violet app identity across Windows and Android. Disconnected camera sources and the standalone virtual camera display the Lensio no-signal artwork. Select **LensioCam** in video apps; **OBS Virtual Camera** remains a separate device.

Camera cards live above the pairing QR. Drag a card above or below another card to change camera stacking: the top card is drawn in front. Clicking selects a camera for editing without changing its position or size. Images, text, clocks and other overlays stay in **Sources**, always above all cameras. The order survives reconnects and app restarts. Right-click a camera card for properties, visibility and device controls.

Android camera cards show their reported battery percentage and a lightning indicator while charging. Offline devices are marked **Offline**; browser cameras without battery telemetry show **N/A**. Battery refresh uses existing telemetry and does not poll the phones or rebuild the cards.

- Use up to four phones over Wi-Fi or USB (ADB).
- Add a built-in laptop camera or USB webcam alongside your phones.
- Move, crop, rotate and align sources; add images, text and clocks.
- Use **LensioCam** as a virtual camera in Discord, browsers and other video apps.
- Keep streaming with the phone screen off; stop explicitly from the app or notification.
- Get updates from GitHub Releases with SHA-256 integrity checks.

## Android / iPhone web preview

Use the [latest stable release](https://github.com/Spinti1/Lensio/releases/latest). LensioStudio shows a QR code inside its Camera and Audio panel. Scan that same QR with one or several Android phones or iPhones and press **Start camera & connect**. Each phone appears automatically as its own source. No PC browser window, Add phone button, copied pairing links or return codes are required.

On a small window, select **Scan QR** in the panel header; click the QR to enlarge it. The website runs only on the phones, with just a camera preview, Start camera & connect, and Stop. Keep the phones on the same Wi-Fi, with their camera page visible and screen on. Android's native LensioCam app still connects automatically over Wi-Fi or USB (ADB). Web-camera video is received by an internal Microsoft WebView2 engine; Windows 10/11 with the Evergreen WebView2 Runtime is required. The normal four-camera limit applies across native and web phones together.

The QR is generated locally by LensioStudio before the video receiver starts; it does not need WebView2, a browser download or an internet request. Web-camera reception still needs Microsoft WebView2 and internet access for discovery. If the receiver cannot start, click the connection status below the QR for details and retry. Right-click the QR → **Install / repair Microsoft WebView2...** runs the bundled signed Microsoft bootstrapper after confirmation. Startup failures have time limits and explicit messages instead of leaving an endless QR-loading label.

The phone remembers the paired PC after a successful connection. Opening the normal [LensioCam Web](https://spinti1.github.io/Lensio/) page and pressing Start reconnects to that PC; scanning another PC's QR selects a different PC. This preview supports video; microphone and PC audio remain native Android features. See [setup, limitations and privacy](docs/WEB_CAMERA.md).

## Get started

1. Download **LensioStudio.exe** and **LensioCam.apk** from [Releases](https://github.com/Spinti1/Lensio/releases/latest). Install each file on its corresponding device; the PC does not need to keep a copy of the APK.
2. Install the APK on your Android phone, grant camera/microphone permissions and open LensioCam.
3. Open LensioStudio on Windows. Use the same Wi-Fi network, or enable USB debugging for ADB.
4. On first launch (or after upgrading an older shared-camera version), accept LensioStudio's virtual-camera setup and the Windows administrator prompt. No OBS installation or external scripts are needed. Select **LensioCam** in your video app. **OBS Virtual Camera** remains a separate device. Restart Discord/browser camera sessions after installation so they refresh their device lists. If setup was declined, use the LensioStudio tray menu → **Install / repair virtual camera**.

Windows 10/11 x64 and Android 8+ are required. The single Windows EXE embeds the 32/64-bit virtual camera, ADB USB tools and a signed Microsoft WebView2 bootstrapper. If WebView2 is missing, LensioStudio offers a per-user installation; this step needs internet access. USB still requires the phone owner to enable and authorize USB debugging, and some phones need their manufacturer's USB driver. Actual resolution and frame rate depend on the phone, camera driver, lighting and connection. Screen-off capture can be affected by vendor power management.

## Rotation and source alignment

The native Android camera follows the phone's physical orientation, including portrait, landscape and upside-down. Camera capture remains bound while orientation changes; the sensor is stopped when the camera service is released. The Windows camera source adjusts its aspect ratio around its current center while overlays stay on the scene canvas. Web-phone sources also follow the dimensions received from the browser.

Use **Sources → Add → PC / laptop webcam...** to choose a Windows webcam. Each selected webcam opens once, shares the existing scene compositor and reconnects from the saved workspace. Right-click its camera card and choose **Remove webcam**, or close LensioStudio, to release capture. Windows camera privacy settings apply; no administrator permission is needed. LensioCam's own virtual output and OBS Virtual Camera are excluded to prevent direct feedback. Webcam frames stay local and are not JPEG-encoded or sent through the phone network protocol. Capture requests a supported mode near the current studio resolution and 30 FPS; actual delivery depends on the camera, driver and exposure conditions.

Drag a source to move it. Magnetic guides align visible edges and centers with the canvas and other visible sources. Resize handles also align edges on unrotated sources; camera corners preserve their aspect ratio. Snap targets are collected once at drag start, with no network polling or per-move saves. Hold **Alt** while moving to bypass snapping, or disable **Image → Magnetic snapping**.

Hold **Alt** while starting a handle drag to crop the source instead of scaling it. Cropping is nondestructive. Right-click a source → **Properties** provides fit, center, clockwise/counterclockwise rotation, a custom rotation angle, crop reset, lock/unlock and visibility-shortcut assignment. The outline and hit testing follow rotation and the visible crop.

- **Ctrl+Shift+H:** show/hide the selected source.
- **Ctrl+R / Ctrl+Shift+R:** rotate the selected source clockwise/counterclockwise by 90 degrees.
- **Visibility shortcut...** in **Properties** records a single key or a combination with Ctrl, Alt or Shift. Letters, Space, F keys, arrows, navigation keys and numpad keys are supported. Press **Record shortcut** to change it or **Clear** to remove it. Conflicts with another source are rejected; explicit source bindings override built-in editor shortcuts. Holding the key does not toggle repeatedly. These shortcuts work while LensioStudio is focused and do not intercept text editing. Standalone modifier keys and Windows-reserved combinations are not intercepted.

Rotation, crop and visibility shortcuts are saved with the workspace. Transforms appear directly below opacity on the same Properties page, without tabs or a separate category. The dialog sizes itself to the source content and uses dark controls and window chrome.

Image/text additions and property edits save immediately; the periodic autosave still covers other settings. Imported images are managed independently of the original files. Saved scenes render on startup even without a connected phone. An atomic workspace backup retains the preceding valid layout and its image assets for recovery.

Static image overlays are cached at their actual scene size using high-quality interpolation and alpha compositing. Moving an image reuses its pixels; changing its image, opacity or size refreshes the cache. The original image is retained. The header reports **Camera FPS** for newly rendered main-camera frames and **Scene FPS** for scene composition, so repeated camera frames no longer look like a healthy camera stream. See [performance checks](docs/PERFORMANCE.md).

## Updates

LensioStudio checks GitHub on startup and offers to install a newer stable version. You can also check from the tray menu → **Check for app updates**. The header button shows **Checking for updates...** while checking GitHub over HTTPS. Only the Windows EXE is downloaded. Its length and SHA-256 must match GitHub release metadata; oversized, truncated or changed downloads are rejected. The layout must save successfully before the app closes safely and opens the verified **LensioStudio Setup** with the current installation folder selected. Choose **Install and open** to replace the EXE atomically and reopen the studio. Setup rechecks the hash and does not request elevation. It uses the existing process permissions, including when Windows already runs the app as administrator; administrator membership alone does not block updates. Keep the app in a user-writable folder. These installers are not code-signed.

LensioCam checks GitHub independently when opened, including when its camera service is already running. Settings → **Check for updates** checks manually. APK downloads use HTTPS and must match the release size and SHA-256. Android asks before installing an APK, may ask you to allow updates from LensioCam, and enforces matching package signatures. The PC does not serve, download or remotely install phone updates. Android APKs retain the existing signing key; the unsigned-installer notice refers to Windows EXEs.

Preview releases are intentionally excluded from automatic updates. No USB installation script is needed: `scripts/Install-Android-Usb.bat` is an optional development helper for the first installation through ADB. Users can install the APK directly on their phone; subsequent updates are offered inside the app.

## Privacy

Video and audio travel directly between your devices. There is no hosted media relay or analytics service. The optional iPhone web client uses Cloudflare for temporary device discovery and WebRTC signaling; its video still travels directly between devices. Cloudflare receives normal connection metadata and the temporary signaling data. Update checks contact GitHub, which receives normal connection metadata. Workspace settings and imported images stay in `%LOCALAPPDATA%\LensioStudio`; they are not part of the repository or release downloads.

This repository contains a clean source snapshot, without private Git history, device logs, personal configuration, signing keys or local build caches. Compatibility identifiers such as the Android package `com.apexcam` remain unchanged for existing installations.

## Build

**Windows:** install Visual Studio Build Tools 2022 with C++ x86/x64, Windows SDK and CMake, then run `LensioStudio\build.bat`. It builds both native virtual-camera modules from source and the desktop app with the Windows .NET Framework compiler, writing `LensioStudio.exe` next to the source folder.

**Android:** install JDK 21 and Android SDK 34, set `JAVA_HOME` and `ANDROID_HOME`, then run `LensioCam\build_android.bat`. The APK uses your local Android debug signing key for compatibility with the current development release. Keep that key private and preserve it for future updates. A different key requires uninstalling the previous app.

The public CI builds and tests both platforms; its APK is only a build artifact, not a signed update for installed releases. See [release publishing](docs/RELEASING.md).

## Development

The maintained clients are `LensioStudio/` (C# / WinForms), `LensioCam/android/` (Kotlin / CameraX), and `web/` (buildless Safari/WebRTC client). `scripts/Prepare-BundledDependencies.ps1` verifies dependency archive/component hashes and OBS/Microsoft signatures before embedding compressed components. `tests/` contains desktop composition, persistence, layout, DirectShow, web bridge, pairing-protocol and updater checks; Android unit tests cover streaming lifecycle, YUV packing and release metadata validation.

LensioCam has its own DirectShow class, DLLs, video queue, metadata file and Lensio-branded offline background, and can run alongside OBS Virtual Camera. Its offline image is shown even when LensioStudio is closed; it does not use the OBS logo or standby image. Its native module is built from adapted OBS 32.1.0 source; the modified source and build scripts are included in the repository and release source archive. The old signed official modules are retained only to restore known Lensio-owned legacy OBS registrations during migration. LensioStudio and the new Lensio camera DLLs are not code-signed. OBS Studio and other dependencies retain their respective licenses. See [dependency provenance](docs/BUNDLED_DEPENDENCIES.md).
