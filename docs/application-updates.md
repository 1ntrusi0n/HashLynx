# Application updates

Open **Settings / About** and choose **Check for Updates**. A newer stable GitHub release shows its version, size and notes. Choose **Download update** to retrieve and verify it, then **Restart and update** when recovery and other work are idle. Closing the app normally does not install a staged update.

The changelog and release notes display formatted Markdown, including headings, lists, emphasis, code, quotes, and tables. Select text to copy it, or activate a link to copy its address. Both views follow the application theme and remain scrollable at smaller window sizes. External images and HTML are not loaded.

**Check for Updates Automatically** is unchecked by default. Save preferences to enable it. Startup checks run at most once every 24 hours; a manual check can run sooner. Checking only retrieves public release metadata. Hashes, encrypted files, passwords, wordlists and job information are never sent. The changelog included in About remains available offline.

Only HashLynx is updated. Obtain Hashcat, compute drivers and .NET runtimes separately. Existing settings, queues, targets, histories and results stay in the normal LocalApplicationData workspace. Queues do not restart automatically after an update. Earlier experimental data is not imported.

## Portable installation and recovery

In-app updates require a packaged, writable application folder with `hashlynx-update.json` and the bundled `Updater` directory. Development builds and protected locations offer a release-page link for manual download instead of requesting administrator access. Extract a release as a complete unit into a separate application folder; do not use the local data directory as the installation directory.

Packages are verified against GitHub's asset SHA-256, version, bounded archive structure and per-file manifest. The first-party helper runs from staging, waits for the app to exit, and replaces only application-owned files. Adjacent `hashcat`, `rules`, `wordlist`, and unknown files are preserved. The helper backs up old application files and rolls them back if replacement fails. If another process keeps files locked, close it before retrying. Keep the staging backup if a failure reports that manual recovery is necessary.

Update checks require access to GitHub. Offline access, rate limits, cancelled downloads and unavailable release assets leave recovery usable. There are no prerelease updates or automatic downgrades.

## Maintainers

The shared version is in `Directory.Build.props`. Update `CHANGELOG.md` and user-facing documentation for each release. The changelog is embedded in the app and the displayed version comes from assembly metadata.

Run `scripts/package-release.ps1` after validation to create a fresh Windows x64 framework-dependent publish, its updater, file manifest, release ZIP and SHA-256 checksums. The package deliberately excludes backend binaries and local user collections. For an existing normal publish, generate its file inventory with `scripts/New-UpdateManifest.ps1 -PublishDirectory artifacts/publish/win-x64` only after all published application/helper files are present.

The manual GitHub release workflow restores, builds, tests, runs the WPF smoke checks, packages, and creates a **draft** release. Review the draft and publish it separately. The updater discovers published stable releases through the [GitHub Releases API](https://docs.github.com/en/rest/releases/releases#get-the-latest-release). A version without a valid Windows package/digest is not installable through the app.
