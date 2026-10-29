package com.apexcam.network

import org.junit.Assert.*
import org.junit.Test
import java.nio.ByteBuffer

class YuvPackingTest {
    @Test fun planarPaddingAndPositionAreRespected() {
        val input = ByteBuffer.wrap(byteArrayOf(99,1,2,3,4,88,88,5,6,7,8))
        input.position(1)
        val out = ByteArray(8)
        YuvPacking.copyPlane(input,6,1,4,2,out,0,1)
        assertArrayEquals(byteArrayOf(1,2,3,4,5,6,7,8),out)
        assertEquals(1,input.position())
    }
    @Test fun interleavedChromaAndShortLastRowProduceNv21() {
        val u = ByteBuffer.wrap(byteArrayOf(10,99,20,99,88,88,30,99,40))
        val v = ByteBuffer.wrap(byteArrayOf(50,99,60,99,88,88,70,99,80))
        val out = ByteArray(24)
        YuvPacking.copyPlane(v,6,2,2,2,out,16,2)
        YuvPacking.copyPlane(u,6,2,2,2,out,17,2)
        assertArrayEquals(byteArrayOf(50,10,60,20,70,30,80,40),out.copyOfRange(16,24))
    }
    @Test fun planarChromaProducesSameNv21Order() {
        val out = ByteArray(8)
        YuvPacking.copyPlane(ByteBuffer.wrap(byteArrayOf(1,2,3,4)),2,1,2,2,out,0,2)
        YuvPacking.copyPlane(ByteBuffer.wrap(byteArrayOf(5,6,7,8)),2,1,2,2,out,1,2)
        assertArrayEquals(byteArrayOf(1,5,2,6,3,7,4,8),out)
    }
    @Test(expected=IllegalArgumentException::class)
    fun truncatedPlaneFailsBeforeCopy() {
        YuvPacking.copyPlane(ByteBuffer.wrap(byteArrayOf(1,2)),4,1,4,2,ByteArray(8),0,1)
    }
}
