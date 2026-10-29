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
import androidx.core.content.ContextCompat
import com.apexcam.service.ApexCamService
import com.apexcam.ui.CameraScreen

/** A detachable view of the foreground session; it never owns camera/network workers. */
class MainActivity : ComponentActivity() {
    private var streamService by mutableStateOf<ApexCamService?>(null)
    private var bound = false
    private val connection = object : ServiceConnection {
        override fun onServiceConnected(name: ComponentName?, binder: IBinder?) {
            streamService = (binder as ApexCamService.LocalBinder).service
        }
        override fun onServiceDisconnected(name: ComponentName?) { streamService = null }
    }
    private val permissionLauncher = registerForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) {
        if (hasCapturePermissions()) startSession()
    }
    private fun hasCapturePermissions() = listOf(Manifest.permission.CAMERA,Manifest.permission.RECORD_AUDIO).all {
        ContextCompat.checkSelfPermission(this,it) == PackageManager.PERMISSION_GRANTED
    }
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        onBackPressedDispatcher.addCallback(this,object : androidx.activity.OnBackPressedCallback(true) {
            override fun handleOnBackPressed() { moveTaskToBack(true) }
        })
        setContent {
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
                    onStartUpdate={service.installAvailableUpdate()},
                    onDismissUpdate={service.availableUpdate.value=null},
                    onCheckUpdate={service.checkUpdateNow()},
                    onManualConnect={service.connectToPhoneTarget(it)},
                    onStopStreaming={service.stopStreaming()}
                )
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
    override fun onStop() {
        streamService?.cameraManager?.detachPreview()
        if (bound) { unbindService(connection);bound=false }
        streamService=null
        super.onStop()
    }
}
