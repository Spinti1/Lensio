package com.apexcam.webrtc

import android.os.Build
import android.util.Log
import com.google.gson.Gson
import com.google.gson.JsonObject
import okhttp3.*
import java.util.concurrent.TimeUnit

class SignalingClient(
    private val listener: SignalingListener
) {
    private val tag = "SignalingClient"
    private var webSocket: WebSocket? = null
    private val client: OkHttpClient = OkHttpClient.Builder()
        .pingInterval(5, TimeUnit.SECONDS)
        .retryOnConnectionFailure(true)
        .build()
    private val gson = Gson()
    var isConnected = false
        private set

    interface SignalingListener {
        fun onConnected()
        fun onDisconnected()
        fun onOfferReceived(sdp: String)
        fun onAnswerReceived(sdp: String)
        fun onIceCandidateReceived(candidate: JsonObject)
        fun onCameraControlReceived(control: JsonObject)
    }

    fun connect(url: String, isUsb: Boolean = false) {
        disconnect()
        Log.d(tag, "Connecting to serverul de semnalizare: $url (USB=$isUsb)")

        val request = Request.Builder().url(url).build()
        webSocket = client.newWebSocket(request, object : WebSocketListener() {
            override fun onOpen(ws: WebSocket, response: Response) {
                isConnected = true
                Log.d(tag, "WebSocket conectat cu succes!")
                listener.onConnected()

                // Register phone device identity
                val regMsg = JsonObject().apply {
                    addProperty("type", "REGISTER")
                    addProperty("role", "phone")
                    addProperty("name", "${Build.MANUFACTURER} ${Build.MODEL}")
                    addProperty("deviceId", "android_${Build.SERIAL ?: Build.MODEL.hashCode()}")
                    addProperty("isUsb", isUsb)
                }
                ws.send(gson.toJson(regMsg))
            }

            override fun onMessage(ws: WebSocket, text: String) {
                try {
                    val json = gson.fromJson(text, JsonObject::class.java)
                    when (json.get("type")?.asString) {
                        "ANSWER" -> {
                            val sdpObj = json.getAsJsonObject("sdp")
                            val sdpString = sdpObj?.get("sdp")?.asString ?: ""
                            listener.onAnswerReceived(sdpString)
                        }
                        "OFFER" -> {
                            val sdpObj = json.getAsJsonObject("sdp")
                            val sdpString = sdpObj?.get("sdp")?.asString ?: ""
                            listener.onOfferReceived(sdpString)
                        }
                        "CANDIDATE" -> {
                            val candidateObj = json.getAsJsonObject("candidate")
                            if (candidateObj != null) {
                                listener.onIceCandidateReceived(candidateObj)
                            }
                        }
                        "CAMERA_CONTROL" -> {
                            listener.onCameraControlReceived(json)
                        }
                    }
                } catch (e: Exception) {
                    Log.e(tag, "Error parsare mesaj semnalizare", e)
                }
            }

            override fun onClosing(ws: WebSocket, code: Int, reason: String) {
                isConnected = false
                listener.onDisconnected()
            }

            override fun onFailure(ws: WebSocket, t: Throwable, response: Response?) {
                isConnected = false
                Log.w(tag, "Eșec conexiune WebSocket: ${t.message}")
                listener.onDisconnected()
            }
        })
    }

    fun sendOffer(sdp: String) {
        val msg = JsonObject().apply {
            addProperty("type", "OFFER")
            val sdpObj = JsonObject().apply {
                addProperty("type", "offer")
                addProperty("sdp", sdp)
            }
            add("sdp", sdpObj)
        }
        webSocket?.send(gson.toJson(msg))
    }

    fun sendAnswer(sdp: String) {
        val msg = JsonObject().apply {
            addProperty("type", "ANSWER")
            val sdpObj = JsonObject().apply {
                addProperty("type", "answer")
                addProperty("sdp", sdp)
            }
            add("sdp", sdpObj)
        }
        webSocket?.send(gson.toJson(msg))
    }

    fun sendIceCandidate(candidate: org.webrtc.IceCandidate) {
        val msg = JsonObject().apply {
            addProperty("type", "CANDIDATE")
            val cObj = JsonObject().apply {
                addProperty("sdpMid", candidate.sdpMid)
                addProperty("sdpMLineIndex", candidate.sdpMLineIndex)
                addProperty("candidate", candidate.sdp)
            }
            add("candidate", cObj)
        }
        webSocket?.send(gson.toJson(msg))
    }

    fun sendTelemetry(
        battery: Int,
        isCharging: Boolean,
        tempC: Float,
        wifiRssi: Int,
        fps: Int,
        resolution: String
    ) {
        val msg = JsonObject().apply {
            addProperty("type", "TELEMETRY")
            addProperty("batteryLevel", battery)
            addProperty("isCharging", isCharging)
            addProperty("temperatureC", tempC)
            addProperty("wifiRssi", wifiRssi)
            addProperty("fps", fps)
            addProperty("resolution", resolution)
        }
        webSocket?.send(gson.toJson(msg))
    }

    fun disconnect() {
        webSocket?.close(1000, "Normal closure")
        webSocket = null
        isConnected = false
    }
}
