using ProcareDownloader.Models;

#if ANDROID
using System.Runtime.Versioning;
using Android.Content;
using Android.Provider;
using AndroidEnvironment = Android.OS.Environment;
using AndroidApp = Android.App.Application;
#endif

namespace ProcareDownloader.Mobile.Services;

public sealed class MobileMediaSaveService
{
    public string RootDisplayPath => MobileDownloadPathHelper.GetRootDisplayPath();

    public bool Exists(string relativeFolderPath, string fileName)
    {
#if ANDROID
        return ExistsInAndroidMediaStore(relativeFolderPath, fileName);
#else
        var folderPath = ResolveFallbackFolderPath(relativeFolderPath);
        return File.Exists(Path.Combine(folderPath, fileName));
#endif
    }

    public async Task<string> SaveAsync(
        Photo photo,
        byte[] bytes,
        string relativeFolderPath,
        string fileName,
        CancellationToken ct = default)
    {
#if ANDROID
        return await SaveToAndroidMediaStoreAsync(bytes, relativeFolderPath, fileName, ct);
#else
        var folderPath = ResolveFallbackFolderPath(relativeFolderPath);
        Directory.CreateDirectory(folderPath);
        var fullPath = Path.Combine(folderPath, fileName);
        await File.WriteAllBytesAsync(fullPath, bytes, ct);
        return fullPath;
#endif
    }

#if ANDROID
    private static bool ExistsInAndroidMediaStore(string relativeFolderPath, string fileName)
    {
        var resolver = AndroidApp.Context.ContentResolver
            ?? throw new InvalidOperationException("Android content resolver is not available.");

        using var cursor = resolver.Query(
            GetCollectionUri(fileName),
            null,
            BuildQuerySelection(),
            BuildQueryArguments(relativeFolderPath, fileName),
            null);

        return cursor?.MoveToFirst() == true;
    }

    private async Task<string> SaveToAndroidMediaStoreAsync(
        byte[] bytes,
        string relativeFolderPath,
        string fileName,
        CancellationToken ct)
    {
        var resolver = AndroidApp.Context.ContentResolver
            ?? throw new InvalidOperationException("Android content resolver is not available.");
        var relativePath = BuildAndroidRelativePath(relativeFolderPath);
        var collectionUri = GetCollectionUri(fileName);
        var contentValues = new ContentValues();
        contentValues.Put(MediaStore.IMediaColumns.DisplayName, fileName);
        contentValues.Put(MediaStore.IMediaColumns.MimeType, GetMimeType(fileName));

        Android.Net.Uri? itemUri = null;

        try
        {
            if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.Q)
            {
                SetPendingInsertValues(contentValues, relativePath);
            }
            else
            {
                var absoluteFolder = ResolveLegacyAndroidFolderPath(relativeFolderPath);
                Directory.CreateDirectory(absoluteFolder);
                contentValues.Put(MediaStore.IMediaColumns.Data, Path.Combine(absoluteFolder, fileName));
            }

            itemUri = resolver.Insert(collectionUri, contentValues)
                ?? throw new IOException($"Unable to create media store entry for {fileName}.");

            using (var outputStream = resolver.OpenOutputStream(itemUri)
                   ?? throw new IOException($"Unable to open media store output stream for {fileName}."))
            {
                await outputStream.WriteAsync(bytes.AsMemory(0, bytes.Length), ct);
                await outputStream.FlushAsync(ct);
            }

            if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.Q)
            {
                resolver.Update(itemUri, CreatePublishValues(), null, null);
            }

            return string.IsNullOrWhiteSpace(relativeFolderPath)
                ? Path.Combine(RootDisplayPath, fileName)
                : Path.Combine(RootDisplayPath, relativeFolderPath, fileName);
        }
        catch
        {
            if (itemUri != null)
            {
                resolver.Delete(itemUri, null, null);
            }

            throw;
        }
    }

    private static Android.Net.Uri GetCollectionUri(string fileName)
    {
        var isVideo = IsVideo(fileName);
        if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.Q)
        {
            return GetPrimaryCollectionUri(isVideo);
        }

        return isVideo
            ? MediaStore.Video.Media.ExternalContentUri
            : MediaStore.Images.Media.ExternalContentUri;
    }

    private static string BuildQuerySelection()
    {
        return Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.Q
            ? BuildScopedStorageQuerySelection()
            : $"{MediaStore.IMediaColumns.Data} = ?";
    }

    private static string[] BuildQueryArguments(string relativeFolderPath, string fileName)
    {
        return Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.Q
            ? [fileName, BuildAndroidRelativePath(relativeFolderPath)]
            : [Path.Combine(ResolveLegacyAndroidFolderPath(relativeFolderPath), fileName)];
    }

    private static string BuildAndroidRelativePath(string relativeFolderPath)
    {
        var segments = new List<string>
        {
            AndroidEnvironment.DirectoryPictures ?? "Pictures",
            MobileDownloadPathHelper.AndroidPicturesFolderName
        };

        if (!string.IsNullOrWhiteSpace(relativeFolderPath))
        {
            segments.AddRange(relativeFolderPath
                .Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries));
        }

        return string.Join("/", segments) + "/";
    }

    [SupportedOSPlatform("android29.0")]
    private static Android.Net.Uri GetPrimaryCollectionUri(bool isVideo)
    {
        return isVideo
            ? MediaStore.Video.Media.GetContentUri(MediaStore.VolumeExternalPrimary)
            : MediaStore.Images.Media.GetContentUri(MediaStore.VolumeExternalPrimary);
    }

    [SupportedOSPlatform("android29.0")]
    private static string BuildScopedStorageQuerySelection()
    {
        return $"{MediaStore.IMediaColumns.DisplayName} = ? AND {MediaStore.IMediaColumns.RelativePath} = ?";
    }

    [SupportedOSPlatform("android29.0")]
    private static void SetPendingInsertValues(ContentValues contentValues, string relativePath)
    {
        contentValues.Put(MediaStore.IMediaColumns.RelativePath, relativePath);
        contentValues.Put(MediaStore.IMediaColumns.IsPending, 1);
    }

    [SupportedOSPlatform("android29.0")]
    private static ContentValues CreatePublishValues()
    {
        var values = new ContentValues();
        values.Put(MediaStore.IMediaColumns.IsPending, 0);
        return values;
    }

    private static string ResolveLegacyAndroidFolderPath(string relativeFolderPath)
    {
        var baseFolder = Path.Combine(
            AndroidEnvironment.GetExternalStoragePublicDirectory(AndroidEnvironment.DirectoryPictures)?.AbsolutePath
            ?? throw new DirectoryNotFoundException("Pictures directory is not available."),
            MobileDownloadPathHelper.AndroidPicturesFolderName);

        if (string.IsNullOrWhiteSpace(relativeFolderPath))
        {
            return baseFolder;
        }

        var segments = relativeFolderPath
            .Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        return Path.Combine([baseFolder, .. segments]);
    }

    private static string GetMimeType(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension switch
        {
            ".jpeg" => "image/jpeg",
            ".jpg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            ".mp4" => "video/mp4",
            ".mov" => "video/quicktime",
            _ => "image/jpeg"
        };
    }

    private static bool IsVideo(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension is ".mp4" or ".mov";
    }
#endif

    private string ResolveFallbackFolderPath(string relativeFolderPath)
    {
        var root = RootDisplayPath;
        if (string.IsNullOrWhiteSpace(relativeFolderPath))
        {
            return root;
        }

        var segments = relativeFolderPath
            .Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        return Path.Combine([root, .. segments]);
    }
}
