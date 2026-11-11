package com.apexcam.camera

/** Shared lifecycle contract for texture and CPU-readable hardware-encoder inputs. */
internal interface HardwareCapture {
    val diagnostics:String
    val outputStalled:Boolean
    val outputHealthy:Boolean
    fun requestKeyframe()
    fun setRotation(degrees:Int)
    fun close()
    fun awaitReleased(timeoutMs:Long):Boolean
}
