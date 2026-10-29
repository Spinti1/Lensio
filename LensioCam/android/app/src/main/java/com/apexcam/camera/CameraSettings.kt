package com.apexcam.camera

import android.util.Size

enum class CameraLensType {
    BACK_MAIN,      // Standard 1x Back Camera
    BACK_ULTRAWIDE, // 0.5x Ultra-Wide Back Camera
    FRONT_SELFIE    // Front Selfie Camera
}

data class CameraSettings(
    val resolution: Size = Size(1280, 720),
    val fps: Int = 30,
    val lensType: CameraLensType = CameraLensType.BACK_MAIN,
    val isFrontCamera: Boolean = false,
    val isTorchOn: Boolean = false,
    val zoomRatio: Float = 1.0f,
    val exposureIndex: Int = 0,
    val isAutoFocus: Boolean = true
) {
    companion object {
        val RESOLUTION_720P = Size(1280, 720)
        val RESOLUTION_1080P = Size(1920, 1080)
        val RESOLUTION_2K = Size(2560, 1440)
        val RESOLUTION_4K = Size(3840, 2160)

        fun parseResolution(resString: String): Size {
            val s = resString.trim().lowercase()
            return when {
                s.contains("4k") || s.contains("3840") || s.contains("2160") -> RESOLUTION_4K
                s.contains("2k") || s.contains("2560") || s.contains("1440") -> RESOLUTION_2K
                s.contains("1080") || s.contains("fhd") || s.contains("full") -> RESOLUTION_1080P
                s.contains("720") || s.contains("hd") -> RESOLUTION_720P
                else -> RESOLUTION_720P
            }
        }
    }
}
