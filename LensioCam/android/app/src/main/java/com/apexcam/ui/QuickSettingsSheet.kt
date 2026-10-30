package com.apexcam.ui

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.apexcam.camera.CameraSettings

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun QuickSettingsSheet(
    settings: CameraSettings,
    currentServerUrl: String,
    onDismiss: () -> Unit,
    onResolutionSelected: (String) -> Unit,
    onFpsSelected: (Int) -> Unit,
    onCheckUpdate: () -> Unit = {},
    onManualConnect: (String) -> Unit
) {
    var manualIp by remember { mutableStateOf(currentServerUrl.removePrefix("ws://").substringBefore(":")) }

    ModalBottomSheet(
        onDismissRequest = onDismiss,
        containerColor = Color(0xFF1E293B),
        shape = RoundedCornerShape(topStart = 20.dp, topEnd = 20.dp)
    ) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .padding(24.dp),
            verticalArrangement = Arrangement.spacedBy(18.dp)
        ) {
            Text(
                text = "LensioCam Quick Settings",
                color = Color.White,
                fontSize = 18.sp,
                style = MaterialTheme.typography.titleMedium
            )

            // Resolution Info (Controlled exclusively from PC)
            Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
                Text(text = "Video Resolution (Controlled from PC)", color = Color(0xFF94A3B8), fontSize = 13.sp)
                Card(
                    modifier = Modifier.fillMaxWidth(),
                    colors = CardDefaults.cardColors(containerColor = Color(0xFF0F172A)),
                    shape = RoundedCornerShape(10.dp)
                ) {
                    Row(
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(horizontal = 14.dp, vertical = 12.dp),
                        horizontalArrangement = Arrangement.SpaceBetween,
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        Column {
                            Text(
                                text = if (settings.resolution.width >= 1920) "1080p Full HD" else "720p HD",
                                color = Color(0xFF38BDF8),
                                fontSize = 15.sp,
                                fontWeight = FontWeight.Bold
                            )
                            Text(
                                text = "${settings.resolution.width} x ${settings.resolution.height} • 30 FPS",
                                color = Color(0xFF94A3B8),
                                fontSize = 12.sp
                            )
                        }
                        Text(
                            text = "Change from PC",
                            color = Color(0xFF64748B),
                            fontSize = 12.sp,
                            fontWeight = FontWeight.Medium
                        )
                    }
                }
            }

            // Framerate Selector
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text(text = "Frame Rate (FPS)", color = Color(0xFF94A3B8), fontSize = 13.sp)
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(8.dp)
                ) {
                    listOf(30, 60).forEach { fps ->
                        val isSelected = settings.fps == fps
                        Button(
                            onClick = { onFpsSelected(fps) },
                            modifier = Modifier.weight(1f),
                            colors = ButtonDefaults.buttonColors(
                                containerColor = if (isSelected) Color(0xFF0284C7) else Color(0xFF334155)
                            )
                        ) {
                            Text(text = "$fps FPS", fontSize = 12.sp, color = Color.White)
                        }
                    }
                }
            }

            // Manual IP Entry
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text(text = "Manual PC IP Address (Optional)", color = Color(0xFF94A3B8), fontSize = 13.sp)
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(8.dp)
                ) {
                    OutlinedTextField(
                        value = manualIp,
                        onValueChange = { manualIp = it },
                        modifier = Modifier.weight(1f),
                        placeholder = { Text("192.168.1.X", color = Color.Gray) },
                        singleLine = true,
                        colors = OutlinedTextFieldDefaults.colors(
                            focusedTextColor = Color.White,
                            unfocusedTextColor = Color.White,
                            focusedBorderColor = Color(0xFF0284C7),
                            unfocusedBorderColor = Color(0xFF475569)
                        )
                    )
                    Button(
                        onClick = {
                            val clean = manualIp.trim()
                            val fullUrl = if (clean.startsWith("ws://")) clean else "ws://$clean:8554"
                            onManualConnect(fullUrl)
                            onDismiss()
                        },
                        colors = ButtonDefaults.buttonColors(containerColor = Color(0xFF0284C7))
                    ) {
                        Text("Connect")
                    }
                }
            }

            // Independent GitHub update check
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text(text = "LensioCam updates (GitHub)", color = Color(0xFF94A3B8), fontSize = 13.sp)
                Button(
                    onClick = {
                        onCheckUpdate()
                        onDismiss()
                    },
                    modifier = Modifier.fillMaxWidth(),
                    colors = ButtonDefaults.buttonColors(containerColor = Color(0xFF0F766E)),
                    shape = RoundedCornerShape(10.dp)
                ) {
                    Text("Check for updates", fontSize = 13.sp, fontWeight = FontWeight.Bold, color = Color.White)
                }
            }

            Spacer(modifier = Modifier.height(12.dp))
        }
    }
}
