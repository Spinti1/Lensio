package com.apexcam.network

import android.content.Context
import android.content.pm.PackageInfo
import android.content.pm.PackageManager
import android.os.Build
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
    private val client = OkHttpClient.Builder()
        .connectTimeout(3, TimeUnit.SECONDS)
        .readTimeout(30, TimeUnit.SECONDS)
        .callTimeout(2, TimeUnit.MINUTES)
        .followSslRedirects(false)
        .build()

    private const val REPOSITORY = "Spinti1/Lensio"
    private var offeredUpdate: VersionInfo? = null
    private var lastGitHubCheck=0L
    internal fun trustedApkUrl(version: String): String? =
        if (version.matches(Regex("[0-9]+\\.[0-9]+\\.[0-9]+")))
            "https://github.com/$REPOSITORY/releases/download/v$version/LensioCam.apk" else null
    internal fun isNewerVersion(candidate: String, installed: String): Boolean {
        fun parts(value: String): List<Int>? {
            if (!value.matches(Regex("[0-9]+\\.[0-9]+\\.[0-9]+"))) return null
            return value.split('.').map { it.toIntOrNull() ?: return null }
        }
        val next = parts(candidate) ?: return false
        val current = parts(installed) ?: return false
        for (i in 0..2) if (next[i] != current[i]) return next[i] > current[i]
        return false
    }

    internal fun parseGitHubRelease(json: String, installedVersion: String = BuildConfig.VERSION_NAME): VersionInfo? {
        val release = com.google.gson.JsonParser.parseString(json).asJsonObject
        if (release.get("draft")?.asBoolean == true || release.get("prerelease")?.asBoolean == true) return null
        val version = release.get("tag_name").asString.removePrefix("v")
        if (!isNewerVersion(version, installedVersion)) return null
        val asset = release.getAsJsonArray("assets").map { it.asJsonObject }.firstOrNull { it.get("name").asString == "LensioCam.apk" } ?: return null
        val url=asset.get("browser_download_url").asString
        val digest=asset.get("digest")?.asString ?: return null
        val size=asset.get("size").asLong
        if (url != trustedApkUrl(version) ||
            !digest.matches(Regex("sha256:[0-9a-fA-F]{64}")) || size !in 1..200*1024*1024L) return null
        return VersionInfo(version,size,url,release.get("body")?.asString ?: "",digest.removePrefix("sha256:"))
    }
    suspend fun checkUpdate(context: Context, forceGitHub: Boolean = false): VersionInfo? = withContext(Dispatchers.IO) {
        val installed = context.packageManager.getPackageInfo(context.packageName, 0).versionName ?: ""
        // Reconcile even a cached offer against Android's installed package, not UI/process state.
        offeredUpdate = offeredUpdate?.takeIf { isNewerVersion(it.versionName, installed) }
        val now=android.os.SystemClock.elapsedRealtime()
        if (!forceGitHub && lastGitHubCheck!=0L && now-lastGitHubCheck<15*60*1000L) return@withContext offeredUpdate
        val request=Request.Builder().url("https://api.github.com/repos/$REPOSITORY/releases/latest").header("User-Agent","LensioCam").header("Cache-Control","no-cache").build()
        client.newCall(request).execute().use { response ->
            if (!response.isSuccessful) throw java.io.IOException("GitHub update check failed (${response.code})")
            val body=response.body ?: throw java.io.IOException("Empty GitHub response")
            if(body.contentLength()>1024*1024)throw java.io.IOException("Release metadata is too large")
            val json=body.charStream().use { reader ->
                val text=StringBuilder();val buffer=CharArray(4096)
                while(true){val count=reader.read(buffer);if(count<0)break;if(text.length+count>1024*1024)throw java.io.IOException("Release metadata is too large");text.append(buffer,0,count)}
                text.toString()
            }
            offeredUpdate=parseGitHubRelease(json, installed)
            lastGitHubCheck=now
            offeredUpdate
        }
    }

    suspend fun prepareUpdate(
        context: Context,
        onProgress: (Float) -> Unit,
        onStage: (String) -> Unit = {}
    ): File = withContext(Dispatchers.IO) {
        val offer=offeredUpdate ?: checkUpdate(context, true)
            ?: throw java.io.IOException("LensioCam is already up to date")
        if(offer.apkUrl != trustedApkUrl(offer.versionName) ||
            !offer.sha256.matches(Regex("[0-9a-fA-F]{64}")) || offer.apkSize !in 1..200*1024*1024L) throw java.io.IOException("Unverified update offer")
        val directory=File(context.cacheDir,"updates").apply { mkdirs() }
        val targetFile=File(directory,"LensioCam-${offer.versionName}.apk")
        try {
            // Returning from Settings or a cancelled installer must not download the APK again.
            if(targetFile.isFile) {
                withContext(Dispatchers.Main) { onStage("Verifying update…") }
                val verified=try {
                    val discard=object: java.io.OutputStream() {
                        override fun write(value: Int) {}
                        override fun write(buffer: ByteArray, offset: Int, length: Int) {}
                    }
                    targetFile.inputStream().use { copyVerifiedApk(it,discard,offer.apkSize,offer.sha256) }
                    true
                } catch(_: java.io.IOException) { false }
                if(verified) { validateArchive(context,targetFile,offer); return@withContext targetFile }
                targetFile.delete()
            }
            withContext(Dispatchers.Main) { onStage("Downloading update…") }
            val downloadUrl=offer.apkUrl
            val request = Request.Builder().url(downloadUrl).build()
            client.newCall(request).execute().use { response ->
                if (!response.isSuccessful) {
                    throw java.io.IOException("Download failed (${response.code})")
                }

                val body = response.body ?: throw java.io.IOException("Empty download response")

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
            }
            withContext(Dispatchers.Main) { onStage("Verifying update…") }
            validateArchive(context,targetFile,offer)
            targetFile
        } catch (e: Exception) {
            targetFile.delete()
            throw e
        }
    }

    @Suppress("DEPRECATION")
    internal fun validateArchive(context: Context, file: File, offer: VersionInfo) {
        val flags=if(Build.VERSION.SDK_INT>=28)PackageManager.GET_SIGNING_CERTIFICATES else PackageManager.GET_SIGNATURES
        val installed=context.packageManager.getPackageInfo(context.packageName,flags)
        val archive=context.packageManager.getPackageArchiveInfo(file.absolutePath,flags)
            ?: throw java.io.IOException("Invalid update APK")
        fun code(info: PackageInfo)=if(Build.VERSION.SDK_INT>=28)info.longVersionCode else info.versionCode.toLong()
        validatePackageVersion(context.packageName,installed.versionName ?: "",code(installed),
            archive.packageName,archive.versionName ?: "",code(archive),offer.versionName)
        fun signers(info: PackageInfo)=
            (if(Build.VERSION.SDK_INT>=28)info.signingInfo?.apkContentsSigners else info.signatures)
                ?.map { it.toCharsString() }?.toSet().orEmpty()
        if(signers(installed).isEmpty() || signers(installed)!=signers(archive))
            throw java.io.IOException("Update signing certificate does not match LensioCam")
    }

    internal fun validatePackageVersion(installedPackage: String, installedName: String, installedCode: Long,
        archivePackage: String, archiveName: String, archiveCode: Long, releaseName: String) {
        if(archivePackage!=installedPackage)throw java.io.IOException("Unexpected update package")
        if(archiveName!=releaseName)throw java.io.IOException("APK version does not match the GitHub release")
        if(!isNewerVersion(archiveName,installedName) || archiveCode<=installedCode)
            throw java.io.IOException("This APK is not newer than the installed LensioCam")
    }

    internal suspend fun copyVerifiedApk(
        input: java.io.InputStream, output: java.io.OutputStream,
        expectedSize: Long, expectedHash: String, onProgress: suspend (Float) -> Unit = {}
    ) {
        require(expectedSize in 1..200*1024*1024L && expectedHash.matches(Regex("[0-9a-fA-F]{64}")))
        val digest=java.security.MessageDigest.getInstance("SHA-256")
        val buffer=ByteArray(32768);var total=0L;var reportedPercent=-1
        while(true){
            val count=input.read(buffer);if(count<0)break
            total+=count
            if(total>expectedSize)throw java.io.IOException("Update exceeds release size")
            digest.update(buffer,0,count);output.write(buffer,0,count)
            val percent=(total*99/expectedSize).toInt()
            if(percent!=reportedPercent){reportedPercent=percent;onProgress((total.toFloat()/expectedSize).coerceAtMost(0.99f))}
        }
        val hash=digest.digest().joinToString("") { "%02x".format(it) }
        if(total!=expectedSize || !hash.equals(expectedHash,true))throw java.io.IOException("Update integrity verification failed")
        onProgress(1f)
    }

}
