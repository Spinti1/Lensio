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

## Encoder handoff in 2.11.5

Closing a session releases its MediaCodec encoder immediately on its texture thread while keeping the camera surface alive until CameraX returns it. A replacement encoder waits for the preceding encoder's release, off the main thread and bounded to five seconds. The initial output allowance is ten seconds; an established encoder still recovers after six seconds without output. The existing socket watchdog remains active. The camera catalog exposes texture-input count, whether encoded output started, and output idle time for diagnosis without ADB.

Validation without a phone covers Windows real AVC decoding, rotation, color samples, TCP negotiation/reconnect, malformed inputs, fallback, frame pacing, overlays, persistence and unit-tested Android socket lifecycle. Phone hardware encoder behavior, screen-off capture, thermal behavior and JPEG/AVC image-quality parity still require real-device validation. Neither the negotiated codec nor a 30 FPS target guarantees that a phone camera delivers 30 unique frames per second.

## Automatic exposure correction in 2.11.7

On the connected Galaxy A21s running 2.11.6, the encoder handoff correction sustained 29–30 encoded FPS and the catalog reported a fixed 30–30 capture range with +0.7 EV compensation. The user still reported a dark image. Earlier confirmation of good brightness in 2.11.4 occurred in the adaptive capture mode, not fixed 30–30 mode; it therefore did not validate that exposure compensation solved the fixed-range problem.

Version 2.11.7 gives hardware and JPEG capture the same advertised auto-exposure range ending at the requested 30 FPS, preferring a variable range over fixed 30–30 when both exist. The fixed-frame interval is no longer imposed solely because H.264 is active. The shutter-based compensation heuristic and its per-frame capture callback have been removed; normal auto-exposure and explicit exposure controls remain active. The encoder stays at 30 FPS and its existing bitrate, with the GPU texture path, orientation correction and encoder lifecycle protections unchanged.

This restores the camera's ability to choose longer exposure in dim light. It cannot promise 30 unique sensor frames per second when the scene requires a shutter time longer than 1/30 second. The [Camera2 AE frame-rate documentation](https://developer.android.com/reference/android/hardware/camera2/CaptureRequest#CONTROL_AE_TARGET_FPS_RANGE) describes the range as the interval in which auto-exposure can adjust capture rate to maintain exposure. `VideoFpsPolicyTest` checks variable-range preference, a 30 FPS upper target, advertised-only selection and rejected fixed modes. Actual brightness still needs confirmation after installing this APK.

## Transient codec recovery in 2.11.8

Previously, one Android encoder failure or output-watchdog timeout set a service-lifetime failure flag. Every later H.264 negotiation then returned JPEG, even after reconnecting. A PC decoder exception likewise rejected AVC immediately for that connection. Neither path first attempted a bounded codec recovery. The connected 2.11.7 phone was confirmed in JPEG at approximately 10–12 delivered FPS when this issue was reported; without ADB, the original vendor MediaCodec failure could not be retrieved.

Android now schedules two hardware restarts, after one and two seconds, using the existing health tick. Continued failure temporarily uses JPEG and retries H.264 after 30 seconds; further unsuccessful probes back off to 60 and then 120 seconds. Thirty seconds of recent encoded output resets the failure budget. A genuinely absent hardware encoder does not retry. Repeated negotiation during a pending retry cannot bypass the cooldown. The catalog exposes the latest bounded failure reason, failure count and pending-retry flag, and output starvation remains independently protected by the existing camera/socket watchdogs.

Camera callbacks and SurfaceRequests carry a capture generation; callbacks from a superseded binding cannot deliver frames or disable the new encoder. Failure reporting posts once per capture. Commands and connection events carry the socket generation through the service's main-thread queue, preventing an old JPEG command or disconnect event from affecting a newer connection. Invalid commands remain caught on the main thread.

A socket disconnect disables sending but retains a healthy encoder and camera surface. The next connection negotiates its codec; an older receiver that does not negotiate H.264 returns to JPEG after a 2.5-second negotiation allowance. Explicit JPEG selection and service shutdown still release hardware capture. This avoids repeatedly tearing down MediaCodec and CameraX when Wi-Fi briefly drops.

On Windows, the first decoder failure clears stale compressed references and requests a fresh IDR. A second failed recovery retries H.264 with the inbox software decoder. Three consecutive failed recoveries request JPEG; sixty successfully decoded frames reset the budget. Geometry, payload limits and bounded queues remain enforced. Native GPU initialization already has a software fallback and remains unchanged.

Unit tests cover cooldown timing, capped backoff, sustained-output reset, absent codec behavior and original socket generations on queued commands. `AvcReceiverSmoke` injects damaged access units, verifies same-connection H.264 resynchronization, and checks bounded fallback for persistent corruption. Its optional `--soak` mode sends 1,800 frames through three TCP sessions over sixty seconds. Physical vendor-encoder failure/recovery and screen-off longevity still require testing with the updated APK; synthetic transport tests cannot establish that the phone's MediaCodec issue is eliminated. Resolution, bitrate, automatic exposure and the encoder's 30 FPS target are unchanged.

## Camera/encoder EGL ownership in 2.11.9

