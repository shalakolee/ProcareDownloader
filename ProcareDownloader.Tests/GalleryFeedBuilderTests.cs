using ProcareDownloader.Models;
using ProcareDownloader.Services;

namespace ProcareDownloader.Tests;

public sealed class GalleryFeedBuilderTests
{
    [Fact]
    public void Build_GroupsByMonthDescendingAndChunksPhotoRows()
    {
        var photos = new[]
        {
            Photo("jan-1", new DateTime(2026, 1, 20)),
            Photo("feb-1", new DateTime(2026, 2, 3)),
            Photo("feb-2", new DateTime(2026, 2, 2)),
            Photo("feb-3", new DateTime(2026, 2, 1))
        };

        var rows = GalleryFeedBuilder.Build(photos, columns: 2);

        Assert.Collection(
            rows,
            row =>
            {
                Assert.True(row.IsMonthHeader);
                Assert.Equal("February 2026", row.MonthLabel);
                Assert.Equal(3, row.MonthPhotoCount);
            },
            row =>
            {
                Assert.True(row.IsPhotoRow);
                Assert.Equal(["feb-1", "feb-2"], row.Photos.Select(photo => photo.Id));
            },
            row =>
            {
                Assert.True(row.IsPhotoRow);
                Assert.Equal(["feb-3"], row.Photos.Select(photo => photo.Id));
            },
            row =>
            {
                Assert.True(row.IsMonthHeader);
                Assert.Equal("January 2026", row.MonthLabel);
                Assert.Equal(1, row.MonthPhotoCount);
            },
            row =>
            {
                Assert.True(row.IsPhotoRow);
                Assert.Equal(["jan-1"], row.Photos.Select(photo => photo.Id));
            });
    }

    [Fact]
    public void Build_UsesUnknownDateBucketForDefaultDates()
    {
        var rows = GalleryFeedBuilder.Build([Photo("unknown", default)], columns: 3);

        Assert.Equal("Unknown Date", rows[0].MonthLabel);
        Assert.Equal(DateTime.MinValue, rows[0].SortDate);
        Assert.Equal(["unknown"], rows[1].Photos.Select(photo => photo.Id));
    }

    [Fact]
    public void Build_SkipsPhotoRowsForCollapsedMonths()
    {
        var rows = GalleryFeedBuilder.Build(
            [Photo("one", new DateTime(2026, 3, 1))],
            columns: 3,
            collapsedMonthLabels: new HashSet<string> { "March 2026" });

        Assert.Single(rows);
        Assert.True(rows[0].IsMonthHeader);
    }

    private static Photo Photo(string id, DateTime createdAt)
    {
        return new Photo
        {
            Id = id,
            CreatedAt = createdAt,
            ThumbnailUrl = $"https://example.test/{id}-thumb.jpg",
            OriginalUrl = $"https://example.test/{id}.jpg"
        };
    }
}
