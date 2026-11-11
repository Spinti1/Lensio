package com.apexcam.camera

import android.graphics.Matrix
import android.opengl.GLES20
import android.os.Handler
import android.os.HandlerThread
import androidx.test.ext.junit.runners.AndroidJUnit4
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.webrtc.*
import java.nio.ByteBuffer
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicReference

@RunWith(AndroidJUnit4::class)
class CameraTexturePoolDeviceTest {
    @Test fun copiesPreservePixelsAndDoNotRetainTheCameraOrDestroyHeldTextures(){
        val thread=HandlerThread("Lensio-GPU-test").apply{start()};val handler=Handler(thread.looper)
        val done=CountDownLatch(1);val error=AtomicReference<Throwable?>()
        handler.post {
            var egl:EglBase?=null
            try{
                val context=EglBase.create();egl=context;context.createDummyPbufferSurface();context.makeCurrent()
                val sourceFbo=GlTextureFrameBuffer(GLES20.GL_RGBA);sourceFbo.setSize(8,8)
                GLES20.glBindFramebuffer(GLES20.GL_FRAMEBUFFER,sourceFbo.frameBufferId)
                GLES20.glEnable(GLES20.GL_SCISSOR_TEST)
                for(y in 0..1)for(x in 0..1){GLES20.glScissor(x*4,y*4,4,4);GLES20.glClearColor(x.toFloat(),y.toFloat(),.25f,1f);GLES20.glClear(GLES20.GL_COLOR_BUFFER_BIT)}
                GLES20.glDisable(GLES20.GL_SCISSOR_TEST);GLES20.glFinish()
                fun pixels():ByteArray{val data=ByteBuffer.allocateDirect(8*8*4);GLES20.glReadPixels(0,0,8,8,GLES20.GL_RGBA,GLES20.GL_UNSIGNED_BYTE,data);return ByteArray(data.capacity()).also{data.get(it)}}
                val expected=pixels();GLES20.glBindFramebuffer(GLES20.GL_FRAMEBUFFER,0)
                var sourceReleased=false;val yuv=YuvConverter()
                val source=TextureBufferImpl(8,8,VideoFrame.TextureBuffer.Type.RGB,sourceFbo.textureId,Matrix(),handler,yuv){sourceReleased=true}
                val pool=CameraTexturePool(handler,8,8)
                val copies=List(3){pool.copy(source)!!};assertNull("GPU pool must stay bounded",pool.copy(source))
                source.release();assertTrue("Consumers retained the camera texture",sourceReleased)
                val readback=IntArray(1);GLES20.glGenFramebuffers(1,readback,0)
                for(copy in copies){GLES20.glBindFramebuffer(GLES20.GL_FRAMEBUFFER,readback[0]);GLES20.glFramebufferTexture2D(GLES20.GL_FRAMEBUFFER,GLES20.GL_COLOR_ATTACHMENT0,GLES20.GL_TEXTURE_2D,copy.textureId,0);assertArrayEquals(expected,pixels())}
                GLES20.glBindFramebuffer(GLES20.GL_FRAMEBUFFER,0);GLES20.glDeleteFramebuffers(1,readback,0)
                var closed=false;pool.close{closed=true}
                assertFalse("Held GPU textures were destroyed",closed)
                copies.forEach{it.release()};assertTrue("GPU resources did not close after final consumer release",closed)
                sourceFbo.release();yuv.release()
            }catch(failure:Throwable){error.set(failure)}finally{egl?.release();done.countDown()}
        }
        assertTrue("GPU test stalled",done.await(20,TimeUnit.SECONDS));thread.quitSafely();thread.join(2000)
        error.get()?.let{throw AssertionError("Device GPU test failed",it)}
    }
}
