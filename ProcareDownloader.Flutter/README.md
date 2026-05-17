# Procare Photo Downloader Flutter App

This is the active mobile app for Procare Photo Downloader.

## Requirements

- Flutter SDK
- Android SDK for Android builds
- Xcode and Apple signing assets for iOS device, archive, or TestFlight builds

## Run Locally

From this folder:

```powershell
flutter pub get
flutter run
```

Run checks:

```powershell
flutter analyze
flutter test
```

## Android Builds

Debug APK:

```powershell
flutter build apk --debug
```

Release APK:

```powershell
flutter build apk --release
```

GitHub Actions can also build APKs from `.github/workflows/android-release-apk.yml`.

## iOS Builds

Local iOS builds require macOS and Xcode:

```bash
flutter build ios --release
```

Signed CI builds use the root workflow `.github/workflows/ios-signed-build.yml`. The setup helper in `../tools/ios-signing` uploads Apple signing assets to GitHub Actions secrets.

## Privacy Notes

- The app opens the Procare login page in an embedded WebView.
- The app does not collect or store the user's Procare password.
- API session data is kept in memory and cleared on sign out.
- Download history is stored locally on the device so previously saved photos can be marked.
- Downloaded photos are saved only to the destination selected by the user.

Do not commit local downloads, screenshots, emulator captures, logs, API responses, signing files, or other account-specific data.
