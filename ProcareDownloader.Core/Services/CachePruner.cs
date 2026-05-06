namespace ProcareDownloader.Services;

public sealed record CacheUsageInfo(long Bytes, int FileCount)
{
    public string DisplayText => $"{FormatBytes(Bytes)} in {FileCount} files";

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = Math.Max(0, bytes);
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }
}

public static class CachePruner
{
    public static CacheUsageInfo GetUsage(string root)
    {
        if (!Directory.Exists(root))
        {
            return new CacheUsageInfo(0, 0);
        }

        long bytes = 0;
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            try
            {
                var info = new FileInfo(file);
                bytes += info.Length;
                count++;
            }
            catch
            {
                // Ignore files that disappear while cache usage is being calculated.
            }
        }

        return new CacheUsageInfo(bytes, count);
    }

    public static void PruneByLastAccess(string root, long maxBytes)
    {
        if (maxBytes <= 0 || !Directory.Exists(root))
        {
            return;
        }

        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path =>
            {
                try
                {
                    var info = new FileInfo(path);
                    var touched = info.LastAccessTimeUtc > DateTime.MinValue
                        ? info.LastAccessTimeUtc
                        : info.LastWriteTimeUtc;
                    return new CacheFile(path, info.Length, touched);
                }
                catch
                {
                    return null;
                }
            })
            .Where(file => file != null)
            .Cast<CacheFile>()
            .OrderBy(file => file.LastAccessUtc)
            .ToList();

        var total = files.Sum(file => file.Length);
        foreach (var file in files)
        {
            if (total <= maxBytes)
            {
                break;
            }

            try
            {
                File.Delete(file.Path);
                total -= file.Length;
            }
            catch
            {
                // Best effort pruning only.
            }
        }
    }

    public static void Clear(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.Delete(file);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    private sealed record CacheFile(string Path, long Length, DateTime LastAccessUtc);
}
