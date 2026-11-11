package com.apexcam.camera

import android.os.Handler
import android.os.HandlerThread
import android.os.Looper
import android.os.SystemClock
import androidx.camera.core.ImageProxy
import org.webrtc.*
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean

/** Hardware AVC without a camera SurfaceTexture/release-fence round trip. */
internal class HardwareYuvCapture(
    private val predecessor:HardwareCapture?,
    initialRotation:Int,
    private val preview:()->VideoSink?,
    private val onFrame:()->Unit,
    private val onEncoded:(ByteArray,Int,Int,Int,Boolean,Long)->Unit,
    private val onFailure:(String,Boolean)->Unit
):HardwareCapture {
    private val closed=AtomicBoolean(false)
    private val failurePosted=AtomicBoolean(false)
    private val scheduled=AtomicBoolean(false)
    private val keyframe=AtomicBoolean(true)
    private val released=CountDownLatch(1)
    private val pool=CameraYuvPool()
    private val pending=LatestFrameMailbox<VideoFrame>{it.release()}
    private val thread=HandlerThread("Lensio-AVC-YUV").apply{start()}
    private val handler=Handler(thread.looper)
    private var encoder:VideoEncoder?=null
    private var width=0;private var height=0
    private var failures=0;private var lastKeyframeAt=0L
    @Volatile private var rotation=initialRotation
    @Volatile private var inputFrames=0L
    @Volatile private var outputFrames=0L
    @Volatile private var drops=0L
    @Volatile private var hasOutput=false
    @Volatile private var lastOutputAt=SystemClock.elapsedRealtime()
    override val outputStalled:Boolean get()=SystemClock.elapsedRealtime()-lastOutputAt>=if(hasOutput)6000 else 10000
    override val outputHealthy:Boolean get()=hasOutput&&!closed.get()&&SystemClock.elapsedRealtime()-lastOutputAt<2000
    override val diagnostics:String get()="input=yuv inputs=$inputFrames outputs=$outputFrames output=$hasOutput queuedDrops=$drops idleMs=${SystemClock.elapsedRealtime()-lastOutputAt}"

    /** Always closes the camera image before invoking any downstream consumer. */
    fun accept(image:ImageProxy){
        var copy:JavaI420Buffer?=null
        val timestamp=image.imageInfo.timestamp
        try{
            if(closed.get())return
            onFrame();inputFrames++
            pending.take()?.let{drops++;it.release()}
            copy=pool.copy(image)
        }catch(error:Exception){fail(error.message?:"YUV camera copy failed")}
        finally{image.close()}
        val owned=copy ?: return
        if(closed.get()){owned.release();return}
        val frame=VideoFrame(owned,rotation,timestamp)
        var handedOff=false
        try{
            preview()?.onFrame(frame)
            handedOff=true
            if(pending.offer(frame))schedule()
        }catch(error:Exception){fail(error.message?:"YUV preview failed")}
        finally{if(!handedOff)frame.release()}
    }
    private fun createEncoder(frame:VideoFrame):VideoEncoder {
        check(predecessor?.awaitReleased(5000)!=false){"Previous hardware encoder did not release"}
        check(!closed.get()){"Capture closed"}
        // A null EGL context explicitly selects MediaCodec's CPU-readable YUV input.
        // Compression is still performed by the hardware encoder, at the same bitrate.
        val factory=HardwareVideoEncoderFactory(null,false,false){info->
            if(android.os.Build.VERSION.SDK_INT>=29)info.isHardwareAccelerated
            else !info.name.startsWith("OMX.google.")&&!info.name.startsWith("c2.android.")
        }
        val codec=factory.supportedCodecs.firstOrNull{it.name.equals("H264",true)&&it.params["packetization-mode"]=="1"}
            ?: throw UnsupportedOperationException("No hardware H.264 YUV encoder")
        val active=factory.createEncoder(codec) ?: throw UnsupportedOperationException("Hardware AVC YUV input unavailable")
        encoder=active;width=frame.buffer.width;height=frame.buffer.height
        val bitrate=if(width*height>1280*720)24000 else 12000
        val settings=VideoEncoder.Settings(Runtime.getRuntime().availableProcessors(),width,height,bitrate,30,1,false,VideoEncoder.Capabilities(false))
        val status=active.initEncode(settings){image,_->
            if(!closed.get()){
                lastOutputAt=SystemClock.elapsedRealtime();hasOutput=true;outputFrames++
                val bytes=ByteArray(image.buffer.remaining());image.buffer.duplicate().get(bytes)
                onEncoded(bytes,image.encodedWidth,image.encodedHeight,image.rotation,
                    image.frameType==EncodedImage.FrameType.VideoFrameKey,image.captureTimeNs/100)
            }
        }
        check(status==VideoCodecStatus.OK){"Hardware AVC YUV init: $status"}
        android.util.Log.i("LensioAVC","YUV encoder: ${active.implementationName} ${width}x$height")
        return active
    }
    private fun schedule(){
        if(!scheduled.compareAndSet(false,true))return
        if(!handler.post{
            try{
                while(!closed.get()){
                    val frame=pending.take() ?: break
                    try{
                        val active=encoder ?: createEncoder(frame)
                        check(frame.buffer.width==width&&frame.buffer.height==height){"Camera YUV dimensions changed"}
                        val now=SystemClock.elapsedRealtime()
                        val sync=keyframe.get()&&(lastKeyframeAt==0L||now-lastKeyframeAt>=500)
                        if(sync){keyframe.set(false);lastKeyframeAt=now}
                        val type=if(sync)EncodedImage.FrameType.VideoFrameKey else EncodedImage.FrameType.VideoFrameDelta
                        val status=active.encode(frame,VideoEncoder.EncodeInfo(arrayOf(type)))
                        if(sync&&status!=VideoCodecStatus.OK)keyframe.set(true)
                        if(status==VideoCodecStatus.OK||status==VideoCodecStatus.NO_OUTPUT)failures=0
                        else if(++failures>=3)fail("Hardware AVC YUV encode: $status")
                    }catch(error:Exception){fail(error.message?:"Hardware YUV encoding failed",error is UnsupportedOperationException)}
                    finally{frame.release()}
                }
            }finally{scheduled.set(false);if(!closed.get()&&pending.hasPending)schedule()}
        }){scheduled.set(false);pending.close()}
    }
    private fun fail(message:String,unsupported:Boolean=false){
        if(!closed.get()&&failurePosted.compareAndSet(false,true))Handler(Looper.getMainLooper()).post{
            if(!closed.get())onFailure(message,unsupported)
        }
    }
    override fun requestKeyframe(){keyframe.set(true)}
    override fun setRotation(degrees:Int){if(degrees in listOf(0,90,180,270))rotation=degrees}
    override fun awaitReleased(timeoutMs:Long)=released.await(timeoutMs,TimeUnit.MILLISECONDS)
    override fun close(){
        if(!closed.compareAndSet(false,true))return
        pending.close();pool.close()
        handler.post{
            try{runCatching{encoder?.release()}}
            finally{encoder=null;released.countDown();thread.quitSafely()}
        }
    }
}
