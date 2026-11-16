package com.apexcam.network
import org.junit.Assert.*
import org.junit.Test
class GitHubUpdateTest {
 private fun release(url:String="https://github.com/Spinti1/Lensio/releases/download/v99.0.0/LensioCam.apk",digest:String="sha256:"+"a".repeat(64),draft:Boolean=false,version:String="99.0.0")="""{"tag_name":"v$version","draft":$draft,"prerelease":false,"body":"Update","assets":[{"name":"LensioCam.apk","size":123,"browser_download_url":"$url","digest":"$digest"}]}"""
 @Test fun acceptsStableNewerRelease(){val info=UpdateChecker.parseGitHubRelease(release());assertNotNull(info);assertEquals(64,info!!.sha256.length)}
 @Test fun doesNotOfferTheInstalledReleaseAgain(){
  assertNull(UpdateChecker.parseGitHubRelease(release(),"99.0.0"))
  assertNull(UpdateChecker.parseGitHubRelease(release(),"99.0.1"))
  assertNotNull(UpdateChecker.parseGitHubRelease(release(),"98.99.99"))
 }
 @Test fun versionComparisonUsesNumericComponents(){
  assertTrue(UpdateChecker.isNewerVersion("2.11.13","2.11.9"))
  for(version in listOf("2.11.13","2.11.12","2.10.99","2.11.14-beta","2147483648.0.0"))
   assertFalse(UpdateChecker.isNewerVersion(version,"2.11.13"))
 }
 @Test fun rejectsAnOldOrMislabeledApkEvenWithAValidReleaseDigest(){
  fun validate(name:String="2.11.13",code:Long=54,pkg:String="com.apexcam")=
   UpdateChecker.validatePackageVersion("com.apexcam","2.11.12",53,pkg,name,code,"2.11.13")
  validate()
  for((name,code,pkg) in listOf(Triple("2.11.12",53L,"com.apexcam"),Triple("2.11.13",53L,"com.apexcam"),
   Triple("2.11.13",52L,"com.apexcam"),Triple("2.11.14",55L,"com.apexcam"),Triple("2.11.13",54L,"another.package"))){
   try {validate(name,code,pkg);fail("Accepted incompatible APK $name / $code / $pkg")}catch(_:java.io.IOException){}
  }
 }
 @Test fun rejectsAnAlreadyInstalledApkAfterReturningFromInstaller(){
  try {UpdateChecker.validatePackageVersion("com.apexcam","2.11.13",54,"com.apexcam","2.11.13",54,"2.11.13");fail("Offered an installed version")}
  catch(_:java.io.IOException){}
 }
 @Test fun ignoresDraftAndOldVersion(){assertNull(UpdateChecker.parseGitHubRelease(release(draft=true)));assertNull(UpdateChecker.parseGitHubRelease(release(version="1.0.0")))}
 @Test fun rejectsUntrustedLocation(){assertNull(UpdateChecker.parseGitHubRelease(release(url="https://example.com/app.apk")))}
 @Test fun requiresIntegrityDigest(){assertNull(UpdateChecker.parseGitHubRelease(release(digest="")));assertNull(UpdateChecker.parseGitHubRelease(release(digest="sha256:bad")))}
 @Test fun rejectsWrongVersionAndUrlSuffixes(){assertNull(UpdateChecker.parseGitHubRelease(release(url="https://github.com/Spinti1/Lensio/releases/download/v98.0.0/LensioCam.apk")));assertNull(UpdateChecker.parseGitHubRelease(release(url="https://github.com/Spinti1/Lensio/releases/download/v99.0.0/LensioCam.apk?override=1")))}
 @Test fun ignoresPreviewAndUnsignedMetadata(){assertNull(UpdateChecker.parseGitHubRelease(release().replace("\"prerelease\":false","\"prerelease\":true")));assertNull(UpdateChecker.parseGitHubRelease(release().replace("\"size\":123","\"size\":0")))}
 @Test fun verifiesDownloadedBytesAndRejectsWrongSizeOrDigest()=kotlinx.coroutines.runBlocking {
  val data="verified APK data".toByteArray()
  val hash=java.security.MessageDigest.getInstance("SHA-256").digest(data).joinToString(""){"%02x".format(it)}
  val output=java.io.ByteArrayOutputStream()
  UpdateChecker.copyVerifiedApk(data.inputStream(),output,data.size.toLong(),hash)
  assertArrayEquals(data,output.toByteArray())
  for((size,digest) in listOf(data.size-1 to hash,data.size+1 to hash,data.size to "0".repeat(64))){
   var rejected=false
   try{UpdateChecker.copyVerifiedApk(data.inputStream(),java.io.ByteArrayOutputStream(),size.toLong(),digest)}catch(_:java.io.IOException){rejected=true}
   assertTrue("Wrong size or hash accepted",rejected)
  }
 }
}
