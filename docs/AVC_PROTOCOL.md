# Native Android AVC protocol

The connection remains phone-initiated TCP on the existing receiver port. Existing APCI identity and APCM JPEG framing remain supported. New identities advertise `videoProtocol: 1` and `codecs: ["h264","jpeg"]`. Only the PC may offer `{ "videoCodec":"h264", "videoProtocol":1 }`, after verifying that its embedded decoder can load. Without this negotiation, the phone sends JPEG.

An AVC packet is 40 bytes of little-endian header followed by one Annex-B H.264 access unit:

| Offset | Size | Meaning |
| --- | --- | --- |
| 0 | 4 | ASCII LH24 |
| 4 | 4 | Encoded width before rotation |
| 8 | 4 | Encoded height before rotation |
| 12 | 4 | Payload byte count |
| 16 | 4 | Actual transmitted FPS estimate |
| 20 | 4 | Battery percent in low 16 bits; bit 16 indicates charging |
| 24 | 4 | Temperature in tenths of a degree Celsius |
| 28 | 4 | Flags: version 1 in bits 24–31, clockwise rotation 0/90/180/270 in bits 8–16, IDR/sync marker in bit 0 |
| 32 | 8 | Capture timestamp in 100 ns units, relative to the phone's monotonic clock |

This timestamp is not directly comparable with the PC clock; it must not be displayed as a one-way latency without clock synchronization. The decoder uses it as the input sample timestamp.

The receiver requires even dimensions, at most 1920 on either axis and 1920×1080 pixels total; payloads are capped at 4 MiB. The decoder further validates negotiated output size before allocating/copying visible output. Android hardware capture uses one CameraX Preview texture with shared EGL contexts. The existing WebRTC hardware encoder wrapper controls MediaCodec; no Iriun code or binaries are used. Codec SPS/PPS accompany key access units as emitted by that wrapper.

The phone's network worker owns a bounded four-packet queue and never writes on the camera/encoder thread. JPEG and AVC packets serialize through the session output lock. A blocked session is closed by the existing watchdog. Overflow discards the dependent queued sequence and waits for a fresh sync frame; requested sync frames are throttled at the encoder. PC pending compressed input is bounded to 24 packets or 4 MiB, independently of per-packet limits. Missing sync frames cause a keyframe request at most once per second. AVC decode failure/no output causes a single switch request to JPEG; AVC is then ignored for that session. Hardware encoder failure also returns CameraX to its existing JPEG analysis path.

`{ "videoKeyframe":true }` requests a fresh sync frame. `{ "videoCodec":"jpeg" }` returns to JPEG. Every reconnect receives fresh negotiation. Device identity, audio controls, camera catalog and all existing lifecycle watchdogs remain in use.

The native decoder is built from `LensioStudio/native-codec`, uses Windows inbox Media Foundation and D3D11, and is embedded in LensioStudio.exe. It is extracted to a per-user content-addressed directory, checked against its embedded SHA-256 before loading, and loaded by absolute path with the dependency search restricted to that directory and System32. It does not register COM filters or request elevation. Software AVC decoding and then JPEG provide fallbacks when hardware or Media Foundation is unavailable. This protocol retains the existing trusted-LAN transport model; it does not add encryption or authentication to native LAN streaming.

## Texture orientation in 2.11.3

A Camera2 SurfaceTexture already carries the camera's sensor transform. Applying the sensor-to-target rotation again without normalizing that transform can rotate and stretch the preview and encoded image. Hardware capture now applies a centered inverse sensor rotation and front-camera mirror correction to the existing GPU texture. The physical phone orientation is carried separately in frame metadata, as in the [WebRTC Camera2 capture pipeline](https://webrtc.googlesource.com/src/+/refs/heads/main/sdk/android/src/java/org/webrtc/Camera2Session.java). See also [CameraX surface orientation](https://developer.android.com/reference/androidx/camera/core/Preview).

Changing physical orientation updates frame metadata from CameraInfo without changing the Preview target and recreating the hardware surface. GPU preview and encoding use the same normalized texture; Windows applies the metadata during its existing YUV-to-RGB pass. No intermediate RGB rotation or additional camera stream is introduced. Unit tests cover cancellation of all four sensor orientations, front/back mirrors, corners and center; physical preview and stream alignment still need confirmation after installation.

## Encoder output recovery in 2.11.4

Hardware capture negotiates its frame-rate range through the public Preview.setTargetFrameRate API. The existing service health tick also checks encoder output, independently of texture arrival and socket writes. Six seconds without encoded output triggers an adaptive-range retry for that camera when fixed capture was in use; continued output starvation switches to JPEG. The per-camera rejection is retained during the service session, so reconnecting cannot repeatedly select the same failed fixed-rate mode. This recovery does not add network probes or permissions, and keeps the existing socket/camera watchdogs.

Stopping capture marks the encoder closed but keeps consuming camera textures until CameraX's SurfaceRequest result confirms that the HAL released the provided surface. Surface, encoder and EGL disposal then run once on the texture thread. This avoids releasing a live camera surface during unbind/reconnect.

For fixed-rate hardware capture, automatic exposure receives modest compensation only after two one-second sensor samples show a shutter time near the 30 FPS ceiling. Compensation is capped near +0.67 EV and the camera's supported range; two bright samples restore neutral exposure. Explicit exposure commands override this behavior. Capture-result metadata is sampled at most once per second, and a control request is made only when compensation changes. Resolution and requested FPS stay unchanged; sensor gain and noise can increase in dim light. Unit tests cover the neutral case, hysteresis, camera limits and reset.

Validation without a phone covers Windows real AVC decoding, rotation, color samples, TCP negotiation/reconnect, malformed inputs, fallback, frame pacing, overlays, persistence and unit-tested Android socket lifecycle. Phone hardware encoder behavior, screen-off capture, thermal behavior and JPEG/AVC image-quality parity still require real-device validation. Neither the negotiated codec nor a 30 FPS target guarantees that a phone camera delivers 30 unique frames per second.
