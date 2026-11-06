# Native Android AVC protocol (preview)

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

Validation without a phone covers Windows real AVC decoding, rotation, color samples, TCP negotiation/reconnect, malformed inputs, fallback, frame pacing, overlays, persistence and unit-tested Android socket lifecycle. Phone hardware encoder behavior, screen-off capture, thermal behavior and JPEG/AVC image-quality parity require real-device validation before promoting this preview to stable.
