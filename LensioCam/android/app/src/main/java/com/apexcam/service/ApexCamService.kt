package com.apexcam.service

import android.content.Intent
import android.os.Build
import android.util.Log
import android.widget.Toast
import androidx.lifecycle.lifecycleScope
import com.apexcam.camera.CameraXManager
import com.apexcam.network.HttpStreamServer
import com.apexcam.network.NativeStreamSender
import com.apexcam.network.NsdDiscovery
import com.apexcam.network.UdpDiscovery
import com.apexcam.webrtc.SignalingClient
import com.apexcam.webrtc.WebRtcClient
import com.apexcam.network.UpdateChecker
import com.apexcam.network.VersionInfo
import android.content.IntentFilter
import android.os.BatteryManager
import com.google.gson.JsonObject
import kotlinx.coroutines.launch
import org.webrtc.EglBase

import android.app.*
import android.content.Context
import android.net.wifi.WifiManager
import android.os.IBinder
import android.os.Binder
import android.os.PowerManager
import androidx.core.app.NotificationCompat
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleOwner
import androidx.lifecycle.LifecycleRegistry
import com.apexcam.MainActivity
class ApexCamService : Service(), LifecycleOwner, SignalingClient.SignalingListener {
    companion object {
        const val ACTION_STOP = "com.apexcam.STOP_STREAMING"
        var instance: ApexCamService? = null
            private set
    }
    private val captureLifecycle = LifecycleRegistry(this)
    override val lifecycle: Lifecycle get() = captureLifecycle
    val sessionActive = androidx.compose.runtime.mutableStateOf(true)
    private var isDestroyed = false
    inner class LocalBinder : Binder() { val service get() = this@ApexCamService }
    private val binder = LocalBinder()
    private val channelId = "apexcam_streaming_channel"
    private val notificationId = 10101
    private var wakeLock: PowerManager.WakeLock? = null
    private var wifiLock: WifiManager.WifiLock? = null
    var audioReceiverEngine: com.apexcam.audio.AudioReceiverEngine? = null
        private set
    var micCaptureEngine: com.apexcam.audio.MicCaptureEngine? = null
        private set
    private val tag = "LensioStreamingService"
    private lateinit var rootEglBase: EglBase
    lateinit var cameraManager: CameraXManager
    private lateinit var signalingClient: SignalingClient
    private lateinit var webRtcClient: WebRtcClient
    private lateinit var nsdDiscovery: NsdDiscovery
    private lateinit var udpDiscovery: UdpDiscovery
    private lateinit var telemetryMonitor: TelemetryMonitor

    var currentServerUrl = ""
    var isUsbMode = false
    val isStreamConnected = androidx.compose.runtime.mutableStateOf(false)
    val phoneIpState = androidx.compose.runtime.mutableStateOf("0.0.0.0")
    val framesSentState = androidx.compose.runtime.mutableStateOf(0L)
    val fpsState = androidx.compose.runtime.mutableStateOf(30)
    val isPcAudioActive = androidx.compose.runtime.mutableStateOf(false)
    val isMicActive = androidx.compose.runtime.mutableStateOf(false)

    val availableUpdate = androidx.compose.runtime.mutableStateOf<VersionInfo?>(null)
    val isDownloadingUpdate = androidx.compose.runtime.mutableStateOf(false)
    val downloadProgress = androidx.compose.runtime.mutableStateOf(0f)
    private var lastUpdateCheckTime = 0L

    private var cachedBatteryLevel: Int = 100
    private var cachedIsCharging: Boolean = false
    private var cachedDeviceTemp: Float = 30.0f

