using ProcareDownloader.Services;

namespace ProcareDownloader.Tests;

public sealed class CachePrunerTests
{
    [Fact]
    public void PruneByLastAccess_RemovesOldestFilesUntilUnderLimit()
    {
        var root = Directory.CreateTempSubdirectory("procare-cache-test-").FullName;
        try
        {
            var oldFile = CreateFile(root, "old.bin", 100, DateTime.UtcNow.AddDays(-5));
            var newFile = CreateFile(root, "new.bin", 100, DateTime.UtcNow);

            CachePruner.PruneByLastAccess(root, maxBytes: 120);

            Assert.False(File.Exists(oldFile));
            Assert.True(File.Exists(newFile));
            Assert.True(CachePruner.GetUsage(root).Bytes <= 120);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Clear_RemovesCacheFiles()
    {
        var root = Directory.CreateTempSubdirectory("procare-cache-test-").FullName;
        try
        {
            CreateFile(root, "nested/photo.bin", 64, DateTime.UtcNow);

            CachePruner.Clear(root);

            Assert.Equal(0, CachePruner.GetUsage(root).FileCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateFile(string root, string relativePath, int bytes, DateTime lastAccessUtc)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Enumerable.Repeat((byte)1, bytes).ToArray());
        File.SetLastAccessTimeUtc(path, lastAccessUtc);
        File.SetLastWriteTimeUtc(path, lastAccessUtc);
        return path;
    }
}
