using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using ProcareDownloader.Models;

namespace ProcareDownloader.Services;

/// <summary>
/// Intercepts network requests inside WebView2 to capture the Bearer token
/// that Procare issues after login, without needing to scrape the page DOM.
/// </summary>
public class TokenInterceptorService
{
    private TokenInfo? _captured;

    public event EventHandler<TokenInfo>? TokenCaptured;

    public TokenInfo? CapturedToken => _captured;

    /// <summary>
    /// Call this once after WebView2 is initialized to wire up request interception.
    /// </summary>
    public void Attach(CoreWebView2 webView)
    {
        // Intercept outgoing request headers so we can read the Authorization header
        webView.AddWebResourceRequestedFilter(
            "https://api.procareconnect.com/*",
            CoreWebView2WebResourceContext.All);
        webView.AddWebResourceRequestedFilter(
            "https://schools.procareconnect.com/*",
            CoreWebView2WebResourceContext.All);

        webView.WebResourceRequested += OnWebResourceRequested;

        // Also watch for navigation to catch token in browser storage.
        webView.NavigationCompleted += OnNavigationCompleted;
    }

    private void OnWebResourceRequested(object? sender,
        CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (_captured != null) return;

        // Pull Authorization header from outgoing API requests
        var authHeader = e.Request.Headers
            .FirstOrDefault(h => h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase));

        if (authHeader.Value?.StartsWith("Bearer ") == true)
        {
            var token = authHeader.Value["Bearer ".Length..].Trim();
            if (!string.IsNullOrEmpty(token))
            {
                // Try to also grab org id
                var orgHeader = e.Request.Headers
                    .FirstOrDefault(h => h.Key.Equals("X-Organization-Id",
                        StringComparison.OrdinalIgnoreCase));

                var info = new TokenInfo
                {
                    AccessToken = token,
                    OrganizationId = orgHeader.Value
                };

                Publish(info);
            }
        }
    }

    private async void OnNavigationCompleted(object? sender,
        CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_captured != null) return;
        if (sender is not CoreWebView2 wv) return;

        try
        {
            await TryCaptureFromPageAsync(wv);
        }
        catch { /* JS execution can fail on non-app pages */ }
    }

    public async Task<bool> TryCaptureFromPageAsync(CoreWebView2 webView)
    {
        if (_captured != null) return true;

        var result = await webView.ExecuteScriptAsync(@"
            (function() {
                const normalize = (value) => {
                    if (!value || typeof value !== 'string') return null;
                    const trimmed = value.trim();
                    if (!trimmed) return null;
                    if (trimmed.startsWith('Bearer ')) {
                        return { accessToken: trimmed.slice(7).trim() };
                    }
                    if (/^[A-Za-z0-9-_]+\.[A-Za-z0-9-_]+\.[A-Za-z0-9-_]+$/.test(trimmed)) {
                        return { accessToken: trimmed };
                    }
                    try {
                        const parsed = JSON.parse(trimmed);
                        if (parsed && typeof parsed === 'object') {
                            const accessToken = parsed.access_token || parsed.accessToken || parsed.token || parsed.authToken;
                            const organizationId = parsed.organization_id || parsed.organizationId || parsed.orgId;
                            if (accessToken) {
                                return { accessToken, organizationId };
                            }
                        }
                    } catch {}
                    return null;
                };

                const readStore = (store) => {
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
                    readStore(localStorage) ||
                    readStore(sessionStorage) ||
                    null
                );
            })()
        ");

        if (string.IsNullOrWhiteSpace(result) || result == "null")
        {
            return false;
        }

        var cleaned = result.Trim('"').Replace("\\\"", "\"").Replace("\\\\", "\\");
        var node = JsonNode.Parse(cleaned);
        var token = node?["accessToken"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        Publish(new TokenInfo
        {
            AccessToken = token,
            OrganizationId = node?["organizationId"]?.GetValue<string>()
        });

        return true;
    }

    public bool TryCapture(TokenInfo info)
    {
        if (_captured != null || string.IsNullOrWhiteSpace(info.AccessToken))
        {
            return false;
        }

        Publish(info);
        return true;
    }

    private void Publish(TokenInfo info)
    {
        if (_captured != null) return;

        _captured = info;
        TokenCaptured?.Invoke(this, info);
    }

    public void Reset()
    {
        _captured = null;
    }
}
