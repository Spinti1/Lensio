package com.apexcam.network

import android.graphics.YuvImage
import android.graphics.ImageFormat
import android.graphics.Rect
import android.os.SystemClock
import android.util.Log
import androidx.camera.core.ImageProxy
import java.io.ByteArrayOutputStream
import java.net.InetSocketAddress
import java.net.Socket
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean

/** One encoder, one in-flight frame, and socket ownership scoped to a connection generation. */
class NativeStreamSender(private val clock: () -> Long = { SystemClock.elapsedRealtime() }) {
    private data class Session(val socket: Socket, val host: String, val port: Int,val generation:Long) {
        val output = socket.getOutputStream()
        val outputLock=Any()
        @Volatile var writeStartedAt=0L
    }
    private val avcQueue=java.util.concurrent.ArrayBlockingQueue<AvcPacket>(4)
    private data class AvcPacket(val owner:Session,val bytes:ByteArray,val width:Int,val height:Int,val rotation:Int,val key:Boolean,val timestamp:Long,val battery:Int,val charging:Boolean,val temp:Float)
    private val avcExecutor=Executors.newSingleThreadExecutor { Thread(it,"Lensio-AVC-network") }
    @Volatile private var avcEnabled=false
    private val avcNeedsKey=AtomicBoolean(true)
    var onVideoKeyframeRequested:(()->Unit)?=null
    private val stateLock = Any()
    private val streamExecutor = Executors.newSingleThreadExecutor { Thread(it, "Lensio-encoder") }
    private val receiveExecutor = Executors.newSingleThreadExecutor { Thread(it, "Lensio-commands") }
    private val connectExecutor = Executors.newSingleThreadExecutor { Thread(it, "Lensio-connect") }
    private val busy = AtomicBoolean(false)
    private val closed = AtomicBoolean(false)
    private val buffer = ByteArrayOutputStream(256 * 1024)
    @Volatile private var session: Session? = null
    private var pendingSocket: Socket? = null
    private var generation = 0L
    @Volatile private var connecting = false
    private var rotatedYuv=ByteArray(0)
    private var yuvBytes = ByteArray(0)
    @Volatile private var busySince = 0L
    @Volatile private var lastSentAt = 0L
    @Volatile private var busySession: Session? = null
    val isConnected: Boolean get() = session != null
    val connectionGeneration:Long get()=synchronized(stateLock){generation}
    var deviceIdentityJson: String? = null
    var onCommandReceived: ((String,Long) -> Unit)? = null
    var onConnectionChanged: ((Boolean,Long) -> Unit)? = null
    var onConnectedHost: ((String) -> Unit)? = null
    var onPreviewJpeg: ((ByteArray) -> Unit)? = null
    var isPreviewVisible: (() -> Boolean)? = null
    var httpStreamServer: HttpStreamServer? = null
    @Volatile var totalFramesSent = 0L
        private set
    @Volatile var currentFps = 0
        private set
    private var frameCount = 0
    private var fpsSince = clock()

