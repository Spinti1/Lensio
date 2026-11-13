package com.apexcam.camera

import androidx.test.ext.junit.runners.AndroidJUnit4
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.nio.ByteBuffer

@RunWith(AndroidJUnit4::class)
class NativeYuvDeviceTest {
    @Test fun nativeCopyMatchesSamplesAndLeavesPaddingAndPositionsIntact(){
        assertTrue("Native YUV kernel did not load",NativeYuvPlane.available)
        for(pixel in 1..4)for(width in listOf(1,15,16,17,31,32,33,640,960)){
            val height=5;val span=(width-1)*pixel+1;val stride=span+7
            val source=ByteBuffer.allocateDirect(3+(height-1)*stride+span)
            val dest=ByteBuffer.allocateDirect(2+height*(width+5))
            for(i in 0 until source.capacity())source.put(i,(i*37).toByte())
            for(i in 0 until dest.capacity())dest.put(i,0x6a)
            source.position(3);dest.position(2)
            assertTrue(NativeYuvPlane.copy(source,stride,pixel,dest,width+5,width,height))
            for(y in 0 until height)for(x in 0 until width+5){
                assertEquals(if(x<width)source.get(3+y*stride+x*pixel)else 0x6a.toByte(),dest.get(2+y*(width+5)+x))
            }
            assertEquals(3,source.position());assertEquals(2,dest.position())
            assertFalse(NativeYuvPlane.copy(source,stride,pixel,dest.asReadOnlyBuffer(),width+5,width,height))
        }
    }
}
