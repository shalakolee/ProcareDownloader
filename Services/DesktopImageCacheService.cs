using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;
using ProcareDownloader.Models;

namespace ProcareDownloader.Services;

public sealed class DesktopImageCacheService
{
    private readonly ProcareApiService _api;
    private readonly SemaphoreSlim _thumbnailSemaphore = new(6);

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
        if (!File.Exists(path))
        {
            await _thumbnailSemaphore.WaitAsync(ct);
            try
            {
                if (!File.Exists(path))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var sourceUrl = string.IsNullOrWhiteSpace(photo.ThumbnailUrl) ? photo.OriginalUrl : photo.ThumbnailUrl;
                    var bytes = await _api.GetThumbnailBytesAsync(sourceUrl, ct);
                    await File.WriteAllBytesAsync(path, bytes, ct);
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
        var cacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProcareDownloader",
            "cache",
            "thumbnails");
        var sourceUrl = string.IsNullOrWhiteSpace(photo.ThumbnailUrl) ? photo.OriginalUrl : photo.ThumbnailUrl;
        var extension = GetExtension(sourceUrl);
        var safeId = SanitizeSegment(string.IsNullOrWhiteSpace(photo.Id) ? Guid.NewGuid().ToString("N") : photo.Id);
        return Path.Combine(cacheRoot, $"{safeId}{extension}");
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
