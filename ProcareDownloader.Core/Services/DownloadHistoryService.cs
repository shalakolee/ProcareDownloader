using System;
using System.IO;
using System.Text.Json;
using ProcareDownloader.Models;

namespace ProcareDownloader.Services;

public sealed class DownloadHistoryRecord
{
    public string PhotoId { get; set; } = "";
    public string OriginalUrl { get; set; } = "";
    public string DestinationPath { get; set; } = "";
    public DateTime DownloadedAt { get; set; }
}

public sealed class ImportHistoryResult
{
    public int Imported { get; set; }
    public int MatchedFiles { get; set; }
    public int ScannedFiles { get; set; }
}

public sealed class DownloadHistoryService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _historyPath;
    private readonly object _sync = new();
    private List<DownloadHistoryRecord>? _records;

    public DownloadHistoryService()
    {
        var settingsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProcareDownloader");
        Directory.CreateDirectory(settingsFolder);
        _historyPath = Path.Combine(settingsFolder, "download-history.json");
    }

    public bool IsDownloaded(Photo photo)
    {
        lock (_sync)
        {
            return EnsureLoaded().Any(record => Matches(record, photo) && RecordExists(record));
        }
    }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return EnsureLoaded()
                    .Where(RecordExists)
                    .Select(record => record.DestinationPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
            }
        }
    }

    public void MarkDownloaded(Photo photo, string destinationPath)
    {
        if (string.IsNullOrWhiteSpace(photo.Id) && string.IsNullOrWhiteSpace(photo.OriginalUrl))
        {
            return;
        }

        lock (_sync)
        {
            var records = EnsureLoaded();
            var existing = records.FirstOrDefault(record => Matches(record, photo) || PathsEqual(record.DestinationPath, destinationPath));
            if (existing == null)
            {
                records.Add(new DownloadHistoryRecord());
                existing = records[^1];
            }

            existing.PhotoId = photo.Id;
            existing.OriginalUrl = photo.OriginalUrl;
            existing.DestinationPath = destinationPath;
            existing.DownloadedAt = DateTime.Now;

            Save(records);
        }
    }

    public ImportHistoryResult ImportFromFolder(string folder, IReadOnlyCollection<Photo> photos)
    {
        var result = new ImportHistoryResult();
        if (!Directory.Exists(folder))
        {
            return result;
        }

        var photosById = photos
            .Where(photo => !string.IsNullOrWhiteSpace(photo.Id))
            .GroupBy(photo => photo.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var files = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories).ToList();
        result.ScannedFiles = files.Count;

        lock (_sync)
        {
            var records = EnsureLoaded();

            foreach (var file in files)
            {
                var photoId = TryExtractPhotoId(file);
                if (photoId == null || !photosById.TryGetValue(photoId, out var photo))
                {
                    continue;
                }

                result.MatchedFiles++;
                var existed = records.Any(record => Matches(record, photo) || PathsEqual(record.DestinationPath, file));
                if (existed)
                {
                    continue;
                }

                records.Add(new DownloadHistoryRecord
                {
                    PhotoId = photo.Id,
                    OriginalUrl = photo.OriginalUrl,
                    DestinationPath = file,
                    DownloadedAt = File.GetLastWriteTime(file)
                });
                result.Imported++;
            }

            if (result.Imported > 0)
            {
                Save(records);
            }
        }

        return result;
    }

    public void Clear()
    {
        lock (_sync)
        {
            _records = [];
            Save(_records);
        }
    }

    private List<DownloadHistoryRecord> EnsureLoaded()
    {
        if (_records != null)
        {
            return _records;
        }

        try
        {
            if (!File.Exists(_historyPath))
            {
                _records = [];
                return _records;
            }

            var json = File.ReadAllText(_historyPath);
            _records = JsonSerializer.Deserialize<List<DownloadHistoryRecord>>(json, JsonOptions);
            if (_records != null)
            {
                return _records;
            }

            var legacy = JsonSerializer.Deserialize<Dictionary<string, DownloadHistoryRecord>>(json, JsonOptions) ?? [];
            _records = legacy.Values
                .GroupBy(record => $"{record.PhotoId}|{record.OriginalUrl}|{record.DestinationPath}", StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Failed to load download history, using empty state. {ex.Message}");
            _records = [];
        }

        return _records;
    }

    private void Save(List<DownloadHistoryRecord> records)
    {
        try
        {
            var json = JsonSerializer.Serialize(records, JsonOptions);
            File.WriteAllText(_historyPath, json);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Failed to save download history. {ex.Message}");
        }
    }

    private static string? TryExtractPhotoId(string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var underscoreIndex = fileName.IndexOf('_');
        if (underscoreIndex >= 0 && underscoreIndex < fileName.Length - 1)
        {
            return fileName[(underscoreIndex + 1)..];
        }

        return null;
    }

    private static bool Matches(DownloadHistoryRecord record, Photo photo)
    {
        return (!string.IsNullOrWhiteSpace(record.OriginalUrl)
                && !string.IsNullOrWhiteSpace(photo.OriginalUrl)
                && string.Equals(record.OriginalUrl, photo.OriginalUrl, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrWhiteSpace(record.PhotoId)
                && !string.IsNullOrWhiteSpace(photo.Id)
                && string.Equals(record.PhotoId, photo.Id, StringComparison.OrdinalIgnoreCase));
    }

    private static bool RecordExists(DownloadHistoryRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.DestinationPath))
        {
            return true;
        }

        if (Uri.TryCreate(record.DestinationPath, UriKind.Absolute, out var uri) && uri.IsFile == false)
        {
            return true;
        }

        if (!Path.IsPathRooted(record.DestinationPath))
        {
            return true;
        }

        return File.Exists(record.DestinationPath);
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }
}
