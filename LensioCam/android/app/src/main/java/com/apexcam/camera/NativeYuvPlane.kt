package com.apexcam.camera

import java.nio.ByteBuffer

/** Optional exact-copy kernel; the existing Kotlin copy remains the fallback. */
internal object NativeYuvPlane {
    @Volatile var available=runCatching{System.loadLibrary("lensio-yuv")}.isSuccess
        private set
    fun copy(source:ByteBuffer,rowStride:Int,pixelStride:Int,dest:ByteBuffer,destStride:Int,width:Int,height:Int):Boolean {
        if(!available||!source.isDirect||!dest.isDirect||dest.isReadOnly)return false
        return try{copyPlane(source.slice(),rowStride,pixelStride,dest.slice(),destStride,width,height)}
        catch(_:UnsatisfiedLinkError){available=false;false}
    }
    private external fun copyPlane(source:ByteBuffer,rowStride:Int,pixelStride:Int,dest:ByteBuffer,destStride:Int,width:Int,height:Int):Boolean
}
