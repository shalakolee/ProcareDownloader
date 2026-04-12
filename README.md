# Procare Photo Downloader

A Windows desktop app for browsing and downloading full-resolution photos from Procare Connect at `schools.procareconnect.com`.

Built with WPF on .NET 8.

## What It Does

- Uses the real Procare login flow in an embedded WebView2 browser
- Detects the authenticated API session without storing your password
- Lets you choose a student when multiple children are available
- Loads a photo gallery grouped by year and month
- Supports collapsible month sections and month-level selection
- Downloads full-resolution originals, not thumbnail images
- Tracks photos that were already saved and marks them in the gallery
- Adds a `Download Unsaved` action so repeat runs only fetch new photos
- Lets you choose how files are organized on disk from a settings screen
- Lets you import an existing download folder to backfill download history
- Lets you clear the current session and sign in with a different account

## Current Features

### Gallery

- Student selection screen after login
- Photo thumbnails with lazy loading
- Month-by-month grouping such as `April 2026`
- Collapsible month sections
- `Select All` across the gallery
- `Select Month` per month group
- Saved badge for files already downloaded

### Downloading

- Full-resolution photo downloads
- Parallel downloads with progress bar and per-file status
- Download selected photos
- Download only unsaved photos
- Skip previously downloaded photos safely

### Save Layouts

Choose the layout from the in-app settings screen:

- `Single Folder`
- `Year / Month`
- `Student / Year`
- `Student / Year / Month`

Examples:

- `ChosenFolder\\2026\\04\\2026-04-10_12345.jpg`
- `ChosenFolder\\Nathan Lee\\2026\\04\\2026-04-10_12345.jpg`

### Tracking and Logs

- Download history is stored locally so the app can identify previously saved photos
- Existing folders can be imported to mark older downloads as already saved
- Application logs are written locally for troubleshooting

Default local data paths:

- Logs: `%LOCALAPPDATA%\\ProcareDownloader\\logs\\app.log`
- Download history: `%LOCALAPPDATA%\\ProcareDownloader\\download-history.json`
- Settings: `%LOCALAPPDATA%\\ProcareDownloader\\settings.json`
- WebView session data: `%LOCALAPPDATA%\\ProcareDownloader\\WebView2Data`

## Requirements

- Windows 10 or Windows 11
- .NET 8 SDK
- Microsoft Edge WebView2 Runtime

WebView2 is usually already installed on modern Windows systems. If not, install it from:

`https://developer.microsoft.com/microsoft-edge/webview2/`

## Build and Run

```powershell
dotnet build
dotnet run
```

To run the built executable directly:

```powershell
.\\bin\\Debug\\net8.0-windows\\ProcareDownloader.exe
```

You can also open `ProcareDownloader.csproj` in Visual Studio 2022 or later and run it with F5.

## Project Layout

```text
ProcareDownloader/
├── Assets/
│   └── AppIcon.ico
├── Converters/
│   └── Converters.cs
├── Models/
│   └── Models.cs
├── Services/
│   ├── AppLog.cs
│   ├── DownloadHistoryService.cs
│   ├── DownloadService.cs
│   ├── ProcareApiService.cs
│   ├── SettingsService.cs
│   └── TokenInterceptorService.cs
├── ViewModels/
│   └── MainViewModel.cs
├── Views/
│   ├── MainWindow.xaml
│   └── MainWindow.xaml.cs
├── App.xaml
└── ProcareDownloader.csproj
```

## How Authentication Works

1. The app opens the real Procare login page in WebView2.
2. After you sign in, the app watches authenticated browser traffic.
3. It captures the bearer token and organization context from the active session.
4. It uses that session to load students, activities, and photo originals.

The app does not ask for or save your Procare password.

## Troubleshooting

### WebView2 initialization failure

Install or repair the WebView2 Runtime.

### Student or photo loading failures

Check:

- `%LOCALAPPDATA%\\ProcareDownloader\\logs\\app.log`
- your current Procare session in the embedded browser
- whether Procare changed API responses or page behavior

### Previously saved photos are not marked

Open Settings and use `Import Existing Downloads Folder` on the folder tree that already contains downloaded images.

### Wrong account is still logged in

Open Settings and use `Log Out And Change Account`.

## Notes

- The app is intended for personal photo export from your own Procare account.
- Downloaded files are named with the photo date and photo id to make re-runs stable.
- The app is conservative about duplicates and will skip files already known in history.
