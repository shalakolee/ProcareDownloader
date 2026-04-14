using ProcareDownloader.Models;
using ProcareDownloader.Services;

namespace ProcareDownloader.Mobile.Services;

public sealed class MobileDownloadService
{
    private readonly MobileProcareApiService _api;
    private readonly DownloadHistoryService _history;
    private readonly MobileMediaSaveService _mediaSaveService;

    public MobileDownloadService(
        MobileProcareApiService api,
        DownloadHistoryService history,
        MobileMediaSaveService mediaSaveService)
    {
        _api = api;
        _history = history;
        _mediaSaveService = mediaSaveService;
    }

    public string DownloadRootPath => _mediaSaveService.RootDisplayPath;

    public async Task<DownloadResult> DownloadAsync(
        IEnumerable<Photo> photos,
        DownloadLayout layout,
        string? studentName,
        IProgress<(int done, int total, string currentFile)>? progress = null,
        CancellationToken ct = default)
    {
        var list = photos.ToList();
        var processed = 0;
        var succeeded = 0;
        var skipped = 0;
        var failed = 0;
        var errors = new List<string>();
        var outputFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var semaphore = new SemaphoreSlim(3);

        var tasks = list.Select(async photo =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var fileName = MobileDownloadPathHelper.BuildFileName(photo);
                var relativeFolderPath = MobileDownloadPathHelper.BuildRelativeFolderPath(layout, studentName, photo);
                var displayFolderPath = string.IsNullOrWhiteSpace(relativeFolderPath)
                    ? DownloadRootPath
                    : Path.Combine(DownloadRootPath, relativeFolderPath);

                lock (outputFolders)
                {
                    outputFolders.Add(displayFolderPath);
                }

                var displayFilePath = MobileDownloadPathHelper.BuildDisplayFilePath(displayFolderPath, fileName);
                if (_history.IsDownloaded(photo))
                {
                    Interlocked.Increment(ref skipped);
                }
                else if (_mediaSaveService.Exists(relativeFolderPath, fileName))
                {
                    _history.MarkDownloaded(photo, displayFilePath);
                    Interlocked.Increment(ref skipped);
                }
                else
                {
                    var bytes = await _api.GetPhotoBytesAsync(photo, ct);
                    var savedPath = await _mediaSaveService.SaveAsync(photo, bytes, relativeFolderPath, fileName, ct);
                    _history.MarkDownloaded(photo, savedPath);
                    Interlocked.Increment(ref succeeded);
                }

                var done = Interlocked.Increment(ref processed);
                progress?.Report((done, list.Count, fileName));
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failed);
                Interlocked.Increment(ref processed);

                lock (errors)
                {
                    errors.Add($"{photo.Id}: {ex.Message}");
                }

                AppLog.Error($"Mobile download failed for photo {photo.Id}.", ex);
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
            MobileDownloadPathHelper.BuildOutputSummary(outputFolders, layout, studentName),
            errors);
    }
}
