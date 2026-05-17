using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ProcareDownloader.Models;
using ProcareDownloader.Services;

namespace ProcareDownloader.Mobile.Services;

public class MobileProcareApiService : IProcareMediaClient
{
    private const string ApiBaseUrl = "https://api-school.procareconnect.com/api/web";
    private const string ParentKidsPath = "/parent/kids";
    private const string ParentActivitiesPath = "/parent/activities";
    private const string ParentDailyActivitiesPath = "/parent/daily_activities";
    private const string BrowserAuthKey = "__procareDownloaderAuth";
    private const int MaxPhotoPages = 500;

    private readonly ProcareApiClient _httpApi;
    private Func<string, Task<string>>? _evaluateJavaScriptAsync;

    public MobileProcareApiService()
    {
        _httpApi = new ProcareApiClient();
    }

    public bool HasToken => _httpApi.HasToken;

    public void AttachBrowser(Func<string, Task<string>> evaluateJavaScriptAsync)
    {
        _evaluateJavaScriptAsync = evaluateJavaScriptAsync;
        AppLog.Info("Attached MAUI WebView browser to mobile API service.");
    }

    public void SetCredentials(TokenInfo tokenInfo)
    {
        _httpApi.SetCredentials(tokenInfo);
    }

    public void ClearCredentials()
    {
        _httpApi.ClearCredentials();
    }

    public async Task<List<Student>> GetStudentsAsync(CancellationToken ct = default)
    {
        await TrySyncBrowserCredentialsAsync();
        AppLog.Info($"Mobile GetStudentsAsync started. Browser attached: {_evaluateJavaScriptAsync != null}. Has token: {HasToken}.");

        if (_evaluateJavaScriptAsync != null)
        {
            var browserStudents = await GetStudentsFromBrowserAsync(ct);
            if (browserStudents.Count > 0)
            {
                AppLog.Info($"Loaded {browserStudents.Count} students from mobile browser session.");
                return browserStudents;
            }
        }

        if (!HasToken)
        {
            AppLog.Info("Mobile GetStudentsAsync has no token and browser fetch returned no students.");
            return [];
        }

        return await _httpApi.GetStudentsAsync(ct);
    }

