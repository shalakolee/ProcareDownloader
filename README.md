# Procare Photo Downloader

Export photos from your Procare account to your phone or computer.

## Mobile App

The mobile app is built with Flutter and lives in `ProcareDownloader.Flutter`. It supports Android and iOS.

Features:

- Sign in with the Procare web login.
- Choose a student after login.
- Browse photos in a day-by-day timeline.
- View thumbnails and open full-size photos.
- Swipe between photos in the viewer.
- Save new photos or redownload photos that were already saved.
- Save to Camera Roll, app storage, or a custom Android folder.
- Organize downloads by student name, date, or both.

See [ProcareDownloader.Flutter/README.md](ProcareDownloader.Flutter/README.md) for mobile setup and build commands.

## Android APK

Android APKs are built by GitHub Actions and attached to GitHub Releases.

1. Open the repository's Releases page.
2. Download the latest `app-debug.apk` or `app-release.apk`.
3. Install the APK on your Android device.

Use debug APKs for testing. Use release APKs for normal installs when a signed release is available.

## Windows Desktop App

The original Windows app is still available in the root WPF project.

Requirements:

- Windows 10 or Windows 11
- .NET 8 SDK
- Microsoft Edge WebView2 Runtime

Build and run:

```powershell
dotnet build
dotnet run
```

The Windows app stores its settings, logs, download history, and browser session data in the current user's local app data folder.

## Builds

Android APK workflow:

- `.github/workflows/android-release-apk.yml`
- Manual runs can build `debug`, `release`, or `both`.

iOS signed build workflow:

- `.github/workflows/ios-signed-build.yml`
- Requires App Store Connect and Apple signing secrets.
- Setup scripts are in `tools/ios-signing`.
