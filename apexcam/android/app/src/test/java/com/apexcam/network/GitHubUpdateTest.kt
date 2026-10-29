package com.apexcam.network
import org.junit.Assert.*
import org.junit.Test
class GitHubUpdateTest {
 private fun release(url:String="https://github.com/Spinti1/Lensio/releases/download/v99.0.0/LensioCam.apk",digest:String="sha256:"+"a".repeat(64),draft:Boolean=false,version:String="99.0.0")="""{"tag_name":"v$version","draft":$draft,"prerelease":false,"body":"Update","assets":[{"name":"LensioCam.apk","size":123,"browser_download_url":"$url","digest":"$digest"}]}"""
 @Test fun acceptsStableNewerRelease(){val info=UpdateChecker.parseGitHubRelease(release());assertNotNull(info);assertEquals(64,info!!.sha256.length)}
 @Test fun ignoresDraftAndOldVersion(){assertNull(UpdateChecker.parseGitHubRelease(release(draft=true)));assertNull(UpdateChecker.parseGitHubRelease(release(version="1.0.0")))}
 @Test fun rejectsUntrustedLocation(){assertNull(UpdateChecker.parseGitHubRelease(release(url="https://example.com/app.apk")))}
 @Test fun requiresIntegrityDigest(){assertNull(UpdateChecker.parseGitHubRelease(release(digest="")));assertNull(UpdateChecker.parseGitHubRelease(release(digest="sha256:bad")))}
}
