package com.apexcam.network
import com.apexcam.camera.VideoFpsPolicy
import org.junit.Assert.*
import org.junit.Test

class VideoFpsPolicyTest {
    @Test fun hardwareAndJpegKeepAutomaticExposureAtTheSameThirtyFpsTarget(){
        val ranges=listOf(30 to 30,15 to 30,7 to 30,15 to 15)
        assertEquals(15 to 30,VideoFpsPolicy.select(ranges,30))
        assertEquals(15 to 30,VideoFpsPolicy.select(ranges.reversed(),30))
        assertEquals(15 to 30,VideoFpsPolicy.select(ranges,30,false))
    }
    @Test fun honorsAdvertisedLimitsAndRejectedFixedRanges(){
        assertEquals(30 to 30,VideoFpsPolicy.select(listOf(30 to 30,60 to 60),30))
        assertEquals(10 to 24,VideoFpsPolicy.select(listOf(24 to 24,10 to 24,10 to 60),30))
        assertNull(VideoFpsPolicy.select(listOf(30 to 30),30,false))
        assertNull(VideoFpsPolicy.select(listOf(0 to 30,30 to 15,60 to 60),30))
        assertNull(VideoFpsPolicy.select(emptyList(),30))
    }
}
