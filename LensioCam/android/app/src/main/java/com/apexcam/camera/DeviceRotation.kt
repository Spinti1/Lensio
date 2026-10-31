package com.apexcam.camera

/** Physical device orientation with hysteresis; independent of Android's screen-rotation lock. */
internal object DeviceRotation {
    fun fromDegrees(degrees: Int, previous: Int): Int {
        if(degrees !in 0..359)return previous
        val oldDegrees=when(previous){1->270;2->180;3->90;else->0}
        val distance=kotlin.math.abs(degrees-oldDegrees).let { minOf(it,360-it) }
        if(distance<=60)return previous
        return when((degrees+45)/90%4){1->3;2->2;3->1;else->0}
    }
}
