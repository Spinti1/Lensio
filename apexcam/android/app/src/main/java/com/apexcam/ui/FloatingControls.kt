package com.apexcam.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.*
import androidx.compose.material3.*
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.apexcam.camera.CameraLensType

@Composable
fun FloatingControls(
    isConnected: Boolean,
    isUsb: Boolean,
    isTorchOn: Boolean,
    isFrontCamera: Boolean,
    currentLens: CameraLensType = CameraLensType.BACK_MAIN,
    phoneIp: String = "0.0.0.0",
    framesCount: Long = 0,
    fps: Int = 30,
    isPcAudioActive: Boolean = false,
    isMicActive: Boolean = false,
    onSwitchCamera: () -> Unit,
    onSelectLens: (CameraLensType) -> Unit = {},
    onToggleTorch: () -> Unit,
    onOpenSettings: () -> Unit
) {
    Box(
        modifier = Modifier
            .fillMaxSize()
            .padding(16.dp)
    ) {
        // Top Left: Status & Audio Badges
        Column(
            modifier = Modifier.align(Alignment.TopStart),
            verticalArrangement = Arrangement.spacedBy(6.dp)
        ) {
            Row(
                modifier = Modifier
                    .background(
                        color = Color.Black.copy(alpha = 0.75f),
                        shape = RoundedCornerShape(12.dp)
                    )
                    .padding(horizontal = 12.dp, vertical = 7.dp),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(8.dp)
            ) {
                Box(
                    modifier = Modifier
                        .size(10.dp)
                        .background(
                            color = if (isConnected) Color(0xFF10B981) else Color(0xFFEF4444),
                            shape = CircleShape
                        )
                )

                Text(
                    text = if (isConnected) "STREAMING ($fps FPS)" else "Waiting for PC...",
                    color = Color.White,
                    fontSize = 11.5.sp
                )

                if (framesCount > 0) {
                    Text(
                        text = "[$framesCount c.]",
                        color = Color(0xFF38BDF8),
                        fontSize = 10.5.sp
                    )
                }

                if (isUsb) {
                    Text(
                        text = "USB (ADB)",
                        color = Color(0xFF60A5FA),
                        fontSize = 10.5.sp
                    )
                }
            }

            // Audio & Mic status badges (shown cleanly when enabled from PC)
            if (isPcAudioActive || isMicActive) {
                Row(
                    horizontalArrangement = Arrangement.spacedBy(6.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    if (isPcAudioActive) {
                        Box(
                            modifier = Modifier
                                .background(
                                    color = Color(0xCC0369A1),
                                    shape = RoundedCornerShape(8.dp)
                                )
                                .padding(horizontal = 9.dp, vertical = 4.dp)
                        ) {
                            Text(
                                text = "🔊 PC Audio",
                                color = Color.White,
                                fontSize = 11.sp,
                                fontWeight = FontWeight.Bold
                            )
                        }
                    }

                    if (isMicActive) {
                        Box(
                            modifier = Modifier
                                .background(
                                    color = Color(0xCC059669),
                                    shape = RoundedCornerShape(8.dp)
                                )
                                .padding(horizontal = 9.dp, vertical = 4.dp)
                        ) {
                            Text(
                                text = "🎙️ Microphone",
                                color = Color.White,
                                fontSize = 11.sp,
                                fontWeight = FontWeight.Bold
                            )
                        }
                    }
                }
            }
        }

        // Top Right: Phone Local IP Badge (For direct access from browser or PC)
        Column(
            modifier = Modifier
                .align(Alignment.TopEnd)
                .background(
                    color = Color.Black.copy(alpha = 0.75f),
                    shape = RoundedCornerShape(12.dp)
                )
                .padding(horizontal = 10.dp, vertical = 6.dp),
            horizontalAlignment = Alignment.End
        ) {
            Text(
                text = "IP: $phoneIp:8080",
                color = Color(0xFF38BDF8),
                fontSize = 11.sp,
                style = MaterialTheme.typography.titleSmall
            )
            Text(
                text = "HTTP MJPEG Active",
                color = Color(0xFF94A3B8),
                fontSize = 9.sp
            )
        }

        // Bottom Center: Camera Lens Selector Pill (0.5x Ultra-Wide, 1x Main, Selfie)
        Row(
            modifier = Modifier
                .align(Alignment.BottomCenter)
                .padding(bottom = 20.dp)
                .background(
                    color = Color.Black.copy(alpha = 0.75f),
                    shape = RoundedCornerShape(24.dp)
                )
                .padding(4.dp),
            horizontalArrangement = Arrangement.spacedBy(4.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            LensPillButton(
                title = "🌐 0.5x",
                isSelected = currentLens == CameraLensType.BACK_ULTRAWIDE,
                onClick = { onSelectLens(CameraLensType.BACK_ULTRAWIDE) }
            )
            LensPillButton(
                title = "📷 1x",
                isSelected = currentLens == CameraLensType.BACK_MAIN,
                onClick = { onSelectLens(CameraLensType.BACK_MAIN) }
            )
            LensPillButton(
                title = "🤳 Selfie",
                isSelected = currentLens == CameraLensType.FRONT_SELFIE,
                onClick = { onSelectLens(CameraLensType.FRONT_SELFIE) }
            )
        }

        // Bottom Right Floating Action Stack
        Column(
            modifier = Modifier
                .align(Alignment.BottomEnd)
                .padding(bottom = 16.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
            horizontalAlignment = Alignment.CenterHorizontally
        ) {
            // Flashlight / Torch
            IconButton(
                onClick = onToggleTorch,
                modifier = Modifier
                    .size(48.dp)
                    .background(
                        color = if (isTorchOn) Color(0xFFF59E0B) else Color.Black.copy(alpha = 0.6f),
                        shape = CircleShape
                    )
            ) {
                Icon(
                    imageVector = if (isTorchOn) Icons.Default.FlashOn else Icons.Default.FlashOff,
                    contentDescription = "Torch",
                    tint = Color.White
                )
            }

            // Switch Camera Front / Back
            IconButton(
                onClick = onSwitchCamera,
                modifier = Modifier
                    .size(48.dp)
                    .background(Color.Black.copy(alpha = 0.6f), shape = CircleShape)
            ) {
                Icon(
                    imageVector = Icons.Default.FlipCameraAndroid,
                    contentDescription = "Switch Camera",
                    tint = Color.White
                )
            }

            // Quick Settings Sheet
            IconButton(
                onClick = onOpenSettings,
                modifier = Modifier
                    .size(48.dp)
                    .background(Color.Black.copy(alpha = 0.6f), shape = CircleShape)
            ) {
                Icon(
                    imageVector = Icons.Default.Settings,
                    contentDescription = "Settings",
                    tint = Color.White
                )
            }
        }
    }
}

@Composable
private fun LensPillButton(
    title: String,
    isSelected: Boolean,
    onClick: () -> Unit
) {
    Box(
        modifier = Modifier
            .background(
                color = if (isSelected) Color(0xFF0284C7) else Color.Transparent,
                shape = RoundedCornerShape(20.dp)
            )
            .clickable { onClick() }
            .padding(horizontal = 14.dp, vertical = 8.dp),
        contentAlignment = Alignment.Center
    ) {
        Text(
            text = title,
            color = if (isSelected) Color.White else Color(0xFFCBD5E1),
            fontSize = 12.sp,
            fontWeight = if (isSelected) FontWeight.Bold else FontWeight.Medium
        )
    }
}
