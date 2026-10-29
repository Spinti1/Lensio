package com.apexcam.service

import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.net.wifi.WifiManager
import android.os.BatteryManager
import android.os.Build
import android.os.PowerManager
import com.apexcam.webrtc.SignalingClient
import kotlinx.coroutines.*

class TelemetryMonitor(
    private val context: Context,
    private val signalingClient: SignalingClient
) {
    private var job: Job? = null
    private val scope = CoroutineScope(Dispatchers.Default + SupervisorJob())

    fun start(resolution: String, getFps: () -> Int) {
        job?.cancel()
        job = scope.launch {
            while (isActive) {
                if (signalingClient.isConnected) {
                    val battery = getBatteryLevel()
                    val charging = isDeviceCharging()
                    val temp = getDeviceTemperature()
                    val wifiRssi = getWifiRssi()
                    val fps = getFps()

                    signalingClient.sendTelemetry(
                        battery = battery,
                        isCharging = charging,
                        tempC = temp,
                        wifiRssi = wifiRssi,
                        fps = fps,
                        resolution = resolution
                    )
                }
                delay(2000)
            }
        }
    }

    private fun getBatteryLevel(): Int {
        val batteryIntent = context.registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED))
        val level = batteryIntent?.getIntExtra(BatteryManager.EXTRA_LEVEL, -1) ?: -1
        val scale = batteryIntent?.getIntExtra(BatteryManager.EXTRA_SCALE, -1) ?: -1
        return if (level >= 0 && scale > 0) ((level * 100) / scale) else 100
    }

    private fun isDeviceCharging(): Boolean {
        val batteryIntent = context.registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED))
        val status = batteryIntent?.getIntExtra(BatteryManager.EXTRA_STATUS, -1) ?: -1
        return status == BatteryManager.BATTERY_STATUS_CHARGING || status == BatteryManager.BATTERY_STATUS_FULL
    }

    private fun getDeviceTemperature(): Float {
        val batteryIntent = context.registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED))
        val tempRaw = batteryIntent?.getIntExtra(BatteryManager.EXTRA_TEMPERATURE, 0) ?: 0
        return if (tempRaw > 0) (tempRaw / 10.0f) else 32.5f
    }

    private fun getWifiRssi(): Int {
        val wifiManager = context.applicationContext.getSystemService(Context.WIFI_SERVICE) as? WifiManager
        return wifiManager?.connectionInfo?.rssi ?: -50
    }

    fun stop() {
        job?.cancel()
        job = null
    }
}
