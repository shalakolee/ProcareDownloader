using ProcareDownloader.Models;
using ProcareDownloader.Services;

namespace ProcareDownloader.Mobile.Services;

internal static class MobileDownloadPathHelper
{
    public const string AndroidPicturesFolderName = "Procare Photo Downloader";

    public static string GetRootDisplayPath()
    {
#if ANDROID
        return Path.Combine("Pictures", AndroidPicturesFolderName);
#else
        return Path.Combine(FileSystem.Current.AppDataDirectory, "Exports");
#endif
    }

    public static string BuildLayoutPreviewPath(DownloadLayout layout, string? studentName)
    {
        return BuildDisplayFolderPath(GetRootDisplayPath(), layout, studentName, year: "YYYY", month: "MM");
    }

    public static string BuildDisplayFolderPath(
        string root,
        DownloadLayout layout,
        string? studentName,
        Photo photo)
    {
        var year = photo.CreatedAt != default ? photo.CreatedAt.ToString("yyyy") : "unknown";
        var month = photo.CreatedAt != default ? photo.CreatedAt.ToString("MM") : "unknown";
        return BuildDisplayFolderPath(root, layout, studentName, year, month);
    }

    public static string BuildRelativeFolderPath(DownloadLayout layout, string? studentName, Photo photo)
    {
        var root = BuildDisplayFolderPath(".", layout, studentName, photo);
        return root == "."
            ? ""
            : root[2..];
    }

    public static string BuildFileName(Photo photo)
    {
        var ext = GetExtension(photo.OriginalUrl);
        var datePart = photo.CreatedAt != default
            ? photo.CreatedAt.ToString("yyyy-MM-dd")
            : "unknown";
        return $"{datePart}_{photo.Id}{ext}";
    }

    public static string BuildDisplayFilePath(string folderPath, string fileName)
    {
        return string.IsNullOrWhiteSpace(folderPath) ? fileName : Path.Combine(folderPath, fileName);
    }

    public static string BuildOutputSummary(
        IReadOnlyCollection<string> outputFolders,
        DownloadLayout layout,
        string? studentName)
    {
        if (outputFolders.Count == 0)
        {
            return BuildLayoutPreviewPath(layout, studentName);
        }

        var ordered = outputFolders.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        if (ordered.Count == 1)
        {
            return ordered[0];
        }

        return $"{ordered[0]} (+{ordered.Count - 1} more folders)";
    }

    private static string BuildDisplayFolderPath(
        string root,
        DownloadLayout layout,
        string? studentName,
        string year,
        string month)
    {
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
