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
    predecessor: HardwareAvcCapture?,
    private val preview: () -> VideoSink?,
    private val onFrame: () -> Unit,
    private val onEncoded: (ByteArray, Int, Int, Int, Boolean, Long) -> Unit,
    private val onFailure: (String,Boolean) -> Unit
) {
    private val size=request.resolution.also { require(it.width%2==0 && it.height%2==0 && it.width.toLong()*it.height<=1920*1080) }
    private val closed = AtomicBoolean(false)
    private val resourcesReleased = AtomicBoolean(false)
    private val failurePosted=AtomicBoolean(false)
    private val encoderReleased=java.util.concurrent.CountDownLatch(1)
    private val helper = SurfaceTextureHelper.create("Lensio-AVC-texture", sharedContext)
        ?: throw IllegalStateException("Camera texture unavailable")
    private val surface = Surface(helper.surfaceTexture)
    private var encoder: VideoEncoder? = null
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
    val diagnostics: String get()="inputs=$inputFrames output=$hasOutput idleMs=${android.os.SystemClock.elapsedRealtime()-lastOutputAt}"
    val outputStalled: Boolean get()=android.os.SystemClock.elapsedRealtime()-lastOutputAt>=if(hasOutput)6000 else 10000
    val outputHealthy:Boolean get()=hasOutput&&!closed.get()&&android.os.SystemClock.elapsedRealtime()-lastOutputAt<2000
    init {
        helper.setTextureSize(size.width,size.height)
        helper.handler.post {
            try {
                check(predecessor?.encoderReleased?.await(5,java.util.concurrent.TimeUnit.SECONDS)!=false){"Previous hardware encoder did not release"}
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
                        val bytes=ByteArray(image.buffer.remaining());image.buffer.duplicate().get(bytes)
                        onEncoded(bytes,image.encodedWidth,image.encodedHeight,image.rotation,
                            image.frameType==EncodedImage.FrameType.VideoFrameKey,image.captureTimeNs/100)
                    }
                }
                check(result==VideoCodecStatus.OK){"Hardware AVC init: $result"}
                helper.startListening { frame ->
                    if(!closed.get()) {
                        try {
                        onFrame()
                        inputFrames++
                        val source=frame.buffer as VideoFrame.TextureBuffer
                        val corrected=source.applyTransformMatrix(textureCorrection,source.width,source.height)
                        val view=VideoFrame(corrected,rotation,frame.timestampNs)
                        try {
                        preview()?.onFrame(view)
                        val now=android.os.SystemClock.elapsedRealtime()
                        val sync=keyframe.get() && (lastKeyframeAt==0L || now-lastKeyframeAt>=500)
                        if(sync){keyframe.set(false);lastKeyframeAt=now}
                        val type=if(sync)EncodedImage.FrameType.VideoFrameKey else EncodedImage.FrameType.VideoFrameDelta
                        val status=active.encode(view,VideoEncoder.EncodeInfo(arrayOf(type)))
                        if(status==VideoCodecStatus.OK||status==VideoCodecStatus.NO_OUTPUT)failures=0
                        else if(++failures>=3)fail("Hardware AVC encode: $status")
                        } finally {view.release()}
                        } catch(error:Exception){fail(error.message?:"Hardware texture delivery failed")}
                    }
                }
                if(closed.get())return@post
                request.provideSurface(surface,{ it.run() }) { closed.set(true);releaseResources() }
            } catch(error:Exception){request.willNotProvideSurface();fail(error.message?:"Hardware AVC failed",error is UnsupportedOperationException);releaseResources()}
        }
    }
    fun requestKeyframe(){keyframe.set(true)}
    fun setRotation(degrees:Int){if(degrees in listOf(0,90,180,270))rotation=degrees}
    private fun fail(message:String,unsupported:Boolean=false){
        if(!closed.get()&&failurePosted.compareAndSet(false,true))Handler(Looper.getMainLooper()).post{if(!closed.get())onFailure(message,unsupported)}
    }
    fun close(){
        if(!closed.compareAndSet(false,true))return
        helper.handler.post { releaseEncoder() }
        // Keep draining the camera texture until CameraX confirms that the HAL released it.
        // Releasing a provided Surface during unbind can wedge the next camera session.
        if(request.willNotProvideSurface())releaseResources()
    }
    private fun releaseResources(){
        if(!resourcesReleased.compareAndSet(false,true))return
        helper.handler.post {
            helper.stopListening()
            releaseEncoder()
            surface.release();helper.dispose()
        }
    }
    private fun releaseEncoder(){try{runCatching{encoder?.release()}}finally{encoder=null;encoderReleased.countDown()}}
}
