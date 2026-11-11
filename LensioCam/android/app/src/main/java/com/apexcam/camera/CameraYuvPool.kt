package com.apexcam.camera

import androidx.camera.core.ImageProxy
import org.webrtc.JavaI420Buffer

/** Three reusable, full-resolution I420 allocations; none retains an ImageReader image. */
internal class CameraYuvPool {
    private class Slot(val data:JavaI420Buffer){var busy=false}
    private val lock=Any()
    private val slots=ArrayList<Slot>(3)
    private var closing=false
    private var scratch=ByteArray(0) // Only the camera analysis executor calls copy().

    fun copy(image:ImageProxy):JavaI420Buffer? {
        val width=image.width;val height=image.height
        require(width>0&&height>0&&width%2==0&&height%2==0&&width.toLong()*height<=1920*1080)
        val slot=synchronized(lock){
            if(closing)return null
            // CameraX can change negotiated dimensions after a rebind.
            val it=slots.iterator()
            while(it.hasNext()){
                val old=it.next()
                if(!old.busy&&(old.data.width!=width||old.data.height!=height)){old.data.release();it.remove()}
            }
            (slots.firstOrNull{!it.busy&&it.data.width==width&&it.data.height==height}
                ?: if(slots.size<3)Slot(JavaI420Buffer.allocate(width,height)).also{slots.add(it)} else return null)
                .also{it.busy=true}
        }
        try{
            require(image.planes.size==3)
            val data=slot.data
            val destinations=arrayOf(data.dataY,data.dataU,data.dataV)
            val strides=intArrayOf(data.strideY,data.strideU,data.strideV)
            for(index in 0..2){
                val plane=image.planes[index];val w=if(index==0)width else width/2;val h=if(index==0)height else height/2
                val needed=(w-1L)*plane.pixelStride+1
                require(needed in 1..(width*4L))
                if(scratch.size<needed)scratch=ByteArray(needed.toInt())
                YuvPlaneCopy.copy(plane.buffer,plane.rowStride,plane.pixelStride,destinations[index],strides[index],w,h,scratch)
            }
            return JavaI420Buffer.wrap(width,height,data.dataY,data.strideY,data.dataU,data.strideU,data.dataV,data.strideV){release(slot)}
        }catch(error:Throwable){release(slot);throw error}
    }
    private fun release(slot:Slot)=synchronized(lock){
        slot.busy=false
        if(closing){slot.data.release();slots.remove(slot)}
        Unit
    }
    fun close()=synchronized(lock){
        closing=true
        val it=slots.iterator()
        while(it.hasNext()){val slot=it.next();if(!slot.busy){slot.data.release();it.remove()}}
    }
}
