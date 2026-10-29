package com.apexcam.network

import android.util.Log
import java.net.ServerSocket
import java.net.Socket
import java.net.InetSocketAddress
import java.util.concurrent.*
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicReference

/** MJPEG subscribers never write on the camera/encoder thread. Each holds only the newest JPEG. */
class HttpStreamServer(private val port: Int = 8080) {
    private class Viewer(val socket: Socket) {
        val latest = AtomicReference<ByteArray?>(null)
        val ready = Semaphore(0)
        @Volatile var writingSince = 0L
        fun offer(jpeg: ByteArray) {
            latest.set(jpeg)
            if (ready.availablePermits() == 0) ready.release()
        }
    }
    private val running = AtomicBoolean(false)
    private val closed = AtomicBoolean(false)
    private val acceptExecutor = Executors.newSingleThreadExecutor { Thread(it, "Lensio-http-accept") }
    private val workers = ThreadPoolExecutor(6, 6, 0, TimeUnit.SECONDS,
        ArrayBlockingQueue<Runnable>(8), ThreadFactory { Thread(it, "Lensio-http-client") })
    private val watchdog = Executors.newSingleThreadScheduledExecutor { Thread(it, "Lensio-http-watchdog") }
    private val sockets = ConcurrentHashMap.newKeySet<Socket>()
    private val viewers = CopyOnWriteArrayList<Viewer>()
    private val viewerSlots = Semaphore(4)
    @Volatile private var serverSocket: ServerSocket? = null
    val activeClientCount: Int get() = viewers.size
    var cameraCatalogProvider: (() -> String)? = null
    var onReconnectRequested: (() -> Unit)? = null

    fun start() {
        if (closed.get() || !running.compareAndSet(false, true)) return
        watchdog.scheduleAtFixedRate({
            val now = System.nanoTime()
            for (viewer in viewers) {
                val since = viewer.writingSince
                if (since != 0L && now - since > TimeUnit.SECONDS.toNanos(2)) {
                    try { viewer.socket.close() } catch (_: Exception) {}
                }
            }
        }, 500, 500, TimeUnit.MILLISECONDS)
        acceptExecutor.execute {
            try {
                val listener = ServerSocket()
                serverSocket = listener
                if (!running.get()) { listener.close(); return@execute }
                listener.reuseAddress = true
                listener.bind(InetSocketAddress(port))
                while (running.get()) {
                    val client = listener.accept()
                    client.tcpNoDelay = true
                    client.soTimeout = 3000
                    client.sendBufferSize = 64 * 1024
                    sockets.add(client)
                    try {
                        workers.execute { handleClient(client) }
                    } catch (_: RejectedExecutionException) {
                        sockets.remove(client)
                        client.close()
                    }
                }
            } catch (e: Exception) {
                if (running.get()) Log.w("HttpStreamServer", "HTTP listener: " + e.message)
            } finally {
                running.set(false)
                try { serverSocket?.close() } catch (_: Exception) {}
            }
        }
    }

    private fun respond(socket: Socket, type: String, body: String, status: String = "200 OK") {
        val bytes = body.toByteArray(Charsets.UTF_8)
        val output = socket.getOutputStream()
        output.write(("HTTP/1.1 " + status + "\r\nContent-Type: " + type +
            "\r\nContent-Length: " + bytes.size +
            "\r\nAccess-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n").toByteArray(Charsets.US_ASCII))
        output.write(bytes)
        output.flush()
    }

    private fun handleClient(socket: Socket) {
        var viewer: Viewer? = null
        var hasSlot = false
        try {
            val reader = socket.getInputStream().bufferedReader()
            val request = reader.readLine() ?: return
            val path = request.split(" ").getOrNull(1)?.substringBefore("?") ?: "/"
            // Bound both header count and idle time.
            var headersEnded = false
            for (i in 0 until 64) {
                val line = reader.readLine() ?: return
                if (line.isEmpty()) { headersEnded = true; break }
            }
            if (!headersEnded) return
            when (path) {
                "/cameras" -> respond(socket, "application/json", cameraCatalogProvider?.invoke() ?: "{\"cameras\":[]}")
                "/video" -> {
                    hasSlot = viewerSlots.tryAcquire()
                    if (!hasSlot) {
                        respond(socket, "text/plain", "Too many viewers", "503 Service Unavailable")
                        return
                    }
                    val subscriber = Viewer(socket)
                    viewer = subscriber
                    viewers.add(subscriber)
                    val output = socket.getOutputStream()
                    subscriber.writingSince = System.nanoTime()
                    output.write(("HTTP/1.1 200 OK\r\nContent-Type: multipart/x-mixed-replace; boundary=frame\r\n" +
                        "Access-Control-Allow-Origin: *\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n").toByteArray())
                    output.flush()
                    subscriber.writingSince = 0
                    while (running.get() && !socket.isClosed) {
                        if (!subscriber.ready.tryAcquire(1, TimeUnit.SECONDS)) continue
                        val jpeg = subscriber.latest.getAndSet(null) ?: continue
                        subscriber.writingSince = System.nanoTime()
                        output.write(("--frame\r\nContent-Type: image/jpeg\r\nContent-Length: " +
                            jpeg.size + "\r\n\r\n").toByteArray(Charsets.US_ASCII))
                        output.write(jpeg)
                        output.write(byteArrayOf(13, 10))
                        output.flush()
                        subscriber.writingSince = 0
                    }
                }
                "/status" -> respond(socket, "application/json", "{\"status\":\"ok\",\"clients\":" + activeClientCount + "}")
                "/reconnect" -> {
                    respond(socket, "application/json", "{\"status\":\"reconnecting\"}")
                    onReconnectRequested?.invoke()
                }
                else -> respond(socket, "text/html; charset=utf-8",
                    "<!doctype html><html><meta name='viewport' content='width=device-width'>" +
                    "<title>LensioCam</title><body style='margin:0;background:#202225;color:#eee;font-family:sans-serif;text-align:center'>" +
                    "<h2>LensioCam · Live</h2><img src='/video' style='max-width:100%;max-height:85vh' alt='Live camera'></body></html>")
            }
        } catch (_: Exception) {
        } finally {
            if (viewer != null) { viewers.remove(viewer); viewer.latest.set(null) }
            if (hasSlot) viewerSlots.release()
            sockets.remove(socket)
            try { socket.close() } catch (_: Exception) {}
        }
    }

    fun broadcastJpeg(jpegData: ByteArray) {
        for (viewer in viewers) viewer.offer(jpegData)
    }

    fun stop() {
        if (!closed.compareAndSet(false, true)) return
        running.set(false)
        try { serverSocket?.close() } catch (_: Exception) {}
        for (socket in sockets) try { socket.close() } catch (_: Exception) {}
        sockets.clear()
        viewers.clear()
        workers.shutdownNow()
        acceptExecutor.shutdownNow()
        watchdog.shutdownNow()
        onReconnectRequested = null
    }
}
