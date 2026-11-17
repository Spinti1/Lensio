# Publishing a release

1. Update the desktop assembly version, Android versionName/versionCode and desktop manifest together. Use a stable three-part version, such as `2.8.1`.
2. Build and test both clients. Sign the APK using the same private key as previous releases. Never commit a keystore, password, token or user workspace.
3. Push the reviewed sources and a matching version tag to this repository.
4. Run `scripts/Publish-Release.ps1 -Version 2.8.1 -DesktopPath <exe> -AndroidPath <apk> -NotesPath <markdown>` from a trusted local machine. Use an authenticated Git credential helper or the `GITHUB_TOKEN` environment variable. Do not put tokens in command arguments.

The script creates a draft, uploads the two specified artifacts, dependency notices, matching native/OBS sources and a checksum file, verifies the GitHub digests and publishes after all uploads succeed. The clients only offer stable published releases with the expected asset names and SHA-256 metadata. A draft or a partially uploaded release is never offered as an update.

The initial APK follows the existing local development-signing lineage so current installations can update. Public CI deliberately does not publish its separately signed debug APK. Keep the original signing key private and backed up; changing it breaks in-place Android updates.

Android compares release offers against PackageManager's installed version. Before opening the system installer it verifies the APK's exact release size and SHA-256, package name, release versionName, increasing versionCode and matching signing certificate. The verified APK is staged with PackageInstaller, writes are closed before commit, and Android's confirmation opens from the resumed activity. Status callbacks use a private, per-attempt receiver and a package-restricted PendingIntent. Permission approval resumes using the cached APK; install cancellation, storage and policy errors restore retry controls. A visible-activity watchdog recovers missing installer callbacks. Download, verification and installer preparation have distinct labels; downloads have a total timeout and never report completion before integrity checks. A GitHub APK button remains available throughout the flow and opens the exact offered release asset. No installation bypass or extra permissions are used.

For the first public snapshot, the old private repository and Git history are intentionally not migrated. Commits use a GitHub noreply address. Do not copy application logs, screenshots of live cameras, workspace files, build caches or AI conversation prompts into future commits.
