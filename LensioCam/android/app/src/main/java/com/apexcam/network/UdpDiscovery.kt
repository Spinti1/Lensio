package com.apexcam.network

import android.content.Context
import android.net.wifi.WifiManager
import android.util.Log
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.NetworkInterface
import java.net.Socket
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean

class UdpDiscovery(private val context: Context) {
    private val tag = "UdpDiscovery"
    private val isRunning = AtomicBoolean(false)
    private val executor = Executors.newFixedThreadPool(3)
    private val scanPool = Executors.newFixedThreadPool(8)
    private val scanning = AtomicBoolean(false)
    private var socket: DatagramSocket? = null
    private var multicastLock: WifiManager.MulticastLock? = null

    var onServerFound: ((String, Int) -> Unit)? = null
    var isConnectedProvider: (() -> Boolean)? = null

    companion object {
        fun getLocalIpAddress(): String {
            try {
                val interfaces = NetworkInterface.getNetworkInterfaces()
                while (interfaces.hasMoreElements()) {
                    val intf = interfaces.nextElement()
                    if (intf.isLoopback || !intf.isUp) continue
                    val addrs = intf.inetAddresses
                    while (addrs.hasMoreElements()) {
                        val addr = addrs.nextElement()
                        if (!addr.isLoopbackAddress && addr is java.net.Inet4Address) {
                            val ip = addr.hostAddress ?: ""
                            if (ip.startsWith("192.168.") || ip.startsWith("10.") || ip.startsWith("172.")) {
                                return ip
                            }
                        }
                    }
                }
            } catch (e: Exception) {}
            return "0.0.0.0"
        }

        fun getSubnetBroadcastAddresses(): List<InetAddress> {
            val list = mutableListOf<InetAddress>()
            try {
                list.add(InetAddress.getByName("255.255.255.255"))
                val localIp = getLocalIpAddress()
                val parts = localIp.split(".")
                if (parts.size == 4) {
                    list.add(InetAddress.getByName("${parts[0]}.${parts[1]}.${parts[2]}.255"))
                }
            } catch (e: Exception) {}
            return list
        }
    }

    fun start() {
        if (isRunning.getAndSet(true)) return

        try {
            val wifi = context.applicationContext.getSystemService(Context.WIFI_SERVICE) as? WifiManager
            multicastLock = wifi?.createMulticastLock("ApexCamMulticastLock")?.apply {
                setReferenceCounted(true)
                acquire()
            }
        } catch (e: Exception) {
            Log.w(tag, "MulticastLock warning: ${e.message}")
        }

        // 1. Thread to listen for PC Beacons
        executor.execute {
            try {
                val sock = DatagramSocket(null).apply {
                    reuseAddress = true
                    broadcast = true
                    bind(InetSocketAddress(8555))
                }
                socket = sock
                val buffer = ByteArray(512)

                while (isRunning.get()) {
                    val packet = DatagramPacket(buffer, buffer.size)
                    sock.receive(packet)
                    val text = String(packet.data, 0, packet.length).trim()

                    if (text.startsWith("APEXCAM_BEACON|")) {
                        val parts = text.split("|")
                        if (parts.size >= 3) {
                            val ip = parts[1].trim()
                            val port = parts[2].trim().toIntOrNull() ?: 8556
                            Log.d(tag, "Beacon recepționat de la PC: $ip:$port")
                            onServerFound?.invoke(ip, port)
                        }
                    }
                }
            } catch (e: Exception) {
                if (isRunning.get()) {
                    Log.w(tag, "Error listener UDP beacon: ${e.message}")
                }
            }
        }

        // 2. Thread to broadcast discovery requests and phone IP to PC
        executor.execute {
            while (isRunning.get()) {
                if (isConnectedProvider?.invoke() == true) {
                    try {
                        Thread.sleep(6000)
                    } catch (e: InterruptedException) {
                        break
                    }
                    continue
                }

                try {
                    val phoneIp = getLocalIpAddress()
                    val targets = getSubnetBroadcastAddresses()

                    // Announce phone presence
                    val phoneMsg = "APEXCAM_PHONE|$phoneIp|8080".toByteArray()
                    // Solicit immediate PC beacon response
                    val discoverMsg = "APEXCAM_DISCOVER".toByteArray()

                    for (target in targets) {
                        try {
                            socket?.send(DatagramPacket(phoneMsg, phoneMsg.size, target, 8555))
                            socket?.send(DatagramPacket(discoverMsg, discoverMsg.size, target, 8555))
                        } catch (e: Exception) {}
                    }
                } catch (e: Exception) {}

                try {
                    Thread.sleep(1200)
                } catch (e: InterruptedException) {
                    break
                }
            }
        }
    }

    // Bounded fallback scan. Probe the HTTP identity endpoint, never the live video socket.
    fun scanSubnetForServer(port: Int = 8556, onFound: (String, Int) -> Unit) {
        if (!isRunning.get() || !scanning.compareAndSet(false, true)) return
        val phoneIp = getLocalIpAddress()
        val parts = phoneIp.split(".")
        if (parts.size != 4) { scanning.set(false); return }
        val prefix = "${parts[0]}.${parts[1]}.${parts[2]}."

        val remaining = java.util.concurrent.atomic.AtomicInteger(253)
        val found = AtomicBoolean(false)

        for (i in 1..254) {
            val ip = "$prefix$i"
            if (ip == phoneIp) continue

            scanPool.execute {
                try {
                    if (found.get() || !isRunning.get() || isConnectedProvider?.invoke() == true) return@execute
                    val request = java.net.URL("http://" + ip + ":8557/api/identity").openConnection() as java.net.HttpURLConnection
                    val matches = try {
                        request.connectTimeout = 250
                        request.readTimeout = 350
                        val body = request.inputStream.bufferedReader().use { reader ->
                            val buffer=CharArray(512);var used=0
                            while(used<buffer.size){val count=reader.read(buffer,used,buffer.size-used);if(count<0)break;used+=count}
                            String(buffer,0,used)
                        }
                        body.contains("\"application\":\"LensioStudio\"")
                    } finally { request.disconnect() }
                    if (!matches || !isRunning.get() || isConnectedProvider?.invoke() == true) return@execute
                    if (found.compareAndSet(false, true)) {
                        Log.d(tag, "PC găsit prin scanare rapidă subnet: $ip:$port!")
                        onFound(ip, port)
                    }
                } catch (e: Exception) {} finally {
                    if (remaining.decrementAndGet() == 0) scanning.set(false)
                }
            }
        }
    }

    fun stop() {
        isRunning.set(false)
        try {
            socket?.close()
        } catch (e: Exception) {}
        socket = null

        try {
            if (multicastLock?.isHeld == true) {
                multicastLock?.release()
            }
        } catch (e: Exception) {}
        multicastLock = null

        executor.shutdownNow()
        scanPool.shutdownNow()
    }
}
