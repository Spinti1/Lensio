package com.apexcam.network

import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.util.Log
import androidx.core.content.FileProvider
import com.apexcam.BuildConfig
import com.google.gson.Gson
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.OkHttpClient
import okhttp3.Request
import java.io.File
import java.io.FileOutputStream
import java.io.InputStream
import java.util.concurrent.TimeUnit

data class VersionInfo(
    val versionCode: Int = 1,
    val versionName: String = "1.0.0",
    val apkSize: Long = 0,
    val apkUrl: String = "/download/LensioCam.apk",
    val releaseNotes: String = "",
    val sha256: String = ""
)

object UpdateChecker {
    private const val TAG = "UpdateChecker"
    private val client = OkHttpClient.Builder()
        .connectTimeout(3, TimeUnit.SECONDS)
        .readTimeout(60, TimeUnit.SECONDS)
        .build()

    private const val REPOSITORY = "Spinti1/Lensio"
    private var offeredUpdate: VersionInfo? = null
    private var lastGitHubCheck=0L
    internal fun parseGitHubRelease(json: String): VersionInfo? {
        val release = com.google.gson.JsonParser.parseString(json).asJsonObject
        if (release.get("draft")?.asBoolean == true || release.get("prerelease")?.asBoolean == true) return null
        val version = release.get("tag_name").asString.removePrefix("v")
        val parts = version.split('.').map { it.toIntOrNull() ?: return null }
        if (parts.size != 3) return null
        val current = BuildConfig.VERSION_NAME.split('.').map { it.toIntOrNull() ?: 0 }
        var newer = false
        for (i in 0..2) { val compare = parts[i].compareTo(current.getOrElse(i) {0}); if (compare != 0) { newer=compare>0; break } }
        if (!newer) return null
        val asset = release.getAsJsonArray("assets").map { it.asJsonObject }.firstOrNull { it.get("name").asString == "LensioCam.apk" } ?: return null
        val url=asset.get("browser_download_url").asString
        val digest=asset.get("digest")?.asString ?: return null
        val size=asset.get("size").asLong
        if (!url.startsWith("https://github.com/$REPOSITORY/releases/download/") ||
            !digest.matches(Regex("sha256:[0-9a-fA-F]{64}")) || size !in 1..200*1024*1024L) return null
        return VersionInfo(BuildConfig.VERSION_CODE+1,version,size,url,release.get("body")?.asString ?: "",digest.removePrefix("sha256:"))
    }
    suspend fun checkUpdate(pcIp: String): VersionInfo? = withContext(Dispatchers.IO) {
        try {
            val now=android.os.SystemClock.elapsedRealtime()
            if (lastGitHubCheck==0L || now-lastGitHubCheck>=15*60*1000L) {
            lastGitHubCheck=now
            val request=Request.Builder().url("https://api.github.com/repos/$REPOSITORY/releases/latest").header("User-Agent","LensioCam").build()
            client.newCall(request).execute().use { response ->
                if (response.isSuccessful) parseGitHubRelease(response.body?.string() ?: "")?.let { offeredUpdate=it; return@withContext it }
            }
            } else if (offeredUpdate?.sha256?.isNotBlank()==true) return@withContext offeredUpdate
        } catch (_: Exception) { /* Local PC updates remain available without internet. */ }
        if (pcIp.isBlank() || pcIp == "127.0.0.1") return@withContext null
        val cleanHost = pcIp.removePrefix("ws://").removePrefix("http://").substringBefore(":")
        val url = "http://$cleanHost:8557/api/version"

        try {
            val request = Request.Builder().url(url).build()
            client.newCall(request).execute().use { response ->
                if (response.isSuccessful) {
                    val bodyString = response.body?.string() ?: return@withContext null
                    val info = Gson().fromJson(bodyString, VersionInfo::class.java)
                    Log.d(TAG, "Versiune primita de la PC: $info (Curenta: ${BuildConfig.VERSION_CODE})")
                    if (info.versionCode > BuildConfig.VERSION_CODE) {
                        offeredUpdate=info
                        return@withContext info
                    }
                }
            }
        } catch (e: Exception) {
            Log.d(TAG, "Nu s-a putut verifica versiunea de pe PC: ${e.message}")
        }
        return@withContext null
    }

    suspend fun downloadAndInstallApk(
        context: Context,
        pcIp: String,
        onProgress: (Float) -> Unit,
        onError: (String) -> Unit
    ) = withContext(Dispatchers.IO) {
        val cleanHost = pcIp.removePrefix("ws://").removePrefix("http://").substringBefore(":")
        val offer=offeredUpdate
        val downloadUrl = if (offer?.sha256?.isNotBlank() == true) offer.apkUrl else "http://$cleanHost:8557/download/LensioCam.apk"

        try {
            val request = Request.Builder().url(downloadUrl).build()
            client.newCall(request).execute().use { response ->
                if (!response.isSuccessful) {
                    withContext(Dispatchers.Main) { onError("Server error: ${response.code}") }
                    return@withContext
                }

                val body = response.body ?: run {
                    withContext(Dispatchers.Main) { onError("Empty server response") }
                    return@withContext
                }

                val contentLength = body.contentLength()
                val targetFile = File(context.cacheDir, "LensioCam_Update.apk")
                if (targetFile.exists()) targetFile.delete()

                val digest=java.security.MessageDigest.getInstance("SHA-256")
                var totalRead = 0L
                body.byteStream().use { input ->
                    FileOutputStream(targetFile).use { output ->
                        val buffer=ByteArray(32768)
                        while (true) {
                            val count=input.read(buffer)
                            if (count<0) break
                            totalRead+=count
                            if (totalRead>200*1024*1024L) throw java.io.IOException("Update exceeds size limit")
                            digest.update(buffer,0,count)
                            output.write(buffer,0,count)
                            if (contentLength>0) withContext(Dispatchers.Main) {onProgress(totalRead.toFloat()/contentLength)}
                        }
                    }
                }
                if (offer?.sha256?.isNotBlank() == true) {
                    val hash=digest.digest().joinToString("") { "%02x".format(it) }
                    if (!hash.equals(offer.sha256,true) || totalRead!=offer.apkSize) {
                        targetFile.delete(); throw java.io.IOException("Update integrity verification failed")
                    }
                }
                // Android also verifies the package signature before allowing an update.
                val archive=context.packageManager.getPackageArchiveInfo(targetFile.absolutePath,0)
                if (archive?.packageName!=context.packageName) {targetFile.delete();throw java.io.IOException("Unexpected update package")}
                withContext(Dispatchers.Main) {
                    onProgress(1.0f)
                    installApk(context, targetFile)
                }
            }
        } catch (e: Exception) {
            Log.e(TAG, "Update download failed: ${e.message}", e)
            withContext(Dispatchers.Main) {
                onError("Connection error: ${e.message}")
            }
        }
    }

    private fun installApk(context: Context, apkFile: File) {
        try {
            val uri: Uri = FileProvider.getUriForFile(
                context,
                "${context.packageName}.provider",
                apkFile
            )

            val installIntent = Intent(Intent.ACTION_VIEW).apply {
                setDataAndType(uri, "application/vnd.android.package-archive")
                flags = Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_GRANT_READ_URI_PERMISSION
            }

            context.startActivity(installIntent)
            Log.i(TAG, "Intent instalare lansat cu succes pentru: ${apkFile.absolutePath}")
        } catch (e: Exception) {
            Log.e(TAG, "Unable to open installer: ${e.message}", e)
            throw e
        }
    }
}
