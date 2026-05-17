# Procare Photo Downloader Mobile

Flutter app for exporting Procare photos on Android and iOS.

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

GitHub Actions can also build APKs from the root Android workflow.

## iOS Builds

Local iOS builds require macOS and Xcode:

```bash
flutter build ios --release
```

Signed CI builds use the root iOS workflow. The setup scripts in `../tools/ios-signing` upload Apple signing assets to GitHub Actions secrets.

## Privacy Notes

- **The app opens the Procare login page in an embedded WebView.**
- **The app does not collect or store the user's Procare password.**
- **API session data is kept in memory and cleared on sign out.**
- Download history is stored locally on the device so previously saved photos can be marked.
- Downloaded photos are saved only to the destination selected by the user.
