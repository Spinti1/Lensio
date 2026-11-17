package com.apexcam

import android.Manifest
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.ServiceConnection
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.os.IBinder
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.runtime.*
import androidx.compose.material3.Text
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.ui.graphics.Color
import androidx.core.content.ContextCompat
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.lifecycleScope
import com.apexcam.network.UpdateChecker
import com.apexcam.network.UpdateInstaller
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import com.apexcam.service.ApexCamService
import com.apexcam.ui.CameraScreen

/** A detachable view of the foreground session; it never owns camera/network workers. */
class MainActivity : ComponentActivity() {
    private var streamService by mutableStateOf<ApexCamService?>(null)
    private var bound = false
    private var pendingInstall = false
    private var permissionInFlight = false
    private var installerInFlight = false
    private var updateStatus by mutableStateOf("")
    private var updateError by mutableStateOf<String?>(null)
    private lateinit var updateInstaller: UpdateInstaller
    private var installerWatchdog: Job? = null
    private var stagedSession = -1
    private var pendingConfirmation: Intent? = null
    private val connection = object : ServiceConnection {
        override fun onServiceConnected(name: ComponentName?, binder: IBinder?) {
            streamService = (binder as ApexCamService.LocalBinder).service
            streamService?.checkForUpdates(force = true)
            continuePendingUpdate()
        }
        override fun onServiceDisconnected(name: ComponentName?) { streamService = null }
    }
    private val permissionLauncher = registerForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) {
        if (hasCapturePermissions()) startSession()
    }
    private val updatePermissionLauncher = registerForActivityResult(ActivityResultContracts.StartActivityForResult()) {
        permissionInFlight = false
        pendingInstall = packageManager.canRequestPackageInstalls()
        if (pendingInstall) continuePendingUpdate()
        else updateError = "Installation permission was not granted. You can also use GitHub APK."
    }
    private val updateInstallerLauncher = registerForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
        // Session status is authoritative. Some OEM confirmation screens return RESULT_CANCELED early.
        if (result.resultCode != RESULT_OK && installerInFlight) watchInstaller(5000)
    }

    private fun finishUpdate(error: String?) {
        installerWatchdog?.cancel()
        installerInFlight = false
        pendingInstall = false
        pendingConfirmation = null
        updateStatus = ""
        updateError = error
        streamService?.checkForUpdates(force = true)
    }

    private fun watchInstaller(timeout: Long = 15000) {
        installerWatchdog?.cancel()
        installerWatchdog = lifecycleScope.launch {
            delay(timeout)
            if (installerInFlight && lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED)) {
                // Slow phones may return to Lensio while Android is still installing. Do not cancel real work.
                if (updateInstaller.isInstalling()) {
                    updateStatus = "Android is installing the update…"
                    watchInstaller(30000)
                    return@launch
                }
                updateInstaller.abandon()
                finishUpdate("Android installer did not open or installation was cancelled. Retry or use GitHub APK.")
            }
        }
    }

    private fun showInstallConfirmation() {
        val confirmation = pendingConfirmation ?: return
        if (!lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED)) return
        pendingConfirmation = null
        try {
            updateInstallerLauncher.launch(confirmation)
        } catch (error: Exception) {
            updateInstaller.abandon()
            finishUpdate("Unable to open Android installer. Use GitHub APK.")
        }
    }

    private fun openGitHubApk(version: String) {
        val url = UpdateChecker.trustedApkUrl(version) ?: return
        try { startActivity(Intent(Intent.ACTION_VIEW,android.net.Uri.parse(url)).addCategory(Intent.CATEGORY_BROWSABLE)) }
        catch (_: Exception) { updateError = "No browser is available to open GitHub APK." }
    }

    private fun continuePendingUpdate() {
        val service = streamService ?: return
        if (!pendingInstall || permissionInFlight || installerInFlight || service.isDownloadingUpdate.value ||
            !lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED)) return
        pendingInstall = false
        updateError = null
        updateStatus = "Preparing update…"
        service.isDownloadingUpdate.value = true
        service.downloadProgress.value = 0f
        lifecycleScope.launch {
            try {
                val apk = UpdateChecker.prepareUpdate(this@MainActivity,
                    onProgress = { service.downloadProgress.value = it }, onStage = { updateStatus = it })
                if (!lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED)) {
                    // Never open an installer over another app when a download finishes in the background.
                    pendingInstall = true
                } else if (!packageManager.canRequestPackageInstalls()) {
                    permissionInFlight = true
                    updatePermissionLauncher.launch(Intent(android.provider.Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES,
                        android.net.Uri.parse("package:$packageName")))
                } else {
                    updateStatus = "Preparing Android installer…"
                    stagedSession = updateInstaller.stage(apk)
                    if (!lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED)) {
                        updateInstaller.abandon(stagedSession)
                        stagedSession = -1
                        pendingInstall = true
                        return@launch
                    }
                    installerInFlight = true
                    updateStatus = "Opening Android installer…"
                    updateInstaller.commit(stagedSession)
                    stagedSession = -1
                    watchInstaller()
                }
            } catch (error: Exception) {
                if (error is kotlinx.coroutines.CancellationException) throw error
                permissionInFlight = false
                installerInFlight = false
                updateError = error.message ?: "Unable to install update. Use GitHub APK."
                service.checkForUpdates(force = true)
            } finally {
                if (stagedSession >= 0) { updateInstaller.abandon(stagedSession); stagedSession = -1 }
                service.isDownloadingUpdate.value = false
            }
        }
    }
    private fun hasCapturePermissions() = listOf(Manifest.permission.CAMERA,Manifest.permission.RECORD_AUDIO).all {
        ContextCompat.checkSelfPermission(this,it) == PackageManager.PERMISSION_GRANTED
    }
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        updateInstaller = UpdateInstaller(this,
            onConfirmation = {
                installerWatchdog?.cancel()
                pendingConfirmation = it
                showInstallConfirmation()
            }, onFinished = { finishUpdate(it) })
        pendingInstall = savedInstanceState?.getBoolean("pendingInstall") ?: false
        permissionInFlight = savedInstanceState?.getBoolean("updatePermissionInFlight") ?: false
        // Activity recreation must never restore a busy flag without an active installer callback.
        installerInFlight = false
        lifecycle.addObserver(androidx.lifecycle.LifecycleEventObserver { _, event ->
            // Activity results and service binding can arrive before Android dispatches RESUMED.
            if (event == Lifecycle.Event.ON_RESUME) {
                showInstallConfirmation()
                continuePendingUpdate()
                if (installerInFlight && pendingConfirmation == null) watchInstaller()
            }
        })
        onBackPressedDispatcher.addCallback(this,object : androidx.activity.OnBackPressedCallback(true) {
            override fun handleOnBackPressed() { moveTaskToBack(true) }
        })
        setContent {
          MaterialTheme(colorScheme=darkColorScheme(primary=Color(0xFF8CABFF),onPrimary=Color(0xFF071329),secondary=Color(0xFFB8A0FF),background=Color(0xFF0A0E19),surface=Color(0xFF171F32))) {
            val service = streamService
            if (service == null) Text("Starting LensioCam…")
            else {
                LaunchedEffect(service.sessionActive.value) {
                    if (!service.sessionActive.value) finishAndRemoveTask()
                }
                CameraScreen(
                    cameraManager=service.cameraManager,
                    isConnected=service.isStreamConnected.value,
                    isUsb=service.isUsbMode,
                    currentServerUrl=service.currentServerUrl,
                    phoneIp=service.phoneIpState.value,
                    framesCount=service.framesSentState.value,
                    fps=service.fpsState.value,
                    isPcAudioActive=service.isPcAudioActive.value,
                    isMicActive=service.isMicActive.value,
                    availableUpdate=service.availableUpdate.value,
                    isDownloadingUpdate=service.isDownloadingUpdate.value,
                    downloadProgress=service.downloadProgress.value,
                    updateStatus=updateStatus,
                    updateError=updateError,
                    installerPending=installerInFlight,
                    onStartUpdate={pendingInstall=true;continuePendingUpdate()},
                    onOpenUpdateApk={service.availableUpdate.value?.let {openGitHubApk(it.versionName)}},
                    onDismissUpdate={service.availableUpdate.value=null},
                    onCheckUpdate={service.checkUpdateNow()},
                    onManualConnect={service.connectToPhoneTarget(it)},
                    onStopStreaming={service.stopStreaming()}
                )
            }
          }
        }
    }
    override fun onStart() {
        super.onStart()
        if (hasCapturePermissions()) startSession()
        else {
            val permissions=mutableListOf(Manifest.permission.CAMERA,Manifest.permission.RECORD_AUDIO)
            if (Build.VERSION.SDK_INT>=33) permissions.add(Manifest.permission.POST_NOTIFICATIONS)
            permissionLauncher.launch(permissions.toTypedArray())
        }
    }
    private fun startSession() {
        if (bound || isFinishing || isDestroyed ||
            !lifecycle.currentState.isAtLeast(androidx.lifecycle.Lifecycle.State.STARTED)) return
        // Camera/microphone foreground services must be started from a visible activity.
        val intent=Intent(this,ApexCamService::class.java)
        ContextCompat.startForegroundService(this,intent)
        bound=bindService(intent,connection,Context.BIND_AUTO_CREATE)
    }
    override fun onResume() {
        super.onResume()
        // Some Android versions publish ON_START after onStart() returns.
        if (hasCapturePermissions()) startSession()
    }
    override fun onSaveInstanceState(outState: Bundle) {
        outState.putBoolean("pendingInstall",pendingInstall)
        outState.putBoolean("updatePermissionInFlight",permissionInFlight)
        super.onSaveInstanceState(outState)
    }
    override fun onStop() {
        streamService?.cameraManager?.detachPreview()
        if (bound) { unbindService(connection);bound=false }
        streamService=null
        super.onStop()
    }
    override fun onDestroy() {
        super.onDestroy()
        installerWatchdog?.cancel()
        if (stagedSession >= 0) updateInstaller.abandon(stagedSession)
        updateInstaller.close()
    }
}
