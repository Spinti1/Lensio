package com.apexcam.camera

/** One small counter, sampled once per second; timestamps stay in their own clock domain. */
internal class FrameRateProbe {
    private var start=0L
    private var last=0L
    private var count=0
    private var rate=0
    @Synchronized fun record(nowNs:Long){
        if(nowNs<=0)return
        if(last==0L||nowNs<last||nowNs-last>2_000_000_000L){start=nowNs;last=nowNs;count=0;rate=0;return}
        last=nowNs;count++
        val elapsed=nowNs-start
        if(elapsed>=1_000_000_000L){rate=kotlin.math.round(count*1_000_000_000.0/elapsed).toInt();count=0;start=nowNs}
    }
    @Synchronized fun value(nowNs:Long):Int=if(last==0L||nowNs<last||nowNs-last>2_000_000_000L)0 else rate
    @Synchronized fun reset(){start=0;last=0;count=0;rate=0}
}