    public async Task<List<Photo>> GetPhotosAsync(
        string studentId,
        IProgress<(int loaded, int total)>? progress = null,
        CancellationToken ct = default,
        IProgress<(IReadOnlyList<Photo> photos, int loaded, int total)>? pageProgress = null)
    {
        await TrySyncBrowserCredentialsAsync();

        if (_evaluateJavaScriptAsync != null)
        {
            var browserPhotos = new List<Photo>();
            var browserPage = 1;
            var browserTotal = 0;
            var seenPhotoIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            while (!ct.IsCancellationRequested)
            {
                if (browserPage > MaxPhotoPages)
                {
                    AppLog.Warn($"Browser photo pagination reached conservative page limit ({MaxPhotoPages}) for student {studentId}. Stopping to avoid infinite loops.");
                    var filteredBrowserPhotos = FilterPhotosForStudent(studentId, browserPhotos);
                    progress?.Report((filteredBrowserPhotos.Count, filteredBrowserPhotos.Count));
                    AppLog.Info(
                        $"Loaded {filteredBrowserPhotos.Count} photos for student {studentId} from mobile browser session.");
                    return filteredBrowserPhotos;
                }

                var pageResult = await GetPhotosPageFromBrowserAsync(studentId, browserPage, ct);
                if (!pageResult.Success)
                {
                    break;
                }

                var newPhotosOnPage = new List<Photo>();
                foreach (var photo in pageResult.Photos)
                {
                    if (!string.IsNullOrWhiteSpace(photo.Id) && seenPhotoIds.Add(photo.Id))
                    {
                        newPhotosOnPage.Add(photo);
                    }
                }

                browserPhotos.AddRange(newPhotosOnPage);
                browserTotal = pageResult.Total > 0 ? pageResult.Total : browserTotal;

                var currentFilteredBrowserPhotos = FilterPhotosForStudent(studentId, browserPhotos);
                var filteredNewPhotosOnPage = FilterPhotosForStudent(studentId, newPhotosOnPage);
                var progressTotal = browserTotal > 0 ? browserTotal : currentFilteredBrowserPhotos.Count;
                progress?.Report((currentFilteredBrowserPhotos.Count, progressTotal));
                if (filteredNewPhotosOnPage.Count > 0)
                {
                    pageProgress?.Report((filteredNewPhotosOnPage, currentFilteredBrowserPhotos.Count, progressTotal));
                }

                if (newPhotosOnPage.Count == 0)
                {
                    AppLog.Warn($"Browser photo pagination for student {studentId} returned a page with no new photo ids (page {browserPage}). Stopping to prevent repeating-page loops.");
                    var filteredBrowserPhotos = FilterPhotosForStudent(studentId, browserPhotos);
                    progress?.Report((filteredBrowserPhotos.Count, filteredBrowserPhotos.Count));
                    AppLog.Info(
                        $"Loaded {filteredBrowserPhotos.Count} photos for student {studentId} from mobile browser session.");
                    return filteredBrowserPhotos;
                }

                if (!pageResult.HasMore || pageResult.ItemCount == 0)
                {
                    var filteredBrowserPhotos = FilterPhotosForStudent(studentId, browserPhotos);
                    progress?.Report((filteredBrowserPhotos.Count, filteredBrowserPhotos.Count));
                    AppLog.Info(
                        $"Loaded {filteredBrowserPhotos.Count} photos for student {studentId} from mobile browser session.");
                    return filteredBrowserPhotos;
                }

                browserPage++;
            }
        }

        if (!HasToken)
        {
            return [];
        }

        return await _httpApi.GetPhotosAsync(studentId, progress, ct, pageProgress);
    }

    public async Task DownloadPhotoAsync(Photo photo, string destinationPath, CancellationToken ct = default)
    {
        var bytes = await GetPhotoBytesAsync(photo, ct);
        await File.WriteAllBytesAsync(destinationPath, bytes, ct);
    }

    public async Task<byte[]> GetPhotoBytesAsync(Photo photo, CancellationToken ct = default)
    {
        return await GetMediaBytesAsync(photo.OriginalUrl, ct);
    }

    public async Task<byte[]> GetMediaBytesAsync(string mediaUrl, CancellationToken ct = default)
    {
        await TrySyncBrowserCredentialsAsync();

        if (HasToken)
        {
            return await _httpApi.GetThumbnailBytesAsync(mediaUrl, ct);
        }

        if (_evaluateJavaScriptAsync == null)
        {
            throw new InvalidOperationException("No active Procare session is available for downloading.");
        }

        var base64 = await ExecuteBrowserStringAsync($$"""
            (async function() {
                const authPayload = (() => {
                    try { return JSON.parse(window['{{BrowserAuthKey}}'] || sessionStorage.getItem('{{BrowserAuthKey}}') || localStorage.getItem('{{BrowserAuthKey}}') || 'null'); } catch { return null; }
                })();

                const headers = { Accept: '*/*' };
                if (authPayload?.accessToken) {
                    headers['Authorization'] = 'Bearer ' + authPayload.accessToken;
                }

                if (authPayload?.organizationId) {
                    headers['X-Organization-Id'] = authPayload.organizationId;
                }

                const response = await fetch({{JsonSerializer.Serialize(mediaUrl)}}, {
                    method: 'GET',
                    credentials: 'include',
                    headers
                });

                if (!response.ok) {
                    return JSON.stringify({
                        error: true,
                        status: response.status,
                        body: await response.text()
                    });
                }

                const blob = await response.blob();
                const dataUrl = await new Promise((resolve, reject) => {
                    const reader = new FileReader();
                    reader.onloadend = () => resolve(reader.result);
                    reader.onerror = reject;
                    reader.readAsDataURL(blob);
                });

                return JSON.stringify({ dataUrl });
            })();
            """, "DownloadPhotoFromBrowserAsync");

        var node = JsonNode.Parse(base64);
        if (node?["error"]?.GetValue<bool?>() == true)
        {
            var status = node["status"]?.GetValue<int?>() ?? 0;
            var body = node["body"]?.GetValue<string>() ?? "";
            throw new InvalidOperationException(
                $"Browser download request failed with status {status}. {AppLog.Truncate(body, 300)}");
        }

        var dataUrl = node?["dataUrl"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(dataUrl) || !dataUrl.Contains(','))
        {
            throw new InvalidOperationException("Browser download did not return file data.");
        }

        return Convert.FromBase64String(dataUrl[(dataUrl.IndexOf(',') + 1)..]);
    }