    private val batteryReceiver = object : android.content.BroadcastReceiver() {
        override fun onReceive(context: android.content.Context?, intent: Intent?) {
            if (intent == null) return
            val level = intent.getIntExtra(BatteryManager.EXTRA_LEVEL, -1)
            val scale = intent.getIntExtra(BatteryManager.EXTRA_SCALE, -1)
            if (level >= 0 && scale > 0) {
                cachedBatteryLevel = (level * 100) / scale
            }
            val status = intent.getIntExtra(BatteryManager.EXTRA_STATUS, -1)
            val plugged = intent.getIntExtra(BatteryManager.EXTRA_PLUGGED, 0)
            cachedIsCharging = (status == BatteryManager.BATTERY_STATUS_CHARGING ||
                                status == BatteryManager.BATTERY_STATUS_FULL ||
                                plugged != 0)
            val tempRaw = intent.getIntExtra(BatteryManager.EXTRA_TEMPERATURE, 0)
            if (tempRaw > 0) {
                cachedDeviceTemp = tempRaw / 10.0f
            }
        }
    }

    private fun getSavedPcIp(): String {
        val prefs = getSharedPreferences("apexcam_prefs", android.content.Context.MODE_PRIVATE)
        return prefs.getString("last_pc_ip", "") ?: ""
    }

    private fun savePcIp(ip: String) {
        if (ip.isBlank() || ip == "127.0.0.1") return
        val prefs = getSharedPreferences("apexcam_prefs", android.content.Context.MODE_PRIVATE)
        prefs.edit().putString("last_pc_ip", ip).apply()
    }

    private val httpStreamServer = HttpStreamServer(8080)
    private val nativeSender = NativeStreamSender()

    private var pipelineStarted = false
    private var reconnectTicks = 0
    private var lastStreamConnectedAt = 0L
    private val reconnectHandler = android.os.Handler(android.os.Looper.getMainLooper())
    private val reconnectRunnable = object : Runnable {
        override fun run() {
            cameraManager.checkCaptureHealth(nativeSender.isConnected || httpStreamServer.activeClientCount > 0 ||
                (lastStreamConnectedAt > 0 && android.os.SystemClock.elapsedRealtime() - lastStreamConnectedAt < 15000))
            nativeSender.checkHealth()

            val connected = nativeSender.isConnected || httpStreamServer.activeClientCount > 0
            if (isStreamConnected.value != connected) {
                isStreamConnected.value = connected
            }
            framesSentState.value = nativeSender.totalFramesSent
            fpsState.value = nativeSender.currentFps
            phoneIpState.value = UdpDiscovery.getLocalIpAddress()

            if (!nativeSender.isConnected && httpStreamServer.activeClientCount == 0) {
                reconnectTicks++
                val clean = currentServerUrl.removePrefix("ws://").removePrefix("http://").trim()
                val host = clean.substringBefore(":")
                val target = if (host.isNotBlank()) host else getSavedPcIp()
                nativeSender.connect(target, 8556)

                // Every 4 cycles (~6 seconds) when disconnected, scan subnet in parallel to find PC automatically
                if (reconnectTicks % 4 == 0 && ::udpDiscovery.isInitialized) {
                    udpDiscovery.scanSubnetForServer(8556) { foundIp, foundPort ->
                        runOnUiThread {
                            if (!nativeSender.isConnected) {
                                savePcIp(foundIp)
                                currentServerUrl = "$foundIp:$foundPort"
                                isUsbMode = false
                                nativeSender.connect(foundIp, foundPort)
                                triggerUpdateCheck(foundIp)
                            }
                        }
                    }
                }
            } else {
                reconnectTicks = 0
            }
            reconnectHandler.postDelayed(this, 1500)
        }
    }

    private var updateCheckJob: kotlinx.coroutines.Job? = null

    fun triggerUpdateCheck(pcIp: String, forceGitHub: Boolean = false) {
        if (updateCheckJob?.isActive == true) return
        val now = System.currentTimeMillis()
        if (now - lastUpdateCheckTime < 10000) return // Check at most every 10 seconds
        lastUpdateCheckTime = now

        val host = pcIp.removePrefix("ws://").removePrefix("http://").substringBefore(":")


        updateCheckJob = lifecycleScope.launch {
            val info = UpdateChecker.checkUpdate(host, forceGitHub)
            if (info != null) {
                availableUpdate.value = info
            }
        }
    }