    init { avcExecutor.execute { while(!closed.get()) {
        val packet=try{avcQueue.poll(250,java.util.concurrent.TimeUnit.MILLISECONDS)}catch(_:InterruptedException){break} ?: continue
        val owner=packet.owner
        if(session!==owner||!avcEnabled)continue
        try {
            val header=ByteBuffer.allocate(40).order(ByteOrder.LITTLE_ENDIAN)
            header.put(byteArrayOf(76,72,50,52)).putInt(packet.width).putInt(packet.height).putInt(packet.bytes.size).putInt(currentFps)
            header.putInt(packet.battery.coerceIn(0,100) or (if(packet.charging)0x10000 else 0)).putInt((packet.temp*10).toInt())
            header.putInt(0x01000000 or (packet.rotation shl 8) or (if(packet.key)1 else 0)).putLong(packet.timestamp)
            val written=synchronized(owner.outputLock){
                if(session!==owner||!avcEnabled)false
                else {owner.writeStartedAt=clock();try{owner.output.write(header.array());owner.output.write(packet.bytes);owner.output.flush();true}finally{owner.writeStartedAt=0}}
            }
            if(written)synchronized(stateLock){if(session===owner){totalFramesSent++;lastSentAt=clock();frameCount++;val elapsed=lastSentAt-fpsSince;if(elapsed>=1000){currentFps=(frameCount*1000L/elapsed).toInt();frameCount=0;fpsSince=lastSentAt}}}
        }catch(error:Exception){Log.w("NativeStreamSender","AVC socket failed",error);disconnectSession(owner)}
    } } }
    fun setAvcEnabled(enabled:Boolean){avcEnabled=enabled;avcQueue.clear();avcNeedsKey.set(true);if(enabled)onVideoKeyframeRequested?.invoke()}
    fun sendAvc(bytes:ByteArray,width:Int,height:Int,rotation:Int,key:Boolean,timestamp:Long,battery:Int,charging:Boolean,temp:Float){
        val owner=session ?: return
        if(!avcEnabled||closed.get()||bytes.isEmpty()||bytes.size>4*1024*1024)return
        synchronized(avcQueue){
            if(avcNeedsKey.get()&&!key)return
            if(!avcQueue.offer(AvcPacket(owner,bytes,width,height,rotation,key,timestamp,battery,charging,temp))){
                avcQueue.clear();avcNeedsKey.set(true);onVideoKeyframeRequested?.invoke()
                if(key){avcQueue.offer(AvcPacket(owner,bytes,width,height,rotation,key,timestamp,battery,charging,temp));avcNeedsKey.set(false)}
            }else if(key)avcNeedsKey.set(false)
        }
    }
    fun connect(host: String, port: Int = 8556, force: Boolean = false) {
        if (host.isBlank()) return
        synchronized(stateLock) {
            if (closed.get()) return
            val active = session
            if (!force && (connecting || (active?.host == host && active.port == port))) return
            generation++
            val ticket = generation
            closeSocketsLocked()
            connecting = true
            connectExecutor.execute {
                val sock = Socket()
                try {
                    synchronized(stateLock) {
                        if (closed.get() || ticket != generation) { sock.close(); return@execute }
                        pendingSocket = sock
                    }
                    sock.tcpNoDelay = true
                    sock.keepAlive = true
                    sock.sendBufferSize = 64 * 1024
                    sock.connect(InetSocketAddress(host, port), 2500)
                    val established = Session(sock, host, port,ticket)
                    deviceIdentityJson?.let { identity ->
                        val data=identity.toByteArray(Charsets.UTF_8)
                        require(data.size in 1..4096)
                        val hello=ByteBuffer.allocate(8).order(ByteOrder.LITTLE_ENDIAN)
                        hello.put(byteArrayOf(65,80,67,73)).putInt(data.size)
                        established.output.write(hello.array())
                        established.output.write(data)
                        established.output.flush()
                    }
                    synchronized(stateLock) {
                        if (closed.get() || ticket != generation) { sock.close(); return@execute }
                        pendingSocket = null
                        session = established
                        connecting = false
                        lastSentAt = clock()
                        frameCount = 0
                        fpsSince = clock()
                        currentFps = 0
                        onConnectedHost?.invoke(host)
                        onConnectionChanged?.invoke(true,generation)
                        receiveExecutor.execute { receiveCommands(established) }
                    }
                } catch (e: Exception) {
                    try { sock.close() } catch (_: Exception) {}
                    synchronized(stateLock) {
                        if (ticket == generation && !closed.get()) {
                            closeSocketsLocked()
                            onConnectionChanged?.invoke(false,generation)
                        }
                    }
                    Log.d("NativeStreamSender", "Connection ended: " + e.message)
                }
            }
        }
    }

    private fun receiveCommands(owner: Session) {
        try {
            val reader = owner.socket.getInputStream().bufferedReader()
            while (session === owner && !closed.get()) {
                val line = reader.readLine() ?: break
                if (session === owner) onCommandReceived?.invoke(line,owner.generation)
            }
        } catch (_: Exception) {
        } finally {
            disconnectSession(owner)
        }
    }

    private fun disconnectSession(owner: Session) {
        synchronized(stateLock) {
            if (session !== owner) return
            generation++
            closeSocketsLocked()
            onConnectionChanged?.invoke(false,generation)
        }
    }

    fun checkHealth() {
        val now = clock()
        val active = session ?: return
        if ((active.writeStartedAt > 0 && now - active.writeStartedAt > 3000) ||
            now - lastSentAt > 12000) {
            Log.w("NativeStreamSender", "Stream watchdog: busyMs="+(now-busySince)+", noFrameMs="+(now-lastSentAt))
            // Only the encoder's finally releases busy, after the socket write has unwound.
            disconnectSession(active)
        }
    }