    private async Task TrySyncBrowserCredentialsAsync()
    {
        if (_evaluateJavaScriptAsync == null || HasToken)
        {
            return;
        }

        try
        {
            var raw = await ExecuteBrowserStringAsync($$"""
                (function() {
                    const key = '{{BrowserAuthKey}}';
                    const read = () => {
                        try { return window[key]; } catch {}
                        try { return sessionStorage.getItem(key); } catch {}
                        try { return localStorage.getItem(key); } catch {}
                        return null;
                    };

                    return read() || 'null';
                })();
                """, "TrySyncBrowserCredentialsAsync");

            if (string.IsNullOrWhiteSpace(raw) || raw == "null")
            {
                return;
            }

            var cleaned = raw.Trim();
            if (cleaned.StartsWith("\"") && cleaned.EndsWith("\""))
            {
                cleaned = cleaned[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
            }

            var node = JsonNode.Parse(cleaned);
            var accessToken = node?["accessToken"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(accessToken))
            {
                SetCredentials(new TokenInfo
                {
                    AccessToken = accessToken,
                    OrganizationId = node?["organizationId"]?.GetValue<string>()
                });
                AppLog.Info("Mobile browser credentials synced from WebView storage.");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Mobile browser credential sync failed: {ex.Message}");
        }
    }

    private async Task<List<Student>> GetStudentsFromBrowserAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var node = await ExecuteBrowserJsonAsync($$"""
            (async function() {
                const authPayload = (() => {
                    try {
                        return JSON.parse(window['{{BrowserAuthKey}}'] || sessionStorage.getItem('{{BrowserAuthKey}}') || localStorage.getItem('{{BrowserAuthKey}}') || 'null');
                    } catch {
                        return null;
                    }
                })();

                if (window.req?.parentKids) {
                    try {
                        const result = await window.req.parentKids();
                        if (result && (Array.isArray(result.kids) || Object.keys(result).length > 0)) {
                            return JSON.stringify(result);
                        }
                    } catch {}
                }

                const headers = { Accept: 'application/json' };
                if (authPayload?.accessToken) {
                    headers['Authorization'] = 'Bearer ' + authPayload.accessToken;
                }

                if (authPayload?.organizationId) {
                    headers['X-Organization-Id'] = authPayload.organizationId;
                }

                try {
                    const response = await fetch({{JsonSerializer.Serialize($"{ApiBaseUrl}{ParentKidsPath}")}}, {
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
                } catch (error) {
                    return JSON.stringify({
                        error: true,
                        status: 0,
                        body: String(error)
                    });
                }
            })();
            """, "GetStudentsFromBrowserAsync");

        if (node == null || IsEmptyObject(node) || node["error"]?.GetValue<bool?>() == true)
        {
            AppLog.Info($"Mobile browser student fetch returned no usable payload. Node type: {AppLog.DescribeNode(node)}");
            return [];
        }

        var students = ParseStudents(node, "mobile browser student fetch");
        AppLog.Info($"Mobile browser student fetch parsed {students.Count} students.");
        return students;
    }

    private async Task<(List<Photo> Photos, int Total, int ItemCount, bool HasMore, bool Success)> GetPhotosPageFromBrowserAsync(
        string studentId,
        int page,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var node = await ExecuteBrowserJsonAsync($$"""
            (async function() {
                const studentId = {{JsonSerializer.Serialize(studentId)}};
                const page = {{page}};
                const perPage = 50;

                const authPayload = (() => {
                    try {
                        return JSON.parse(window['{{BrowserAuthKey}}'] || sessionStorage.getItem('{{BrowserAuthKey}}') || localStorage.getItem('{{BrowserAuthKey}}') || 'null');
                    } catch {
                        return null;
                    }
                })();

                const headers = { Accept: 'application/json' };
                if (authPayload?.accessToken) {
                    headers['Authorization'] = 'Bearer ' + authPayload.accessToken;
                }

                if (authPayload?.organizationId) {
                    headers['X-Organization-Id'] = authPayload.organizationId;
                }

                const hasItems = (payload) => {
                    if (!payload || typeof payload !== 'object') return false;
                    return Array.isArray(payload.activities)
                        || Array.isArray(payload.daily_activities)
                        || Array.isArray(payload.photos)
                        || Array.isArray(payload.data)
                        || Array.isArray(payload.items);
                };

                const attempts = [
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

                for (const attempt of attempts) {
                    if (typeof attempt.fn !== 'function') continue;

                    for (const params of attempt.params) {
                        try {
                            const result = await attempt.fn(params);
                            if (hasItems(result)) {
                                return JSON.stringify(result);
                            }
                        } catch {}
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
                    } catch {}
                }

                return JSON.stringify({
                    error: true,
                    status: 0,
                    body: 'No browser activity request succeeded.'
                });
            })();
            """, $"GetPhotosPageFromBrowserAsync({studentId}, {page})");

        if (node == null || IsEmptyObject(node) || node["error"]?.GetValue<bool?>() == true)
        {
            return ([], 0, 0, false, false);
        }

        var photos = ParsePhotos(node, $"mobile browser activity fetch for student {studentId}, page {page}");
        var itemCount = CountItems(node);
        var total = node["total"]?.GetValue<int?>() ?? 0;
        var perPage = node["per_page"]?.GetValue<int?>() ?? Math.Max(itemCount, 50);
        var currentPage = node["page"]?.GetValue<int?>() ?? page;
        var hasMore = total > 0
            ? currentPage * perPage < total
            : itemCount >= perPage && perPage > 0;

        return (photos, total, itemCount, hasMore, itemCount > 0 || photos.Count > 0);
    }

    private async Task<JsonNode?> ExecuteBrowserJsonAsync(string script, string operation)
    {
        var raw = await ExecuteBrowserStringAsync(script, operation);
        if (string.IsNullOrWhiteSpace(raw) || raw == "null")
        {
            return null;
        }

        return JsonNode.Parse(raw);
    }

    private async Task<string> ExecuteBrowserStringAsync(string script, string operation)
    {
        if (_evaluateJavaScriptAsync == null)
        {
            throw new InvalidOperationException("No browser is attached to the mobile API service.");
        }

        var result = await _evaluateJavaScriptAsync(script);
        if (string.IsNullOrWhiteSpace(result) || result == "null")
        {
            return result ?? "";
        }

        var trimmed = result.Trim();
        if (trimmed.StartsWith("\"") && trimmed.EndsWith("\""))
        {
            return trimmed[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
        }

        if (trimmed.Contains("\\\""))
        {
            trimmed = trimmed.Replace("\\\"", "\"").Replace("\\\\", "\\");
        }

        try
        {
            var envelope = JsonNode.Parse(trimmed);
            if (envelope is JsonValue value && value.TryGetValue<string>(out var jsonText))
            {
                return jsonText ?? "";
            }

            return envelope?.ToJsonString() ?? "";
        }
        catch (Exception ex)
        {
            AppLog.Error($"Failed to parse browser result for {operation}. Raw: {AppLog.Truncate(result, 2000)}", ex);
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
                FirstName = FirstValue(source, "first_name", "firstName") ?? (nameParts.FirstOrDefault() ?? ""),
                LastName = FirstValue(source, "last_name", "lastName") ?? string.Join(' ', nameParts.Skip(1)),
                PhotoUrl = FindStudentPhotoUrl(item, source)
            });
        }

        return students.Where(student => !string.IsNullOrWhiteSpace(student.Id)).ToList();
    }