After USB authorization, logcat exposed a historical fatal `SurfaceTexture.updateTexImage` exception on `Lensio-AVC-texture`: "Unable to update texture contents". The application initialized and called the hardware encoder on SurfaceTextureHelper's own thread. The encoder creates and makes its own EGL context current during initialization and texture encoding, replacing the camera helper's current context. This is a concrete ownership violation consistent with the captured exception: [Android requires the texture's owning context to be current during updateTexImage](https://developer.android.com/reference/android/graphics/SurfaceTexture#updateTexImage()), and the [WebRTC hardware encoder creates its own current EGL surface](https://webrtc.googlesource.com/src/+/refs/heads/main/sdk/android/src/java/org/webrtc/HardwareVideoEncoder.java).

Initialization, encoding and encoder disposal now run on one dedicated `Lensio-AVC-encoder` thread. SurfaceTexture acquisition and disposal stay on the helper's original thread. They share GPU resources through the existing shared context without replacing each other's current context. A retained normalized texture is handed to the encoder worker and released in `finally`; SurfaceTextureHelper permits only one outstanding texture, so this handoff adds no growing frame queue or RGB readback. Preview and encoding still receive the same transformed GPU pixels. Encoder release is ordered on its worker before the predecessor handoff latch is signaled; CameraX still controls when its provided surface can be released.

On the connected Galaxy A21s, the corrected code sustained H.264 through five explicit TCP reconnects without rebuilding the encoder: input/output counters advanced from 879/879 to 1481/1481 with zero recovery failures. A separate forty-second screen-off check stayed in H.264 with matching counters through 2419/2419 and zero recovery failures. The adaptive-exposure camera delivered approximately 16–20 FPS under the current scene; this test does not establish 30 sensor FPS or hours-long stability. These real-device checks complement, rather than replace, the synthetic transport and recovery tests.

## Independent camera textures in 2.11.10

A slow encoder or preview previously retained the helper's only camera texture. That prevented the helper from draining later Camera2 buffers. A controlled eight-second application pause on the connected Galaxy A21s reproduced a HAL device error followed by a hardware restart. This is evidence that prolonged consumer stalls can disrupt the capture session, not proof that every vendor failure has that cause.

Capture now copies the normalized frame into a bounded pool of three full-resolution GPU textures, completing the copy before returning the original camera buffer. Preview and MediaCodec retain these independent copies. There is one encoding operation and at most one pending newest frame; overload replaces stale pending copies instead of accumulating work or retaining Camera2 buffers. The pool waits for outstanding consumers before destroying its textures and helper context. It does not read camera pixels back to the CPU, reduce dimensions, change auto-exposure, lower bitrate, or change the encoder's 30 FPS target.

If an IDR input is rejected with `NO_OUTPUT`, its sync request remains pending. While the sender is waiting for an IDR it reasserts the local request flag; the existing half-second encoder throttle remains in effect. These notifications make no network request. This prevents a lost keyframe request from suppressing all delta frames until the watchdog disconnects. If output recovers before a scheduled first or second hardware restart, the existing health tick cancels that unnecessary restart; the failure budget still requires thirty seconds of healthy output to reset.

`LatestFrameMailboxTest` checks bounded ownership and concurrent release; `AvcTransportTest` checks sync re-requesting without premature delta transmission. The device-only `CameraTexturePoolDeviceTest` compares every RGBA output byte with its source, holds all three slots, checks immediate source release, and verifies deferred resource destruction. Run the instrumentation suite on an authorized Android device; it also includes the UDP audio check. A real longevity check should leave the final APK connected with the screen off for at least twenty-five minutes and monitor encoded input/output progress, codec, failures and playback diagnostics. A passing bounded test is not a guarantee for every handset or hours of use.

## Vendor FD exhaustion and YUV input in 2.11.11

The 2.11.10 long test reproduced the failure despite matching encoded inputs/outputs and zero pending-frame drops. Vendor logging then reported `Too many open files` from the Samsung camera-provider process, followed by Ion allocation failures, HAL request failures, repeated reopen attempts and eventually a provider-process death. Restarting the application did not clear the exhausted provider state. The provider's reported soft FD limit was 32,768. Its individual FD table was inaccessible without additional privilege, so the exact leaking descriptor type was not established. The Android UI's old `c.` label means transmitted frames, not temperature, and uses a 64-bit counter; approximately 30,000 is a diagnostic observation, not a configured capture limit.

The Galaxy A21s now binds only CameraX ImageAnalysis and feeds hardware H.264 through CPU-readable YUV input, with no camera Preview/SurfaceTexture output. Each image's visible Y/U/V samples are copied into one of three reusable full-resolution I420 buffers; the ImageProxy is closed before preview or encoding receives that copy. The encoder factory receives no EGL context and explicitly uses MediaCodec's YUV input. Preview renders the same independent I420 pixels. Pixel strides, row padding, final-row limits and source buffer positions are respected. No JPEG round trip, RGB conversion, reduction of resolution, bitrate or encoder target is introduced. CPU work and hardware codec compatibility can differ from texture input; actual throughput requires device validation.

Other devices retain texture capture initially and select YUV input for a bounded recovery after a non-permanent texture failure. Both input implementations share lifecycle, keyframe requests and watchdog checks. Rotation remains sensor-relative metadata; each capture generation owns its analyzer. Camera2 auto-exposure/white-balance/focus bindings and native/web protocol safeguards remain intact. The camera catalog reports `hardwareInput` and each encoder diagnostic includes its input mode.

`YuvPlaneCopyTest` verifies every visible sample for planar and interleaved chroma, varying row strides, nonzero buffer positions and padding, including a last row without trailing padding and rejection of truncated input before publication. All 37 Android unit tests and lint pass. The new YUV MediaCodec path has not yet been tested on the physical phone after installation, and eliminating the vendor failure for long sessions is not established by compilation or these copy tests. It cannot clear descriptors already leaked inside a separate OS driver process by an older capture session.
