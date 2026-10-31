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