    private static string? FindStudentPhotoUrl(JsonNode? item, JsonNode? source)
    {
        var direct = FirstValue(
            source,
            "photo_url",
            "photoUrl",
            "profile_photo_url",
            "profilePhotoUrl",
            "profile_image_url",
            "profileImageUrl",
            "avatar_url",
            "avatarUrl",
            "image_url",
            "imageUrl",
            "picture_url",
            "pictureUrl",
            "thumbnail_url",
            "thumbnailUrl");

        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        direct = FirstValue(
            item,
            "photo_url",
            "photoUrl",
            "profile_photo_url",
            "profilePhotoUrl",
            "profile_image_url",
            "profileImageUrl",
            "avatar_url",
            "avatarUrl",
            "image_url",
            "imageUrl",
            "picture_url",
            "pictureUrl",
            "thumbnail_url",
            "thumbnailUrl");

        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        return FindNestedStudentImageUrl(source) ?? FindNestedStudentImageUrl(item);
    }

    private static string? FindNestedStudentImageUrl(JsonNode? node, string path = "", int depth = 0)
    {
        if (node == null || depth > 5)
        {
            return null;
        }

        if (node is JsonObject obj)
        {
            foreach (var property in obj)
            {
                var childPath = string.IsNullOrWhiteSpace(path) ? property.Key : $"{path}.{property.Key}";
                if (property.Value is JsonValue value
                    && value.TryGetValue<string>(out var text)
                    && IsStudentImageUrl(childPath, text))
                {
                    return text;
                }

                var nested = FindNestedStudentImageUrl(property.Value, childPath, depth + 1);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }
        else if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                var nested = FindNestedStudentImageUrl(array[i], $"{path}[{i}]", depth + 1);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static bool IsStudentImageUrl(string path, string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out _)
            || !value.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var lowerPath = path.ToLowerInvariant();
        return lowerPath.Contains("avatar")
               || lowerPath.Contains("profile")
               || lowerPath.Contains("photo")
               || lowerPath.Contains("image")
               || lowerPath.Contains("picture")
               || lowerPath.Contains("thumbnail");
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

        return photos
            .Where(photo => !string.IsNullOrWhiteSpace(photo.Id) && !string.IsNullOrWhiteSpace(photo.OriginalUrl))
            .ToList();
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
            $"Payload for {context} was not an array. Node type: {AppLog.DescribeNode(node)}. Payload: {AppLog.Truncate(AppLog.SerializeNode(node), 2000)}");
        return null;
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

    private static string BuildParentActivityUrl(string path, string studentId, int page, bool? useKidIdsArray)
    {
        var baseUrl = $"{ApiBaseUrl}{path}/?page={page}&per_page=50";
        if (useKidIdsArray == null)
        {
            return baseUrl;
        }

        var filterKey = useKidIdsArray.Value ? Uri.EscapeDataString("kid_ids[]") : "kid_id";
        return $"{baseUrl}&{filterKey}={Uri.EscapeDataString(studentId)}";
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

    private static bool IsEmptyObject(JsonNode? node)
    {
        return node is JsonObject obj && obj.Count == 0;
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
                     ?? CreateUrlId(originalUrl);

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

    private static List<Photo> FilterPhotosForStudent(string studentId, List<Photo> photos)
    {
        var photosWithStudentIds = photos.Where(photo => photo.StudentIds.Count > 0).ToList();
        if (photosWithStudentIds.Count == 0)
        {
            return photos;
        }

        return photos
            .Where(photo => photo.StudentIds.Contains(studentId, StringComparer.OrdinalIgnoreCase))
            .ToList();
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
}
