package com.apexcam.ui

import android.view.ViewGroup
import androidx.camera.view.PreviewView
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.gestures.detectTransformGestures
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Close
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import com.apexcam.camera.CameraXManager
import com.apexcam.network.VersionInfo

@Composable
fun CameraScreen(
    cameraManager: CameraXManager,
    isConnected: Boolean,
    isUsb: Boolean,
    currentServerUrl: String,
    phoneIp: String = "0.0.0.0",
    framesCount: Long = 0,
    fps: Int = 30,
    isPcAudioActive: Boolean = false,
    isMicActive: Boolean = false,
    availableUpdate: VersionInfo? = null,
    isDownloadingUpdate: Boolean = false,
    downloadProgress: Float = 0f,
    onStartUpdate: () -> Unit = {},
    onDismissUpdate: () -> Unit = {},
    onCheckUpdate: () -> Unit = {},
    onManualConnect: (String) -> Unit,
    onStopStreaming: () -> Unit = {}
) {
    var previewViewRef by remember { mutableStateOf<PreviewView?>(null) }
    var currentLens by remember { mutableStateOf(cameraManager.settings.lensType) }
    var isTorchOn by remember { mutableStateOf(cameraManager.settings.isTorchOn) }
    var isFrontCamera by remember { mutableStateOf(cameraManager.settings.isFrontCamera) }
    var showSettingsSheet by remember { mutableStateOf(false) }

    DisposableEffect(cameraManager) {
        cameraManager.onLensChanged = { lens ->
            currentLens = lens
            isFrontCamera = (lens == com.apexcam.camera.CameraLensType.FRONT_SELFIE)
        }
        onDispose {
            cameraManager.onLensChanged = null
        }
    }

    val initialIp = remember(currentServerUrl) {
        val clean = currentServerUrl.removePrefix("ws://").removePrefix("http://").substringBefore(":")
        if (clean.isBlank() || clean == "127.0.0.1") "" else clean
    }
    var ipInput by remember { mutableStateOf(initialIp) }

    LaunchedEffect(currentServerUrl) {
        val clean = currentServerUrl.removePrefix("ws://").removePrefix("http://").substringBefore(":")
        if (clean.isNotBlank() && clean != "127.0.0.1") {
            ipInput = clean
        }
    }

    Box(modifier = Modifier.fillMaxSize()) {
        // CameraX Surface Viewfinder with Gestures (Pinch to Zoom, Tap to Focus)
        AndroidView(
            factory = { context ->
                PreviewView(context).apply {
                    layoutParams = ViewGroup.LayoutParams(
                        ViewGroup.LayoutParams.MATCH_PARENT,
                        ViewGroup.LayoutParams.MATCH_PARENT
                    )
                    scaleType = PreviewView.ScaleType.FILL_CENTER
                    implementationMode = PreviewView.ImplementationMode.COMPATIBLE
                    previewViewRef = this
                    cameraManager.initialize(this) {}
                }
            },
            modifier = Modifier
                .fillMaxSize()
                .pointerInput(Unit) {
                    // Tap to focus
                    detectTapGestures { offset ->
                        previewViewRef?.let { pv ->
                            cameraManager.focusAt(offset.x, offset.y, pv)
                        }
                    }
                }
                .pointerInput(Unit) {
                    // Pinch to zoom
                    detectTransformGestures { _, _, zoom, _ ->
                        val currentZoom = cameraManager.settings.zoomRatio
                        val newZoom = (currentZoom * zoom).coerceIn(1.0f, 5.0f)
                        cameraManager.setZoom(newZoom)
                    }
                }
        )

        TextButton(onClick=onStopStreaming, modifier=Modifier.align(Alignment.TopCenter).padding(top=12.dp)
            .background(Color(0xCC0F172A),RoundedCornerShape(12.dp))) {
            Text("Stop camera",color=Color.White)
        }

        // Floating Connection Card shown when not connected
        if (!isConnected) {
            Box(
                modifier = Modifier
                    .align(Alignment.BottomStart)
                    .padding(start = 16.dp, end = 84.dp, bottom = 24.dp)
                    .background(
                        color = Color(0xEE0F172A),
                        shape = RoundedCornerShape(16.dp)
                    )
                    .padding(14.dp)
            ) {
                Column(
                    verticalArrangement = Arrangement.spacedBy(8.dp)
                ) {
                    Text(
                        text = "Connect to PC (Wi-Fi / USB)",
                        color = Color.White,
                        fontSize = 13.sp,
                        style = MaterialTheme.typography.titleMedium
                    )

                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.spacedBy(6.dp),
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        OutlinedTextField(
                            value = ipInput,
                            onValueChange = { ipInput = it },
                            modifier = Modifier.weight(1f),
                            singleLine = true,
                            placeholder = { Text("PC address", fontSize = 11.sp, color = Color.Gray) },
                            colors = OutlinedTextFieldDefaults.colors(
                                focusedTextColor = Color.White,
                                unfocusedTextColor = Color.White,
                                focusedBorderColor = Color(0xFF38BDF8),
                                unfocusedBorderColor = Color(0xFF475569)
                            )
                        )

                        Button(
                            onClick = {
                                val target = ipInput.trim()
                                onManualConnect(target)
                            },
                            colors = ButtonDefaults.buttonColors(containerColor = Color(0xFF0284C7)),
                            contentPadding = PaddingValues(horizontal = 12.dp, vertical = 6.dp)
                        ) {
                            Text("Connect", fontSize = 11.sp)
                        }

                        Button(
                            onClick = {
                                onManualConnect("127.0.0.1:8556")
                            },
                            colors = ButtonDefaults.buttonColors(containerColor = Color(0xFF10B981)),
                            contentPadding = PaddingValues(horizontal = 8.dp, vertical = 6.dp)
                        ) {
                            Text("USB", fontSize = 11.sp)
                        }
                    }

                    Text(
                        text = "● Searching automatically. Open LensioStudio on your PC.",
                        color = Color(0xFF94A3B8),
                        fontSize = 10.5.sp
                    )
                }
            }
        }

        // Floating Overlays
        FloatingControls(
            isConnected = isConnected,
            isUsb = isUsb,
            isTorchOn = isTorchOn,
            isFrontCamera = isFrontCamera,
            currentLens = currentLens,
            phoneIp = phoneIp,
            framesCount = framesCount,
            fps = fps,
            isPcAudioActive = isPcAudioActive,
            isMicActive = isMicActive,
            onSwitchCamera = {
                cameraManager.switchCamera()
            },
            onSelectLens = { lens ->
                cameraManager.setCameraLens(lens)
            },
            onToggleTorch = {
                isTorchOn = cameraManager.toggleTorch()
            },
            onOpenSettings = {
                showSettingsSheet = true
            }
        )

        // Floating OTA Wi-Fi Update Card
        if (availableUpdate != null) {
            Box(
                modifier = Modifier
                    .align(Alignment.TopCenter)
                    .padding(top = 60.dp, start = 16.dp, end = 16.dp)
                    .fillMaxWidth()
                    .background(
                        color = Color(0xF20F172A),
                        shape = RoundedCornerShape(16.dp)
                    )
                    .padding(14.dp)
            ) {
                Column(
                    verticalArrangement = Arrangement.spacedBy(8.dp),
                    horizontalAlignment = Alignment.CenterHorizontally
                ) {
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.SpaceBetween,
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        Text(
                            text = "Update available (v${availableUpdate.versionName})",
                            color = Color(0xFF38BDF8),
                            fontSize = 13.5.sp,
                            fontWeight = FontWeight.Bold
                        )
                        IconButton(
                            onClick = onDismissUpdate,
                            modifier = Modifier.size(24.dp)
                        ) {
                            Icon(
                                imageVector = Icons.Default.Close,
                                contentDescription = "Close",
                                tint = Color(0xFF94A3B8)
                            )
                        }
                    }

                    Text(
                        text = availableUpdate.releaseNotes.ifBlank { "A new LensioCam version is available on GitHub. Downloads are verified by size and SHA-256; Android will ask you to confirm installation." },
                        color = Color(0xFFCBD5E1),
                        fontSize = 11.5.sp
                    )

                    if (isDownloadingUpdate) {
                        Column(
                            modifier = Modifier.fillMaxWidth(),
                            verticalArrangement = Arrangement.spacedBy(4.dp)
                        ) {
                            LinearProgressIndicator(
                                progress = { downloadProgress },
                                modifier = Modifier.fillMaxWidth(),
                                color = Color(0xFF10B981),
                                trackColor = Color(0xFF334155)
                            )
                            Text(
                                text = "Downloading: ${(downloadProgress * 100).toInt()}%...",
                                color = Color(0xFF10B981),
                                fontSize = 11.sp
                            )
                        }
                    } else {
                        Button(
                            onClick = onStartUpdate,
                            colors = ButtonDefaults.buttonColors(containerColor = Color(0xFF0284C7)),
                            shape = RoundedCornerShape(10.dp),
                            modifier = Modifier.fillMaxWidth(),
                            contentPadding = PaddingValues(vertical = 10.dp)
                        ) {
                            Text("Install update", fontSize = 12.sp, fontWeight = FontWeight.Bold)
                        }
                    }
                }
            }
        }

        // Settings Sheet
        if (showSettingsSheet) {
            QuickSettingsSheet(
                settings = cameraManager.settings,
                currentServerUrl = currentServerUrl,
                onDismiss = { showSettingsSheet = false },
                onResolutionSelected = { },
                onFpsSelected = { },
                onCheckUpdate = onCheckUpdate,
                onManualConnect = onManualConnect
            )
        }
    }
}