    fun startUpdateDownload(pcIp: String) {
        val host = pcIp.removePrefix("ws://").removePrefix("http://").substringBefore(":")
        isDownloadingUpdate.value = true
        downloadProgress.value = 0f
        lifecycleScope.launch {
            UpdateChecker.downloadAndInstallApk(
                context = this@ApexCamService,
                pcIp = host,
                onProgress = { p -> downloadProgress.value = p },
                onError = { err ->
                    isDownloadingUpdate.value = false
                    Toast.makeText(this@ApexCamService, "Update error: $err", Toast.LENGTH_LONG).show()
                }
            )
        }
    }


    override fun onCreate() {
        super.onCreate()
        instance = this
        captureLifecycle.currentState = Lifecycle.State.CREATED
        createNotificationChannel()
        rootEglBase = EglBase.create()
        cameraManager = CameraXManager(this, this)
        signalingClient = SignalingClient(this)
        webRtcClient = WebRtcClient(this, rootEglBase, signalingClient)
        nsdDiscovery = NsdDiscovery(this)
        telemetryMonitor = TelemetryMonitor(this, signalingClient)

        // Register live battery & charging receiver
        val batteryFilter = IntentFilter(Intent.ACTION_BATTERY_CHANGED)
        val stickyBattery = registerReceiver(batteryReceiver, batteryFilter)
        if (stickyBattery != null) {
            batteryReceiver.onReceive(this, stickyBattery)
        }

        // Start embedded HTTP server & link with native sender
        val devicePrefs = getSharedPreferences("lensio_device", MODE_PRIVATE)
        val deviceId = devicePrefs.getString("id",null) ?: java.util.UUID.randomUUID().toString().also {
            devicePrefs.edit().putString("id",it).apply()
        }
        nativeSender.deviceIdentityJson = com.google.gson.Gson().toJson(mapOf(
            "id" to deviceId, "name" to android.os.Build.MODEL, "version" to com.apexcam.BuildConfig.VERSION_NAME))
        nativeSender.httpStreamServer = httpStreamServer
        httpStreamServer.onReconnectRequested = {
            Log.d(tag, "Cerere de reconectare primita prin HTTP")
            nativeSender.disconnect()
        }
        httpStreamServer.cameraCatalogProvider = { cameraManager.cameraCatalogJson() }
        httpStreamServer.start()
        phoneIpState.value = UdpDiscovery.getLocalIpAddress()

        nativeSender.onConnectedHost = { host ->
            runOnUiThread {
                savePcIp(host)
                currentServerUrl = "$host:8556"
                triggerUpdateCheck(host)
            }
        }

        nativeSender.onCommandReceived = { cmd ->
            try {
                val json = com.google.gson.JsonParser.parseString(cmd).asJsonObject
                onCameraControlReceived(json)
            } catch (e: Exception) {}
        }

        nativeSender.onConnectionChanged = { connected ->
            runOnUiThread {
                if (connected) {
                    lastStreamConnectedAt = android.os.SystemClock.elapsedRealtime()
                    cameraManager.checkCaptureHealth(true)
                }
                isStreamConnected.value = connected || httpStreamServer.activeClientCount > 0
            }
        }

        currentServerUrl = "${getSavedPcIp()}:8556"
        triggerUpdateCheck(getSavedPcIp())
    }
    private fun runOnUiThread(action: () -> Unit) {
        if (android.os.Looper.myLooper() == android.os.Looper.getMainLooper()) {
            if (!isDestroyed) action()
        } else reconnectHandler.post { if (!isDestroyed) action() }
    }
    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_STOP) {
            stopStreaming()
            return START_NOT_STICKY
        }
        if (!isDestroyed) {
            startForeground(notificationId, buildNotification())
            if (!pipelineStarted) {
                acquireLocks()
                captureLifecycle.currentState = Lifecycle.State.RESUMED
                setupStreamingPipeline()
            }
        }
        return START_NOT_STICKY
    }
    override fun onBind(intent: Intent?): IBinder = binder
    fun connectToPhoneTarget(input: String) {
        val clean = input.removePrefix("ws://").removePrefix("http://").trim()
        val host = clean.substringBefore(":")
        val port = clean.substringAfter(":", "8556").toIntOrNull() ?: 8556
        currentServerUrl = "$host:$port"
        isUsbMode = host == "127.0.0.1"
        savePcIp(host)
        nativeSender.connect(host, port, force = true)
        triggerUpdateCheck(host)
    }
    fun checkUpdateNow() { lastUpdateCheckTime = 0L; triggerUpdateCheck(currentServerUrl, forceGitHub = true) }
    fun installAvailableUpdate() { startUpdateDownload(currentServerUrl) }
    fun startPcAudio(targetHost: String) {
        if (audioReceiverEngine == null) {
            audioReceiverEngine = com.apexcam.audio.AudioReceiverEngine(targetHost, 44100).apply {
                start()
            }
            updateNotification("PC Audio")
        }
    }

    fun stopPcAudio() {
        audioReceiverEngine?.stop()
        audioReceiverEngine = null
        updateNotification()
    }

    fun startMic(targetHost: String, ctx: Context = this) {
        if (micCaptureEngine == null) {
            micCaptureEngine = com.apexcam.audio.MicCaptureEngine(targetHost, 44101, ctx).apply {
                start()
            }
            updateNotification("Microphone")
        }
    }

    fun stopMic() {
        micCaptureEngine?.stop()
        micCaptureEngine = null
        updateNotification()
    }

    private fun buildNotification(extraStatus: String? = null): Notification {
        val notificationIntent = Intent(this, MainActivity::class.java)
        val pendingIntent = PendingIntent.getActivity(
            this,
            0,
            notificationIntent,
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        )

        var details = "Camera active • continues with the screen off."
        val activeParts = mutableListOf<String>()
        if (audioReceiverEngine != null) activeParts.add("🔊 PC Audio")
        if (micCaptureEngine != null) activeParts.add("🎙️ Microphone")
        if (extraStatus != null && !activeParts.contains(extraStatus)) activeParts.add(extraStatus)

        if (activeParts.isNotEmpty()) {
            details = activeParts.joinToString(" • ") + " • Streaming."
        }

        val stopIntent = PendingIntent.getService(this, 1,
            Intent(this, ApexCamService::class.java).setAction(ACTION_STOP),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val notification = NotificationCompat.Builder(this, channelId)
            .setContentTitle("LensioCam")
            .setContentText(details)
            .setSmallIcon(android.R.drawable.ic_menu_camera)
            .setContentIntent(pendingIntent)
            .addAction(android.R.drawable.ic_media_pause, "Stop camera", stopIntent)
            .setOngoing(true)
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .build()

        return notification
    }
    private fun updateNotification(extraStatus: String? = null) {
        if (!isDestroyed) getSystemService(NotificationManager::class.java)?.notify(notificationId,buildNotification(extraStatus))
    }

    private fun acquireLocks() {
        val powerManager = getSystemService(Context.POWER_SERVICE) as PowerManager
        wakeLock = powerManager.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "ApexCam::StreamingWakeLock").apply {
            setReferenceCounted(false)
            acquire() // Released only by explicit session stop/service destruction.
        }

        val wifiManager = applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
        wifiLock = wifiManager.createWifiLock(WifiManager.WIFI_MODE_FULL_HIGH_PERF, "ApexCam::StreamingWifiLock").apply {
            acquire()
        }
    }

    private fun createNotificationChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val channel = NotificationChannel(
                channelId,
                "LensioCam Video Streaming",
                NotificationManager.IMPORTANCE_LOW
            ).apply {
                description = "Persistent notification for low-latency streaming"
            }
            val manager = getSystemService(NotificationManager::class.java)
            manager?.createNotificationChannel(channel)
        }
    }

    private fun setupStreamingPipeline() {
        if (pipelineStarted || isDestroyed) return
        pipelineStarted = true
        // 2. Connect CameraX frame pipeline to native high-speed sender (zero WebRTC overhead)
        if (cameraManager.usesSharedPreview) nativeSender.onPreviewJpeg = { cameraManager.showEncodedPreview(it) }
        cameraManager.onFrameAvailable = { imageProxy ->
            nativeSender.sendFrame(imageProxy, cachedBatteryLevel, cachedDeviceTemp, cachedIsCharging)
        }

        // 4. Start local UDP discovery on Wi-Fi (bidirectional)
        udpDiscovery = UdpDiscovery(this)
        udpDiscovery.isConnectedProvider = { nativeSender.isConnected || httpStreamServer.activeClientCount > 0 }
        udpDiscovery.onServerFound = { ip, port ->
            runOnUiThread {
                if (!nativeSender.isConnected) {
                    Log.d(tag, "PC găsit prin UDP Beacon: $ip:$port! Conectare automată...")
                    savePcIp(ip)
                    currentServerUrl = "$ip:$port"
                    isUsbMode = false
                    nativeSender.connect(ip, port)
                }
            }
        }
        udpDiscovery.start()

        nsdDiscovery.onServerFound = found@ { serverUrl ->
            if (nativeSender.isConnected || isDestroyed) return@found
            Log.d(tag, "Conectare automată la PC găsit prin mDNS: $serverUrl")
            val host = serverUrl.removePrefix("ws://").substringBefore(":")
            savePcIp(host)
            currentServerUrl = "$host:8556"
            nativeSender.connect(host, 8556)
        }
        nsdDiscovery.startDiscovery()

        // 5. Connect directly to saved or default PC LAN IP
        nativeSender.connect(getSavedPcIp(), 8556)
        reconnectHandler.postDelayed(reconnectRunnable, 1500)

        // 6. Start Telemetry Reporting
        cameraManager.startCapture()

        telemetryMonitor.start(
            resolution = "${cameraManager.settings.resolution.width}x${cameraManager.settings.resolution.height}",
            getFps = { cameraManager.settings.fps }
        )
    }

    // SignalingListener Implementations
    override fun onConnected() {
        Log.d(tag, "Semnalizare conectată! Inițiere PeerConnection WebRTC...")
        runOnUiThread {
            webRtcClient.createPeerConnection()
            webRtcClient.sendOffer()
        }
    }

    override fun onDisconnected() {
        Log.d(tag, "Semnalizare deconectată.")
    }

    override fun onOfferReceived(sdp: String) {
        // Desktop can also offer
    }

    override fun onAnswerReceived(sdp: String) {
        Log.d(tag, "Recepționat SDP Answer de la PC.")
        runOnUiThread {
            webRtcClient.handleRemoteAnswer(sdp)
        }
    }

    override fun onIceCandidateReceived(candidate: JsonObject) {
        runOnUiThread {
            webRtcClient.addRemoteIceCandidate(candidate)
        }
    }

    override fun onCameraControlReceived(control: JsonObject) {
        runOnUiThread {
            if (control.has("shutdown") && control.get("shutdown").asBoolean) {
                stopStreaming()
                return@runOnUiThread
            }
            Log.d(tag, "Comandă cameră primită de la PC: $control")
            if (control.has("cameraLens")) {
                val lensStr = control.get("cameraLens").asString.lowercase()
                val targetLens = when {
                    lensStr.contains("ultra") || lensStr.contains("0.5") -> com.apexcam.camera.CameraLensType.BACK_ULTRAWIDE
                    lensStr.contains("selfie") || lensStr.contains("front") -> com.apexcam.camera.CameraLensType.FRONT_SELFIE
                    else -> com.apexcam.camera.CameraLensType.BACK_MAIN
                }
                cameraManager.setCameraLens(targetLens)
            }
            if (control.has("stabilization")) cameraManager.setStabilization(control.get("stabilization").asString)
            if (control.has("cameraId")) {
                val targetId = control.get("cameraId").asString.trim()
                cameraManager.setDirectCameraId(targetId)
            }
            if (control.has("switchCamera")) {
                cameraManager.switchCamera()
            }
            if (control.has("zoom")) {
                val zoom = control.get("zoom").asFloat
                cameraManager.setZoom(zoom)
            }
            if (control.has("toggleTorch")) {
                cameraManager.toggleTorch()
            }
            if (control.has("muteAudio")) {
                val mute = control.get("muteAudio").asBoolean
                webRtcClient.setMuteAudio(mute)
            }
            if (control.has("resolution")) {
                val resStr = control.get("resolution").asString.lowercase()
                val targetSize = com.apexcam.camera.CameraSettings.parseResolution(resStr)
                if (cameraManager.settings.resolution != targetSize) {
                    cameraManager.setResolution(targetSize)
                    Toast.makeText(this@ApexCamService, "PC Resolution: ${targetSize.width}x${targetSize.height}", Toast.LENGTH_SHORT).show()
                }
            }
            if (control.has("exposure")) {
                val exp = control.get("exposure").asInt
                cameraManager.setExposure(exp)
            }
            if (control.has("streamPcAudio")) {
                val active = control.get("streamPcAudio").asBoolean
                isPcAudioActive.value = active
                val clean = currentServerUrl.removePrefix("ws://").removePrefix("http://").trim()
                val host = clean.substringBefore(":")
                val targetHost = if (host.isNotBlank()) host else getSavedPcIp()

                if (active) {
                    ApexCamService.instance?.startPcAudio(targetHost)
                } else {
                    ApexCamService.instance?.stopPcAudio()
                }
            }
            if (control.has("enableMic")) {
                val active = control.get("enableMic").asBoolean
                isMicActive.value = active
                val clean = currentServerUrl.removePrefix("ws://").removePrefix("http://").trim()
                val host = clean.substringBefore(":")
                val targetHost = if (host.isNotBlank()) host else getSavedPcIp()

                if (active) {
                    ApexCamService.instance?.startMic(targetHost, this@ApexCamService)
                } else {
                    ApexCamService.instance?.stopMic()
                }
            }
            if (control.has("triggerUpdate")) {
                val host = if (control.has("host")) control.get("host").asString else getSavedPcIp()
                startUpdateDownload(host)
                Toast.makeText(this@ApexCamService, "Downloading update from PC ($host)...", Toast.LENGTH_SHORT).show()
            } else if (control.has("check_update")) {
                val host = if (control.has("host")) control.get("host").asString else getSavedPcIp()
                triggerUpdateCheck(host)
            }
        }
    }


    fun stopStreaming() {
        if (isDestroyed) return
        isDestroyed = true
        sessionActive.value = false
        reconnectHandler.removeCallbacksAndMessages(null)
        runCatching { cameraManager.release() }
        runCatching { nativeSender.close() }
        runCatching { httpStreamServer.stop() }
        runCatching { if (::udpDiscovery.isInitialized) udpDiscovery.stop() }
        runCatching { nsdDiscovery.stopDiscovery() }
        runCatching { telemetryMonitor.stop() }
        runCatching { signalingClient.disconnect() }
        runCatching { webRtcClient.close() }
        runCatching { rootEglBase.release() }
        runCatching { ApexCamService.instance?.stopPcAudio() }
        runCatching { ApexCamService.instance?.stopMic() }
        runCatching { unregisterReceiver(batteryReceiver) }
        captureLifecycle.currentState = Lifecycle.State.DESTROYED
        wakeLock?.let { if (it.isHeld) it.release() }
        wifiLock?.let { if (it.isHeld) it.release() }
        stopForeground(STOP_FOREGROUND_REMOVE)
        stopSelf()
    }
    override fun onDestroy() {
        stopStreaming()
        if (instance == this) instance = null
        super.onDestroy()
    }
}
