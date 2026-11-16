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
import androidx.core.content.FileProvider
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.lifecycleScope
import com.apexcam.network.UpdateChecker
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
        else updateMessage("Installation permission was not granted")
    }
    private val updateInstallerLauncher = registerForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
        installerInFlight = false
        pendingInstall = false
        streamService?.checkForUpdates(force = true)
        if (result.resultCode != RESULT_OK) updateMessage("Update was not installed. You can retry without downloading again.")
    }
    private fun updateMessage(message: String) = android.widget.Toast.makeText(this,message,android.widget.Toast.LENGTH_LONG).show()

    @Suppress("DEPRECATION") // ACTION_INSTALL_PACKAGE supports the existing Android 8+ clients and returns the install result.
    private fun continuePendingUpdate() {
        val service = streamService ?: return
        if (!pendingInstall || permissionInFlight || installerInFlight || service.isDownloadingUpdate.value ||
            !lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED)) return
        pendingInstall = false
        service.isDownloadingUpdate.value = true
        service.downloadProgress.value = 0f
        lifecycleScope.launch {
            try {
                val apk = UpdateChecker.prepareUpdate(this@MainActivity) { service.downloadProgress.value = it }
                if (!lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED)) {
                    // Never open an installer over another app when a download finishes in the background.
                    pendingInstall = true
                } else if (!packageManager.canRequestPackageInstalls()) {
                    permissionInFlight = true
                    updatePermissionLauncher.launch(Intent(android.provider.Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES,
                        android.net.Uri.parse("package:$packageName")))
                } else {
                    val uri=FileProvider.getUriForFile(this@MainActivity,"$packageName.provider",apk)
                    installerInFlight = true
                    updateInstallerLauncher.launch(Intent(Intent.ACTION_INSTALL_PACKAGE).apply {
                        setDataAndType(uri,"application/vnd.android.package-archive")
                        addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
                        putExtra(Intent.EXTRA_RETURN_RESULT,true)
                    })
                }
            } catch (error: Exception) {
                if (error is kotlinx.coroutines.CancellationException) throw error
                permissionInFlight = false
                installerInFlight = false
                updateMessage(error.message ?: "Unable to install update")
                service.checkForUpdates(force = true)
            } finally { service.isDownloadingUpdate.value = false }
        }
    }
    private fun hasCapturePermissions() = listOf(Manifest.permission.CAMERA,Manifest.permission.RECORD_AUDIO).all {
        ContextCompat.checkSelfPermission(this,it) == PackageManager.PERMISSION_GRANTED
    }
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        pendingInstall = savedInstanceState?.getBoolean("pendingInstall") ?: false
        permissionInFlight = savedInstanceState?.getBoolean("updatePermissionInFlight") ?: false
        installerInFlight = savedInstanceState?.getBoolean("updateInstallerInFlight") ?: false
        lifecycle.addObserver(androidx.lifecycle.LifecycleEventObserver { _, event ->
            // Activity results and service binding can arrive before Android dispatches RESUMED.
            if (event == Lifecycle.Event.ON_RESUME) continuePendingUpdate()
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
                    onStartUpdate={pendingInstall=true;continuePendingUpdate()},
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
        outState.putBoolean("updateInstallerInFlight",installerInFlight)
        super.onSaveInstanceState(outState)
    }
    override fun onStop() {
        streamService?.cameraManager?.detachPreview()
        if (bound) { unbindService(connection);bound=false }
        streamService=null
        super.onStop()
    }
}
