# Procare Photo Downloader

Procare Photo Downloader helps families export photos from their own Procare account.

The repository contains:

- `ProcareDownloader.Flutter`: the current mobile app for Android and iOS.
- `ProcareDownloader`: the original Windows desktop app.
- `ProcareDownloader.Core`: shared models and download logic used by the .NET apps.
- `ProcareDownloader.Mobile`: the earlier .NET MAUI mobile target.

## Download The Android APK

Android APK builds are published from GitHub Actions.

1. Open the repository's GitHub Releases page.
2. Download the newest `app-debug.apk` or `app-release.apk`.
3. Install the APK on your Android device.

Debug APKs are intended for local testing. Release APKs are the better choice for normal use when a signed release is available.

## Current Mobile App

The Flutter app is the active mobile implementation. See [ProcareDownloader.Flutter/README.md](ProcareDownloader.Flutter/README.md) for mobile setup, build, and release commands.

Current Flutter features include:

- Embedded Procare login through a WebView.
- Student selection after login.
- Timeline Explorer photo gallery grouped by day.
- Background photo scanning with incremental progress.
- Thumbnail and full-image preview.
- Swipe navigation in the photo viewer.
- Save selected new photos or intentionally redownload saved photos.
- Save to Camera Roll, app storage, or a custom Android folder.
- Configurable folder layout, including the default `By Student Name` layout.

The app does not ask for or store your Procare password. It uses the active browser session to reach the Procare APIs and keeps API credentials in memory while the app is running.

## Windows Desktop App

The Windows app is the original WPF implementation.

Requirements:

- Windows 10 or Windows 11
- .NET 8 SDK
- Microsoft Edge WebView2 Runtime

Build and run:

```powershell
dotnet build
dotnet run
```

The desktop app stores settings, logs, download history, and WebView session data under the current user's local application data folder.

## CI Builds

Android:

- `.github/workflows/android-release-apk.yml`
- Manual workflow input can build `debug`, `release`, or `both`.
- Release APK signing uses GitHub Actions secrets.

iOS:

- `.github/workflows/ios-signed-build.yml`
- Requires App Store Connect API key, signing certificate, and provisioning profile stored as GitHub Actions secrets.
- Helper scripts live in `tools/ios-signing`.

Do not commit signing keys, provisioning profiles, keystores, `.env` files, downloaded photos, logs, caches, or local planning/design notes.

## Repository Hygiene

The public repository intentionally excludes:

- Local planning notes and design exploration files.
- Screenshots and emulator captures.
- Downloaded Procare photos or cached thumbnails.
- App signing keys, App Store Connect keys, provisioning profiles, and keystores.
- Local logs and runtime data.

If you create local notes or screenshots while working on the app, keep them in ignored folders such as `docs/` or `stitch-screenshots/`.
