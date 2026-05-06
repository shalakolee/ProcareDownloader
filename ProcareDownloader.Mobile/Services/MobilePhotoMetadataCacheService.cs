using System.Text.Json;
using ProcareDownloader.Models;
using ProcareDownloader.Services;

namespace ProcareDownloader.Mobile.Services;

public sealed class MobilePhotoMetadataCacheService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly TimeSpan _freshDuration = TimeSpan.FromMinutes(15);

    public async Task<MobilePhotoCacheResult> TryLoadAsync(string studentId, CancellationToken ct = default)
    {
        var path = GetCachePath(studentId);
        if (!File.Exists(path))
        {
            return MobilePhotoCacheResult.Miss;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            var entry = JsonSerializer.Deserialize<MobilePhotoCacheEntry>(json, JsonOptions);
            if (entry == null)
            {
                return MobilePhotoCacheResult.Miss;
            }

            var age = DateTimeOffset.UtcNow - entry.SavedAtUtc;
            return new MobilePhotoCacheResult(
                true,
                age <= _freshDuration,
                entry.Photos ?? [],
                entry.SavedAtUtc);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Failed to load mobile photo metadata cache for student {studentId}. {ex.Message}");
            return MobilePhotoCacheResult.Miss;
        }
    }

    public async Task SaveAsync(string studentId, IReadOnlyCollection<Photo> photos, CancellationToken ct = default)
    {
        try
        {
            var path = GetCachePath(studentId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var entry = new MobilePhotoCacheEntry
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
            AppLog.Warn($"Failed to save mobile photo metadata cache for student {studentId}. {ex.Message}");
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
        var safeId = string.IsNullOrWhiteSpace(studentId)
            ? "unknown"
            : string.Concat(studentId.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        return Path.Combine(FileSystem.Current.CacheDirectory, "metadata-cache", $"{safeId}.json");
    }
}

public sealed class MobilePhotoCacheEntry
{
    public string StudentId { get; set; } = "";
    public DateTimeOffset SavedAtUtc { get; set; }
    public List<Photo> Photos { get; set; } = [];
}

public readonly record struct MobilePhotoCacheResult(
    bool HasCache,
    bool IsFresh,
    IReadOnlyList<Photo> Photos,
    DateTimeOffset SavedAtUtc)
{
    public static MobilePhotoCacheResult Miss => new(false, false, [], DateTimeOffset.MinValue);
}
