using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using ProcareDownloader.Models;

namespace ProcareDownloader.Services;

public class ProcareApiClient : IProcareMediaClient
{
    private const string ApiBaseUrl = "https://api-school.procareconnect.com/api/web";
    private const string ParentKidsPath = "/parent/kids";
    private const string ParentActivitiesPath = "/parent/activities";
    private const string ParentDailyActivitiesPath = "/parent/daily_activities";
    private const int MaxPhotoPages = 500;

    private readonly HttpClient _http;
    private string _token = "";
    private string _orgId = "";

    public ProcareApiClient()
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

    public bool HasToken => !string.IsNullOrWhiteSpace(_token);

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

        AppLog.Info($"Core API credentials updated. OrganizationId present: {!string.IsNullOrWhiteSpace(_orgId)}");
    }

    public void ClearCredentials()
    {
        _token = "";
        _orgId = "";
        _http.DefaultRequestHeaders.Authorization = null;
        _http.DefaultRequestHeaders.Remove("X-Organization-Id");
    }

    public async Task<List<Student>> GetStudentsAsync(CancellationToken ct = default)
    {
        var url = $"{ApiBaseUrl}{ParentKidsPath}";
        using var response = await _http.GetAsync(url, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            AppLog.Warn($"Student HTTP request failed. Status: {(int)response.StatusCode}.");
            response.EnsureSuccessStatusCode();
        }

        return ParseStudents(JsonNode.Parse(body), "core student fetch");
    }

    public async Task<List<Photo>> GetPhotosAsync(
        string studentId,
        IProgress<(int loaded, int total)>? progress = null,
        CancellationToken ct = default,
        IProgress<(IReadOnlyList<Photo> photos, int loaded, int total)>? pageProgress = null)
    {
        var photos = new List<Photo>();
        var page = 1;
        var total = 0;
        var seenPhotoIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (!ct.IsCancellationRequested)
        {
            if (page > MaxPhotoPages)
            {
                AppLog.Warn($"Photo pagination reached conservative page limit ({MaxPhotoPages}) for student {studentId}. Stopping to avoid infinite loops.");
                break;
            }

            var pageResult = await GetPhotosPageAsync(studentId, page, ct);
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

            photos.AddRange(newPhotosOnPage);
            total = pageResult.Total > 0 ? pageResult.Total : total;
            var filtered = FilterPhotosForStudent(studentId, photos);
            var filteredPage = FilterPhotosForStudent(studentId, newPhotosOnPage);
            var progressTotal = total > 0 ? total : filtered.Count;
            progress?.Report((filtered.Count, progressTotal));
            if (filteredPage.Count > 0)
            {
                pageProgress?.Report((filteredPage, filtered.Count, progressTotal));
            }

            if (newPhotosOnPage.Count == 0)
            {
                AppLog.Warn($"Photo pagination for student {studentId} returned a page with no new photo ids (page {page}). Stopping to prevent repeating-page loops.");
                break;
            }

            if (!pageResult.HasMore || pageResult.ItemCount == 0)
            {
                break;
            }

            page++;
        }

        var finalFiltered = FilterPhotosForStudent(studentId, photos);
        progress?.Report((finalFiltered.Count, finalFiltered.Count));
        return finalFiltered;
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

    private async Task<(List<Photo> Photos, int Total, int ItemCount, bool HasMore, bool Success)> GetPhotosPageAsync(
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
                failures.Add($"Status: {(int)response.StatusCode}.");
                continue;
            }

            var node = JsonNode.Parse(body);
            var photos = ParsePhotos(node, $"core activity fetch for student {studentId}, page {page}");
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
            $"Core activity HTTP request failed for student {studentId}. Attempts: {string.Join(" | ", failures)}");
        return ([], 0, 0, false, false);
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
            $"Payload for {context} was not an array. Node type: {AppLog.DescribeNode(node)}.");
        return null;
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
