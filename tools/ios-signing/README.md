# iOS CI Signing Tool

This folder contains the portable setup tool for the Flutter iOS signing flow.

The repository workflow is `.github/workflows/ios-signed-build.yml`. It runs on a GitHub macOS runner, imports your signing assets into a temporary keychain, builds `ProcareDownloader.Flutter`, exports a signed `.ipa`, and optionally uploads an App Store build to TestFlight.

## What You Need

- A GitHub repository with Actions enabled.
- GitHub CLI (`gh`) authenticated on the machine where you run the setup script.
- An App Store Connect API key: `AuthKey_<KEY_ID>.p8`, key ID, and issuer ID.
- An Apple Distribution `.p12` certificate and its export password.
- A distribution `.mobileprovision` profile for the app bundle ID.

Do not commit the `.p8`, `.p12`, `.mobileprovision`, or passwords. The setup script stores them as GitHub Actions secrets.

## Apple Setup

1. In App Store Connect, create an API key under Users and Access > Integrations.
2. Download the private key once and keep the key ID and issuer ID.
3. In Apple Developer Certificates, Identifiers & Profiles, create or select the explicit App ID for the app bundle ID.
4. Create an Apple Distribution certificate, export it from Keychain Access as `.p12`, and remember the password.
5. Create an App Store Connect or Ad Hoc provisioning profile using that App ID and distribution certificate, then download the `.mobileprovision`.

## Upload Secrets

Run one of these from the repo root. macOS gives the best validation because it can inspect the provisioning profile.

```bash
./tools/ios-signing/setup-ios-signing.sh \
  --repo shalakolee/ProcareDownloader \
  --api-key ~/Downloads/AuthKey_ABC123DEFG.p8 \
  --issuer-id 11111111-2222-3333-4444-555555555555 \
  --certificate ~/Desktop/apple_distribution.p12 \
  --profile ~/Downloads/ProcareDownloader_AppStore.mobileprovision \
  --certificate-password 'p12-password'
```

PowerShell, useful from Windows:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\ios-signing\setup-ios-signing.ps1 `
  -Repo shalakolee/ProcareDownloader `
  -ApiKey "$HOME\Downloads\AuthKey_ABC123DEFG.p8" `
  -IssuerId "11111111-2222-3333-4444-555555555555" `
  -Certificate "$HOME\Desktop\apple_distribution.p12" `
  -Profile "$HOME\Downloads\ProcareDownloader_AppStore.mobileprovision" `
  -CertificatePassword "p12-password"
```

Use `--dry-run` first if you want to validate inputs without writing secrets.
For PowerShell, use `-DryRun`.

Use `--trigger` to start the workflow immediately after uploading secrets:

```bash
./tools/ios-signing/setup-ios-signing.sh \
  --repo shalakolee/ProcareDownloader \
  --api-key ~/Downloads/AuthKey_ABC123DEFG.p8 \
  --issuer-id 11111111-2222-3333-4444-555555555555 \
  --certificate ~/Desktop/apple_distribution.p12 \
  --profile ~/Downloads/ProcareDownloader_AppStore.mobileprovision \
  --certificate-password 'p12-password' \
  --export-method app-store \
  --upload-testflight true \
  --trigger
```

PowerShell trigger:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\ios-signing\setup-ios-signing.ps1 `
  -Repo shalakolee/ProcareDownloader `
  -ApiKey "$HOME\Downloads\AuthKey_ABC123DEFG.p8" `
  -IssuerId "11111111-2222-3333-4444-555555555555" `
  -Certificate "$HOME\Desktop\apple_distribution.p12" `
  -Profile "$HOME\Downloads\ProcareDownloader_AppStore.mobileprovision" `
  -CertificatePassword "p12-password" `
  -ExportMethod app-store `
  -UploadTestFlight true `
  -Trigger
```

## GitHub Secrets

The tool writes these secrets:

- `APP_STORE_CONNECT_KEY_ID`
- `APP_STORE_CONNECT_ISSUER_ID`
- `APP_STORE_CONNECT_API_KEY_BASE64`
- `IOS_DISTRIBUTION_CERTIFICATE_BASE64`
- `IOS_DISTRIBUTION_CERTIFICATE_PASSWORD`
- `IOS_PROVISIONING_PROFILE_BASE64`

Optional:

- `IOS_SIGNING_CERTIFICATE_NAME`, for example `Apple Distribution`.

## Build Outputs

Run the `iOS Signed Build` workflow manually from GitHub Actions.

- `export_method=app-store`: signed IPA suitable for App Store Connect/TestFlight.
- `export_method=ad-hoc`: signed IPA for registered test devices.
- `upload_testflight=true`: uploads the app-store IPA to TestFlight after export.

The workflow also uploads the `.ipa` as a GitHub Actions artifact.
