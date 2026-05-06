using System.IO;
using System.Threading;
using ProcareDownloader.Models;

namespace ProcareDownloader.Services;

public record DownloadResult(int Succeeded, int Skipped, int Failed, string OutputSummary, List<string> Errors);

public interface IProcareMediaClient
{
    Task DownloadPhotoAsync(Photo photo, string destinationPath, CancellationToken ct = default);
}

public class DownloadService
{
    private readonly IProcareMediaClient _api;
    private readonly DownloadHistoryService _history;

    public DownloadService(IProcareMediaClient api, DownloadHistoryService history)
    {
        _api = api;
        _history = history;
    }

    /// <summary>
    /// Downloads selected photos to outputFolder.
    /// Files are named: YYYY-MM-DD_{photoId}.jpg
    /// </summary>
    public async Task<DownloadResult> DownloadAsync(
        IEnumerable<Photo> photos,
        string outputFolder,
        DownloadLayout layout,
        string? studentName,
        IProgress<(int done, int total, string currentFile)>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputFolder);

        var list = photos.ToList();
        int processed = 0, succeeded = 0, skipped = 0, failed = 0;
        var errors = new List<string>();
        var outputFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Parallel with cap to avoid hammering the server
        var semaphore = new SemaphoreSlim(4);

        var tasks = list.Select(async photo =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var ext = GetExtension(photo.OriginalUrl);
                var datePart = photo.CreatedAt != default
                    ? photo.CreatedAt.ToString("yyyy-MM-dd")
                    : "unknown";
                var fileName = $"{datePart}_{photo.Id}{ext}";
                var destFolder = BuildDestinationFolder(outputFolder, layout, studentName, photo);
                Directory.CreateDirectory(destFolder);
                lock (outputFolders)
                {
                    outputFolders.Add(destFolder);
                }
                var destPath = Path.Combine(destFolder, fileName);

                if (_history.IsDownloaded(photo))
                {
                    Interlocked.Increment(ref skipped);
                }
                else if (File.Exists(destPath))
                {
                    _history.MarkDownloaded(photo, destPath);
                    Interlocked.Increment(ref skipped);
                }
                else
                {
                    await _api.DownloadPhotoAsync(photo, destPath, ct);
                    _history.MarkDownloaded(photo, destPath);
                    Interlocked.Increment(ref succeeded);
                }

                var done = Interlocked.Increment(ref processed);
                progress?.Report((done, list.Count, fileName));
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failed);
                Interlocked.Increment(ref processed);
                errors.Add($"{photo.Id}: {ex.Message}");
                AppLog.Error($"Download failed for photo {photo.Id}.", ex);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
        return new DownloadResult(
            succeeded,
            skipped,
            failed,
            BuildOutputSummary(outputFolders, outputFolder, layout, studentName),
            errors);
    }

    private static string BuildDestinationFolder(string root, DownloadLayout layout, string? studentName, Photo photo)
    {
        var year = photo.CreatedAt != default ? photo.CreatedAt.ToString("yyyy") : "unknown";
        var month = photo.CreatedAt != default ? photo.CreatedAt.ToString("MM") : "unknown";
        var safeStudentName = SanitizePathSegment(string.IsNullOrWhiteSpace(studentName) ? "student" : studentName);

        return layout switch
        {
            DownloadLayout.YearMonth => Path.Combine(root, year, month),
            DownloadLayout.StudentYear => Path.Combine(root, safeStudentName, year),
            DownloadLayout.StudentYearMonth => Path.Combine(root, safeStudentName, year, month),
            _ => root
        };
    }

    private static string SanitizePathSegment(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized;
    }

    public static string BuildLayoutPreviewPath(string root, DownloadLayout layout, string? studentName)
    {
        var safeStudentName = SanitizePathSegment(string.IsNullOrWhiteSpace(studentName) ? "student" : studentName);

        return layout switch
        {
            DownloadLayout.YearMonth => Path.Combine(root, "YYYY", "MM"),
            DownloadLayout.StudentYear => Path.Combine(root, safeStudentName, "YYYY"),
            DownloadLayout.StudentYearMonth => Path.Combine(root, safeStudentName, "YYYY", "MM"),
            _ => root
        };
    }

    private static string BuildOutputSummary(
        HashSet<string> outputFolders,
        string root,
        DownloadLayout layout,
        string? studentName)
    {
        if (outputFolders.Count == 0)
        {
            return BuildLayoutPreviewPath(root, layout, studentName);
        }

        var ordered = outputFolders.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        if (ordered.Count == 1)
        {
            return ordered[0];
        }

        return $"{ordered[0]} (+{ordered.Count - 1} more folders)";
    }

    private static string GetExtension(string url)
    {
        try
        {
            var path = new Uri(url).AbsolutePath;
            var ext = Path.GetExtension(path);
            return string.IsNullOrEmpty(ext) ? ".jpg" : ext;
        }
        catch
        {
            return ".jpg";
        }
    }
}
