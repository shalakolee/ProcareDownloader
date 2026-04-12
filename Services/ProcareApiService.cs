using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using ProcareDownloader.Models;

namespace ProcareDownloader.Services;

public class ProcareApiService
{
    private const string ApiBaseUrl = "https://api-school.procareconnect.com/api/web";
    private const string ParentKidsPath = "/parent/kids";
    private const string ParentActivitiesPath = "/parent/activities";
    private const string ParentDailyActivitiesPath = "/parent/daily_activities";

    private readonly HttpClient _http;
    private string _token = "";
    private string _orgId = "";
    private CoreWebView2? _webView;

    public ProcareApiService()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            UseCookies = true
        };

        _http = new HttpClient(handler);
        _http.DefaultRequestHeaders.Add(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120 Safari/537.36");
        _http.DefaultRequestHeaders.Add("Accept", "application/json");
        _http.DefaultRequestHeaders.Add("Origin", "https://schools.procareconnect.com");
        _http.DefaultRequestHeaders.Add("Referer", "https://schools.procareconnect.com/");
    }

    public void SetCredentials(TokenInfo tokenInfo)
    {
        _token = tokenInfo.AccessToken;
        _orgId = tokenInfo.OrganizationId ?? "";
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);

        _http.DefaultRequestHeaders.Remove("X-Organization-Id");
        if (!string.IsNullOrWhiteSpace(_orgId))
        {
            _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Organization-Id", _orgId);
        }

        AppLog.Info($"Credentials updated. OrganizationId present: {!string.IsNullOrWhiteSpace(_orgId)}");
    }

    public void ClearCredentials()
    {
        _token = "";
        _orgId = "";
        _http.DefaultRequestHeaders.Authorization = null;
        _http.DefaultRequestHeaders.Remove("X-Organization-Id");
        AppLog.Info("Cleared API credentials.");
    }

    public void AttachBrowser(CoreWebView2 webView)
    {
        _webView = webView;
        AppLog.Info("Attached WebView2 browser to API service.");
    }

    public bool HasToken => !string.IsNullOrEmpty(_token);

    public async Task<List<Student>> GetStudentsAsync()
    {
        if (_webView != null)
        {
            var browserStudents = await GetStudentsFromBrowserAsync();
            if (browserStudents.Count > 0)
            {
                AppLog.Info($"Loaded {browserStudents.Count} students from browser session.");
                return browserStudents;
            }

            AppLog.Info("Browser student lookup returned no students. Falling back to direct API.");
        }

        return await GetStudentsFromHttpAsync();
    }

    public async Task<List<Photo>> GetPhotosAsync(
        string studentId,
        IProgress<(int loaded, int total)>? progress = null,
        CancellationToken ct = default)
    {
        if (_webView != null)
        {
            var browserPhotos = new List<Photo>();
            var browserPage = 1;
            var browserTotal = 0;

            while (!ct.IsCancellationRequested)
            {
                var pageResult = await GetPhotosPageFromBrowserAsync(studentId, browserPage, ct);
                if (!pageResult.Success)
                {
                    break;
                }

                browserPhotos.AddRange(pageResult.Photos);
                browserTotal = pageResult.Total > 0 ? pageResult.Total : browserTotal;
                progress?.Report((browserPhotos.Count, browserTotal > 0 ? browserTotal : browserPhotos.Count));

                if (!pageResult.HasMore)
                {
                    var filteredBrowserPhotos = FilterPhotosForStudent(studentId, browserPhotos);
                    AppLog.Info($"Loaded {filteredBrowserPhotos.Count} photos for student {studentId} from browser session.");
                    progress?.Report((filteredBrowserPhotos.Count, filteredBrowserPhotos.Count));
                    return filteredBrowserPhotos;
                }

                if (pageResult.ItemCount == 0)
                {
                    break;
                }

                browserPage++;
            }

            AppLog.Warn($"Browser activity lookup returned no photos for student {studentId}. Falling back to direct API.");
        }

        var photos = new List<Photo>();
        var page = 1;
        var total = 0;

        while (!ct.IsCancellationRequested)
        {
            var pageResult = await GetPhotosPageFromHttpAsync(studentId, page, ct);
            if (!pageResult.Success)
            {
                break;
            }

            photos.AddRange(pageResult.Photos);
            total = pageResult.Total > 0 ? pageResult.Total : total;
            progress?.Report((photos.Count, total > 0 ? total : photos.Count));

            if (!pageResult.HasMore)
            {
                break;
            }

            if (pageResult.ItemCount == 0)
            {
                break;
            }

            page++;
        }

        var filteredPhotos = FilterPhotosForStudent(studentId, photos);
        progress?.Report((filteredPhotos.Count, filteredPhotos.Count));
        return filteredPhotos;
    }

    public async Task DownloadPhotoAsync(Photo photo, string destinationPath, CancellationToken ct = default)
    {
        var bytes = await _http.GetByteArrayAsync(photo.OriginalUrl, ct);
        await File.WriteAllBytesAsync(destinationPath, bytes, ct);
    }

    public async Task<byte[]> GetThumbnailBytesAsync(string url, CancellationToken ct = default)
    {
        return await _http.GetByteArrayAsync(url, ct);
    }

    private async Task<List<Student>> GetStudentsFromBrowserAsync()
    {
        var script = $$"""
            (async function() {
                if (window.req?.parentKids) {
                    try {
                        const result = await window.req.parentKids();
                        if (result && (Array.isArray(result.kids) || Object.keys(result).length > 0)) {
                            return JSON.stringify(result);
                        }
                    } catch (error) {
                        console.warn('parentKids request failed in WebView', error);
                    }
                }

                const url = {{JsonSerializer.Serialize($"{ApiBaseUrl}{ParentKidsPath}")}};
                const headers = {
                    Accept: 'application/json',
                    Authorization: 'Bearer ' + {{JsonSerializer.Serialize(_token)}}
                };

                const organizationId = {{JsonSerializer.Serialize(_orgId)}};
                if (organizationId) {
                    headers['X-Organization-Id'] = organizationId;
                }

                const response = await fetch(url, {
                    method: 'GET',
                    headers,
                    credentials: 'include'
                });

                if (!response.ok) {
                    return JSON.stringify({
                        error: true,
                        status: response.status,
                        body: await response.text()
                    });
                }

                return JSON.stringify(await response.json());
            })();
            """;

        var node = await ExecuteBrowserJsonAsync(script, "GetStudentsFromBrowserAsync");
        if (IsEmptyObject(node))
        {
            AppLog.Info("Browser student fetch returned an empty object.");
            return [];
        }

        return ParseStudents(node, "browser student fetch");
    }

    private async Task<(List<Photo> Photos, int Total, int ItemCount, bool HasMore, bool Success)> GetPhotosPageFromBrowserAsync(
        string studentId,
        int page,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var script = $$"""
            (async function() {
                const studentId = {{JsonSerializer.Serialize(studentId)}};
                const page = {{page}};
                const perPage = 50;
                const hasItems = (payload) => {
                    if (!payload || typeof payload !== 'object') {
                        return false;
                    }

                    return Array.isArray(payload.activities)
                        || Array.isArray(payload.daily_activities)
                        || Array.isArray(payload.photos)
                        || Array.isArray(payload.data)
                        || Array.isArray(payload.items);
                };

                const requestAttempts = [
                    {
                        name: 'kidActivitiesV2',
                        fn: window.req?.kidActivitiesV2,
                        params: [
                            { kid_id: studentId, page, per_page: perPage },
                            { kid_ids: [studentId], page, per_page: perPage },
                            { kid_id: studentId, kid_ids: [studentId], page, per_page: perPage }
                        ]
                    },
                    {
                        name: 'kidActivities',
                        fn: window.req?.kidActivities,
                        params: [
                            { kid_id: studentId, page, per_page: perPage },
                            { kid_ids: [studentId], page, per_page: perPage },
                            { kid_id: studentId, kid_ids: [studentId], page, per_page: perPage }
                        ]
                    }
                ];

                for (const attempt of requestAttempts) {
                    if (typeof attempt.fn !== 'function') {
                        continue;
                    }

                    for (const params of attempt.params) {
                        try {
                            const result = await attempt.fn(params);
                            if (hasItems(result)) {
                                return JSON.stringify(result);
                            }
                        } catch (error) {
                            console.warn('activity request failed in WebView', attempt.name, params, error);
                        }
                    }
                }

                const urls = [
                    {{JsonSerializer.Serialize(BuildParentActivityUrl(ParentActivitiesPath, studentId, page, false))}},
                    {{JsonSerializer.Serialize(BuildParentActivityUrl(ParentActivitiesPath, studentId, page, true))}},
                    {{JsonSerializer.Serialize(BuildParentActivityUrl(ParentActivitiesPath, studentId, page, null))}},
                    {{JsonSerializer.Serialize(BuildParentActivityUrl(ParentDailyActivitiesPath, studentId, page, false))}},
                    {{JsonSerializer.Serialize(BuildParentActivityUrl(ParentDailyActivitiesPath, studentId, page, true))}},
                    {{JsonSerializer.Serialize(BuildParentActivityUrl(ParentDailyActivitiesPath, studentId, page, null))}}
                ];

                const headers = {
                    Accept: 'application/json',
                    Authorization: 'Bearer ' + {{JsonSerializer.Serialize(_token)}}
                };

                const organizationId = {{JsonSerializer.Serialize(_orgId)}};
                if (organizationId) {
                    headers['X-Organization-Id'] = organizationId;
                }

                for (const url of urls) {
                    try {
                        const response = await fetch(url, {
                            method: 'GET',
                            headers,
                            credentials: 'include'
                        });

                        if (!response.ok) {
                            continue;
                        }

                        const payload = await response.json();
                        if (hasItems(payload)) {
                            return JSON.stringify(payload);
                        }
                    } catch (error) {
                        console.warn('activity fetch failed in WebView', url, error);
                    }
                }

                if (window.req?.photos) {
                    const attempts = [
                        { kid_id: studentId, page, per_page: perPage },
                        { kid_ids: [studentId], page, per_page: perPage }
                    ];

                    for (const params of attempts) {
                        try {
                            const result = await window.req.photos(params);
                            if (result && (
                                (Array.isArray(result.photos) && result.photos.length > 0) ||
                                Object.keys(result).length > 0 && JSON.stringify(result) !== '{}'
                            )) {
                                return JSON.stringify(result);
                            }
                        } catch (error) {
                            console.warn('photos request failed in WebView', params, error);
                        }
                    }
                }

                const sleep = (ms) => new Promise(resolve => setTimeout(resolve, ms));
                const clickPhotosTab = () => {
                    const candidates = Array.from(document.querySelectorAll('a, button, [role="tab"], [role="button"]'));
                    const tab = candidates.find(el => (el.textContent || '').toUpperCase().includes('PHOTOS/VIDEOS'));
                    if (tab) {
                        tab.click();
                        return true;
                    }

                    return false;
                };

                const toPhotoEntry = (img, index) => {
                    const src = img.currentSrc || img.src || '';
                    if (!src || !/^https?:/i.test(src)) {
                        return null;
                    }

                    const ancestor = img.closest('a, article, li, section, div');
                    const text = (ancestor?.innerText || '').trim();
                    const hasPhotoContext = /photo|video/i.test(text);
                    const width = img.naturalWidth || img.width || 0;
                    const height = img.naturalHeight || img.height || 0;
                    const lower = src.toLowerCase();

                    if (!hasPhotoContext) {
                        return null;
                    }

                    if (width > 0 && height > 0 && width < 120 && height < 120) {
                        return null;
                    }

                    if (lower.includes('logo') || lower.includes('icon') || lower.includes('avatar') || lower.includes('profile')) {
                        return null;
                    }

                    const anchor = img.closest('a[href]');
                    const originalUrl = anchor?.href || src;
                    const id = (originalUrl.split('/').pop() || ('dom-photo-' + index)).split('?')[0];

                    return {
                        id,
                        thumbnail_url: src,
                        original_url: originalUrl,
                        caption: img.alt || null,
                        created_at: null
                    };
                };

                let clickedPhotosTab = false;
                if (page === 1) {
                    clickedPhotosTab = clickPhotosTab();
                }

                if (page === 1 && clickedPhotosTab) {
                    await sleep(1500);

                    for (let i = 0; i < 8; i++) {
                        window.scrollTo(0, document.body.scrollHeight);
                        await sleep(400);
                    }

                    const photos = Array.from(document.querySelectorAll('img'))
                        .map((img, index) => toPhotoEntry(img, index))
                        .filter(Boolean);

                    const deduped = [];
                    const seen = new Set();
                    for (const photo of photos) {
                        if (seen.has(photo.original_url)) continue;
                        seen.add(photo.original_url);
                        deduped.push(photo);
                    }

                    if (deduped.length > 0) {
                        return JSON.stringify({
                            page: 1,
                            per_page: deduped.length,
                            total: deduped.length,
                            photos: deduped
                        });
                    }
                }

                return JSON.stringify({
                    error: true,
                    status: 0,
                    body: 'No parent activity request succeeded.',
                    diagnostics: {
                        href: location.href,
                        title: document.title,
                        clickedPhotosTab,
                        imageCount: document.querySelectorAll('img').length,
                        requestKeys: Object.keys(window.req || {}).sort(),
                        photoTabCandidates: Array.from(document.querySelectorAll('a, button, [role=\"tab\"], [role=\"button\"]'))
                            .map(el => (el.textContent || '').trim())
                            .filter(text => /photo|video/i.test(text))
                            .slice(0, 20)
                    }
                });
            })();
            """;

        var node = await ExecuteBrowserJsonAsync(script, $"GetPhotosPageFromBrowserAsync({studentId}, {page})");
        if (IsEmptyObject(node))
        {
            AppLog.Info($"Browser activity fetch returned an empty object for student {studentId}, page {page}.");
            return ([], 0, 0, false, false);
        }

        var photos = ParsePhotos(node, $"browser activity fetch for student {studentId}, page {page}");
        var itemCount = CountItems(node);
        var total = node?["total"]?.GetValue<int?>() ?? 0;
        var perPage = node?["per_page"]?.GetValue<int?>() ?? Math.Max(itemCount, 50);
        var currentPage = node?["page"]?.GetValue<int?>() ?? page;
        var hasMore = total > 0
            ? currentPage * perPage < total
            : itemCount >= perPage && perPage > 0;

        return (photos, total, itemCount, hasMore, itemCount > 0 || photos.Count > 0);
    }

    private async Task<List<Student>> GetStudentsFromHttpAsync(CancellationToken ct = default)
    {
        var url = $"{ApiBaseUrl}{ParentKidsPath}";
        using var response = await _http.GetAsync(url, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            AppLog.Warn($"Student HTTP request failed. Status: {(int)response.StatusCode}. Body: {AppLog.Truncate(body, 4000)}");
            response.EnsureSuccessStatusCode();
        }

        return ParseStudents(JsonNode.Parse(body), "direct student fetch");
    }

    private async Task<(List<Photo> Photos, int Total, int ItemCount, bool HasMore, bool Success)> GetPhotosPageFromHttpAsync(
        string studentId,
        int page,
        CancellationToken ct)
    {
        var failures = new List<string>();

        foreach (var url in BuildParentActivityUrls(studentId, page))
        {
            using var response = await _http.GetAsync(url, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                failures.Add($"Url: {url}. Status: {(int)response.StatusCode}. Body: {AppLog.Truncate(body, 1000)}");
                continue;
            }

            var node = JsonNode.Parse(body);
            var photos = ParsePhotos(node, $"direct activity fetch for student {studentId}, page {page}");
            var itemCount = CountItems(node);
            var total = node?["total"]?.GetValue<int?>() ?? 0;
            var perPage = node?["per_page"]?.GetValue<int?>() ?? Math.Max(itemCount, 50);
            var currentPage = node?["page"]?.GetValue<int?>() ?? page;
            var hasMore = total > 0
                ? currentPage * perPage < total
                : itemCount >= perPage && perPage > 0;

            return (photos, total, itemCount, hasMore, itemCount > 0 || photos.Count > 0);
        }

        AppLog.Warn(
            $"Activity HTTP request failed for student {studentId}. Attempts: {string.Join(" | ", failures)}");
        return ([], 0, 0, false, false);
    }

    private async Task<JsonNode?> ExecuteBrowserJsonAsync(string script, string operation)
    {
        if (_webView == null)
        {
            return null;
        }

        var result = await _webView.ExecuteScriptAsync(script);
        if (string.IsNullOrWhiteSpace(result) || result == "null")
        {
            AppLog.Warn($"{operation} returned an empty WebView result.");
            return null;
        }

        try
        {
            var envelope = JsonNode.Parse(result);
            if (envelope is JsonValue value && value.TryGetValue<string>(out var jsonText))
            {
                if (string.IsNullOrWhiteSpace(jsonText))
                {
                    AppLog.Warn($"{operation} returned an empty JSON payload string.");
                    return null;
                }

                return JsonNode.Parse(jsonText);
            }

            return envelope;
        }
        catch (Exception ex)
        {
            AppLog.Error(
                $"{operation} failed to parse browser JSON. Raw result: {AppLog.Truncate(result, 4000)}",
                ex);
            throw;
        }
    }

    private static List<Student> ParseStudents(JsonNode? node, string context)
    {
        var items = TryGetArray(node, context);
        if (items == null)
        {
            return [];
        }

        var students = new List<Student>();
        foreach (var item in items)
        {
            var source = item?["attributes"] ?? item;
            var fullName = FirstValue(source, "full_name", "fullName", "name");
            var nameParts = string.IsNullOrWhiteSpace(fullName)
                ? []
                : fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            students.Add(new Student
            {
                Id = FirstValue(source, "id", "student_id", "studentId", "kid_id", "kidId")
                     ?? item?["id"]?.GetValue<string>()
                     ?? "",
                FirstName = FirstValue(source, "first_name", "firstName")
                            ?? (nameParts.FirstOrDefault() ?? ""),
                LastName = FirstValue(source, "last_name", "lastName")
                           ?? string.Join(' ', nameParts.Skip(1)),
                PhotoUrl = FirstValue(source, "photo_url", "photoUrl", "profile_photo_url", "profilePhotoUrl")
            });
        }

        return students.Where(student => !string.IsNullOrWhiteSpace(student.Id)).ToList();
    }

    private static List<Photo> ParsePhotos(JsonNode? node, string context)
    {
        var items = TryGetArray(node, context);
        if (items == null)
        {
            return [];
        }

        var photos = new List<Photo>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var photo = BuildPhotoFromItem(item);
            if (photo == null)
            {
                continue;
            }

            if (seenUrls.Add(photo.OriginalUrl))
            {
                photos.Add(photo);
            }
        }

        return photos.Where(photo => !string.IsNullOrWhiteSpace(photo.Id) && !string.IsNullOrWhiteSpace(photo.OriginalUrl)).ToList();
    }

    private static string? FirstValue(JsonNode? node, params string[] keys)
    {
        if (node is not JsonObject obj)
        {
            return null;
        }

        foreach (var key in keys)
        {
            var value = obj[key]?.GetValue<string?>();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static JsonArray? TryGetArray(JsonNode? node, string context)
    {
        if (node is JsonArray array)
        {
            return array;
        }

        if (node is JsonObject obj)
        {
            foreach (var key in new[] { "data", "students", "photos", "results", "items", "value", "kids", "activities", "daily_activities" })
            {
                if (obj[key] is JsonArray directArray)
                {
                    return directArray;
                }

                if (obj[key] is JsonObject nested)
                {
                    foreach (var nestedKey in new[] { "data", "students", "photos", "results", "items", "value", "kids", "activities", "daily_activities" })
                    {
                        if (nested[nestedKey] is JsonArray nestedArray)
                        {
                            return nestedArray;
                        }
                    }
                }
            }
        }

        AppLog.Warn(
            $"Browser payload for {context} was not an array. Node type: {AppLog.DescribeNode(node)}. Payload: {AppLog.Truncate(AppLog.SerializeNode(node), 4000)}");
        return null;
    }

    private static bool IsEmptyObject(JsonNode? node)
    {
        return node is JsonObject obj && obj.Count == 0;
    }

    private static IEnumerable<string> BuildParentActivityUrls(string studentId, int page)
    {
        yield return BuildParentActivityUrl(ParentActivitiesPath, studentId, page, false);
        yield return BuildParentActivityUrl(ParentActivitiesPath, studentId, page, true);
        yield return BuildParentActivityUrl(ParentActivitiesPath, studentId, page, null);
        yield return BuildParentActivityUrl(ParentDailyActivitiesPath, studentId, page, false);
        yield return BuildParentActivityUrl(ParentDailyActivitiesPath, studentId, page, true);
        yield return BuildParentActivityUrl(ParentDailyActivitiesPath, studentId, page, null);
    }

    private static string BuildParentActivityUrl(string path, string studentId, int page, bool? useKidIdsArray)
    {
        var baseUrl = $"{ApiBaseUrl}{path}/?page={page}&per_page=50";
        if (useKidIdsArray == null)
        {
            return baseUrl;
        }

        var filterKey = useKidIdsArray.Value ? EncodeQueryKey("kid_ids[]") : "kid_id";
        return $"{baseUrl}&{filterKey}={Uri.EscapeDataString(studentId)}";
    }

    private static List<string> ExtractStudentIds(JsonNode? item, JsonNode? source)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddId(ids, FirstValue(source, "kid_id", "kidId", "student_id", "studentId"));
        AddIds(ids, source?["kid_ids"] as JsonArray);
        AddIds(ids, source?["kidIds"] as JsonArray);
        AddIds(ids, source?["activity_participants"] as JsonArray);
        AddIds(ids, source?["activityParticipants"] as JsonArray);

        if (item?["relationships"]?["kids"]?["data"] is JsonArray relationshipKids)
        {
            foreach (var kid in relationshipKids)
            {
                AddId(ids, kid?["id"]?.GetValue<string>());
            }
        }

        return ids.ToList();
    }

    private static int CountItems(JsonNode? node)
    {
        return FindArray(node)?.Count ?? 0;
    }

    private static JsonArray? FindArray(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            return array;
        }

        if (node is not JsonObject obj)
        {
            return null;
        }

        foreach (var key in new[] { "data", "students", "photos", "results", "items", "value", "kids", "activities", "daily_activities" })
        {
            if (obj[key] is JsonArray directArray)
            {
                return directArray;
            }

            if (obj[key] is JsonObject nested)
            {
                foreach (var nestedKey in new[] { "data", "students", "photos", "results", "items", "value", "kids", "activities", "daily_activities" })
                {
                    if (nested[nestedKey] is JsonArray nestedArray)
                    {
                        return nestedArray;
                    }
                }
            }
        }

        return null;
    }

    private static Photo? BuildPhotoFromItem(JsonNode? item)
    {
        var source = item?["attributes"] ?? item;
        var mediaUrls = CollectMediaUrls(item).ToList();
        if (mediaUrls.Count == 0)
        {
            return null;
        }

        var activityType = (FirstValue(source, "activity_type", "activityType", "type") ?? "").ToLowerInvariant();
        var mediaHint = mediaUrls.Any(url => url.Path.Contains("photo", StringComparison.OrdinalIgnoreCase)
            || url.Path.Contains("image", StringComparison.OrdinalIgnoreCase)
            || url.Path.Contains("video", StringComparison.OrdinalIgnoreCase)
            || url.Path.Contains("media", StringComparison.OrdinalIgnoreCase)
            || url.Path.Contains("attachment", StringComparison.OrdinalIgnoreCase));

        if (!mediaHint
            && activityType.Length > 0
            && !activityType.Contains("photo", StringComparison.OrdinalIgnoreCase)
            && !activityType.Contains("video", StringComparison.OrdinalIgnoreCase)
            && !activityType.Contains("image", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var originalUrl = SelectBestUrl(mediaUrls, preferLarge: true);
        var thumbnailUrl = SelectBestUrl(mediaUrls, preferLarge: false) ?? originalUrl;
        if (string.IsNullOrWhiteSpace(originalUrl))
        {
            return null;
        }

        DateTime.TryParse(
            FirstValue(source, "created_at", "createdAt", "activity_date", "activity_time", "date"),
            out var createdAt);

        var baseId = FirstValue(source, "id", "photo_id", "photoId", "activity_id", "activityId")
                     ?? item?["id"]?.GetValue<string>()
                     ?? "";

        if (string.IsNullOrWhiteSpace(baseId))
        {
            baseId = CreateUrlId(originalUrl);
        }

        return new Photo
        {
            Id = baseId,
            ThumbnailUrl = thumbnailUrl ?? originalUrl,
            OriginalUrl = originalUrl,
            CreatedAt = createdAt,
            Caption = FirstValue(source, "caption", "description", "notes", "title"),
            StudentIds = ExtractStudentIds(item, source)
        };
    }

    private static IEnumerable<(string Path, string Url)> CollectMediaUrls(JsonNode? node, string path = "$", int depth = 0)
    {
        if (node == null || depth > 6)
        {
            yield break;
        }

        if (node is JsonObject obj)
        {
            foreach (var property in obj)
            {
                var childPath = $"{path}.{property.Key}";
                if (property.Value is JsonValue value
                    && value.TryGetValue<string>(out var text)
                    && IsLikelyMediaUrl(property.Key, text))
                {
                    yield return (childPath, text);
                }

                foreach (var nested in CollectMediaUrls(property.Value, childPath, depth + 1))
                {
                    yield return nested;
                }
            }

            yield break;
        }

        if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                foreach (var nested in CollectMediaUrls(array[i], $"{path}[{i}]", depth + 1))
                {
                    yield return nested;
                }
            }
        }
    }

    private static bool IsLikelyMediaUrl(string key, string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out _))
        {
            return false;
        }

        if (!value.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var lowerKey = key.ToLowerInvariant();
        var lowerValue = value.ToLowerInvariant();
        if (lowerValue.Contains("logo") || lowerValue.Contains("avatar") || lowerValue.Contains("profile"))
        {
            return false;
        }

        if (lowerKey.Contains("thumb")
            || lowerKey.Contains("thumbnail")
            || lowerKey.Contains("photo")
            || lowerKey.Contains("image")
            || lowerKey.Contains("video")
            || lowerKey.Contains("media")
            || lowerKey.Contains("attachment")
            || lowerKey.Contains("original")
            || lowerKey.Contains("preview")
            || lowerKey.Contains("download")
            || lowerKey.Contains("url")
            || lowerKey.Contains("src"))
        {
            return true;
        }

        return lowerValue.EndsWith(".jpg")
            || lowerValue.EndsWith(".jpeg")
            || lowerValue.EndsWith(".png")
            || lowerValue.EndsWith(".gif")
            || lowerValue.EndsWith(".webp")
            || lowerValue.EndsWith(".bmp")
            || lowerValue.EndsWith(".mp4")
            || lowerValue.EndsWith(".mov");
    }

    private static string? SelectBestUrl(IEnumerable<(string Path, string Url)> mediaUrls, bool preferLarge)
    {
        var ordered = preferLarge
            ? mediaUrls.OrderByDescending(item => ScoreMediaUrl(item.Path, item.Url))
            : mediaUrls.OrderBy(item => ScoreMediaUrl(item.Path, item.Url));

        return ordered.Select(item => item.Url).FirstOrDefault();
    }

    private static int ScoreMediaUrl(string path, string url)
    {
        var score = 0;
        var lowerPath = path.ToLowerInvariant();
        var lowerUrl = url.ToLowerInvariant();

        if (lowerPath.Contains("original") || lowerPath.Contains("full") || lowerPath.Contains("large") || lowerPath.Contains("download"))
        {
            score += 6;
        }

        if (lowerPath.Contains("thumb") || lowerPath.Contains("thumbnail") || lowerPath.Contains("small") || lowerPath.Contains("preview"))
        {
            score -= 4;
        }

        if (lowerPath.Contains("photo") || lowerPath.Contains("image") || lowerPath.Contains("video"))
        {
            score += 3;
        }

        if (lowerUrl.Contains("original") || lowerUrl.Contains("full") || lowerUrl.Contains("large"))
        {
            score += 2;
        }

        if (lowerUrl.Contains("thumb") || lowerUrl.Contains("thumbnail") || lowerUrl.Contains("small") || lowerUrl.Contains("preview"))
        {
            score -= 2;
        }

        return score;
    }

    private static string CreateUrlId(string url)
    {
        var candidate = url.Split('/').LastOrDefault() ?? url;
        candidate = candidate.Split('?')[0];
        return string.IsNullOrWhiteSpace(candidate) ? Guid.NewGuid().ToString("N") : candidate;
    }

    private static List<Photo> FilterPhotosForStudent(string studentId, List<Photo> photos)
    {
        var photosWithStudentIds = photos.Where(photo => photo.StudentIds.Count > 0).ToList();
        if (photosWithStudentIds.Count == 0)
        {
            AppLog.Info($"Photo payload did not expose student ids for student {studentId}. Returning {photos.Count} unfiltered photos.");
            return photos;
        }

        var filtered = photos.Where(photo => photo.StudentIds.Contains(studentId, StringComparer.OrdinalIgnoreCase)).ToList();
        AppLog.Info($"Filtered {photos.Count} photos down to {filtered.Count} for student {studentId} using embedded student ids.");
        return filtered;
    }

    private static void AddIds(HashSet<string> ids, JsonArray? array)
    {
        if (array == null)
        {
            return;
        }

        foreach (var node in array)
        {
            if (node is JsonValue value && value.TryGetValue<string>(out var text))
            {
                AddId(ids, text);
                continue;
            }

            AddId(ids, node?["id"]?.GetValue<string>());
        }
    }

    private static void AddId(HashSet<string> ids, string? id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            ids.Add(id);
        }
    }

    private static string EncodeQueryKey(string key)
    {
        return Uri.EscapeDataString(key);
    }
}
