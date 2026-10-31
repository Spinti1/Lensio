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
    private data class Session(val socket: Socket, val host: String, val port: Int) {
        val output = socket.getOutputStream()
    }
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
    @Volatile private var writeStartedAt = 0L
    @Volatile private var busySince = 0L
    @Volatile private var lastSentAt = 0L
    @Volatile private var busySession: Session? = null
    val isConnected: Boolean get() = session != null
    var deviceIdentityJson: String? = null
    var onCommandReceived: ((String) -> Unit)? = null
    var onConnectionChanged: ((Boolean) -> Unit)? = null
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
                    val established = Session(sock, host, port)
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
                        onConnectionChanged?.invoke(true)
                        receiveExecutor.execute { receiveCommands(established) }
                    }
                } catch (e: Exception) {
                    try { sock.close() } catch (_: Exception) {}
                    synchronized(stateLock) {
                        if (ticket == generation && !closed.get()) {
                            closeSocketsLocked()
                            onConnectionChanged?.invoke(false)
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
                if (session === owner) onCommandReceived?.invoke(line)
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
            onConnectionChanged?.invoke(false)
        }
    }

    fun checkHealth() {
        val now = clock()
        val active = session ?: return
        if ((busySession === active && writeStartedAt > 0 && now - writeStartedAt > 3000) ||
            now - lastSentAt > 12000) {
            Log.w("NativeStreamSender", "Stream watchdog: busyMs="+(now-busySince)+", noFrameMs="+(now-lastSentAt))
            // Only the encoder's finally releases busy, after the socket write has unwound.
            disconnectSession(active)
        }
    }

    fun sendFrame(imageProxy: ImageProxy, batteryLevel: Int, tempC: Float, isCharging: Boolean = false) {
        val owner = session
        val previewVisible = onPreviewJpeg != null && (isPreviewVisible?.invoke() != false)
        if (closed.get() || (owner == null && (httpStreamServer?.activeClientCount ?: 0) == 0 &&
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
                        writeStartedAt = clock()
                        owner.output.write(header.array())
                        buffer.writeTo(owner.output)
                        owner.output.flush()
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
                    writeStartedAt = 0L
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
            onConnectionChanged?.invoke(false)
        }
    }

    private fun closeSocketsLocked() {
        val old = session
        session = null
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
            connectExecutor.shutdownNow()
            receiveExecutor.shutdownNow()
            streamExecutor.shutdown()
        }
    }
}
