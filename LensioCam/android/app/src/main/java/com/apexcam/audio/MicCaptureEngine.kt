package com.apexcam.audio

import android.annotation.SuppressLint
import android.content.Context
import android.media.AudioDeviceInfo
import android.media.AudioFormat
import android.media.AudioManager
import android.media.AudioRecord
import android.media.MediaRecorder
import android.os.Build
import android.os.Process
import android.os.SystemClock
import java.io.OutputStream
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket
import kotlin.math.sqrt

/**
 * Listener for real-time microphone status and audio meter feedback.
 */
interface MicCaptureListener {
    fun onMicStarted(sampleRate: Int, sourceName: String)
    fun onMicStats(packetsSent: Long, levelRms: Int)
    fun onMicError(error: String)
}

/**
 * High-performance, low-latency microphone capture and streaming engine for Android.
 * Supports Bluetooth headset microphone (Bluetooth SCO / Communication Device) and built-in mic.
 * Handles fallback across 48kHz, 44.1kHz, and 16kHz with high-fidelity linear upsampling to 48kHz.
 * Supports dual transport:
 * 1. Direct USB Cable via TCP socket (127.0.0.1:44101 via ADB reverse).
 * 2. Wi-Fi / LAN via UDP datagrams (port 44101).
 */
class MicCaptureEngine(
    private val serverIp: String,
    private val serverPort: Int = 44101,
    private val context: Context,
    var listener: MicCaptureListener? = null
) {
    companion object {
        const val MAGIC_0: Byte = 0x4D // 'M'
        const val MAGIC_1: Byte = 0x49 // 'I'
        const val HEADER_SIZE = 8
        const val TARGET_SAMPLE_RATE = 48000
        const val TARGET_SAMPLES_PER_FRAME = 480 // 10ms at 48kHz
        const val PAYLOAD_SIZE = TARGET_SAMPLES_PER_FRAME * 2 // 960 bytes (16-bit mono)
        const val PACKET_SIZE = HEADER_SIZE + PAYLOAD_SIZE // 968 bytes
    }

    private val audioManager = context.getSystemService(Context.AUDIO_SERVICE) as AudioManager
    private var audioRecord: AudioRecord? = null
    private var udpSocket: DatagramSocket? = null
    private var tcpSocket: Socket? = null
    private var tcpOutputStream: OutputStream? = null
    private var recordThread: Thread? = null

    @Volatile
    private var isRunning = false

    @Volatile
    var isMuted = false

    private var activeSampleRate = TARGET_SAMPLE_RATE

    @SuppressLint("MissingPermission")
    fun start() {
        if (isRunning) return
        isRunning = true

        // 1. Enable Bluetooth headset microphone routing if available
        setupBluetoothAudioRouting()

        // 2. Start recording worker thread
        recordThread = Thread({
            try {
                Process.setThreadPriority(Process.THREAD_PRIORITY_URGENT_AUDIO)
            } catch (_: Exception) {}

            runCaptureLoop()
        }, "MicCaptureThread").apply {
            priority = Thread.MAX_PRIORITY
            start()
        }
    }

    private fun setupBluetoothAudioRouting() {
        try {
            audioManager.mode = AudioManager.MODE_IN_COMMUNICATION

            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
                val commDevices = audioManager.availableCommunicationDevices
                val btDevice = commDevices.find {
                    it.type == AudioDeviceInfo.TYPE_BLUETOOTH_SCO ||
                    it.type == AudioDeviceInfo.TYPE_BLE_HEADSET ||
                    it.type == AudioDeviceInfo.TYPE_WIRED_HEADSET
                }
                if (btDevice != null) {
                    audioManager.setCommunicationDevice(btDevice)
                }
            }

            if (audioManager.isBluetoothScoAvailableOffCall) {
                try {
                    audioManager.startBluetoothSco()
                    audioManager.isBluetoothScoOn = true
                } catch (_: Exception) {}
            }
        } catch (e: Exception) {
            listener?.onMicError("Unable to enable Bluetooth audio: ${e.message}")
        }
    }

    private data class RecordConfig(
        val source: Int,
        val sampleRate: Int,
        val sourceName: String
    )

    @SuppressLint("MissingPermission")
    private fun createAudioRecordWithFallback(): Pair<AudioRecord, RecordConfig>? {
        val configs = listOf(
            RecordConfig(MediaRecorder.AudioSource.VOICE_COMMUNICATION, 16000, "Bluetooth Headset SCO (16 kHz -> 48 kHz)"),
            RecordConfig(MediaRecorder.AudioSource.VOICE_COMMUNICATION, 48000, "Bluetooth Headset HD (48 kHz)"),
            RecordConfig(MediaRecorder.AudioSource.VOICE_RECOGNITION, 16000, "Headset Microphone (16 kHz -> 48 kHz)"),
            RecordConfig(MediaRecorder.AudioSource.MIC, 48000, "Phone Microphone (48 kHz)"),
            RecordConfig(MediaRecorder.AudioSource.MIC, 16000, "Phone Microphone (16 kHz -> 48 kHz)"),
            RecordConfig(MediaRecorder.AudioSource.DEFAULT, 16000, "Standard (16 kHz -> 48 kHz)"),
            RecordConfig(MediaRecorder.AudioSource.DEFAULT, 48000, "Standard (48 kHz)")
        )

        for (cfg in configs) {
            try {
                val minBuffer = AudioRecord.getMinBufferSize(
                    cfg.sampleRate,
                    AudioFormat.CHANNEL_IN_MONO,
                    AudioFormat.ENCODING_PCM_16BIT
                )
                if (minBuffer <= 0) continue

                val bufferSize = minBuffer.coerceAtLeast(cfg.sampleRate / 100 * 2)
                val record = AudioRecord(
                    cfg.source,
                    cfg.sampleRate,
                    AudioFormat.CHANNEL_IN_MONO,
                    AudioFormat.ENCODING_PCM_16BIT,
                    bufferSize
                )

                if (record.state == AudioRecord.STATE_INITIALIZED) {
                    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
                        try {
                            val inputDevices = audioManager.getDevices(AudioManager.GET_DEVICES_INPUTS)
                            val btMic = inputDevices.find {
                                it.type == AudioDeviceInfo.TYPE_BLUETOOTH_SCO ||
                                it.type == AudioDeviceInfo.TYPE_BLE_HEADSET ||
                                it.type == AudioDeviceInfo.TYPE_WIRED_HEADSET
                            }
                            if (btMic != null) {
                                record.preferredDevice = btMic
                            }
                        } catch (_: Exception) {}
                    }
                    return Pair(record, cfg)
                } else {
                    record.release()
                }
            } catch (_: Exception) {
                // Try next configuration
            }
        }
        return null
    }

    @SuppressLint("MissingPermission")
    private fun runCaptureLoop() {
        val recordPair = createAudioRecordWithFallback()
        if (recordPair == null) {
            listener?.onMicError("Unable to initialize the microphone. Check microphone permissions.")
            cleanup()
            return
        }

        audioRecord = recordPair.first
        val config = recordPair.second
        activeSampleRate = config.sampleRate

        var displaySourceName = config.sourceName
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
            val routed = audioRecord?.routedDevice
            if (routed != null && routed.productName.isNotBlank()) {
                displaySourceName = "${config.sourceName} [${routed.productName}]"
            }
        }
        listener?.onMicStarted(activeSampleRate, displaySourceName)

        try {
            audioRecord?.startRecording()
        } catch (e: Exception) {
            listener?.onMicError("Unable to start recording: ${e.message}")
            cleanup()
            return
        }

        val isUsbCable = serverIp == "127.0.0.1" || serverIp.equals("localhost", ignoreCase = true)

        try {
            var serverAddr: InetAddress? = null
            var useTcp = false

            // Try lossless TCP first (for both USB and Wi-Fi)
            try {
                val s = Socket().apply {
                    tcpNoDelay = true
                    sendBufferSize = 128 * 1024
                    soTimeout = 4000
                    try { trafficClass = 0xB8 } catch (_: Exception) {}
                }
                s.connect(InetSocketAddress(serverIp, serverPort), 2500)
                tcpSocket = s
                tcpOutputStream = s.getOutputStream()
                useTcp = true
            } catch (_: Exception) {
                useTcp = false
            }

            if (!useTcp && !isUsbCable) {
                try {
                    serverAddr = InetAddress.getByName(serverIp)
                    udpSocket = DatagramSocket().apply {
                        sendBufferSize = 64 * 1024
                        try { trafficClass = 0xB8 } catch (_: Exception) {}
                    }
                } catch (_: Exception) {}
            }

            // Buffers
            val isNative48k = (activeSampleRate == 48000)
            val captureSamplesPer10ms = activeSampleRate / 100 // 480 for 48k, 160 for 16k
            val captureBytesPer10ms = captureSamplesPer10ms * 2

            val rawCaptureBuffer = ByteArray(captureBytesPer10ms)
            val pcm48kBuffer = ByteArray(PAYLOAD_SIZE)
            val sendPacketBuffer = ByteArray(PACKET_SIZE)
            val zeroSilence = ByteArray(PAYLOAD_SIZE)
            var udpPacket = if (!useTcp && serverAddr != null) {
                DatagramPacket(sendPacketBuffer, sendPacketBuffer.size, serverAddr, serverPort)
            } else null

            sendPacketBuffer[0] = MAGIC_0
            sendPacketBuffer[1] = MAGIC_1

            var seqNumber = 0
            var totalPacketsSent = 0L
            var lastStatsTime = SystemClock.elapsedRealtime()

            while (isRunning) {
                val record = audioRecord ?: break

                if (record.recordingState != AudioRecord.RECORDSTATE_RECORDING) {
                    try {
                        record.startRecording()
                    } catch (_: Exception) {}
                    Thread.sleep(20)
                    continue
                }

                // Read exact 10ms of PCM data
                var bytesReadTotal = 0
                while (bytesReadTotal < captureBytesPer10ms && isRunning) {
                    val read = record.read(rawCaptureBuffer, bytesReadTotal, captureBytesPer10ms - bytesReadTotal)
                    if (read > 0) {
                        bytesReadTotal += read
                    } else if (read < 0) {
                        Thread.sleep(5)
                        break
                    }
                }

                if (!isRunning) break

                if (bytesReadTotal == captureBytesPer10ms) {
                    if (isNative48k) {
                        System.arraycopy(rawCaptureBuffer, 0, pcm48kBuffer, 0, PAYLOAD_SIZE)
                    } else if (activeSampleRate == 16000) {
                        upsample16kTo48k(rawCaptureBuffer, pcm48kBuffer)
                    } else {
                        System.arraycopy(rawCaptureBuffer, 0, pcm48kBuffer, 0, Math.min(bytesReadTotal, PAYLOAD_SIZE))
                    }

                    // Sequence number (uint16)
                    sendPacketBuffer[2] = (seqNumber shr 8).toByte()
                    sendPacketBuffer[3] = (seqNumber and 0xFF).toByte()
                    seqNumber = (seqNumber + 1) and 0xFFFF

                    // Timestamp (uint32 ms)
                    val ts = (SystemClock.elapsedRealtime() and 0xFFFFFFFFL).toInt()
                    sendPacketBuffer[4] = (ts shr 24).toByte()
                    sendPacketBuffer[5] = (ts shr 16).toByte()
                    sendPacketBuffer[6] = (ts shr 8).toByte()
                    sendPacketBuffer[7] = (ts and 0xFF).toByte()

                    // Payload: voice or silence
                    if (isMuted) {
                        System.arraycopy(zeroSilence, 0, sendPacketBuffer, HEADER_SIZE, PAYLOAD_SIZE)
                    } else {
                        System.arraycopy(pcm48kBuffer, 0, sendPacketBuffer, HEADER_SIZE, PAYLOAD_SIZE)
                    }

                    // Dispatch to transport (lossless TCP or fallback UDP)
                    if (useTcp && tcpOutputStream != null) {
                        try {
                            tcpOutputStream?.write(sendPacketBuffer, 0, PACKET_SIZE)
                            totalPacketsSent++
                        } catch (_: Exception) {
                            useTcp = false
                            if (udpSocket == null && !isUsbCable) {
                                try {
                                    serverAddr = InetAddress.getByName(serverIp)
                                    udpSocket = DatagramSocket().apply {
                                        sendBufferSize = 64 * 1024
                                        try { trafficClass = 0xB8 } catch (_: Exception) {}
                                    }
                                } catch (_: Exception) {}
                            }
                        }
                    } else if (udpSocket != null && serverAddr != null) {
                        try {
                            if (udpPacket == null) {
                                udpPacket = DatagramPacket(sendPacketBuffer, sendPacketBuffer.size, serverAddr, serverPort)
                            }
                            udpSocket?.send(udpPacket)
                            totalPacketsSent++
                        } catch (_: Exception) {}
                    }

                    // Periodic stats
                    val now = SystemClock.elapsedRealtime()
                    if (now - lastStatsTime >= 200) {
                        val levelRms = if (isMuted) 0 else computeRms(pcm48kBuffer)
                        listener?.onMicStats(totalPacketsSent, levelRms)
                        lastStatsTime = now
                    }
                }
            }
        } catch (e: Exception) {
            listener?.onMicError("Microphone stream error: ${e.message}")
        } finally {
            cleanup()
        }
    }

    private var lastSample16k: Int = 0

    private fun upsample16kTo48k(src: ByteArray, dst: ByteArray) {
        val numSamples = 160
        var prev = lastSample16k

        for (i in 0 until numSamples) {
            val idx = i * 2
            var curr = (src[idx].toInt() and 0xFF) or (src[idx + 1].toInt() shl 8)
            if (curr >= 32768) curr -= 65536

            curr = (curr * 5 / 4).coerceIn(-32768, 32767)

            val out0 = (prev * 2 + curr) / 3
            val out1 = (prev + curr * 2) / 3
            val out2 = curr

            val idxDst = i * 6
            dst[idxDst] = (out0 and 0xFF).toByte()
            dst[idxDst + 1] = ((out0 shr 8) and 0xFF).toByte()
            dst[idxDst + 2] = (out1 and 0xFF).toByte()
            dst[idxDst + 3] = ((out1 shr 8) and 0xFF).toByte()
            dst[idxDst + 4] = (out2 and 0xFF).toByte()
            dst[idxDst + 5] = ((out2 shr 8) and 0xFF).toByte()

            prev = curr
        }
        lastSample16k = prev
    }

    private fun computeRms(buffer: ByteArray): Int {
        var sum = 0.0
        val sampleCount = buffer.size / 2
        for (i in 0 until sampleCount) {
            val idx = i * 2
            var sample = (buffer[idx].toInt() and 0xFF) or (buffer[idx + 1].toInt() shl 8)
            if (sample >= 32768) sample -= 65536
            sum += sample * sample
        }
        val mean = sum / sampleCount
        return sqrt(mean).toInt()
    }

    private fun cleanup() {
        try {
            audioRecord?.stop()
            audioRecord?.release()
        } catch (_: Exception) {}
        audioRecord = null

        try {
            tcpOutputStream?.close()
        } catch (_: Exception) {}
        tcpOutputStream = null

        try {
            tcpSocket?.close()
        } catch (_: Exception) {}
        tcpSocket = null

        try {
            udpSocket?.close()
        } catch (_: Exception) {}
        udpSocket = null

        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
                audioManager.clearCommunicationDevice()
            } else {
                if (audioManager.isBluetoothScoOn) {
                    audioManager.stopBluetoothSco()
                    audioManager.isBluetoothScoOn = false
                }
            }
            audioManager.mode = AudioManager.MODE_NORMAL
        } catch (_: Exception) {}
    }

    fun stop() {
        if (!isRunning) return
        isRunning = false
        recordThread?.interrupt()
        recordThread = null
    }
}
