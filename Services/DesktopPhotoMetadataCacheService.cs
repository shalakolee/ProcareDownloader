using System.IO;
using System.Linq;
using System.Text.Json;
using ProcareDownloader.Models;

namespace ProcareDownloader.Services;

public sealed class DesktopPhotoMetadataCacheService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly TimeSpan _freshDuration = TimeSpan.FromMinutes(15);

    public async Task<DesktopPhotoCacheResult> TryLoadAsync(string studentId, CancellationToken ct = default)
    {
        var path = GetCachePath(studentId);
        if (!File.Exists(path))
        {
            return DesktopPhotoCacheResult.Miss;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            var entry = JsonSerializer.Deserialize<DesktopPhotoCacheEntry>(json, JsonOptions);
            if (entry == null)
            {
                return DesktopPhotoCacheResult.Miss;
            }

            var age = DateTimeOffset.UtcNow - entry.SavedAtUtc;
            return new DesktopPhotoCacheResult(
                true,
                age <= _freshDuration,
                entry.Photos ?? [],
                entry.SavedAtUtc);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Failed to load desktop photo metadata cache for student {studentId}. {ex.Message}");
            return DesktopPhotoCacheResult.Miss;
        }
    }

    public async Task SaveAsync(string studentId, IReadOnlyCollection<Photo> photos, CancellationToken ct = default)
    {
        try
        {
            var path = GetCachePath(studentId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var entry = new DesktopPhotoCacheEntry
            {
                StudentId = studentId,
                SavedAtUtc = DateTimeOffset.UtcNow,
                Photos = photos.ToList()
            };

            var json = JsonSerializer.Serialize(entry, JsonOptions);
            await File.WriteAllTextAsync(path, json, ct);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Failed to save desktop photo metadata cache for student {studentId}. {ex.Message}");
        }
    }

    public static bool AreEquivalent(IReadOnlyList<Photo> left, IReadOnlyList<Photo> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!string.Equals(left[index].Id, right[index].Id, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(left[index].OriginalUrl, right[index].OriginalUrl, StringComparison.OrdinalIgnoreCase)
                || left[index].CreatedAt != right[index].CreatedAt)
            {
                return false;
            }
        }

        return true;
    }

    private static string GetCachePath(string studentId)
    {
        var safeId = SanitizeSegment(string.IsNullOrWhiteSpace(studentId) ? "unknown" : studentId);
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProcareDownloader",
            "cache",
            "metadata");
        return Path.Combine(root, $"{safeId}.json");
    }

    private static string SanitizeSegment(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized;
    }
}

public sealed class DesktopPhotoCacheEntry
{
    public string StudentId { get; set; } = "";
    public DateTimeOffset SavedAtUtc { get; set; }
    public List<Photo> Photos { get; set; } = [];
}

public readonly record struct DesktopPhotoCacheResult(
    bool HasCache,
    bool IsFresh,
    IReadOnlyList<Photo> Photos,
    DateTimeOffset SavedAtUtc)
{
    public static DesktopPhotoCacheResult Miss => new(false, false, [], DateTimeOffset.MinValue);
}
