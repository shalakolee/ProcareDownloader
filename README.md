# Procare Photo Downloader

A WPF (.NET 8) desktop app to download all your photos from Procare Connect (schools.procareconnect.com).

## How It Works

1. **Login** — An embedded browser (WebView2) loads the real Procare login page. Log in normally.
2. **Token capture** — The app intercepts the Bearer token from outgoing API requests. No credentials are stored.
3. **Student select** — If you have multiple children, pick one.
4. **Gallery** — All photos load as thumbnails. Click to select/deselect. Use "Select All" for bulk select.
5. **Download** — Choose a folder. Photos download at full resolution, named `YYYY-MM-DD_{id}.jpg`.

## Prerequisites

- **Windows 10/11**
- **.NET 8 SDK** — https://dotnet.microsoft.com/download
- **WebView2 Runtime** — Usually pre-installed on Windows 11. If not:
  https://developer.microsoft.com/microsoft-edge/webview2/

## Build & Run

```bash
git clone <this-repo>
cd ProcareDownloader
dotnet run
```

Or open `ProcareDownloader.csproj` in Visual Studio 2022+ and hit F5.

## Project Structure

```
ProcareDownloader/
├── Models/
│   └── Models.cs              # Student, Photo, TokenInfo
├── Services/
│   ├── ProcareApiService.cs   # All HTTP calls to Procare API
│   ├── TokenInterceptorService.cs  # Captures Bearer token from WebView2
│   └── DownloadService.cs     # Parallel file download manager
├── ViewModels/
│   └── MainViewModel.cs       # MVVM state machine + commands
├── Views/
│   ├── MainWindow.xaml        # Dark gallery UI
│   └── MainWindow.xaml.cs     # WebView2 init, click handlers
└── Converters/
    └── Converters.cs          # State → Visibility converters
```

## Troubleshooting

**"WebView2 init failed"** — Install the WebView2 Runtime from the link above.

**Photos not loading / API errors** — Procare may have updated their API. Open DevTools in the
embedded browser (right-click → Inspect) to check the actual API endpoints and update
`ProcareApiService.cs` accordingly. Look for requests to `api.procareconnect.com`.

**Token not captured** — Try navigating to a page that shows photos in the embedded browser.
The interceptor fires when an API call with `Authorization: Bearer ...` goes out.

## Notes

- Photo files are skipped if they already exist in the destination folder (safe to re-run).
- Downloads are parallelized with a cap of 4 concurrent connections.
- WebView2 session is persisted in `%LocalAppData%\ProcareDownloader\WebView2Data`,
  so you stay logged in between runs.
