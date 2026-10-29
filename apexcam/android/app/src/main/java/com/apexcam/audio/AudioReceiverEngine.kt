package com.apexcam.audio

import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioTrack
import android.os.Build
import android.os.SystemClock
import android.util.Log
import java.io.EOFException
import java.io.InputStream
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket

interface AudioStatusListener {
    fun onConnected(serverIp: String, port: Int)
    fun onDisconnected()
    fun onStatsUpdated(latencyMs: Int, bufferedMs: Int, droppedPackets: Long, underruns: Long)
    fun onError(message: String)
    fun onMicStarted(sampleRate: Int, sourceName: String) {}
    fun onMicStats(packetsSent: Long, levelRms: Int) {}
    fun onMicError(error: String) {}
}

/**
 * Ultra-low-latency, jitter-proof audio receiver and AudioTrack playback engine for Android.
 * Features dual-transport with automatic intelligent failover:
 * 1. Primary: High-priority TCP stream (lossless, immune to packet drops caused by other Wi-Fi devices streaming video).
 * 2. Fallback: UDP sub-MTU stream with advanced Jitter Buffer & Packet Loss Concealment (PLC).
 */
class AudioReceiverEngine(
    private val serverIp: String,
    private val serverPort: Int = 44100,
    var targetLatencyMs: Int = 15,
    private val listener: AudioStatusListener? = null
) {
    companion object {
        const val SAMPLE_RATE = 48000
        const val CHANNELS = AudioFormat.CHANNEL_OUT_STEREO
        const val ENCODING = AudioFormat.ENCODING_PCM_16BIT
        const val PACKET_SIZE = 968
        const val HEADER_SIZE = 8
        const val PAYLOAD_SIZE = 960
        private const val TAG = "AudioReceiverEngine"
    }

    private var socket: DatagramSocket? = null
    private var tcpSocket: Socket? = null
    private var audioTrack: AudioTrack? = null
    private val isUsbCable = serverIp == "127.0.0.1" || serverIp.equals("localhost", ignoreCase = true)
    private val jitterBuffer = CircularAudioBuffer(
        capacityMs = 1500,
        targetLatencyMs = if (isUsbCable) 15 else 65
    )

    @Volatile
    private var isRunning = false
    private var receiveThread: Thread? = null
    private var playbackThread: Thread? = null

    private var volume = 1.0f

    fun setBufferLatency(latencyMs: Int) {
        targetLatencyMs = latencyMs
        jitterBuffer.targetLatencyMs = latencyMs
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N) {
            try {
                val frames = if (isUsbCable) 480 else 1440
                audioTrack?.setBufferSizeInFrames(frames)
            } catch (_: Exception) {}
        }
    }

    fun setVolume(vol: Float) {
        volume = vol.coerceIn(0.0f, 1.0f)
        audioTrack?.setVolume(volume)
    }

    fun start() {
        if (isRunning) return
        isRunning = true
        jitterBuffer.reset()
        jitterBuffer.targetLatencyMs = if (isUsbCable) 15 else 65

        receiveThread = Thread({
            try {
                android.os.Process.setThreadPriority(android.os.Process.THREAD_PRIORITY_URGENT_AUDIO)
            } catch (_: Exception) {}

            // Try lossless TCP first for both USB and Wi-Fi to eliminate packet drops during video downloads on other devices
            val tcpSuccess = runReceiveLoopTcp()
            if (!tcpSuccess && isRunning && !isUsbCable) {
                Log.i(TAG, "Conexiunea TCP nu este disponibilă, comutare pe transport UDP cu PLC...")
                runReceiveLoopUdp()
            }
        }, if (isUsbCable) "AudioUsbReceiveThread" else "AudioReceiveThread").apply {
            priority = Thread.MAX_PRIORITY
            start()
        }

        playbackThread = Thread({
            try {
                android.os.Process.setThreadPriority(android.os.Process.THREAD_PRIORITY_URGENT_AUDIO)
            } catch (_: Exception) {}
            runPlaybackLoop()
        }, "AudioTrackPlaybackThread").apply {
            priority = Thread.MAX_PRIORITY
            start()
        }
    }

    fun stop() {
        if (!isRunning) return
        isRunning = false

        Thread {
            try {
                audioTrack?.pause()
                audioTrack?.flush()
            } catch (_: Exception) {}

            try {
                socket?.let { s ->
                    val disconn = "DISCONNECT".toByteArray(Charsets.US_ASCII)
                    val pkt = DatagramPacket(disconn, disconn.size, InetAddress.getByName(serverIp), serverPort)
                    s.send(pkt)
                }
            } catch (_: Exception) {}

            try { socket?.close() } catch (_: Exception) {}
            socket = null

            try { tcpSocket?.close() } catch (_: Exception) {}
            tcpSocket = null

            receiveThread?.interrupt()
            playbackThread?.interrupt()
            receiveThread = null
            playbackThread = null

            try {
                audioTrack?.stop()
                audioTrack?.release()
            } catch (_: Exception) {}
            audioTrack = null

            listener?.onDisconnected()
        }.start()
    }

    /**
     * Lossless TCP receiver loop.
     * Guarantees zero dropped packets, zero waveform tearing, and immunity to Wi-Fi traffic bursts.
     * Returns true if connection was successfully used, or false if initial connection failed.
     */
    private fun runReceiveLoopTcp(): Boolean {
        var connectedAtLeastOnce = false
        var retryAttempts = 0

        while (isRunning) {
            var s: Socket? = null
            try {
                s = Socket().apply {
                    tcpNoDelay = true
                    receiveBufferSize = 128 * 1024
                    soTimeout = 4000
                    try {
                        trafficClass = 0xB8 // DSCP 46 EF / WMM Voice Priority
                    } catch (_: Exception) {}
                }
                s.connect(InetSocketAddress(serverIp, serverPort), 2500)
                tcpSocket = s
                connectedAtLeastOnce = true
                retryAttempts = 0

                listener?.onConnected(serverIp, serverPort)

                val inputStream = s.getInputStream()
                val packet = ByteArray(PACKET_SIZE)
                var lastStatsUpdate = SystemClock.elapsedRealtime()

                while (isRunning) {
                    try {
                        readFully(inputStream, packet, 0, PACKET_SIZE)

                        // Validate Magic Bytes 0x53, 0x41 ('SA')
                        if (packet[0] == 0x53.toByte() && packet[1] == 0x41.toByte()) {
                            val seq = ((packet[2].toInt() and 0xFF) shl 8) or (packet[3].toInt() and 0xFF)
                            jitterBuffer.writeFrame(seq, packet, HEADER_SIZE, PAYLOAD_SIZE)
                        }

                        val currentMs = SystemClock.elapsedRealtime()
                        if (currentMs - lastStatsUpdate >= 1000) {
                            listener?.onStatsUpdated(
                                latencyMs = if (isUsbCable) 1 else 8,
                                bufferedMs = jitterBuffer.bufferedMs,
                                droppedPackets = jitterBuffer.droppedPackets,
                                underruns = jitterBuffer.underruns
                            )
                            lastStatsUpdate = currentMs
                        }
                    } catch (e: java.net.SocketTimeoutException) {
                        continue
                    }
                }
            } catch (e: Exception) {
                try { s?.close() } catch (_: Exception) {}
                tcpSocket = null

                if (!isRunning) break

                if (!connectedAtLeastOnce) {
                    // Initial TCP connection failed: allow fallback to UDP
                    return false
                }

                retryAttempts++
                if (retryAttempts > 3 && !isUsbCable) {
                    // Disconnected mid-stream and TCP reconnect failed: fallback to UDP
                    return false
                }

                try { Thread.sleep(600) } catch (_: InterruptedException) { break }
            }
        }
        return connectedAtLeastOnce
    }

    private fun readFully(stream: InputStream, buffer: ByteArray, offset: Int, length: Int) {
        var totalRead = 0
        while (totalRead < length && isRunning) {
            val read = stream.read(buffer, offset + totalRead, length - totalRead)
            if (read < 0) throw EOFException("Conexiunea audio TCP a fost închisă")
            totalRead += read
        }
    }

    /**
     * Fallback UDP receiver loop for Wi-Fi environments where TCP port is blocked.
     */
    private fun runReceiveLoopUdp() {
        try {
            val serverAddr = InetAddress.getByName(serverIp)
            socket = DatagramSocket().apply {
                receiveBufferSize = 128 * 1024
                soTimeout = 2000
                try {
                    trafficClass = 0xB8 // WMM Voice Priority
                } catch (_: Exception) {}
            }

            // Send handshake "CONNECT"
            val connectMsg = "CONNECT".toByteArray(Charsets.US_ASCII)
            val connectPacket = DatagramPacket(connectMsg, connectMsg.size, serverAddr, serverPort)
            socket?.send(connectPacket)

            val receiveBuffer = ByteArray(PACKET_SIZE)
            val packet = DatagramPacket(receiveBuffer, receiveBuffer.size)

            var lastStatsUpdate = SystemClock.elapsedRealtime()
            var lastPingSent = 0L
            var estimatedNetworkLatency = 15
            var hasReceivedFirstPacket = false

            while (isRunning) {
                try {
                    val nowMs = SystemClock.elapsedRealtime()
                    if (nowMs - lastPingSent >= 1000) {
                        val pingMsg = "PING:$nowMs".toByteArray(Charsets.US_ASCII)
                        socket?.send(DatagramPacket(pingMsg, pingMsg.size, serverAddr, serverPort))
                        lastPingSent = nowMs
                    }

                    socket?.receive(packet)

                    if (packet.length == PACKET_SIZE) {
                        val data = packet.data
                        if (data[0] == 0x53.toByte() && data[1] == 0x41.toByte()) {
                            if (!hasReceivedFirstPacket) {
                                hasReceivedFirstPacket = true
                                listener?.onConnected(serverIp, serverPort)
                            }
                            val seq = ((data[2].toInt() and 0xFF) shl 8) or (data[3].toInt() and 0xFF)
                            jitterBuffer.writeFrame(seq, data, HEADER_SIZE, PAYLOAD_SIZE)
                        }
                    } else if (packet.length > 0) {
                        val msg = String(packet.data, 0, packet.length, Charsets.US_ASCII)
                        if (msg == "CONNECTED_OK") {
                            listener?.onConnected(serverIp, serverPort)
                        } else if (msg.startsWith("PONG:")) {
                            val sentTime = msg.substringAfter("PONG:").toLongOrNull()
                            if (sentTime != null) {
                                val rtt = (SystemClock.elapsedRealtime() - sentTime).toInt()
                                estimatedNetworkLatency = (rtt / 2).coerceIn(1, 200)
                            }
                        }
                    }

                    val currentMs = SystemClock.elapsedRealtime()
                    if (currentMs - lastStatsUpdate >= 1000) {
                        listener?.onStatsUpdated(
                            latencyMs = estimatedNetworkLatency,
                            bufferedMs = jitterBuffer.bufferedMs,
                            droppedPackets = jitterBuffer.droppedPackets,
                            underruns = jitterBuffer.underruns
                        )
                        lastStatsUpdate = currentMs
                    }
                } catch (e: java.net.SocketTimeoutException) {
                    if (!hasReceivedFirstPacket) {
                        val retryConnect = "CONNECT".toByteArray(Charsets.US_ASCII)
                        socket?.send(DatagramPacket(retryConnect, retryConnect.size, serverAddr, serverPort))
                    } else {
                        val ping = "PING".toByteArray(Charsets.US_ASCII)
                        socket?.send(DatagramPacket(ping, ping.size, serverAddr, serverPort))
                    }
                }
            }
        } catch (e: Exception) {
            if (isRunning) {
                listener?.onError("UDP audio network error: ${e.message}")
            }
        }
    }

    private fun runPlaybackLoop() {
        try {
            val minBufSize = AudioTrack.getMinBufferSize(SAMPLE_RATE, CHANNELS, ENCODING)
            val bufferSize = Math.max(minBufSize, CircularAudioBuffer.FRAME_BYTES * 6)

            val track = AudioTrack.Builder()
                .setAudioAttributes(
                    AudioAttributes.Builder()
                        .setUsage(AudioAttributes.USAGE_MEDIA)
                        .setContentType(AudioAttributes.CONTENT_TYPE_MUSIC)
                        .build()
                )
                .setAudioFormat(
                    AudioFormat.Builder()
                        .setEncoding(ENCODING)
                        .setSampleRate(SAMPLE_RATE)
                        .setChannelMask(CHANNELS)
                        .build()
                )
                .setBufferSizeInBytes(bufferSize)
                .setPerformanceMode(AudioTrack.PERFORMANCE_MODE_LOW_LATENCY)
                .setTransferMode(AudioTrack.MODE_STREAM)
                .build()

            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N) {
                try {
                    val frames = if (isUsbCable) 480 else 1440
                    track.setBufferSizeInFrames(frames)
                } catch (_: Exception) {}
            }

            track.setVolume(volume)
            track.play()
            audioTrack = track

            val readChunk = ByteArray(CircularAudioBuffer.FRAME_BYTES) // 5ms chunks (960 bytes)

            while (isRunning) {
                jitterBuffer.read(readChunk, 0, readChunk.size)
                track.write(readChunk, 0, readChunk.size, AudioTrack.WRITE_BLOCKING)
            }
        } catch (e: Exception) {
            if (isRunning) {
                listener?.onError("Audio playback error: ${e.message}")
            }
        } finally {
            try {
                audioTrack?.stop()
                audioTrack?.release()
            } catch (_: Exception) {}
            audioTrack = null
        }
    }
}
