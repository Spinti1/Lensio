# Publishing a release

1. Update the desktop assembly version, Android versionName/versionCode and local update-server metadata together. Use a stable three-part version, such as `2.8.1`.
2. Build and test both clients. Sign the APK using the same private key as previous releases. Never commit a keystore, password, token or user workspace.
3. Push the reviewed sources and a matching version tag to this repository.
4. Run `scripts/Publish-Release.ps1 -Version 2.8.1 -DesktopPath <exe> -AndroidPath <apk> -NotesPath <markdown>` from a trusted local machine. Use an authenticated Git credential helper or the `GITHUB_TOKEN` environment variable. Do not put tokens in command arguments.

The script creates a draft, uploads only the two specified artifacts and a checksum file, verifies the GitHub digests and publishes after all uploads succeed. The clients only offer stable published releases with the expected asset names and SHA-256 metadata. A draft or a partially uploaded release is never offered as an update.

The initial APK follows the existing local development-signing lineage so current installations can update. Public CI deliberately does not publish its separately signed debug APK. Keep the original signing key private and backed up; changing it breaks in-place Android updates.

For the first public snapshot, the old private repository and Git history are intentionally not migrated. Commits use a GitHub noreply address. Do not copy application logs, screenshots of live cameras, workspace files, build caches or AI conversation prompts into future commits.
