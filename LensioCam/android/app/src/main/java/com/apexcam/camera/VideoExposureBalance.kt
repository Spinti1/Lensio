package com.apexcam.camera

/** Modest sensor-side compensation only while fixed-rate exposure is near its shutter ceiling. */
internal class VideoExposureBalance {
    private var dim=0
    private var bright=0
    var index=0;private set
    fun reset(){dim=0;bright=0;index=0}
    fun update(exposureNs:Long,stepEv:Double,maximum:Int):Int {
        if(stepEv<=0 || !stepEv.isFinite() || maximum<=0){reset();return 0}
        dim=if(exposureNs>=28_000_000L)dim+1 else 0
        bright=if(exposureNs in 1..20_000_000L)bright+1 else 0
        if(dim>=2)index=kotlin.math.round(.67/stepEv).toInt().coerceIn(0,maximum)
        else if(bright>=2)index=0
        return index
    }
}
