package com.apexcam.camera

import android.content.Context
import android.hardware.camera2.CameraCharacteristics
import android.hardware.camera2.CameraManager
import android.hardware.camera2.CaptureRequest
import android.util.Log
import android.util.Range
import android.util.Size
import androidx.annotation.OptIn
import androidx.camera.camera2.interop.Camera2CameraInfo
import androidx.camera.camera2.interop.Camera2Interop
import androidx.camera.camera2.interop.ExperimentalCamera2Interop
import androidx.camera.core.*
import androidx.camera.core.resolutionselector.AspectRatioStrategy
import androidx.camera.core.resolutionselector.ResolutionSelector
import androidx.camera.core.resolutionselector.ResolutionStrategy
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.view.PreviewView
import androidx.core.content.ContextCompat
import androidx.lifecycle.LifecycleOwner
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors

@OptIn(ExperimentalCamera2Interop::class)
class CameraXManager(
    private val context: Context,
    private val lifecycleOwner: LifecycleOwner
) {
    private val tag = "CameraXManager"
    private var cameraProvider: ProcessCameraProvider? = null
    private var providerLoading = false
    private val readyCallbacks = mutableListOf<() -> Unit>()
    private var camera: Camera? = null
    private var preview: Preview? = null
    private var imageAnalysis: ImageAnalysis? = null
    private val cameraExecutor: ExecutorService = Executors.newSingleThreadExecutor()

    var settings: CameraSettings = CameraSettings()
        private set

    var onFrameAvailable: ((ImageProxy) -> Unit)? = null
    var onLensChanged: ((CameraLensType) -> Unit)? = null
    private var currentPreviewView: PreviewView? = null
    private var customCameraId: String? = null
    @Volatile private var activeCameraId: String? = null
    @Volatile private var cameraIds: Set<String> = emptySet()
    @Volatile private var stabilizationMode = "auto"
    @Volatile private var cameraSelectionError = ""
    @Volatile private var previousCameraSelection: Pair<String?,CameraSettings>? = null
    @Volatile private var selectionFrames = 0

    fun cameraCatalogJson(): String = com.google.gson.Gson().toJson(mapOf(
        "version" to com.apexcam.BuildConfig.VERSION_NAME,
        "deviceId" to context.getSharedPreferences("lensio_device",Context.MODE_PRIVATE).getString("id",""),
        "activeId" to activeCameraId,
        "zoom" to settings.zoomRatio,
        "torch" to settings.isTorchOn,
        "stabilization" to stabilizationMode,
        "error" to cameraSelectionError,
        "cameras" to CameraCatalog(context).read(findMainCameraId(), cameraIds)))

    fun setStabilization(mode: String) {
        val cam = camera ?: return
        val info = Camera2CameraInfo.from(cam.cameraInfo)
        val eis = info.getCameraCharacteristic(CameraCharacteristics.CONTROL_AVAILABLE_VIDEO_STABILIZATION_MODES) ?: intArrayOf()
        val ois = info.getCameraCharacteristic(CameraCharacteristics.LENS_INFO_AVAILABLE_OPTICAL_STABILIZATION) ?: intArrayOf()
        if (mode !in setOf("off","eis","ois","auto") ||
            (mode == "eis" && !eis.contains(1)) || (mode == "ois" && !ois.contains(1))) return
        val options = androidx.camera.camera2.interop.CaptureRequestOptions.Builder()
        if(eis.contains(0)) options.setCaptureRequestOption(CaptureRequest.CONTROL_VIDEO_STABILIZATION_MODE, if(mode == "eis") 1 else 0)
        if(ois.contains(0)) options.setCaptureRequestOption(CaptureRequest.LENS_OPTICAL_STABILIZATION_MODE, if(mode == "ois") 1 else 0)
        val update = androidx.camera.camera2.interop.Camera2CameraControl.from(cam.cameraControl).addCaptureRequestOptions(options.build())
        update.addListener({
            try { update.get(); stabilizationMode = mode; cameraSelectionError = "" }
            catch (_: Exception) { cameraSelectionError = "The camera rejected the stabilization mode" }
        }, ContextCompat.getMainExecutor(context))
    }
    @Volatile private var released = false
    val usesSharedPreview = android.os.Build.DEVICE.equals("a21s", true)
    @Volatile private var softwarePreview: android.widget.ImageView? = null
    val hasVisiblePreview: Boolean get() = softwarePreview != null && !released
    private val previewPending = java.util.concurrent.atomic.AtomicBoolean(false)
    private val previewExecutor = Executors.newSingleThreadExecutor { Thread(it, "Lensio-preview") }

    fun showEncodedPreview(jpeg: ByteArray) {
        val target = softwarePreview ?: return
        if (!usesSharedPreview || released || !previewPending.compareAndSet(false, true)) return
        try {
            // Decode independently of the encoder/socket worker. One pending frame avoids lag.
            previewExecutor.execute {
                try {
                    if (released || softwarePreview !== target) { previewPending.set(false); return@execute }
                    val options = android.graphics.BitmapFactory.Options().apply { inSampleSize = 2 }
                    val bitmap = android.graphics.BitmapFactory.decodeByteArray(jpeg,0,jpeg.size,options)
                    android.os.Handler(android.os.Looper.getMainLooper()).post {
                        try {
                            if (!released && softwarePreview === target) target.setImageBitmap(bitmap)
                            else bitmap?.recycle()
                        } finally { previewPending.set(false) }
                    }
                } catch (_: Exception) { previewPending.set(false) }
            }
        } catch (_: Exception) { previewPending.set(false) }
    }
    @Volatile private var lastCaptureAt = 0L
    private var lastRecoveryAt = 0L

    /** Recover capture starvation, not slow Wi-Fi. Called on the main thread. */
    fun checkCaptureHealth(streaming: Boolean) {
        val now = android.os.SystemClock.elapsedRealtime()
        if (!streaming || camera == null || lastCaptureAt == 0L || now-lastCaptureAt < 3500) return
        val previous = previousCameraSelection
        if(previous != null) {
            previousCameraSelection = null
            cameraSelectionError = "Selected camera stopped delivering frames; restored previous camera"
            customCameraId = previous.first
            settings = previous.second
            bindCamera(currentPreviewView)
            return
        }
        if(now-lastRecoveryAt < 15000) return
        lastRecoveryAt = now
        Log.w(tag, "Capture stalled; rebuilding CameraX use cases")
        bindCamera(currentPreviewView)
    }

    private fun deliverFrame(image: ImageProxy) {
        lastCaptureAt = android.os.SystemClock.elapsedRealtime()
        if(previousCameraSelection != null && ++selectionFrames >= 30) previousCameraSelection = null
        try { onFrameAvailable?.invoke(image) ?: image.close() }
        catch (error: Exception) {
            image.close()
            Log.w(tag, "Frame delivery failed", error)
        }
    }

    fun initialize(previewView: PreviewView, onReady: () -> Unit) {
        if (released) return
        if (currentPreviewView === previewView) { onReady(); return }
        this.currentPreviewView = previewView
        if (usesSharedPreview && softwarePreview == null) {
            softwarePreview = android.widget.ImageView(previewView.context).apply {
                scaleType = android.widget.ImageView.ScaleType.FIT_CENTER
            }
            previewView.addView(softwarePreview, android.widget.FrameLayout.LayoutParams(-1,-1))
        }
        if (cameraProvider != null) {
            if (!usesSharedPreview || camera == null) bindCamera()
            onReady()
        }
        else startCapture(onReady)
    }

    /** Capture belongs to the foreground service, even with no activity/viewfinder. */
    fun startCapture(onReady: () -> Unit = {}) {
        if (released) return
        if (cameraProvider != null) { if (camera == null) bindCamera(); onReady(); return }
        readyCallbacks.add(onReady)
        if (providerLoading) return
        providerLoading = true
        val cameraProviderFuture = ProcessCameraProvider.getInstance(context)
        cameraProviderFuture.addListener({
            providerLoading = false
            if (released) return@addListener
            try {
                cameraProvider = cameraProviderFuture.get()
                cameraIds = cameraProvider!!.availableCameraInfos.map { Camera2CameraInfo.from(it).cameraId }.toSet()
                bindCamera()
                readyCallbacks.toList().forEach { it() }
            } catch (error: Exception) { Log.e(tag,"Cannot initialize camera provider",error) }
            finally { readyCallbacks.clear() }
        }, ContextCompat.getMainExecutor(context))
    }

    fun detachPreview() {
        if (currentPreviewView == null) return
        softwarePreview?.setImageDrawable(null)
        softwarePreview = null
        currentPreviewView = null
        // A21s already uses one analysis stream; do not restart its HAL when the screen turns off.
        if (!usesSharedPreview) bindCamera()
    }

    fun findMainCameraId(): String? {
        cameraProvider?.let { provider ->
            runCatching { CameraSelector.DEFAULT_BACK_CAMERA.filter(provider.availableCameraInfos).firstOrNull() }
                .getOrNull()?.let { return Camera2CameraInfo.from(it).cameraId }
        }
        val cm=context.getSystemService(Context.CAMERA_SERVICE) as CameraManager
        return runCatching { cm.cameraIdList.firstOrNull {
            cm.getCameraCharacteristics(it).get(CameraCharacteristics.LENS_FACING)==CameraCharacteristics.LENS_FACING_BACK
        } }.getOrNull()
    }

    fun findUltraWideCameraId(): String? {
        val baseline=findMainCameraId() ?: return null
        val catalog=CameraCatalog(context).read(baseline,cameraIds)
        return catalog.filter { it["selectable"]==true && it["facing"]=="Back" &&
            (it["ratio"] as? Float ?: 1f)<.85f }.minByOrNull { it["ratio"] as? Float ?: 1f }?.get("id") as? String
    }

    private fun getOptimalFpsRange(info: CameraInfo?): Range<Int>? {
        if (info == null) return null
        return try {
            val ranges = Camera2CameraInfo.from(info)
                .getCameraCharacteristic(CameraCharacteristics.CONTROL_AE_AVAILABLE_TARGET_FPS_RANGES)
                ?: return null
            // A fixed 30/30 range limits exposure to ~33ms and darkens low-light scenes.
            ranges.filter { it.upper == settings.fps && it.lower < it.upper }.maxByOrNull { it.lower }
                ?: ranges.firstOrNull { it.lower == settings.fps && it.upper == settings.fps }
                ?: ranges.filter { it.upper <= settings.fps }.maxByOrNull { it.upper }
        } catch (e: Exception) {
            Log.w(tag, "Cannot read selected camera FPS capabilities; use driver defaults", e)
            null
        }
    }

    fun bindCamera(previewView: PreviewView? = currentPreviewView) {
        if (previewView != null) {
            currentPreviewView = previewView
        }
        if (released) return
        val pv = currentPreviewView
        val provider = cameraProvider ?: return
        lastCaptureAt = android.os.SystemClock.elapsedRealtime()
        imageAnalysis?.clearAnalyzer()
        provider.unbindAll()

        val cameraSelector = if (customCameraId != null) {
            val cid = customCameraId!!
            CameraSelector.Builder()
                .addCameraFilter { cameraInfos ->
                    cameraInfos.filter { info ->
                        val c2Info = Camera2CameraInfo.from(info)
                        c2Info.cameraId == cid
                    }
                }
                .build()
        } else {
            when (settings.lensType) {
                CameraLensType.FRONT_SELFIE -> CameraSelector.DEFAULT_FRONT_CAMERA
                CameraLensType.BACK_ULTRAWIDE -> {
                    val uwId = findUltraWideCameraId()
                    if (uwId != null) {
                        try {
                            CameraSelector.Builder()
                                .addCameraFilter { cameraInfos ->
                                    cameraInfos.filter { info ->
                                        val c2Info = Camera2CameraInfo.from(info)
                                        c2Info.cameraId == uwId
                                    }
                                }
                                .build()
                        } catch (e: Exception) {
                            CameraSelector.DEFAULT_BACK_CAMERA
                        }
                    } else {
                        CameraSelector.DEFAULT_BACK_CAMERA
                    }
                }
                CameraLensType.BACK_MAIN -> {
                    val mainId = findMainCameraId()
                    if (mainId != null) {
                        try {
                            CameraSelector.Builder()
                                .addCameraFilter { cameraInfos ->
                                    cameraInfos.filter { info ->
                                        val c2Info = Camera2CameraInfo.from(info)
                                        c2Info.cameraId == mainId
                                    }
                                }
                                .build()
                        } catch (e: Exception) {
                            CameraSelector.DEFAULT_BACK_CAMERA
                        }
                    } else {
                        CameraSelector.DEFAULT_BACK_CAMERA
                    }
                }
            }
        }

        // Clamp ImageAnalysis to at most 1080p (1920x1080) to prevent Android Camera2 HAL combination crashes
        val analysisSize = if (settings.resolution.width > 1920 || settings.resolution.height > 1080) {
            CameraSettings.RESOLUTION_1080P
        } else {
            settings.resolution
        }

        // Configure 16:9 Widescreen Resolution Selector
        val resolutionSelector = ResolutionSelector.Builder()
            .setAspectRatioStrategy(AspectRatioStrategy.RATIO_16_9_FALLBACK_AUTO_STRATEGY)
            .setResolutionStrategy(
                ResolutionStrategy(
                    settings.resolution,
                    ResolutionStrategy.FALLBACK_RULE_CLOSEST_LOWER_THEN_HIGHER
                )
            )
            .build()

        val analysisResolutionSelector = ResolutionSelector.Builder()
            .setAspectRatioStrategy(AspectRatioStrategy.RATIO_16_9_FALLBACK_AUTO_STRATEGY)
            .setResolutionStrategy(
                ResolutionStrategy(
                    analysisSize,
                    ResolutionStrategy.FALLBACK_RULE_CLOSEST_LOWER_THEN_HIGHER
                )
            )
            .build()

        val selectedInfo = runCatching { cameraSelector.filter(provider.availableCameraInfos).firstOrNull() }.getOrNull()
        val targetFpsRange = getOptimalFpsRange(selectedInfo)
        val focusModes = runCatching { selectedInfo?.let { Camera2CameraInfo.from(it)
            .getCameraCharacteristic(CameraCharacteristics.CONTROL_AF_AVAILABLE_MODES) } }.getOrNull()
        val focusMode = when {
            focusModes?.contains(CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_VIDEO) == true -> CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_VIDEO
            focusModes?.contains(CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_PICTURE) == true -> CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_PICTURE
            else -> null
        }
        Log.i(tag, "Selected camera=" + selectedInfo?.let { Camera2CameraInfo.from(it).cameraId } +
            " fps=" + targetFpsRange + " focus=" + focusMode)

        val previewBuilder = Preview.Builder()
            .setResolutionSelector(resolutionSelector)
        try {
            Camera2Interop.Extender(previewBuilder)
                .apply { targetFpsRange?.let { setCaptureRequestOption(CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE, it) } }
                .setCaptureRequestOption(CaptureRequest.CONTROL_AE_MODE, CaptureRequest.CONTROL_AE_MODE_ON)
                .apply { focusMode?.let { setCaptureRequestOption(CaptureRequest.CONTROL_AF_MODE, it) } }
                .setCaptureRequestOption(CaptureRequest.CONTROL_AWB_MODE, CaptureRequest.CONTROL_AWB_MODE_AUTO)
        } catch (e: Exception) { }

        preview = previewBuilder.build().also {
            if (pv != null) it.setSurfaceProvider(pv.surfaceProvider)
        }

        val analysisBuilder = ImageAnalysis.Builder()
            .setResolutionSelector(analysisResolutionSelector)
            .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST)
            .setOutputImageFormat(ImageAnalysis.OUTPUT_IMAGE_FORMAT_YUV_420_888)
        try {
            Camera2Interop.Extender(analysisBuilder)
                .apply { targetFpsRange?.let { setCaptureRequestOption(CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE, it) } }
                .setCaptureRequestOption(CaptureRequest.CONTROL_AE_MODE, CaptureRequest.CONTROL_AE_MODE_ON)
                .apply { focusMode?.let { setCaptureRequestOption(CaptureRequest.CONTROL_AF_MODE, it) } }
                .setCaptureRequestOption(CaptureRequest.CONTROL_AWB_MODE, CaptureRequest.CONTROL_AWB_MODE_AUTO)
        } catch (e: Exception) { }

        imageAnalysis = analysisBuilder.build().also { analysis ->
            analysis.setAnalyzer(cameraExecutor) { imageProxy ->
                deliverFrame(imageProxy)
            }
        }

        try {
            camera = provider.bindToLifecycle(
                lifecycleOwner,
                cameraSelector,
                *if (usesSharedPreview || pv == null) arrayOf(imageAnalysis!!) else arrayOf(preview!!, imageAnalysis!!)
            )
            try {
                camera?.let { cam ->
                    val c2Control = androidx.camera.camera2.interop.Camera2CameraControl.from(cam.cameraControl)
                    val opts = androidx.camera.camera2.interop.CaptureRequestOptions.Builder()
                        .apply { targetFpsRange?.let { setCaptureRequestOption(CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE, it) } }
                        .setCaptureRequestOption(CaptureRequest.CONTROL_AE_MODE, CaptureRequest.CONTROL_AE_MODE_ON)
                        .apply { focusMode?.let { setCaptureRequestOption(CaptureRequest.CONTROL_AF_MODE, it) } }
                        .setCaptureRequestOption(CaptureRequest.CONTROL_AWB_MODE, CaptureRequest.CONTROL_AWB_MODE_AUTO)
                        .build()
                    c2Control.setCaptureRequestOptions(opts)
                }
            } catch (e: Exception) { }
            applySettings()
            Log.d(tag, "CameraX conectată cu succes: Rezoluție ${settings.resolution}, Tip=${settings.lensType}, FPS=$targetFpsRange")
        } catch (exc: Exception) {
            Log.w(tag, "Error la legarea camerei cu rezoluția ${settings.resolution}: ${exc.message}. Se încearcă fallback la 720p.")
            if (settings.resolution != CameraSettings.RESOLUTION_720P) {
                try {
                    settings = settings.copy(resolution = CameraSettings.RESOLUTION_720P)
                    val fallbackSelector = ResolutionSelector.Builder()
                        .setAspectRatioStrategy(AspectRatioStrategy.RATIO_16_9_FALLBACK_AUTO_STRATEGY)
                        .setResolutionStrategy(
                            ResolutionStrategy(
                                CameraSettings.RESOLUTION_720P,
                                ResolutionStrategy.FALLBACK_RULE_CLOSEST_HIGHER_THEN_LOWER
                            )
                        )
                        .build()
                    val fbPrevBuilder = Preview.Builder().setResolutionSelector(fallbackSelector)
                    try {
                        Camera2Interop.Extender(fbPrevBuilder)
                            .apply { targetFpsRange?.let { setCaptureRequestOption(CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE, it) } }
                            .setCaptureRequestOption(CaptureRequest.CONTROL_AE_MODE, CaptureRequest.CONTROL_AE_MODE_ON)
                            .apply { focusMode?.let { setCaptureRequestOption(CaptureRequest.CONTROL_AF_MODE, it) } }
                            .setCaptureRequestOption(CaptureRequest.CONTROL_AWB_MODE, CaptureRequest.CONTROL_AWB_MODE_AUTO)
                    } catch (e: Exception) { }
                    preview = fbPrevBuilder.build().also { if (pv != null) it.setSurfaceProvider(pv.surfaceProvider) }

                    val fbAnaBuilder = ImageAnalysis.Builder().setResolutionSelector(fallbackSelector)
                        .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST)
                        .setOutputImageFormat(ImageAnalysis.OUTPUT_IMAGE_FORMAT_YUV_420_888)
                    try {
                        Camera2Interop.Extender(fbAnaBuilder)
                            .apply { targetFpsRange?.let { setCaptureRequestOption(CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE, it) } }
                            .setCaptureRequestOption(CaptureRequest.CONTROL_AE_MODE, CaptureRequest.CONTROL_AE_MODE_ON)
                            .apply { focusMode?.let { setCaptureRequestOption(CaptureRequest.CONTROL_AF_MODE, it) } }
                            .setCaptureRequestOption(CaptureRequest.CONTROL_AWB_MODE, CaptureRequest.CONTROL_AWB_MODE_AUTO)
                    } catch (e: Exception) { }
                    imageAnalysis = fbAnaBuilder.build().also { analysis ->
                        analysis.setAnalyzer(cameraExecutor) { imageProxy -> deliverFrame(imageProxy) }
                    }
                    camera = provider.bindToLifecycle(lifecycleOwner, cameraSelector,
                        *if (usesSharedPreview || pv == null) arrayOf(imageAnalysis!!) else arrayOf(preview!!, imageAnalysis!!))
                    try {
                        camera?.let { cam ->
                            val c2Control = androidx.camera.camera2.interop.Camera2CameraControl.from(cam.cameraControl)
                            val opts = androidx.camera.camera2.interop.CaptureRequestOptions.Builder()
                                .apply { targetFpsRange?.let { setCaptureRequestOption(CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE, it) } }
                                .setCaptureRequestOption(CaptureRequest.CONTROL_AE_MODE, CaptureRequest.CONTROL_AE_MODE_ON)
                                .apply { focusMode?.let { setCaptureRequestOption(CaptureRequest.CONTROL_AF_MODE, it) } }
                                .setCaptureRequestOption(CaptureRequest.CONTROL_AWB_MODE, CaptureRequest.CONTROL_AWB_MODE_AUTO)
                                .build()
                            c2Control.setCaptureRequestOptions(opts)
                        }
                    } catch (e: Exception) { }
                    applySettings()
                    Log.d(tag, "Fallback CameraX la 720p a reușit!")
                } catch (e2: Exception) {
                    Log.e(tag, "Error critică la fallback CameraX", e2)
                }
            }
        }
    }

    private fun applySettings() {
        activeCameraId = camera?.let { Camera2CameraInfo.from(it.cameraInfo).cameraId }
        stabilizationMode = "auto"
        val control = camera?.cameraControl ?: return
        val info = camera?.cameraInfo
        val minZoom = info?.zoomState?.value?.minZoomRatio ?: 1.0f
        val maxZoom = info?.zoomState?.value?.maxZoomRatio ?: 5.0f

        if (customCameraId != null) {
            control.setZoomRatio(settings.zoomRatio.coerceIn(minZoom,maxZoom))
        } else if (settings.lensType == CameraLensType.BACK_ULTRAWIDE) {
            // Optical or logical 0.5x ultra-wide
            val targetZoom = if (minZoom < 1.0f) minZoom else 0.5f
            control.setZoomRatio(targetZoom.coerceIn(minZoom, maxZoom))
        } else if (settings.lensType == CameraLensType.BACK_MAIN) {
            control.setZoomRatio(settings.zoomRatio.coerceIn(1.0f, maxZoom))
        } else {
            control.setZoomRatio(settings.zoomRatio.coerceIn(minZoom, maxZoom))
        }

        if (settings.isTorchOn) {
            setTorchState(true)
        }
        control.setExposureCompensationIndex(settings.exposureIndex)
    }

    fun setCameraLens(lens: CameraLensType, previewView: PreviewView? = currentPreviewView) {
        if (camera != null && customCameraId == null && settings.lensType == lens) return
        customCameraId = null
        settings = settings.copy(
            lensType = lens,
            isFrontCamera = (lens == CameraLensType.FRONT_SELFIE),
            zoomRatio = if (lens == CameraLensType.BACK_ULTRAWIDE) 0.5f else 1.0f
        )
        bindCamera(previewView)
        onLensChanged?.invoke(lens)
    }

    fun setDirectCameraId(targetId: String, previewView: PreviewView? = currentPreviewView) {
        if (camera != null && activeCameraId == targetId) return
        val entry = CameraCatalog(context).read(findMainCameraId(), cameraIds).firstOrNull { it["id"] == targetId }
        if (entry?.get("selectable") != true) {
            cameraSelectionError = "Camera $targetId cannot provide a color stream"
            return
        }
        val previousId = customCameraId
        val previousSettings = settings
        val front = entry["facing"] == "Front"
        val lens = if(front) CameraLensType.FRONT_SELFIE else CameraLensType.BACK_MAIN
        customCameraId = targetId
        settings = settings.copy(lensType=lens, isFrontCamera=front, zoomRatio=1f, isTorchOn=false)
        cameraSelectionError = ""
        selectionFrames = 0
        previousCameraSelection = Pair(previousId,previousSettings)
        camera = null
        bindCamera(previewView)
        if(camera == null) {
            previousCameraSelection = null
            cameraSelectionError = "Unable to open camera $targetId; restored previous camera"
            customCameraId = previousId
            settings = previousSettings
            bindCamera(previewView)
        }
        onLensChanged?.invoke(settings.lensType)
    }

    fun setZoom(ratio: Float) {
        settings = settings.copy(zoomRatio = ratio)
        camera?.cameraControl?.setZoomRatio(ratio)
    }

    fun setTorchState(enabled: Boolean): Boolean {
        settings = settings.copy(isTorchOn = enabled)
        var applied = false

        // 1. First try CameraX on currently active camera
        try {
            if (camera?.cameraInfo?.hasFlashUnit() == true) {
                camera?.cameraControl?.enableTorch(enabled)
                applied = true
            }
        } catch (e: Exception) {
            Log.w(tag, "CameraX enableTorch eroare: ${e.message}")
        }

        // 2. Fallback to CameraManager.setTorchMode (esential pentru 0.5x Ultra-Wide unde blitz-ul fizic este pe senzorul principal)
        if (!applied) {
            try {
                val cm = context.getSystemService(Context.CAMERA_SERVICE) as CameraManager
                for (id in cm.cameraIdList) {
                    val chars = cm.getCameraCharacteristics(id)
                    val facing = chars.get(CameraCharacteristics.LENS_FACING)
                    val hasFlash = chars.get(CameraCharacteristics.FLASH_INFO_AVAILABLE) ?: false
                    if (facing == CameraCharacteristics.LENS_FACING_BACK && hasFlash) {
                        cm.setTorchMode(id, enabled)
                        applied = true
                        Log.d(tag, "Lanterna aprinsa cu succes prin CameraManager pe camera ID $id (stare=$enabled)")
                        break
                    }
                }
            } catch (e: Exception) {
                Log.w(tag, "CameraManager setTorchMode eroare: ${e.message}")
            }
        }
        return enabled
    }

    fun toggleTorch(): Boolean {
        val newState = !settings.isTorchOn
        return setTorchState(newState)
    }

    fun switchCamera(previewView: PreviewView? = currentPreviewView): CameraLensType {
        val next = when (settings.lensType) {
            CameraLensType.BACK_MAIN -> CameraLensType.FRONT_SELFIE
            CameraLensType.FRONT_SELFIE -> CameraLensType.BACK_MAIN
            CameraLensType.BACK_ULTRAWIDE -> CameraLensType.BACK_MAIN
        }
        setCameraLens(next, previewView)
        return next
    }

    fun setResolution(res: Size, previewView: PreviewView? = currentPreviewView) {
        if (settings.resolution == res && camera != null) {
            Log.d(tag, "Rezoluția este deja $res, se omite re-bind.")
            return
        }
        settings = settings.copy(resolution = res)
        bindCamera(previewView)
    }

    fun setExposure(index: Int) {
        settings = settings.copy(exposureIndex = index)
        camera?.cameraControl?.setExposureCompensationIndex(index)
    }

    fun focusAt(x: Float, y: Float, previewView: PreviewView? = currentPreviewView) {
        val pv = previewView ?: currentPreviewView ?: return
        val factory = pv.meteringPointFactory
        val point = factory.createPoint(x, y)
        val action = FocusMeteringAction.Builder(point, FocusMeteringAction.FLAG_AF)
            .setAutoCancelDuration(3, java.util.concurrent.TimeUnit.SECONDS)
            .build()
        camera?.cameraControl?.startFocusAndMetering(action)
    }

    fun release() {
        released = true
        readyCallbacks.clear()
        softwarePreview?.setImageDrawable(null)
        softwarePreview = null
        imageAnalysis?.clearAnalyzer()
        cameraProvider?.unbindAll()
        cameraExecutor.shutdown()
        previewExecutor.shutdownNow()
        onFrameAvailable = null
        onLensChanged = null
        currentPreviewView = null
        camera = null
    }
}
