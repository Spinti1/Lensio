# Scene performance

Static overlays should not force large source images to be resampled for every camera frame. LensioStudio caches each image at its output size, including opacity, using high-quality bicubic interpolation. The source image remains intact. Movement reuses the cache; image replacement, resize and opacity edits invalidate it. Cache dimensions are bounded; unusually large transforms use the existing renderer. Disposing a source releases its cache.

The header shows two independent rates:

- **Camera FPS** counts newly rendered main-camera frames. Repeated frames do not increment it; a stream with no new frames reports zero after two seconds.
- **Scene FPS** counts completed scene compositions, including frames with unchanged camera content.

These are local rendering measurements, not a guarantee of capture or delivery rates in another app.

## Local validation for 2.9.6

A Windows composition benchmark used a changing 1280 x 720 camera source with three transparent 4000 x 3000 image overlays, each rendered at 500 x 375 scene units. After warmup, 90 compositions averaged 424.95 ms before the image cache and 3.67 ms after it on the same PC. Original image resolution, interpolation and camera settings were unchanged.

A separate 60-second synthetic pipeline test submitted 1,800 changing frames at 30 FPS with the same overlays. It exercised device-frame processing, filtering, composition, preview drawing and workspace saving. It reported 30 Camera FPS, 13.52 ms average work per frame, 4.3 MB private-memory growth and no increase in GDI objects. This is a bounded local test, not a long-duration physical-phone test or a performance guarantee for other hardware.

`tests/ImageCacheSmoke.cs` checks transparent compositing, cache reuse and invalidation, and stale-camera FPS reporting. `tests/SceneSoakBenchmark.cs` is the optional 60-second pipeline check. Compile it against the built LensioStudio.exe with the .NET Framework C# compiler and references to System, System.Core, System.Drawing, System.Windows.Forms and System.Web.Extensions; place the resulting test EXE beside LensioStudio.exe. It uses isolated synthetic devices and a unique workspace beneath `test-data`, not the user's saved layout.

## Editor and local capture in 2.10.0

The compositor reuses two output surfaces rather than allocating a new full-frame bitmap on every tick. A frame revision invalidates the preview cache even when the same surface is reused. Preview painting uses a nonblocking frame-lock attempt: if a swap is in progress it draws the cached preview and the current selection outline, without queueing more work. The existing full-resolution camera interpolation and image cache remain in use.

Hidden properties are built when their dialog opens. Dragging does not rebuild those controls or write the workspace; the completed drag saves once. Snapping uses one snapshot of other sources for each gesture. Neither transforms nor magnetic guides send requests to a phone.

Windows webcams use a local DirectShow capture graph, RGB frame conversion and a single pending-frame slot. New samples replace stale pending frames rather than accumulating a queue. Filter/compositor work runs outside the DirectShow callback. Each device has one capture owner, including repeated Add actions; removal and shutdown stop the graph. Mode negotiation follows Microsoft's [capture-format guidance](https://learn.microsoft.com/en-us/windows/win32/directshow/configure-the-video-output-format); callback work is kept separate from scene processing as described in [Sample Grabber guidance](https://learn.microsoft.com/en-us/windows/win32/directshow/using-the-sample-grabber).

The same 60-second synthetic pipeline check maintained 30 Camera FPS after these changes. One run averaged 9.93 ms work per frame; a repeat while the Android build was running averaged 12.54 ms, with 4.3 MB private-memory growth and no GDI-object growth. Results vary with system load. The image sources, output resolution, interpolation and camera target FPS were unchanged.

`tests/SourceEditingSmoke.cs` checks magnetic alignment, cropped/rotated pixels and hit testing at 720p/1080p, shortcut repeat suppression, saved transforms, surface reuse and nonblocking preview. `tests/LocalCameraSmoke.cs` and `tests/LocalCameraStudioSmoke.cs` are optional hardware checks: capture/reopen, scene ingestion, duplicate prevention, saved reconnect and release on source removal. They require an available webcam and normal Windows camera permissions. Hardware checks do not assert that every camera actually delivers its negotiated 30 FPS.

## Preview synchronization in 2.11.1

Preview scaling now retains its input surface and releases the publication lock before processing pixels. The compositor can publish newer frames while a slow preview is still reading an older one. A retained surface cannot be overwritten or disposed; it returns to the bounded reusable surface pool after the reader releases it. Full-resolution output and interpolation remain unchanged.

`tests/ScenePipelineSmoke.cs` checks neutral filters after startup and resizing, holds a preview reader while scene publication continues, and exercises a 1080p scene with eight large image overlays for 15 seconds. The development PC measured 30 Scene FPS and 29–30 Camera FPS with a synthetic 30 FPS camera after warmup. Running this benchmark alongside an Android build produced lower rates; it is a local performance check, not a phone or system-load guarantee.
