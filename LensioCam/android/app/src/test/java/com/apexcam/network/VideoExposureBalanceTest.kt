package com.apexcam.network
import com.apexcam.camera.VideoExposureBalance
import org.junit.Assert.*
import org.junit.Test
class VideoExposureBalanceTest {
    @Test fun normalExposureStaysNeutralAndShutterCeilingNeedsTwoSamples(){
        val balance=VideoExposureBalance()
        repeat(8){assertEquals(0,balance.update(12_000_000,.333,6))}
        assertEquals(0,balance.update(33_000_000,.333,6))
        assertEquals(2,balance.update(33_000_000,.333,6))
        assertEquals(2,balance.update(24_000_000,.333,6))
        assertEquals(2,balance.update(12_000_000,.333,6))
        assertEquals(0,balance.update(12_000_000,.333,6))
    }
    @Test fun honorsCameraLimitsAndReset(){
        val balance=VideoExposureBalance()
        repeat(4){balance.update(33_000_000,.1,3)};assertEquals(3,balance.index)
        balance.reset();assertEquals(0,balance.index)
        repeat(4){assertEquals(0,balance.update(33_000_000,0.0,3))}
        repeat(4){assertEquals(0,balance.update(33_000_000,.333,0))}
    }
}
