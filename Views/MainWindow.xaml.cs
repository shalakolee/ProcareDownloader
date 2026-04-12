using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using ProcareDownloader.Models;
using ProcareDownloader.Services;
using ProcareDownloader.ViewModels;

namespace ProcareDownloader.Views;

public partial class MainWindow : Window
{
    private readonly string _userDataFolder;
    private readonly ProcareApiService _api;
    private readonly MainViewModel _vm;
    private readonly TokenInterceptorService _interceptor;
    private readonly DispatcherTimer _tokenPollTimer;

    public MainWindow()
    {
        InitializeComponent();

        _userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProcareDownloader",
            "WebView2Data");

        _api = new ProcareApiService();
        var history = new DownloadHistoryService();
        var settings = new SettingsService();
        var downloader = new DownloadService(_api, history);
        _interceptor = new TokenInterceptorService();
        _vm = new MainViewModel(_api, downloader, history, settings);
        _tokenPollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _tokenPollTimer.Tick += OnTokenPoll;

        DataContext = _vm;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await InitWebViewAsync();
    }

    private async Task InitWebViewAsync()
    {
        try
        {
            Directory.CreateDirectory(_userDataFolder);

            var env = await CoreWebView2Environment.CreateAsync(null, _userDataFolder);
            await LoginWebView.EnsureCoreWebView2Async(env);
            _api.AttachBrowser(LoginWebView.CoreWebView2);

            _interceptor.Attach(LoginWebView.CoreWebView2);
            _interceptor.TokenCaptured += OnTokenCaptured;
            LoginWebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            await LoginWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(@"
                (() => {
                    if (window.__procareDownloaderHookInstalled) return;
                    window.__procareDownloaderHookInstalled = true;

                    const postAuth = (authorization, organizationId, url) => {
                        if (!authorization || typeof authorization !== 'string') return;
                        const trimmed = authorization.trim();
                        if (!trimmed.toLowerCase().startsWith('bearer ')) return;

                        const message = {
                            type: 'auth',
                            accessToken: trimmed.slice(7).trim(),
                            organizationId: organizationId || null,
                            url: url || location.href
                        };

                        try {
                            window.chrome.webview.postMessage(JSON.stringify(message));
                        } catch {}
                    };

                    const readHeaders = (headers) => {
                        let authorization = null;
                        let organizationId = null;
                        if (!headers) return { authorization, organizationId };

                        if (headers instanceof Headers) {
                            authorization = headers.get('authorization');
                            organizationId = headers.get('x-organization-id');
                            return { authorization, organizationId };
                        }

                        if (Array.isArray(headers)) {
                            for (const entry of headers) {
                                if (!Array.isArray(entry) || entry.length < 2) continue;
                                const name = String(entry[0]).toLowerCase();
                                if (name === 'authorization') authorization = entry[1];
                                if (name === 'x-organization-id') organizationId = entry[1];
                            }
                            return { authorization, organizationId };
                        }

                        for (const [key, value] of Object.entries(headers)) {
                            const name = String(key).toLowerCase();
                            if (name === 'authorization') authorization = value;
                            if (name === 'x-organization-id') organizationId = value;
                        }

                        return { authorization, organizationId };
                    };

                    const originalFetch = window.fetch;
                    window.fetch = function(input, init) {
                        try {
                            const requestUrl = typeof input === 'string' ? input : (input && input.url) || location.href;
                            const headers = readHeaders((init && init.headers) || (input && input.headers));
                            postAuth(headers.authorization, headers.organizationId, requestUrl);
                        } catch {}
                        return originalFetch.apply(this, arguments);
                    };

                    const originalOpen = XMLHttpRequest.prototype.open;
                    const originalSetRequestHeader = XMLHttpRequest.prototype.setRequestHeader;

                    XMLHttpRequest.prototype.open = function(method, url) {
                        this.__procareDownloaderUrl = url;
                        return originalOpen.apply(this, arguments);
                    };

                    XMLHttpRequest.prototype.setRequestHeader = function(name, value) {
                        try {
                            const lower = String(name).toLowerCase();
                            this.__procareDownloaderHeaders = this.__procareDownloaderHeaders || {};
                            this.__procareDownloaderHeaders[lower] = value;
                            postAuth(
                                this.__procareDownloaderHeaders['authorization'],
                                this.__procareDownloaderHeaders['x-organization-id'],
                                this.__procareDownloaderUrl
                            );
                        } catch {}

                        return originalSetRequestHeader.apply(this, arguments);
                    };
                })();
            ");

            _vm.StatusMessage = "Log in to Procare. The gallery will appear after photos load.";
            _tokenPollTimer.Start();
            AppLog.Info("Initialized WebView2. Login page: https://schools.procareconnect.com/login");
            LoginWebView.CoreWebView2.Navigate("https://schools.procareconnect.com/login");
        }
        catch (Exception ex)
        {
            AppLog.Error("WebView2 initialization failed.", ex);
            _vm.StatusMessage = $"WebView2 init failed: {ex.Message}. Make sure WebView2 Runtime is installed.";
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var message = e.TryGetWebMessageAsString();
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            var node = JsonNode.Parse(message);
            if (!string.Equals(node?["type"]?.GetValue<string>(), "auth", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var info = new TokenInfo
            {
                AccessToken = node?["accessToken"]?.GetValue<string>() ?? "",
                OrganizationId = node?["organizationId"]?.GetValue<string>()
            };

            if (_interceptor.TryCapture(info))
            {
                _tokenPollTimer.Stop();
                AppLog.Info("Captured authentication token from browser message.");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Malformed browser message ignored: {ex.Message}");
        }
    }

    private async void OnTokenPoll(object? sender, EventArgs e)
    {
        try
        {
            if (LoginWebView.CoreWebView2 == null)
            {
                return;
            }

            if (await _interceptor.TryCaptureFromPageAsync(LoginWebView.CoreWebView2))
            {
                _tokenPollTimer.Stop();
                AppLog.Info("Captured authentication token from browser storage polling.");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Browser token polling failed: {ex.Message}");
        }
    }

    private async void OnTokenCaptured(object? sender, TokenInfo token)
    {
        _tokenPollTimer.Stop();
        AppLog.Info("Token captured event received by main window.");

        await Dispatcher.InvokeAsync(async () => { await _vm.OnTokenCapturedAsync(token); });
    }

    private void Photo_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PhotoViewModel photo)
        {
            photo.IsSelected = !photo.IsSelected;
            _vm.NotifySelectionChanged();
        }
    }

    private async void DownloadBtn_Click(object sender, RoutedEventArgs e)
    {
        var selectedPath = SelectDownloadFolder();
        if (selectedPath != null)
        {
            await _vm.DownloadSelectedAsync(selectedPath);
        }
    }

    private async void DownloadUnsavedBtn_Click(object sender, RoutedEventArgs e)
    {
        var selectedPath = SelectDownloadFolder();
        if (selectedPath != null)
        {
            await _vm.DownloadUnsavedAsync(selectedPath);
        }
    }

    private void ImportDownloadsBtn_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose a folder to scan for previously downloaded photos",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }

        var message = _vm.ImportExistingDownloads(dialog.SelectedPath);
        _vm.SubStatus = message;
        _vm.StatusMessage = "Import existing downloads completed.";
        AppLog.Info($"{message} Source folder: {dialog.SelectedPath}");
    }

    private async void LogoutBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _vm.CloseSettingsCommand.Execute(null);
            _vm.PrepareForLogin("Clearing session. Log in with a different account.");
            await ResetBrowserSessionAsync();
            _tokenPollTimer.Start();
            _vm.PrepareForLogin("Session cleared. Log in to Procare with a different account.");
        }
        catch (Exception ex)
        {
            AppLog.Error("Failed to reset browser session.", ex);
            _vm.PrepareForLogin($"Could not clear the session cleanly: {ex.Message}");
        }
    }

    private async Task ResetBrowserSessionAsync()
    {
        if (LoginWebView.CoreWebView2 == null)
        {
            return;
        }

        _tokenPollTimer.Stop();
        _interceptor.Reset();
        _api.ClearCredentials();

        try
        {
            LoginWebView.CoreWebView2.CookieManager.DeleteAllCookies();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Cookie reset failed: {ex.Message}");
        }

        try
        {
            await LoginWebView.CoreWebView2.Profile.ClearBrowsingDataAsync();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Profile data reset failed: {ex.Message}");
        }

        try
        {
            await LoginWebView.CoreWebView2.ExecuteScriptAsync(@"
                (async function() {
                    try { localStorage.clear(); } catch {}
                    try { sessionStorage.clear(); } catch {}
                    try {
                        if (window.indexedDB && indexedDB.databases) {
                            const dbs = await indexedDB.databases();
                            for (const db of dbs) {
                                if (db && db.name) {
                                    indexedDB.deleteDatabase(db.name);
                                }
                            }
                        }
                    } catch {}
                })();
            ");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Browser storage reset script failed: {ex.Message}");
        }

        LoginWebView.CoreWebView2.Navigate("about:blank");
        await Task.Delay(400);
        LoginWebView.CoreWebView2.Navigate("https://schools.procareconnect.com/login");
        AppLog.Info("Reset browser session and navigated back to login.");
    }

    private static string? SelectDownloadFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose where to save your photos",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };

        return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK
            ? dialog.SelectedPath
            : null;
    }
}
