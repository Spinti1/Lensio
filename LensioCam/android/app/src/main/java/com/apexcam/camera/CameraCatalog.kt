package com.apexcam.camera

import android.content.Context
import android.graphics.ImageFormat
import android.hardware.camera2.CameraCharacteristics as C
import android.hardware.camera2.CameraManager
import android.os.Build
import java.util.Locale

/** Describes what the ROM actually exposes; never invents camera IDs or optical zoom. */
class CameraCatalog(context: Context) {
    private val manager = context.getSystemService(Context.CAMERA_SERVICE) as CameraManager
    fun read(mainId: String?, availableIds: Set<String>): List<Map<String, Any?>> {
        val publicIds = manager.cameraIdList.toSet()
        val allIds = publicIds.intersect(availableIds)
        val parents = emptyMap<String,String>()
        val main = mainId?.let { runCatching { manager.getCameraCharacteristics(it) }.getOrNull() }
        fun equivalent(c: android.hardware.camera2.CameraCharacteristics?): Float? {
            val focal = c?.get(C.LENS_INFO_AVAILABLE_FOCAL_LENGTHS)?.firstOrNull() ?: return null
            val sensor = c.get(C.SENSOR_INFO_PHYSICAL_SIZE) ?: return null
            return if (sensor.width > 0 && focal > 0) focal / sensor.width else null
        }
        val baseline = equivalent(main)
        return allIds.sorted().map { id ->
            try {
                val c = manager.getCameraCharacteristics(id)
                val facing = when(c.get(C.LENS_FACING)) {
                    C.LENS_FACING_FRONT -> "Front"
                    C.LENS_FACING_BACK -> "Back"
                    else -> "External"
                }
                val depth = c.get(C.REQUEST_AVAILABLE_CAPABILITIES)?.contains(C.REQUEST_AVAILABLE_CAPABILITIES_DEPTH_OUTPUT) == true
                val yuv = c.get(C.SCALER_STREAM_CONFIGURATION_MAP)?.getOutputSizes(ImageFormat.YUV_420_888)
                val selectable = id in publicIds && id in availableIds && !yuv.isNullOrEmpty()
                val ratio = if(facing == "Back" && baseline != null) equivalent(c)?.div(baseline) else null
                val pixels = c.get(C.SENSOR_INFO_PIXEL_ARRAY_SIZE)
                val mp = if(pixels != null) pixels.width.toDouble()*pixels.height/1000000.0 else null
                val focal = c.get(C.LENS_INFO_AVAILABLE_FOCAL_LENGTHS)?.firstOrNull()
                val eis = c.get(C.CONTROL_AVAILABLE_VIDEO_STABILIZATION_MODES)?.contains(1) == true
                val ois = c.get(C.LENS_INFO_AVAILABLE_OPTICAL_STABILIZATION)?.contains(1) == true
                val kind = when {
                    id == mainId -> "Main"
                    depth -> "Depth-capable"
                    facing == "Front" -> "Selfie"
                    ratio != null && ratio < .85f -> "Wide"
                    ratio != null && ratio > 1.2f -> "Narrow"
                    else -> "Auxiliary"
                }
                val zoom = if(id == mainId) "1x" else ratio?.let { String.format(Locale.US,"~%.1fx",it) } ?: ""
                val label = listOf(zoom,facing,kind,"[ID $id]").filter { it.isNotEmpty() }.joinToString(" ")
                mapOf("id" to id, "label" to label, "facing" to facing, "ratio" to ratio,
                    "megapixels" to mp, "focalMm" to focal, "eis" to eis, "ois" to ois,
                    "maxDigitalZoom" to (c.get(C.SCALER_AVAILABLE_MAX_DIGITAL_ZOOM) ?: 1f),
                    "flash" to (c.get(C.FLASH_INFO_AVAILABLE) == true),
                    "selectable" to selectable, "physicalParent" to parents[id],
                    "reason" to when {
                        id !in publicIds -> "Physical member of logical camera ${parents[id]}; not independently openable"
                        yuv.isNullOrEmpty() -> "No color YUV stream exposed by the ROM"
                        id !in availableIds -> "Not exposed to CameraX by the ROM"
                        else -> ""
                    })
            } catch (e: Exception) {
                mapOf("id" to id, "label" to "Camera [ID $id]", "selectable" to false,
                    "reason" to "Camera characteristics unavailable")
            }
        }
    }
}
