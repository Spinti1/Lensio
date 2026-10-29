package com.apexcam.network

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.util.Log

class NsdDiscovery(private val context: Context) {
    private val tag = "NsdDiscovery"
    private val serviceType = "_apexcam._tcp."
    private var nsdManager: NsdManager? = null
    private var discoveryListener: NsdManager.DiscoveryListener? = null

    var onServerFound: ((String) -> Unit)? = null

    fun startDiscovery() {
        nsdManager = context.getSystemService(Context.NSD_SERVICE) as NsdManager

        discoveryListener = object : NsdManager.DiscoveryListener {
            override fun onStartDiscoveryFailed(serviceType: String?, errorCode: Int) {
                Log.e(tag, "Eșec pornire NSD discovery: $errorCode")
            }

            override fun onStopDiscoveryFailed(serviceType: String?, errorCode: Int) {
                Log.e(tag, "Eșec oprire NSD discovery: $errorCode")
            }

            override fun onDiscoveryStarted(serviceType: String?) {
                Log.d(tag, "Căutare automată PC pe rețeaua locală (_apexcam._tcp.)...")
            }

            override fun onDiscoveryStopped(serviceType: String?) {
                Log.d(tag, "NSD discovery oprit.")
            }

            override fun onServiceFound(serviceInfo: NsdServiceInfo) {
                Log.d(tag, "Serviciu detectat: ${serviceInfo.serviceName}")
                if (serviceInfo.serviceType.contains("apexcam")) {
                    resolveService(serviceInfo)
                }
            }

            override fun onServiceLost(serviceInfo: NsdServiceInfo) {
                Log.d(tag, "Serviciu pierdut: ${serviceInfo.serviceName}")
            }
        }

        try {
            nsdManager?.discoverServices(serviceType, NsdManager.PROTOCOL_DNS_SD, discoveryListener)
        } catch (e: Exception) {
            Log.e(tag, "Error la pornire NsdManager", e)
        }
    }

    private fun resolveService(serviceInfo: NsdServiceInfo) {
        nsdManager?.resolveService(serviceInfo, object : NsdManager.ResolveListener {
            override fun onResolveFailed(serviceInfo: NsdServiceInfo?, errorCode: Int) {
                Log.w(tag, "Eșec rezolvare serviciu: $errorCode")
            }

            override fun onServiceResolved(resolvedInfo: NsdServiceInfo) {
                val host = resolvedInfo.host?.hostAddress
                val port = resolvedInfo.port
                if (host != null && port > 0) {
                    val url = "ws://$host:$port"
                    Log.d(tag, "PC ApexCam local găsit cu succes: $url")
                    onServerFound?.invoke(url)
                }
            }
        })
    }

    fun stopDiscovery() {
        discoveryListener?.let {
            try {
                nsdManager?.stopServiceDiscovery(it)
            } catch (e: Exception) {}
        }
        discoveryListener = null
    }
}
