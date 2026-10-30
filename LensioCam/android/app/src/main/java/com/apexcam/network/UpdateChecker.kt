package com.apexcam.network

import android.content.Context
import android.content.Intent
import android.net.Uri
import android.util.Log
import androidx.core.content.FileProvider
import com.apexcam.BuildConfig
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.OkHttpClient
import okhttp3.Request
import java.io.File
import java.io.FileOutputStream
import java.util.concurrent.TimeUnit

data class VersionInfo(
    val versionName: String = "1.0.0",
    val apkSize: Long = 0,
    val apkUrl: String = "",
    val releaseNotes: String = "",
    val sha256: String = ""
)

object UpdateChecker {
    private const val TAG = "UpdateChecker"
    private val client = OkHttpClient.Builder()
        .connectTimeout(3, TimeUnit.SECONDS)
        .readTimeout(30, TimeUnit.SECONDS)
        .followSslRedirects(false)
        .build()

    private const val REPOSITORY = "Spinti1/Lensio"
    private var offeredUpdate: VersionInfo? = null
    private var lastGitHubCheck=0L
    internal fun parseGitHubRelease(json: String): VersionInfo? {
        val release = com.google.gson.JsonParser.parseString(json).asJsonObject
        if (release.get("draft")?.asBoolean == true || release.get("prerelease")?.asBoolean == true) return null
        val version = release.get("tag_name").asString.removePrefix("v")
        val parts = version.split('.').map { it.toIntOrNull() ?: return null }
        if (parts.size != 3 || parts.any { it < 0 } || !version.matches(Regex("[0-9]+\\.[0-9]+\\.[0-9]+"))) return null
        val current = BuildConfig.VERSION_NAME.split('.').map { it.toIntOrNull() ?: 0 }
        var newer = false
        for (i in 0..2) { val compare = parts[i].compareTo(current.getOrElse(i) {0}); if (compare != 0) { newer=compare>0; break } }
        if (!newer) return null
        val asset = release.getAsJsonArray("assets").map { it.asJsonObject }.firstOrNull { it.get("name").asString == "LensioCam.apk" } ?: return null
        val url=asset.get("browser_download_url").asString
        val digest=asset.get("digest")?.asString ?: return null
        val size=asset.get("size").asLong
        if (url != "https://github.com/$REPOSITORY/releases/download/v$version/LensioCam.apk" ||
            !digest.matches(Regex("sha256:[0-9a-fA-F]{64}")) || size !in 1..200*1024*1024L) return null
        return VersionInfo(version,size,url,release.get("body")?.asString ?: "",digest.removePrefix("sha256:"))
    }
    suspend fun checkUpdate(forceGitHub: Boolean = false): VersionInfo? = withContext(Dispatchers.IO) {
        val now=android.os.SystemClock.elapsedRealtime()
        if (!forceGitHub && lastGitHubCheck!=0L && now-lastGitHubCheck<15*60*1000L) return@withContext offeredUpdate
        val request=Request.Builder().url("https://api.github.com/repos/$REPOSITORY/releases/latest").header("User-Agent","LensioCam").build()
        client.newCall(request).execute().use { response ->
            if (!response.isSuccessful) throw java.io.IOException("GitHub update check failed (${response.code})")
            val body=response.body ?: throw java.io.IOException("Empty GitHub response")
            if(body.contentLength()>1024*1024)throw java.io.IOException("Release metadata is too large")
            val json=body.charStream().use { reader ->
                val text=StringBuilder();val buffer=CharArray(4096)
                while(true){val count=reader.read(buffer);if(count<0)break;if(text.length+count>1024*1024)throw java.io.IOException("Release metadata is too large");text.append(buffer,0,count)}
                text.toString()
            }
            offeredUpdate=parseGitHubRelease(json)
            lastGitHubCheck=now
            offeredUpdate
        }
    }

    suspend fun downloadAndInstallApk(
        context: Context,
        onProgress: (Float) -> Unit,
        onError: (String) -> Unit
    ) = withContext(Dispatchers.IO) {
        val targetFile = File(context.cacheDir, "LensioCam_Update.apk")
        try {
            val offer=offeredUpdate ?: throw java.io.IOException("Check GitHub for updates first")
            if(!offer.apkUrl.startsWith("https://github.com/$REPOSITORY/releases/download/") ||
                !offer.sha256.matches(Regex("[0-9a-fA-F]{64}")) || offer.apkSize !in 1..200*1024*1024L) throw java.io.IOException("Unverified update offer")
            val downloadUrl=offer.apkUrl
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
                if (contentLength>=0 && contentLength!=offer.apkSize) throw java.io.IOException("Update size does not match the release")
                if (targetFile.exists()) targetFile.delete()

                body.byteStream().use { input ->
                    FileOutputStream(targetFile).use { output ->
                        copyVerifiedApk(input,output,offer.apkSize,offer.sha256) { progress ->
                            withContext(Dispatchers.Main) { onProgress(progress) }
                        }
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
            targetFile.delete()
            if(e is kotlinx.coroutines.CancellationException)throw e
            Log.e(TAG, "Update download failed: ${e.message}", e)
            withContext(Dispatchers.Main) {
                onError("Connection error: ${e.message}")
            }
        }
    }

    internal suspend fun copyVerifiedApk(
        input: java.io.InputStream, output: java.io.OutputStream,
        expectedSize: Long, expectedHash: String, onProgress: suspend (Float) -> Unit = {}
    ) {
        require(expectedSize in 1..200*1024*1024L && expectedHash.matches(Regex("[0-9a-fA-F]{64}")))
        val digest=java.security.MessageDigest.getInstance("SHA-256")
        val buffer=ByteArray(32768);var total=0L
        while(true){
            val count=input.read(buffer);if(count<0)break
            total+=count
            if(total>expectedSize)throw java.io.IOException("Update exceeds release size")
            digest.update(buffer,0,count);output.write(buffer,0,count)
            onProgress(total.toFloat()/expectedSize)
        }
        val hash=digest.digest().joinToString("") { "%02x".format(it) }
        if(total!=expectedSize || !hash.equals(expectedHash,true))throw java.io.IOException("Update integrity verification failed")
    }

    private fun installApk(context: Context, apkFile: File) {
        try {
            if(android.os.Build.VERSION.SDK_INT>=26 && !context.packageManager.canRequestPackageInstalls()) {
                context.startActivity(Intent(android.provider.Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES,Uri.parse("package:${context.packageName}")).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
                android.widget.Toast.makeText(context,"Allow updates from LensioCam, then tap Install update again",android.widget.Toast.LENGTH_LONG).show()
                return
            }
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
