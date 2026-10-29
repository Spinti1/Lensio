# Phone web cameras with a native Windows receiver

The website runs on Android/iPhone phones. LensioStudio receives the video internally using Microsoft WebView2; it does not open or require a PC browser window.

## Connect

1. Open the current LensioStudio QR preview on Windows. Its Camera and Audio panel displays a QR code below the phone controls when space permits. At small window sizes, select **Scan QR** in the panel header. Click the QR to enlarge it.
2. Scan the QR using an iPhone or Android camera. It opens LensioCam Web over HTTPS in Safari or another supported mobile browser.
3. Press **Start camera & connect** and allow camera access. The phone connects automatically to that PC and appears as a movable camera source. No copied links, return codes, or PC-side web interface are required.
4. Scan the same QR with additional phones. Up to four native/web cameras together can stream into the existing scene.

The native Android APK continues its existing automatic USB (ADB) and Wi-Fi discovery. Scanning the QR opens the web camera on Android as an optional alternative; it does not install the APK. Native and browser capture cannot necessarily use the same physical sensor simultaneously.

After a successful web connection, the phone remembers that PC's pairing room in its own local browser storage. A later visit to the plain LensioCam Web URL can reconnect after Start camera. Scanning another PC's QR selects its room instead. LensioStudio keeps its private receiver key in `%LOCALAPPDATA%\LensioStudio\web-pairing.key`; preserving that file keeps the QR stable across restarts. Do not publish it or share screenshots of a real pairing QR outside your own devices.

The phone page contains only the camera preview, **Start camera & connect**, and **Stop**. A small preview status and a message shown only on errors replace the previous setup panels. Camera, resolution, zoom and torch are controlled in LensioStudio on the PC; existing device preferences remain stored.

## Network and device limits

Pairing uses the private QR room rather than guessing router membership from public IP addresses, so IPv4/IPv6 differences and shared ISP addresses no longer determine discovery. Receiving the camera still requires direct WebRTC connectivity: the same Wi-Fi without client isolation is recommended. There is no TURN/STUN media relay. A VPN, guest network, local firewall, browser or OS restriction can still prevent the direct media connection.

Keep the camera page visible and the phone screen on. Browser capture is not guaranteed in the background or after screen lock. A screen wake lock is requested where available. Native Android screen-off streaming is unchanged. Camera and resolution selections are limited to what the phone/browser exposes normally; hidden sensors are never guessed or probed. Torch and zoom controls are enabled only when supported.

The web preview supports video only. Microphone and PC audio forwarding remain native Android features. 720p/30 fps and 1080p/30 fps are requested targets, not device-performance guarantees. The internal receiver reads decoded video tracks through a bounded MediaStreamTrackProcessor and OffscreenCanvas rather than window repaint timers, allowing reception without a visible PC web page. The local JPEG handoff uses quality 0.94 and is not lossless. Overall throughput still depends on CPU, hardware codec, phone, lighting and connection.

Windows 10/11 x64 and the Microsoft Evergreen WebView2 Runtime are required for web phones. The runtime is already installed on this development PC. If missing elsewhere, obtain it from Microsoft's official WebView2 Runtime distribution. The EXE embeds the managed SDK libraries and native loader, and uses an isolated receiver profile under `%LOCALAPPDATA%\LensioStudio\web-receiver`. The Windows compiler build script downloads a pinned, hash-verified Microsoft SDK from NuGet when needed; no personal profile, runtime cache or signing key belongs in the repository.

## Privacy and permissions

The PC's QR contains an unguessable pairing-room identifier in the URL fragment, not the receiver's private proof. The fragment is removed from the phone address bar after import and is never part of the GitHub Pages HTTP request. Only scan a PC you want to send video to. Pressing Start camera on that paired page authorizes automatic streaming to the PC. The page records the room only after a successful connection.

Cloudflare hosts temporary WebSocket discovery and SDP signaling. Camera frames travel directly between the phone and LensioStudio's embedded WebRTC engine, then over authenticated loopback into the existing latest-frame native decoder. Cloudflare receives signaling and normal network metadata but no video frames. GitHub serves static files and receives normal web request metadata. There are no analytics or third-party runtime script fetches; the QR encoder is bundled locally with its license.

Knowing the scanned room permits joining as a phone, but is insufficient to become a receiver: the PC proves possession of a separate private key whose SHA-256 digest is the QR room. Phone sockets cannot claim the PC host role. Each phone is paired independently, and up to four phones can belong to the receiver concurrently. Cancellation and reconnect cleanup are scoped to each device/session so a stale peer cannot disconnect its replacement or a different phone.

The loopback bridge validates Host, Origin, session token, per-phone session identifiers, request sizes and JPEG framing. Socket handles are non-inheritable. Device/control queues are bounded. The embedded engine denies camera/microphone/browser permissions, blocks external top-level navigation and popups, and disables developer tools, password storage and autofill. Closing LensioStudio disposes the embedded engine, native sockets and device decoders. Closing the phone page releases its camera. The phone can retain a local preview until Stop camera is pressed if the PC goes away.

## Validation

The desktop build and existing filter, composition, multi-phone, layout, workspace, virtual-camera/updater regression tests cover the unchanged native pipeline. The bridge test covers versioned/embedded engine assets and authenticated frame/control lifecycle. The embedded receiver smoke test starts WebView2 without an external browser, generates a local QR and connects its discovery socket. The tested runtime exposes the background track processor.

Offline discovery tests cover private-room routing across IPv4/IPv6, independent multi-phone ownership, role restrictions, rate/size limits, cancellation and expiry. The public WSS test exchanges two simultaneous synthetic phone offers/answers with the authenticated receiver and rejects a phone claiming the receiver role. Synthetic signaling does not establish real-camera frame rate: the integrated QR flow still needs an on-device Android/iPhone media test.