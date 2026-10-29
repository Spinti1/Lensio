# LensioCam Web preview

Use an iPhone camera in LensioStudio without installing a native iOS app, paying an Apple Developer fee, or renewing an IPA signature. The static web client is published at **https://spinti1.github.io/Lensio/**. Use a current Safari version; compressed pairing codes require Safari 16.4 or later. You can add the page to your Home Screen using Safari's Share menu.

## Connect without copying codes

1. Open the current LensioStudio preview on Windows. Its receiver page opens automatically in your default browser. Keep that window visible. You can reopen it with **Panels → Add phone → iPhone / Web (preview)**.
2. On the iPhone, open **https://spinti1.github.io/Lensio/** in Safari and press **Start camera**. Allow camera access. Keep both devices on the same Wi-Fi network.
3. The phone name appears in the PC receiver. Click **Connect** next to that name.
4. On the phone, press **Accept connection** for your own Windows receiver. No links, offers or return codes need to be copied.
5. The phone appears as a normal LensioStudio source. Click its name to make it the main camera, or drag and resize it in the scene. Select **LensioCam** in Discord or your video app as usual.

Each session requires choosing and accepting the device; matching names are not stored as trusted identities. Discovery uses a public-IP/prefix grouping hint, which cannot prove that two devices share a router. Do not accept an unfamiliar receiver. A VPN, iCloud Private Relay, an IPv4/IPv6 mismatch, carrier address sharing, guest Wi-Fi or client isolation can prevent discovery or media connectivity. Temporarily use the same Wi-Fi without those routing differences, or use the manual pairing fallback. There is no STUN/TURN media relay.

The **Manual pairing** controls remain available if the discovery service is unavailable: create a pairing link on the PC, open it on the phone, create a return code and paste it into the receiver. Closing either page releases its session. Use a separate receiver page for each additional web phone; the native decoder still enforces its four-device limit.

## Supported in this preview

- Safari camera capture with ideal 720p/30 fps or 1080p/30 fps constraints, hardware-accelerated WebRTC where supported, and a muted local preview.
- Only cameras exposed normally by the browser; no probing for hidden sensors or guessed lens IDs.
- A local device name, remembered quality selection, and browser camera selection.
- Camera selection and resolution commands from LensioStudio; torch and digital zoom only when the browser reports those capabilities.
- Normal Windows source composition, transformations, overlays, filters, workspace saving, and virtual camera output.

This preview does not support microphone audio, PC audio forwarding, trusted unattended reconnection, or camera capture with Safari in the background or the iPhone screen locked. It requests a screen wake lock where supported, but this is not a guarantee against screen locking. Keep Safari open and the screen on. Keep the Windows receiver visible as background browser throttling can reduce frame rate. Capture frame rate and encoder quality ultimately depend on the devices, lighting, browser, and network; 30 fps is a requested target, not a guarantee.

The browser receives video over encrypted WebRTC. The PC renders decoded frames to JPEG at quality 0.94 and sends them over an authenticated loopback-only bridge into the existing bounded, latest-frame native decoder. A new frame is skipped while the previous local handoff is in flight, preventing an accumulating queue. The extra local conversion can affect CPU use and image quality; this is not a lossless transport. No Android streaming settings or safeguards are changed.

## Privacy and lifecycle

Video travels directly between the two browsers, then over loopback to LensioStudio. GitHub Pages serves static files and receives ordinary web-request metadata; no video or pairing code is sent to GitHub. Cloudflare hosts a separate discovery/signaling Worker. It processes network metadata, temporary device names and SDP offers/answers, but receives no camera frames. Discovery rooms are grouped using a keyed hash of the public IPv4 address or IPv6 /64 prefix, and idle sessions expire. See connect/README.md for the service design, free-plan capacity and privacy limits. Pairing links put the offer in the URL fragment, which is removed from the address bar after import and is not included in HTTP requests. Offers and answers contain temporary local connection details and should only be shared with your own devices. Copying them through a messaging service is subject to that service's privacy policy.

There are no analytics, third-party scripts, or camera uploads. The client saves only a random device ID, device name, selected camera ID, and resolution in its own browser local storage. LensioStudio saves the web camera's placement and settings with the other sources. Camera frames are not persisted by this feature. Closing the phone page releases its camera tracks. Closing LensioStudio disposes its bridge, accepted sockets and local decoder connections; the receiver notices the lost connection and closes its peer. The iPhone may keep its local camera preview until **Stop camera** or the page is closed.

The HTTP bridge listens on an ephemeral loopback port, requires a fresh cryptographically random session token, validates Host and Origin, limits request/header sizes and simultaneous requests, and rejects unauthenticated writes. Each receiver window has a separate session identifier: stale windows cannot send frames, read commands or disconnect a replacement session. Catalogs and control queues are bounded. Its socket handles are non-inheritable, and idle devices are expired. It serves embedded assets from the EXE; there are no extracted or versioned copies of web files on users' PCs.

## Validation

`node tests/web-protocol.test.mjs` checks compressed/plain codes, link import, wrong-step rejection, bounded decompression, and ICE completion/timeouts. `tests/WebCameraBridgeSmoke.cs` verifies embedded assets, authentication, cross-origin rejection, JPEG delivery through the normal decoder, targeted controls, disconnect and disposal. Existing Windows tests cover composition, filters, multiple Android phones, persistence, layout and updates.

A real Safari/iPhone streaming test is still required. Browser UI automation was unavailable in the development session, so automated checks do not establish Safari compatibility or sustained device frame rate.
