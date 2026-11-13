package com.apexcam.network

import com.apexcam.camera.FrameRateProbe
import org.junit.Assert.*
import org.junit.Test

class FrameRateProbeTest {
    @Test fun countsIntervalsAndExpiresOnStall(){
        val probe=FrameRateProbe();val start=10_000_000_000L
        probe.record(start)
        for(i in 1..30)probe.record(start+i*1_000_000_000L/30)
        assertEquals(30,probe.value(start+1_000_000_000L))
        assertEquals(0,probe.value(start+3_000_000_001L))
        probe.record(start+5_000_000_000L);assertEquals(0,probe.value(start+5_000_000_000L))
    }
    @Test fun sourceRatesRemainIndependentAndResetDoesNotInventFrames(){
        val sensor=FrameRateProbe();val encoded=FrameRateProbe();val start=1_000_000_000L
        for(i in 0..60){val now=start+i*1_000_000_000L/30;sensor.record(now);if(i%2==0)encoded.record(now)}
        assertEquals(30,sensor.value(start+2_000_000_000L));assertEquals(15,encoded.value(start+2_000_000_000L))
        sensor.reset();assertEquals(0,sensor.value(start+2_000_000_000L))
    }
}
