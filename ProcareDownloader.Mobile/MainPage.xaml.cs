using System.Text.Json.Nodes;
using ProcareDownloader.Mobile.Services;
using ProcareDownloader.Mobile.ViewModels;
using ProcareDownloader.Models;
using ProcareDownloader.Services;
#if ANDROID
using AndroidWebView = Android.Webkit.WebView;
#endif

namespace ProcareDownloader.Mobile;

public partial class MainPage : ContentPage
{
    private const string AuthCaptureKey = "__procareDownloaderAuth";
    private readonly MobileProcareApiService _api;
    private readonly MainPageViewModel _viewModel;
    private readonly IDispatcherTimer _authTimer;
    private bool _isCapturing;
    private double _currentScale = 1;
    private double _startScale = 1;
    private double _xOffset;
    private double _yOffset;
    private double _panStartX;
    private double _panStartY;

    public MainPage(MobileProcareApiService api, MainPageViewModel viewModel)
    {
        InitializeComponent();
        _api = api;
        _viewModel = viewModel;
        BindingContext = _viewModel;
        _api.AttachBrowser(script => MainThread.InvokeOnMainThreadAsync(() => LoginWebView.EvaluateJavaScriptAsync(script)));

        _viewModel.ReloadLoginRequested += OnReloadLoginRequested;

        _authTimer = Dispatcher.CreateTimer();
        _authTimer.Interval = TimeSpan.FromSeconds(2);
        _authTimer.Tick += async (_, _) =>
        {
            await EnsureAuthHookAsync();
            if (await _viewModel.TryContinueFromBrowserSessionAsync())
            {
                return;
            }

            await TryCaptureTokenAsync();
        };
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _authTimer.Start();
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    protected override void OnDisappearing()
    {
        _authTimer.Stop();
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        base.OnDisappearing();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainPageViewModel.IsPhotoViewerOpen)
            || e.PropertyName == nameof(MainPageViewModel.PhotoViewerSource))
        {
            MainThread.BeginInvokeOnMainThread(ResetPhotoViewerTransform);
        }
    }

    private async void LoginWebView_Navigated(object? sender, WebNavigatedEventArgs e)
    {
        if (_viewModel.IsLoginVisible)
        {
            await EnsureLoginViewportAsync();

            if (!string.IsNullOrWhiteSpace(e.Url) &&
                !e.Url.Contains("/login", StringComparison.OrdinalIgnoreCase))
            {
                _viewModel.StatusMessage = "Session detected. Finishing sign-in...";
                _viewModel.SubStatus = "Capturing your Procare session automatically.";
            }

            await EnsureAuthHookAsync();
            if (await _viewModel.TryContinueFromBrowserSessionAsync())
            {
                return;
            }

            await TryCaptureTokenAsync();
        }
    }

    private async void LoginWebView_HandlerChanged(object? sender, EventArgs e)
    {
        ConfigureLoginWebView();
        await EnsureLoginViewportAsync();
    }

    private void OnReloadLoginRequested(object? sender, EventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(ResetLoginWebView);
    }

    private void ResetLoginWebView()
    {
        var separator = _viewModel.LoginUrl.Contains('?') ? "&" : "?";
        LoginWebView.Source = $"{_viewModel.LoginUrl}{separator}reload={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
    }

    private void ConfigureLoginWebView()
    {
#if ANDROID
        if (LoginWebView.Handler?.PlatformView is AndroidWebView nativeView)
        {
            var settings = nativeView.Settings;
            settings.UseWideViewPort = false;
            settings.LoadWithOverviewMode = false;
            settings.BuiltInZoomControls = true;
            settings.DisplayZoomControls = false;
            settings.SetSupportZoom(true);
            settings.DomStorageEnabled = true;
        }
#endif
    }

    private async Task EnsureLoginViewportAsync()
    {
        if (!_viewModel.IsLoginVisible)
        {
            return;
        }

        try
        {
            await LoginWebView.EvaluateJavaScriptAsync("""
                (function() {
                    const existing = document.querySelector('meta[name="viewport"]');
                    const content = 'width=device-width, initial-scale=1, maximum-scale=1, user-scalable=yes';
                    if (existing) {
                        existing.setAttribute('content', content);
                    } else {
                        const meta = document.createElement('meta');
                        meta.name = 'viewport';
                        meta.content = content;
                        document.head.appendChild(meta);
                    }

                    if (document.documentElement) {
                        document.documentElement.style.width = '100%';
                        document.documentElement.style.maxWidth = '100vw';
                        document.documentElement.style.overflowX = 'hidden';
                    }

                    if (document.body) {
                        document.body.style.width = '100%';
                        document.body.style.maxWidth = '100vw';
                        document.body.style.overflowX = 'hidden';
                        document.body.style.margin = '0';
                    }

                    for (const element of document.querySelectorAll('img, iframe, video, canvas')) {
                        element.style.maxWidth = '100%';
                        element.style.height = 'auto';
                    }

                    const styleId = 'procare-downloader-mobile-fit';
                    let style = document.getElementById(styleId);
                    if (!style) {
                        style = document.createElement('style');
                        style.id = styleId;
                        document.head.appendChild(style);
                    }

                    style.textContent = `
                        html, body {
                            width: 100% !important;
                            max-width: 100vw !important;
                            min-width: 0 !important;
                            overflow-x: hidden !important;
                        }

                        body * {
                            box-sizing: border-box !important;
                        }

                        main, section, article, form,
                        div[class*="container"], div[class*="content"], div[class*="form"],
                        div[class*="login"], div[class*="auth"], div[class*="panel"] {
                            max-width: calc(100vw - 24px) !important;
                            min-width: 0 !important;
                        }

                        input, textarea, select, button {
                            max-width: 100% !important;
                        }
                    `;

                    const viewportWidth = window.innerWidth || document.documentElement.clientWidth || 0;
                    if (viewportWidth > 0) {
                        const maxContentWidth = Math.max(280, viewportWidth - 24);
                        for (const element of document.querySelectorAll('main, section, article, form, div')) {
                            const rect = element.getBoundingClientRect();
                            if (rect.width > viewportWidth || rect.right > viewportWidth) {
                                element.style.maxWidth = `${maxContentWidth}px`;
                                element.style.minWidth = '0';
                            }
                        }
                    }

                    return 'ok';
                })();
                """);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Failed to apply mobile viewport to login page. {ex.Message}");
        }
    }

    private async Task EnsureAuthHookAsync()
    {
        if (!_viewModel.IsLoginVisible)
        {
            return;
        }

        try
        {
            await LoginWebView.EvaluateJavaScriptAsync($$"""
                (function() {
                    const key = '{{AuthCaptureKey}}';

                    const looksLikeJwt = (value) =>
                        typeof value === 'string' &&
                        /^[A-Za-z0-9-_]+\.[A-Za-z0-9-_]+\.[A-Za-z0-9-_]+$/.test(value.trim());

                    const persistAuth = (accessToken, organizationId, url) => {
                        if (!accessToken || typeof accessToken !== 'string') return false;

                        const payload = JSON.stringify({
                            accessToken: accessToken.trim(),
                            organizationId: organizationId || null,
                            url: url || location.href,
                            capturedAt: new Date().toISOString()
                        });

                        try { window[key] = payload; } catch {}
                        try { sessionStorage.setItem(key, payload); } catch {}
                        try { localStorage.setItem(key, payload); } catch {}
                        return true;
                    };

                    const normalizeString = (value) => {
                        if (!value || typeof value !== 'string') return null;
                        const trimmed = value.trim();
                        if (!trimmed) return null;

                        if (trimmed.startsWith('Bearer ')) {
                            return { accessToken: trimmed.slice(7).trim(), organizationId: null };
                        }

                        if (looksLikeJwt(trimmed)) {
                            return { accessToken: trimmed, organizationId: null };
                        }

                        return null;
                    };

                    const findAuth = (value, depth = 0) => {
                        if (depth > 6 || value == null) return null;

                        if (typeof value === 'string') {
                            const direct = normalizeString(value);
                            if (direct) return direct;

                            try {
                                return findAuth(JSON.parse(value), depth + 1);
                            } catch {
                                return null;
                            }
                        }

                        if (Array.isArray(value)) {
                            for (const item of value) {
                                const found = findAuth(item, depth + 1);
                                if (found) return found;
                            }

                            return null;
                        }

                        if (typeof value === 'object') {
                            const candidates = [
                                value.access_token,
                                value.accessToken,
                                value.token,
                                value.authToken,
                                value.auth_token
                            ];

                            for (const candidate of candidates) {
                                const direct = normalizeString(candidate);
                                if (direct) {
                                    return {
                                        accessToken: direct.accessToken,
                                        organizationId: value.organization_id || value.organizationId || value.orgId || null
                                    };
                                }
                            }

                            for (const nested of Object.values(value)) {
                                const found = findAuth(nested, depth + 1);
                                if (found) return found;
                            }
                        }

                        return null;
                    };

                    const scanStore = (store) => {
                        if (!store) return false;

                        for (let i = 0; i < store.length; i++) {
                            try {
                                const keyName = store.key(i);
                                const raw = store.getItem(keyName);
                                const found = findAuth(raw);
                                if (found) {
                                    return persistAuth(found.accessToken, found.organizationId, location.href);
                                }
                            } catch {}
                        }

                        return false;
                    };

                    const scanGlobals = () => {
                        const globals = [
                            window.__INITIAL_STATE__,
                            window.__NEXT_DATA__,
                            window.__APOLLO_STATE__,
                            window.__NUXT__,
                            window.__STORE__
                        ];

                        for (const candidate of globals) {
                            const found = findAuth(candidate);
                            if (found) {
                                return persistAuth(found.accessToken, found.organizationId, location.href);
                            }
                        }

                        return false;
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

                        for (const [name, value] of Object.entries(headers)) {
                            const lower = String(name).toLowerCase();
                            if (lower === 'authorization') authorization = value;
                            if (lower === 'x-organization-id') organizationId = value;
                        }

                        return { authorization, organizationId };
                    };

                    const captureAuthHeader = (authorization, organizationId, url) => {
                        const normalized = normalizeString(authorization);
                        if (!normalized) return false;
                        return persistAuth(normalized.accessToken, organizationId || normalized.organizationId, url);
                    };

                    if (!window.__procareDownloaderHookInstalled) {
                        window.__procareDownloaderHookInstalled = true;

                        const originalFetch = window.fetch;
                        if (typeof originalFetch === 'function') {
                            window.fetch = function(input, init) {
                                try {
                                    const requestUrl = typeof input === 'string' ? input : (input && input.url) || location.href;
                                    const headers = readHeaders((init && init.headers) || (input && input.headers));
                                    captureAuthHeader(headers.authorization, headers.organizationId, requestUrl);
                                } catch {}

                                return originalFetch.apply(this, arguments);
                            };
                        }

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
                                captureAuthHeader(
                                    this.__procareDownloaderHeaders['authorization'],
                                    this.__procareDownloaderHeaders['x-organization-id'],
                                    this.__procareDownloaderUrl
                                );
                            } catch {}

                            return originalSetRequestHeader.apply(this, arguments);
                        };
                    }

                    return scanStore(sessionStorage) || scanStore(localStorage) || scanGlobals() || 'hooked';
                })();
                """);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Mobile auth hook injection failed: {ex.Message}");
        }
    }

    private async Task TryCaptureTokenAsync()
    {
        if (_isCapturing || !_viewModel.IsLoginVisible)
        {
            return;
        }

        _isCapturing = true;

        try
        {
            var result = await LoginWebView.EvaluateJavaScriptAsync($$"""
                (function() {
                    const key = '{{AuthCaptureKey}}';

                    const looksLikeJwt = (value) =>
                        typeof value === 'string' &&
                        /^[A-Za-z0-9-_]+\.[A-Za-z0-9-_]+\.[A-Za-z0-9-_]+$/.test(value.trim());

                    const normalize = (value, depth = 0) => {
                        if (depth > 6) return null;
                        if (!value || typeof value !== 'string') return null;
                        const trimmed = value.trim();
                        if (!trimmed) return null;
                        if (trimmed.startsWith('Bearer ')) {
                            return { accessToken: trimmed.slice(7).trim() };
                        }
                        if (looksLikeJwt(trimmed)) {
                            return { accessToken: trimmed };
                        }
                        try {
                            const parsed = JSON.parse(trimmed);
                            return findAuth(parsed, depth + 1);
                        } catch {}
                        return null;
                    };

                    const findAuth = (value, depth = 0) => {
                        if (depth > 6 || value == null) return null;

                        if (typeof value === 'string') {
                            return normalize(value, depth + 1);
                        }

                        if (Array.isArray(value)) {
                            for (const item of value) {
                                const found = findAuth(item, depth + 1);
                                if (found) return found;
                            }

                            return null;
                        }

                        if (typeof value === 'object') {
                            const accessToken = value.access_token || value.accessToken || value.token || value.authToken || value.auth_token;
                            const organizationId = value.organization_id || value.organizationId || value.orgId;
                            const normalizedAccessToken = typeof accessToken === 'string' ? normalize(accessToken, depth + 1) : null;
                            if (normalizedAccessToken?.accessToken) {
                                return { accessToken: normalizedAccessToken.accessToken, organizationId };
                            }

                            for (const nested of Object.values(value)) {
                                const found = findAuth(nested, depth + 1);
                                if (found) return found;
                            }
                        }

                        return null;
                    };

                    const readStore = (store) => {
                        try {
                            const captured = findAuth(store.getItem(key));
                            if (captured) return captured;
                        } catch {}

                        const directKeys = ['access_token', 'accessToken', 'token', 'authToken', 'auth_token'];
                        for (const key of directKeys) {
                            try {
                                const found = normalize(store.getItem(key));
                                if (found) return found;
                            } catch {}
                        }

                        for (let i = 0; i < store.length; i++) {
                            try {
                                const key = store.key(i);
                                const found = normalize(store.getItem(key));
                                if (found) return found;
                            } catch {}
                        }

                        return null;
                    };

                    return JSON.stringify(
                        findAuth(window[key]) ||
                        readStore(localStorage) ||
                        readStore(sessionStorage) ||
                        null
                    );
                })();
                """);

            var token = TryParseToken(result);
            if (token == null)
            {
                return;
            }

            AppLog.Info("Captured mobile Procare session from WebView.");
            await _viewModel.OnTokenCapturedAsync(token);
        }
        catch (Exception ex)
        {
            _viewModel.StatusMessage = $"Session capture failed: {ex.Message}";
            _viewModel.SubStatus = "Stay on the Procare page. The app will retry automatically.";
        }
        finally
        {
            _isCapturing = false;
        }
    }

    private static TokenInfo? TryParseToken(string? result)
    {
        if (string.IsNullOrWhiteSpace(result) || result == "null")
        {
            return null;
        }

        var cleaned = result.Trim();
        if (cleaned.StartsWith("\"") && cleaned.EndsWith("\""))
        {
            cleaned = cleaned[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
        }

        var node = JsonNode.Parse(cleaned);
        var accessToken = node?["accessToken"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return null;
        }

        return new TokenInfo
        {
            AccessToken = accessToken,
            OrganizationId = node?["organizationId"]?.GetValue<string>()
        };
    }

    private void PhotoViewerImage_PinchUpdated(object? sender, PinchGestureUpdatedEventArgs e)
    {
        if (sender is not Image image)
        {
            return;
        }

        switch (e.Status)
        {
            case GestureStatus.Started:
                _startScale = _currentScale;
                break;

            case GestureStatus.Running:
                _currentScale = Math.Clamp(_startScale * e.Scale, 1, 4);
                image.Scale = _currentScale;
                ApplyBoundedTranslation(image, _xOffset, _yOffset);
                break;

            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                _xOffset = image.TranslationX;
                _yOffset = image.TranslationY;
                break;
        }
    }

    private void PhotoViewerImage_PanUpdated(object? sender, PanUpdatedEventArgs e)
    {
        if (sender is not Image image || _currentScale <= 1)
        {
            return;
        }

        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _panStartX = _xOffset;
                _panStartY = _yOffset;
                break;

            case GestureStatus.Running:
                ApplyBoundedTranslation(image, _panStartX + e.TotalX, _panStartY + e.TotalY);
                break;

            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                _xOffset = image.TranslationX;
                _yOffset = image.TranslationY;
                break;
        }
    }

    private void PhotoViewerImage_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Image image)
        {
            return;
        }

        if (_currentScale > 1.01)
        {
            ResetPhotoViewerTransform();
            return;
        }

        _currentScale = 2;
        image.Scale = _currentScale;

        if (e.GetPosition(PhotoViewerViewport) is Point tap && PhotoViewerViewport.Width > 0 && PhotoViewerViewport.Height > 0)
        {
            var centerX = PhotoViewerViewport.Width / 2;
            var centerY = PhotoViewerViewport.Height / 2;
            var targetX = (centerX - tap.X) * 1.5;
            var targetY = (centerY - tap.Y) * 1.5;
            ApplyBoundedTranslation(image, targetX, targetY);
        }
        else
        {
            ApplyBoundedTranslation(image, 0, 0);
        }

        _xOffset = image.TranslationX;
        _yOffset = image.TranslationY;
    }

    private void ResetPhotoViewerTransform()
    {
        _currentScale = 1;
        _startScale = 1;
        _xOffset = 0;
        _yOffset = 0;
        _panStartX = 0;
        _panStartY = 0;

        if (PhotoViewerImage == null)
        {
            return;
        }

        PhotoViewerImage.Scale = 1;
        PhotoViewerImage.TranslationX = 0;
        PhotoViewerImage.TranslationY = 0;
    }

    private void ApplyBoundedTranslation(VisualElement element, double targetX, double targetY)
    {
        var bounds = GetTranslationBounds(element);
        element.TranslationX = Math.Clamp(targetX, -bounds.maxX, bounds.maxX);
        element.TranslationY = Math.Clamp(targetY, -bounds.maxY, bounds.maxY);
        _xOffset = element.TranslationX;
        _yOffset = element.TranslationY;
    }

    private (double maxX, double maxY) GetTranslationBounds(VisualElement element)
    {
        var scaledWidth = element.Width * Math.Max(_currentScale, 1);
        var scaledHeight = element.Height * Math.Max(_currentScale, 1);
        var containerWidth = Math.Max(PhotoViewerViewport?.Width ?? 1, 1);
        var containerHeight = Math.Max(PhotoViewerViewport?.Height ?? 1, 1);

        return (
            Math.Max(0, (scaledWidth - containerWidth) / 2),
            Math.Max(0, (scaledHeight - containerHeight) / 2));
    }
}
