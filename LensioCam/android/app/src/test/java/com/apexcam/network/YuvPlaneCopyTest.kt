package com.apexcam.network

import com.apexcam.camera.YuvPlaneCopy
import org.junit.Assert.*
import org.junit.Test
import java.nio.ByteBuffer

class YuvPlaneCopyTest {
    @Test fun planarAndInterleavedChromaKeepEveryVisibleSample(){
        for(pixel in listOf(1,2,3))for(width in listOf(1,7,64))for(height in listOf(1,3,17)){
            val stride=width*pixel+5;val prefix=3
            val span=(width-1)*pixel+1
            val source=ByteBuffer.allocateDirect(prefix+(height-1)*stride+span)
            for(y in 0 until height)for(x in 0 until width)source.put(prefix+y*stride+x*pixel,((x*41+y*17)and 255).toByte())
            source.position(prefix)
            val output=ByteBuffer.allocateDirect(2+height*(width+4))
            for(i in 0 until output.capacity())output.put(i,0x6a)
            output.position(2)
            YuvPlaneCopy.copy(source,stride,pixel,output,width+4,width,height,ByteArray(span))
            for(y in 0 until height){
                for(x in 0 until width)assertEquals(((x*41+y*17)and 255).toByte(),output.get(2+y*(width+4)+x))
                for(x in width until width+4)assertEquals(0x6a.toByte(),output.get(2+y*(width+4)+x))
            }
            assertEquals(prefix,source.position());assertEquals(2,output.position())
        }
    }
    @Test fun finalRowDoesNotRequireUnusedStridePadding(){
        val source=ByteBuffer.wrap(byteArrayOf(10,99,11,99,12,99,99,20,99,21,99,22))
        val out=ByteBuffer.allocate(6)
        YuvPlaneCopy.copy(source,7,2,out,3,3,2,ByteArray(5))
        assertArrayEquals(byteArrayOf(10,11,12,20,21,22),out.array())
    }
    @Test fun truncatedPlaneIsRejectedBeforePublishingAnyPixels(){
        val out=ByteBuffer.allocate(6)
        try{YuvPlaneCopy.copy(ByteBuffer.allocate(11),7,2,out,3,3,2,ByteArray(5));fail("Truncated plane accepted")}
        catch(_:IllegalArgumentException){assertArrayEquals(ByteArray(6),out.array())}
    }
}