    fun sendFrame(imageProxy: ImageProxy, batteryLevel: Int, tempC: Float, isCharging: Boolean = false) {
        val owner = session
        val previewVisible = onPreviewJpeg != null && (isPreviewVisible?.invoke() != false)
        if (avcEnabled || closed.get() || (owner == null && (httpStreamServer?.activeClientCount ?: 0) == 0 &&
                !previewVisible) ||
            !busy.compareAndSet(false, true)) {
            imageProxy.close()
            return
        }
        busySince = clock()
        busySession = owner
        try {
            val width = imageProxy.width
            val height = imageProxy.height
            val rotation = imageProxy.imageInfo.rotationDegrees
            val outputWidth=if(rotation==90||rotation==270)height else width
            val outputHeight=if(rotation==90||rotation==270)width else height
            streamExecutor.execute {
                var imageClosed = false
                try {
                    if (closed.get() || (owner != null && session !== owner)) return@execute
                    require(width % 2 == 0 && height % 2 == 0)
                    require(imageProxy.format == ImageFormat.YUV_420_888)
                    val size = width * height * 3 / 2
                    if (yuvBytes.size != size) yuvBytes = ByteArray(size)
                    val planes = imageProxy.planes
                    YuvPacking.copyPlane(planes[0].buffer, planes[0].rowStride, planes[0].pixelStride,
                        width, height, yuvBytes, 0, 1)
                    YuvPacking.copyPlane(planes[2].buffer, planes[2].rowStride, planes[2].pixelStride,
                        width/2, height/2, yuvBytes, width*height, 2)
                    YuvPacking.copyPlane(planes[1].buffer, planes[1].rowStride, planes[1].pixelStride,
                        width/2, height/2, yuvBytes, width*height+1, 2)
                    imageProxy.close()
                    imageClosed = true
                    var pixels=yuvBytes
                    if(rotation!=0){
                        if(rotatedYuv.size!=size)rotatedYuv=ByteArray(size)
                        YuvPacking.rotateNv21(yuvBytes,rotatedYuv,width,height,rotation)
                        pixels=rotatedYuv
                    }
                    buffer.reset()
                    if (!YuvImage(pixels, ImageFormat.NV21, outputWidth, outputHeight, null)
                        .compressToJpeg(Rect(0,0,outputWidth,outputHeight), 85, buffer)) return@execute
                    if (previewVisible) {
                        onPreviewJpeg?.invoke(buffer.toByteArray())
                    }
                    if ((httpStreamServer?.activeClientCount ?: 0) > 0)
                        httpStreamServer?.broadcastJpeg(buffer.toByteArray())
                    if (owner != null && session === owner) {
                        val header = ByteBuffer.allocate(32).order(ByteOrder.LITTLE_ENDIAN)
                        header.put(byteArrayOf(0x41, 0x50, 0x43, 0x4D))
                        header.putInt(outputWidth).putInt(outputHeight).putInt(buffer.size()).putInt(currentFps)
                        header.putInt(batteryLevel.coerceIn(0, 100) or (if (isCharging) 0x10000 else 0))
                        header.putInt((tempC * 10).toInt()).putInt(if (isCharging) 1 else 0)
                        synchronized(owner.outputLock){
                            if(avcEnabled||session!==owner)return@execute
                            owner.writeStartedAt=clock()
                            try{owner.output.write(header.array());buffer.writeTo(owner.output);owner.output.flush()}finally{owner.writeStartedAt=0}
                        }
                        synchronized(stateLock) {
                            if (session === owner) {
                                totalFramesSent++
                                lastSentAt = clock()
                                frameCount++
                                val elapsed = lastSentAt - fpsSince
                                if (elapsed >= 1000) {
                                    currentFps = (frameCount * 1000L / elapsed).toInt()
                                    frameCount = 0
                                    fpsSince = lastSentAt
                                }
                            }
                        }
                    }
                } catch (e: Exception) {
                    Log.w("NativeStreamSender", "Frame processing or socket write failed", e)
                    // Any partial JPEG invalidates this session.
                    if (owner != null) disconnectSession(owner)
                } finally {
                    if (!imageClosed) runCatching { imageProxy.close() }
                    busySession = null
                    busy.set(false)
                }
            }
        } catch (e: Exception) {
            imageProxy.close()
            busySession = null
            busy.set(false)
            Log.w("NativeStreamSender", "Frame conversion: " + e.message)
        }
    }

    fun disconnect() {
        synchronized(stateLock) {
            generation++
            closeSocketsLocked()
            onConnectionChanged?.invoke(false,generation)
        }
    }

    private fun closeSocketsLocked() {
        val old = session
        session = null
        avcEnabled=false;avcQueue.clear();avcNeedsKey.set(true)
        connecting = false
        currentFps = 0
        // Closing the socket aborts a blocking writer without waiting for its stream monitor.
        try { old?.socket?.close() } catch (_: Exception) {}
        try { pendingSocket?.close() } catch (_: Exception) {}
        pendingSocket = null
    }

    fun close() {
        synchronized(stateLock) {
            if (!closed.compareAndSet(false, true)) return
            generation++
            closeSocketsLocked()
            onCommandReceived = null
            onConnectionChanged = null
            onConnectedHost = null
            httpStreamServer = null
            onPreviewJpeg = null
            isPreviewVisible = null
            avcExecutor.shutdownNow();avcQueue.clear();onVideoKeyframeRequested=null
            connectExecutor.shutdownNow()
            receiveExecutor.shutdownNow()
            streamExecutor.shutdown()
        }
    }
}
