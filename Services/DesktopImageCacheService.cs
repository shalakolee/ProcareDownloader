using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;
using ProcareDownloader.Models;

namespace ProcareDownloader.Services;

public sealed class DesktopImageCacheService
{
    private const long ThumbnailCacheLimitBytes = 512L * 1024 * 1024;
    private const long FullImageCacheLimitBytes = 1024L * 1024 * 1024;
    private readonly ProcareApiService _api;
    private readonly SemaphoreSlim _thumbnailSemaphore = new(6);
    private readonly SemaphoreSlim _fullImageSemaphore = new(2);

    public DesktopImageCacheService(ProcareApiService api)
    {
        _api = api;
    }

    public BitmapImage? GetThumbnail(Photo photo)
    {
        var path = BuildThumbnailPath(photo);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return LoadBitmap(path);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Failed to read cached thumbnail for photo {photo.Id}. {ex.Message}");
            return null;
        }
    }

    public async Task<BitmapImage?> EnsureThumbnailCachedAsync(Photo photo, CancellationToken ct = default)
    {
        var path = BuildThumbnailPath(photo);
        if (!IsValidCachedImageFile(path))
        {
            await _thumbnailSemaphore.WaitAsync(ct);
            try
            {
                if (!IsValidCachedImageFile(path))
                {
                    DeleteIfExists(path);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var sourceUrl = string.IsNullOrWhiteSpace(photo.ThumbnailUrl) ? photo.OriginalUrl : photo.ThumbnailUrl;
                    var bytes = await _api.GetThumbnailBytesAsync(sourceUrl, ct);
                    EnsureImagePayload(bytes, photo.Id, "thumbnail");
                    await WriteAtomicAsync(path, bytes, ct);
                    CachePruner.PruneByLastAccess(BuildBucketRoot("thumbnails"), ThumbnailCacheLimitBytes);
                }
            }
            finally
            {
                _thumbnailSemaphore.Release();
            }
        }

        try
        {
            return LoadBitmap(path);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Failed to load thumbnail image for photo {photo.Id} from cache. {ex.Message}");
            return null;
        }
    }

    public BitmapImage? GetFullImage(Photo photo)
    {
        var path = BuildFullImagePath(photo);
        return IsValidCachedImageFile(path) ? LoadBitmap(path) : null;
    }

    public async Task<BitmapImage?> EnsureFullImageCachedAsync(Photo photo, CancellationToken ct = default)
    {
        var path = BuildFullImagePath(photo);
        if (!IsValidCachedImageFile(path))
        {
            await _fullImageSemaphore.WaitAsync(ct);
            try
            {
                if (!IsValidCachedImageFile(path))
                {
                    DeleteIfExists(path);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var bytes = await _api.GetThumbnailBytesAsync(photo.OriginalUrl, ct);
                    EnsureImagePayload(bytes, photo.Id, "full");
                    await WriteAtomicAsync(path, bytes, ct);
                    CachePruner.PruneByLastAccess(BuildBucketRoot("full"), FullImageCacheLimitBytes);
                }
            }
            finally
            {
                _fullImageSemaphore.Release();
            }
        }

        try
        {
            return LoadBitmap(path);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Failed to load full image for photo {photo.Id} from cache. {ex.Message}");
            return null;
        }
    }

    public CacheUsageInfo GetUsage()
    {
        return CachePruner.GetUsage(BuildCacheRoot());
    }

    public Task ClearAsync()
    {
        CachePruner.Clear(BuildCacheRoot());
        return Task.CompletedTask;
    }

    private static BitmapImage LoadBitmap(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static string BuildThumbnailPath(Photo photo)
    {
        return BuildPath("thumbnails", photo, preferOriginalUrl: false);
    }

    private static string BuildFullImagePath(Photo photo)
    {
        return BuildPath("full", photo, preferOriginalUrl: true);
    }

    private static string BuildPath(string bucket, Photo photo, bool preferOriginalUrl)
    {
        var cacheRoot = BuildBucketRoot(bucket);
        var sourceUrl = preferOriginalUrl || string.IsNullOrWhiteSpace(photo.ThumbnailUrl)
            ? photo.OriginalUrl
            : photo.ThumbnailUrl;
        var extension = GetExtension(sourceUrl);
        var safeId = SanitizeSegment(string.IsNullOrWhiteSpace(photo.Id) ? Guid.NewGuid().ToString("N") : photo.Id);
        return Path.Combine(cacheRoot, $"{safeId}{extension}");
    }

    private static string BuildCacheRoot()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProcareDownloader",
            "cache");
    }

    private static string BuildBucketRoot(string bucket)
    {
        return Path.Combine(BuildCacheRoot(), bucket);
    }

    private static async Task WriteAtomicAsync(string targetPath, byte[] bytes, CancellationToken ct)
    {
        var tempPath = $"{targetPath}.tmp";
        await File.WriteAllBytesAsync(tempPath, bytes, ct);
        File.Move(tempPath, targetPath, true);
    }

    private static bool IsValidCachedImageFile(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var info = new FileInfo(path);
            if (info.Length < 32)
            {
                DeleteIfExists(path);
                return false;
            }

            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[32];
            var read = stream.Read(header);
            if (read <= 0 || !LooksLikeImage(header[..read]))
            {
                DeleteIfExists(path);
                return false;
            }

            info.LastAccessTimeUtc = DateTime.UtcNow;
            return true;
        }
        catch
        {
            DeleteIfExists(path);
            return false;
        }
    }

    private static void EnsureImagePayload(byte[] bytes, string photoId, string kind)
    {
        if (bytes.Length < 32 || !LooksLikeImage(bytes.AsSpan(0, Math.Min(bytes.Length, 32))))
        {
            throw new InvalidOperationException(
                $"Downloaded {kind} payload for photo {photoId} was not a valid image.");
        }
    }

    private static bool LooksLikeImage(ReadOnlySpan<byte> bytes)
    {
        return bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF
               || bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
               || bytes.Length >= 6 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46
               || bytes.Length >= 12 && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[8] == 0x57;
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort cleanup only.
        }
    }

    private static string GetExtension(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return ".jpg";
        }

        try
        {
            var path = new Uri(url).AbsolutePath;
            var extension = Path.GetExtension(path);
            return string.IsNullOrWhiteSpace(extension) ? ".jpg" : extension;
        }
        catch
        {
            return ".jpg";
        }
    }

    private static string SanitizeSegment(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized;
    }
}
