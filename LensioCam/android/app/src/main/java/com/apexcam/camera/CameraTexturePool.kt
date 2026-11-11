package com.apexcam.camera

import android.graphics.Matrix
import android.opengl.GLES20
import android.os.Handler
import android.os.Looper
import org.webrtc.*

/** Full-size GPU copies: consumers never retain the camera's SurfaceTexture buffer. */
internal class CameraTexturePool(private val handler:Handler,private val width:Int,private val height:Int){
    private class Slot(val target:GlTextureFrameBuffer){var busy=false}
    private val slots=ArrayList<Slot>(3)
    private val drawer=GlRectDrawer()
    private val yuv=YuvConverter()
    private var closing=false
    private var released=false
    private var onClosed:(()->Unit)?=null
    fun copy(source:VideoFrame.TextureBuffer):VideoFrame.TextureBuffer? {
        check(Looper.myLooper()==handler.looper)
        if(closing)return null
        val slot=slots.firstOrNull{!it.busy} ?: if(slots.size<3){
            Slot(GlTextureFrameBuffer(GLES20.GL_RGBA).also{it.setSize(width,height)}).also{slots.add(it)}
        }else return null
        GLES20.glBindFramebuffer(GLES20.GL_FRAMEBUFFER,slot.target.frameBufferId)
        try{
            VideoFrameDrawer.drawTexture(drawer,source,Matrix(),width,height,0,0,width,height)
            // Complete this small copy before another context consumes the shared RGB
            // texture; no CPU pixel readback and no wait on MediaCodec's input surface.
            GLES20.glFinish()
            GlUtil.checkNoGLES2Error("Lensio camera texture copy")
        }finally{GLES20.glBindFramebuffer(GLES20.GL_FRAMEBUFFER,0)}
        slot.busy=true
        return TextureBufferImpl(width,height,VideoFrame.TextureBuffer.Type.RGB,slot.target.textureId,
            Matrix(),handler,yuv){
            val release=Runnable{slot.busy=false;finishClose()}
            if(Looper.myLooper()==handler.looper)release.run() else handler.post(release)
        }
    }
    fun close(complete:()->Unit){
        check(Looper.myLooper()==handler.looper)
        if(closing)return
        closing=true;onClosed=complete;finishClose()
    }
    private fun finishClose(){
        if(!closing||released||slots.any{it.busy})return
        released=true
        slots.forEach{it.target.release()};slots.clear();drawer.release();yuv.release()
        onClosed?.invoke();onClosed=null
    }
}
