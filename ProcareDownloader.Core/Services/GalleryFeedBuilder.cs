using ProcareDownloader.Models;

namespace ProcareDownloader.Services;

public enum GalleryFeedRowKind
{
    MonthHeader,
    PhotoRow
}

public sealed record GalleryFeedRow(
    GalleryFeedRowKind Kind,
    string MonthLabel,
    DateTime SortDate,
    IReadOnlyList<Photo> Photos,
    int MonthPhotoCount)
{
    public bool IsMonthHeader => Kind == GalleryFeedRowKind.MonthHeader;
    public bool IsPhotoRow => Kind == GalleryFeedRowKind.PhotoRow;
}

public static class GalleryFeedBuilder
{
    public static IReadOnlyList<GalleryFeedRow> Build(
        IEnumerable<Photo> photos,
        int columns,
        ISet<string>? collapsedMonthLabels = null)
    {
        if (columns < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(columns), "Gallery columns must be at least 1.");
        }

        var rows = new List<GalleryFeedRow>();
        var groups = photos
            .OrderByDescending(photo => photo.CreatedAt)
            .GroupBy(photo => GetMonthKey(photo.CreatedAt))
            .OrderByDescending(group => group.Key.SortDate)
            .ThenBy(group => group.Key.Label);

        foreach (var group in groups)
        {
            var monthPhotos = group.ToList();
            rows.Add(new GalleryFeedRow(
                GalleryFeedRowKind.MonthHeader,
                group.Key.Label,
                group.Key.SortDate,
                Array.Empty<Photo>(),
                monthPhotos.Count));

            if (collapsedMonthLabels?.Contains(group.Key.Label) == true)
            {
                continue;
            }

            foreach (var chunk in monthPhotos.Chunk(columns))
            {
                rows.Add(new GalleryFeedRow(
                    GalleryFeedRowKind.PhotoRow,
                    group.Key.Label,
                    group.Key.SortDate,
                    chunk,
                    monthPhotos.Count));
            }
        }

        return rows;
    }

    public static (DateTime SortDate, string Label) GetMonthKey(DateTime createdAt)
    {
        if (createdAt == default)
        {
            return (DateTime.MinValue, "Unknown Date");
        }

        var bucket = new DateTime(createdAt.Year, createdAt.Month, 1);
        return (bucket, bucket.ToString("MMMM yyyy"));
    }
}
