package com.apexcam.camera

import java.nio.ByteBuffer

/** Copies visible samples only; does not read nonexistent padding on the final row. */
internal object YuvPlaneCopy {
    fun copy(source:ByteBuffer,rowStride:Int,pixelStride:Int,dest:ByteBuffer,
             destStride:Int,width:Int,height:Int,scratch:ByteArray){
        require(width>0&&height>0&&pixelStride>0&&destStride>=width)
        val span=(width-1L)*pixelStride+1
        require(span<=rowStride&&span<=scratch.size)
        val src=source.duplicate();val dst=dest.duplicate()
        val start=src.position();val end=start.toLong()+(height-1L)*rowStride+span
        require(end<=src.limit()&&((height-1L)*destStride+width)<=dst.remaining())
        if(NativeYuvPlane.copy(source,rowStride,pixelStride,dest,destStride,width,height))return
        val outStart=dst.position()
        for(row in 0 until height){
            src.position(start+row*rowStride);dst.position(outStart+row*destStride)
            if(pixelStride==1){src.get(scratch,0,width);dst.put(scratch,0,width)}
            else{
                src.get(scratch,0,span.toInt())
                for(column in 0 until width)scratch[column]=scratch[column*pixelStride]
                dst.put(scratch,0,width)
            }
        }
    }
}
