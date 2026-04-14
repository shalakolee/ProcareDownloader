using Microsoft.Maui.Controls;
using ProcareDownloader.Models;
using ProcareDownloader.Services;

namespace ProcareDownloader.Mobile.Services;

public sealed class MobileImageCacheService
{
    private readonly MobileProcareApiService _api;
    private readonly SemaphoreSlim _thumbnailSemaphore = new(4);
    private readonly SemaphoreSlim _fullImageSemaphore = new(2);

    public MobileImageCacheService(MobileProcareApiService api)
    {
        _api = api;
    }

    public ImageSource? GetThumbnailSource(Photo photo)
    {
        var path = GetExistingThumbnailPath(photo);
        return path == null ? null : ImageSource.FromFile(path);
    }

    public ImageSource? GetFullImageSource(Photo photo)
    {
        var path = GetExistingFullImagePath(photo);
        return path == null ? null : ImageSource.FromFile(path);
    }

    public async Task<string> EnsureThumbnailCachedAsync(Photo photo, CancellationToken ct = default)
    {
        var path = BuildThumbnailPath(photo);
        if (IsValidCachedImageFile(path))
        {
            return path;
        }

        await _thumbnailSemaphore.WaitAsync(ct);
        try
        {
            if (IsValidCachedImageFile(path))
            {
                return path;
            }

            DeleteIfExists(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var primaryUrl = string.IsNullOrWhiteSpace(photo.ThumbnailUrl) ? photo.OriginalUrl : photo.ThumbnailUrl;
            var bytes = await DownloadThumbnailBytesWithFallbackAsync(photo, primaryUrl, ct);
            EnsureImagePayload(bytes, photo.Id, "thumbnail");
            await WriteAtomicAsync(path, bytes, ct);
            return path;
        }
        finally
        {
            _thumbnailSemaphore.Release();
        }
    }

    public async Task<string> EnsureFullImageCachedAsync(Photo photo, CancellationToken ct = default)
    {
        var path = BuildFullImagePath(photo);
        if (IsValidCachedImageFile(path))
        {
            return path;
        }

        await _fullImageSemaphore.WaitAsync(ct);
        try
        {
            if (IsValidCachedImageFile(path))
            {
                return path;
            }

            DeleteIfExists(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = await _api.GetMediaBytesAsync(photo.OriginalUrl, ct);
            EnsureImagePayload(bytes, photo.Id, "full");
            await WriteAtomicAsync(path, bytes, ct);
            return path;
        }
        finally
        {
            _fullImageSemaphore.Release();
        }
    }

    private string? GetExistingThumbnailPath(Photo photo)
    {
        var path = BuildThumbnailPath(photo);
        return IsValidCachedImageFile(path) ? path : null;
    }

    private string? GetExistingFullImagePath(Photo photo)
    {
        var path = BuildFullImagePath(photo);
        return IsValidCachedImageFile(path) ? path : null;
    }

    private static string BuildThumbnailPath(Photo photo)
    {
        return BuildPath("thumbs", photo, preferOriginalUrl: false);
    }

    private static string BuildFullImagePath(Photo photo)
    {
        return BuildPath("full", photo, preferOriginalUrl: true);
    }

    private static string BuildPath(string bucket, Photo photo, bool preferOriginalUrl)
    {
        var root = Path.Combine(FileSystem.Current.CacheDirectory, "photo-cache", bucket);
        var sourceUrl = preferOriginalUrl || string.IsNullOrWhiteSpace(photo.ThumbnailUrl)
            ? photo.OriginalUrl
            : photo.ThumbnailUrl;
        var extension = GetExtension(sourceUrl);
        var safeId = SanitizeSegment(string.IsNullOrWhiteSpace(photo.Id) ? Guid.NewGuid().ToString("N") : photo.Id);
        return Path.Combine(root, $"{safeId}{extension}");
    }

    private static string SanitizeSegment(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized;
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
        catch (Exception ex)
        {
            AppLog.Warn($"Failed to infer image cache extension for '{url}'. {ex.Message}");
            return ".jpg";
        }
    }

    private static async Task WriteAtomicAsync(string targetPath, byte[] bytes, CancellationToken ct)
    {
        var tempPath = $"{targetPath}.tmp";
        await File.WriteAllBytesAsync(tempPath, bytes, ct);
        File.Move(tempPath, targetPath, true);
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
        if (bytes.Length >= 3
            && bytes[0] == 0xFF
            && bytes[1] == 0xD8
            && bytes[2] == 0xFF)
        {
            return true; // JPEG
        }

        if (bytes.Length >= 8
            && bytes[0] == 0x89
            && bytes[1] == 0x50
            && bytes[2] == 0x4E
            && bytes[3] == 0x47
            && bytes[4] == 0x0D
            && bytes[5] == 0x0A
            && bytes[6] == 0x1A
            && bytes[7] == 0x0A)
        {
            return true; // PNG
        }

        if (bytes.Length >= 6
            && bytes[0] == 0x47
            && bytes[1] == 0x49
            && bytes[2] == 0x46
            && bytes[3] == 0x38
            && (bytes[4] == 0x37 || bytes[4] == 0x39)
            && bytes[5] == 0x61)
        {
            return true; // GIF
        }

        if (bytes.Length >= 12
            && bytes[0] == 0x52
            && bytes[1] == 0x49
            && bytes[2] == 0x46
            && bytes[3] == 0x46
            && bytes[8] == 0x57
            && bytes[9] == 0x45
            && bytes[10] == 0x42
            && bytes[11] == 0x50)
        {
            return true; // WEBP
        }

        return false;
    }

    private async Task<byte[]> DownloadThumbnailBytesWithFallbackAsync(
        Photo photo,
        string primaryUrl,
        CancellationToken ct)
    {
        try
        {
            var bytes = await _api.GetMediaBytesAsync(primaryUrl, ct);
            if (bytes.Length >= 32 && LooksLikeImage(bytes.AsSpan(0, Math.Min(bytes.Length, 32))))
            {
                return bytes;
            }

            throw new InvalidOperationException("Primary thumbnail bytes are not a recognized image payload.");
        }
        catch (Exception ex) when (!string.IsNullOrWhiteSpace(photo.ThumbnailUrl)
                                   && !string.Equals(photo.ThumbnailUrl, photo.OriginalUrl, StringComparison.OrdinalIgnoreCase))
        {
            AppLog.Warn($"Thumbnail fetch fallback to original image for photo {photo.Id}. {ex.Message}");
            return await _api.GetMediaBytesAsync(photo.OriginalUrl, ct);
        }
    }
}
