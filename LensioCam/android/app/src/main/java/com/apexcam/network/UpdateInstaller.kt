package com.apexcam.network

import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageInstaller
import android.os.Build
import androidx.core.content.ContextCompat
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withContext
import java.io.File
import java.util.UUID
import kotlin.coroutines.coroutineContext

/** Stages the verified APK using Android's installer, without content-URI/OEM intent handlers. */
internal class UpdateInstaller(
    private val context: Context,
    private val onConfirmation: (Intent) -> Unit,
    private val onFinished: (String?) -> Unit
) : AutoCloseable {
    private val installer = context.packageManager.packageInstaller
    private val action = "${context.packageName}.UPDATE_RESULT.${UUID.randomUUID()}"
    private var sessionId = -1
    @Volatile private var preparedId = -1
    private var callback: PendingIntent? = null
    private val receiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context, intent: Intent) {
            if (intent.action != action || intent.getIntExtra(PackageInstaller.EXTRA_SESSION_ID, -1) != sessionId) return
            when (val status = intent.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE)) {
                PackageInstaller.STATUS_PENDING_USER_ACTION -> {
                    @Suppress("DEPRECATION")
                    val confirmation = intent.getParcelableExtra<Intent>(Intent.EXTRA_INTENT)
                    if (confirmation != null) onConfirmation(confirmation)
                    else { abandon(); onFinished("Android did not provide an install confirmation. Use GitHub APK.") }
                }
                PackageInstaller.STATUS_SUCCESS -> { abandon(); onFinished(null) }
                else -> { abandon(); onFinished(failureMessage(status)) }
            }
        }
    }

    init {
        ContextCompat.registerReceiver(context, receiver, IntentFilter(action), ContextCompat.RECEIVER_NOT_EXPORTED)
    }

    suspend fun stage(apk: File) = withContext(Dispatchers.IO) {
        val params = PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL).apply {
            setAppPackageName(context.packageName)
            setSize(apk.length())
            if (Build.VERSION.SDK_INT >= 31) setRequireUserAction(PackageInstaller.SessionParams.USER_ACTION_REQUIRED)
        }
        val id = installer.createSession(params)
        preparedId = id
        try {
            installer.openSession(id).use { session ->
                session.openWrite("base.apk", 0, apk.length()).use { output ->
                    apk.inputStream().use { input ->
                        val buffer = ByteArray(65536)
                        while (true) {
                            coroutineContext.ensureActive()
                            val count = input.read(buffer)
                            if (count < 0) break
                            output.write(buffer, 0, count)
                        }
                    }
                    session.fsync(output)
                }
            }
            id
        } catch (error: Exception) {
            runCatching { installer.abandonSession(id) }
            preparedId = -1
            throw error
        }
    }

    /** Commit only from the resumed activity, after all writes are closed. */
    fun commit(id: Int) {
        abandon()
        sessionId = id
        preparedId = -1
        val flags = PendingIntent.FLAG_UPDATE_CURRENT or
            (if (Build.VERSION.SDK_INT >= 31) PendingIntent.FLAG_MUTABLE else 0)
        callback = PendingIntent.getBroadcast(context, id, Intent(action).setPackage(context.packageName), flags)
        try { installer.openSession(id).use { it.commit(callback!!.intentSender) } }
        catch (error: Exception) { abandon(); throw error }
    }

    fun abandon(id: Int = sessionId) {
        if (id >= 0) runCatching { installer.abandonSession(id) }
        if (id == preparedId) preparedId = -1
        if (id == sessionId) {
            sessionId = -1
            callback?.cancel()
            callback = null
        }
    }

    fun isInstalling(): Boolean = sessionId >= 0 &&
        runCatching { installer.getSessionInfo(sessionId)?.isActive == true }.getOrDefault(false)

    override fun close() {
        abandon()
        abandon(preparedId)
        context.unregisterReceiver(receiver)
    }

    companion object {
        internal fun failureMessage(status: Int) = when (status) {
            PackageInstaller.STATUS_FAILURE_ABORTED -> "Installation cancelled. Tap Install update to retry."
            PackageInstaller.STATUS_FAILURE_STORAGE -> "Not enough free space to install. Free some space or use GitHub APK."
            PackageInstaller.STATUS_FAILURE_BLOCKED -> "Android blocked installation. Check install permissions or use GitHub APK."
            PackageInstaller.STATUS_FAILURE_CONFLICT -> "The installed app has a different signature or conflicts with this APK."
            PackageInstaller.STATUS_FAILURE_INCOMPATIBLE -> "This APK is not compatible with this device."
            PackageInstaller.STATUS_FAILURE_INVALID -> "Android rejected the APK. Try the GitHub APK download."
            else -> "Android could not install the update. Retry or use GitHub APK."
        }
    }
}
