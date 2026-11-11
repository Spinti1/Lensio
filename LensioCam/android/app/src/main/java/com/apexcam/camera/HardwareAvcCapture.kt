package com.apexcam.camera

import android.os.Handler
import android.os.Looper
import android.view.Surface
import androidx.camera.core.SurfaceRequest
import org.webrtc.*
import java.util.concurrent.atomic.AtomicBoolean

/** One CameraX texture stream fans out on GPU to MediaCodec and the visible phone preview. */
internal class HardwareAvcCapture(
    sharedContext: EglBase.Context,
    private val request: SurfaceRequest,
    sensorOrientation: Int,
    frontFacing: Boolean,
    initialRotation: Int,
    predecessor: HardwareCapture?,
    private val preview: () -> VideoSink?,
    private val onFrame: () -> Unit,
    private val onEncoded: (ByteArray, Int, Int, Int, Boolean, Long) -> Unit,
    private val onFailure: (String,Boolean) -> Unit
) : HardwareCapture {
    private val size=request.resolution.also { require(it.width%2==0 && it.height%2==0 && it.width.toLong()*it.height<=1920*1080) }
    private val closed = AtomicBoolean(false)
    private val resourcesReleased = AtomicBoolean(false)
    private val failurePosted=AtomicBoolean(false)
    private val encoderShutdown=AtomicBoolean(false)
    private val encoderReleased=java.util.concurrent.CountDownLatch(1)
    private val helper = SurfaceTextureHelper.create("Lensio-AVC-texture", sharedContext)
        ?: throw IllegalStateException("Camera texture unavailable")
    private val surface = Surface(helper.surfaceTexture)
    // MediaCodec changes the current EGL context during init/encode/release. It must
    // never execute on SurfaceTextureHelper's thread, which owns the camera texture.
    private val encoderThread=android.os.HandlerThread("Lensio-AVC-encoder").apply {start()}
    private val encoderHandler=Handler(encoderThread.looper)
    private var encoder: VideoEncoder? = null
    private var texturePool:CameraTexturePool?=null
    private val pendingFrames=LatestFrameMailbox<VideoFrame>{it.release()}
    private val encodeScheduled=AtomicBoolean(false)
    @Volatile private var rotation = initialRotation
    private val textureCorrection=android.graphics.Matrix().apply {
        setValues(CameraTextureOrientation.correction(sensorOrientation,frontFacing))
    }
    private val keyframe = AtomicBoolean(true)
    private var failures=0
    private var lastKeyframeAt=0L
    @Volatile private var lastOutputAt=android.os.SystemClock.elapsedRealtime()
    @Volatile private var hasOutput=false
    @Volatile private var inputFrames=0
    @Volatile private var outputFrames=0
    @Volatile private var poolDrops=0
    @Volatile private var queuedDrops=0
    @Volatile private var copyUs=0L
    override val diagnostics: String get()="input=texture inputs=$inputFrames outputs=$outputFrames output=$hasOutput poolDrops=$poolDrops queuedDrops=$queuedDrops copyUs=$copyUs idleMs=${android.os.SystemClock.elapsedRealtime()-lastOutputAt}"
    override val outputStalled: Boolean get()=android.os.SystemClock.elapsedRealtime()-lastOutputAt>=if(hasOutput)6000 else 10000
    override val outputHealthy:Boolean get()=hasOutput&&!closed.get()&&android.os.SystemClock.elapsedRealtime()-lastOutputAt<2000
    init {
        helper.setTextureSize(size.width,size.height)
        encoderHandler.post encoderInit@{
            try {
                check(predecessor?.awaitReleased(5000)!=false){"Previous hardware encoder did not release"}
                if(closed.get())return@encoderInit
                val factory=HardwareVideoEncoderFactory(sharedContext,false,false) { info ->
                    if(android.os.Build.VERSION.SDK_INT>=29)info.isHardwareAccelerated
                    else !info.name.startsWith("OMX.google.") && !info.name.startsWith("c2.android.")
                }
                val codec=factory.supportedCodecs.firstOrNull { it.name.equals("H264",true) && it.params["packetization-mode"]=="1" }
                    ?: throw UnsupportedOperationException("No hardware H.264 encoder")
                val active=factory.createEncoder(codec) ?: throw UnsupportedOperationException("Hardware AVC unavailable")
                encoder=active
                android.util.Log.i("LensioAVC","Encoder: "+active.implementationName+" "+size.width+"x"+size.height)
                val bitrate=if(size.width*size.height>1280*720)24000 else 12000
                val settings=VideoEncoder.Settings(Runtime.getRuntime().availableProcessors(),size.width,size.height,bitrate,30,1,false,VideoEncoder.Capabilities(false))
                val result=active.initEncode(settings) { image,_ ->
                    if(!closed.get()) {
                        lastOutputAt=android.os.SystemClock.elapsedRealtime()
                        hasOutput=true
                        outputFrames++
                        val bytes=ByteArray(image.buffer.remaining());image.buffer.duplicate().get(bytes)
                        onEncoded(bytes,image.encodedWidth,image.encodedHeight,image.rotation,
                            image.frameType==EncodedImage.FrameType.VideoFrameKey,image.captureTimeNs/100)
                    }
                }
                check(result==VideoCodecStatus.OK){"Hardware AVC init: $result"}
                helper.handler.post cameraSurface@{
                    if(closed.get())return@cameraSurface
                    try {
                        texturePool=CameraTexturePool(helper.handler,size.width,size.height)
                        helper.startListening { frame -> deliverTexture(frame,active) }
                        request.provideSurface(surface,{ it.run() }) { closed.set(true);releaseResources() }
                    }catch(error:Exception){request.willNotProvideSurface();fail(error.message?:"Camera surface delivery failed");releaseResources()}
                }
            } catch(error:Exception){request.willNotProvideSurface();fail(error.message?:"Hardware AVC failed",error is UnsupportedOperationException);releaseResources()}
        }
    }
    private fun deliverTexture(frame:VideoFrame,active:VideoEncoder){
        if(closed.get())return
        try {
            onFrame();inputFrames++
            val source=frame.buffer as VideoFrame.TextureBuffer
            val corrected=source.applyTransformMatrix(textureCorrection,source.width,source.height)
            // Evict an old queued copy before acquiring a reusable GPU slot.
            pendingFrames.take()?.let{queuedDrops++;it.release()}
            val started=System.nanoTime()
            val copied=try{texturePool?.copy(corrected)}finally{corrected.release()}
            copyUs=(System.nanoTime()-started)/1000
            if(copied==null){poolDrops++;return}
            if(closed.get()){copied.release();return}
            val view=VideoFrame(copied,rotation,frame.timestampNs)
            var handedOff=false
            try {
                preview()?.onFrame(view)
                handedOff=true
                if(pendingFrames.offer(view))scheduleEncoding(active)
            }finally{if(!handedOff)view.release()}
        }catch(error:Exception){fail(error.message?:"Hardware texture delivery failed")}
    }
    private fun scheduleEncoding(active:VideoEncoder){
        if(!encodeScheduled.compareAndSet(false,true))return
        if(!encoderHandler.post {
            try{
                while(!closed.get()){
                    val view=pendingFrames.take() ?: break
                    try {
                            val now=android.os.SystemClock.elapsedRealtime()
                            val sync=keyframe.get()&&(lastKeyframeAt==0L||now-lastKeyframeAt>=500)
                            if(sync){keyframe.set(false);lastKeyframeAt=now}
                            val type=if(sync)EncodedImage.FrameType.VideoFrameKey else EncodedImage.FrameType.VideoFrameDelta
                            val status=active.encode(view,VideoEncoder.EncodeInfo(arrayOf(type)))
                            // NO_OUTPUT can mean the encoder rejected this input while its
                            // queue was full. Keep the sync request until an input is accepted.
                            if(sync&&status!=VideoCodecStatus.OK)keyframe.set(true)
                            if(status==VideoCodecStatus.OK||status==VideoCodecStatus.NO_OUTPUT)failures=0
                            else if(++failures>=3)fail("Hardware AVC encode: $status")
                    }catch(error:Exception){fail(error.message?:"Hardware texture encoding failed")}
                    finally{view.release()}
                }
            }finally{
                encodeScheduled.set(false)
                // A new frame races with an empty take: its producer schedules again
                // after this flag clears, or this final handoff does so itself.
                if(!closed.get()&&pendingFrames.hasPending)scheduleEncoding(active)
            }
        }){encodeScheduled.set(false);pendingFrames.close()}
    }
    override fun requestKeyframe(){keyframe.set(true)}
    override fun setRotation(degrees:Int){if(degrees in listOf(0,90,180,270))rotation=degrees}
    override fun awaitReleased(timeoutMs:Long)=encoderReleased.await(timeoutMs,java.util.concurrent.TimeUnit.MILLISECONDS)
    private fun fail(message:String,unsupported:Boolean=false){
        if(!closed.get()&&failurePosted.compareAndSet(false,true))Handler(Looper.getMainLooper()).post{if(!closed.get())onFailure(message,unsupported)}
    }
    override fun close(){
        if(!closed.compareAndSet(false,true))return
        pendingFrames.close()
        shutdownEncoder()
        // Keep draining the camera texture until CameraX confirms that the HAL released it.
        // Releasing a provided Surface during unbind can wedge the next camera session.
        if(request.willNotProvideSurface())releaseResources()
    }
    private fun releaseResources(){
        if(!resourcesReleased.compareAndSet(false,true))return
        shutdownEncoder()
        helper.handler.post {
            helper.stopListening()
            surface.release()
            val pool=texturePool
            if(pool!=null)pool.close{helper.dispose()} else helper.dispose()
        }
    }
    private fun shutdownEncoder(){
        if(!encoderShutdown.compareAndSet(false,true))return
        pendingFrames.close()
        encoderHandler.post {
            try{runCatching{encoder?.release()}}
            finally{encoder=null;encoderReleased.countDown();encoderThread.quitSafely()}
        }
    }
}
