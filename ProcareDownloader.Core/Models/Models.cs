using System.Text.Json.Serialization;

namespace ProcareDownloader.Models;

public class Student
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("first_name")]
    public string FirstName { get; set; } = "";

    [JsonPropertyName("last_name")]
    public string LastName { get; set; } = "";

    [JsonPropertyName("photo_url")]
    public string? PhotoUrl { get; set; }

    public string FullName => $"{FirstName} {LastName}".Trim();
}

public class Photo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("thumbnail_url")]
    public string ThumbnailUrl { get; set; } = "";

    [JsonPropertyName("original_url")]
    public string OriginalUrl { get; set; } = "";

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("caption")]
    public string? Caption { get; set; }

    public List<string> StudentIds { get; set; } = [];
}

public class TokenInfo
{
    public string AccessToken { get; set; } = "";
    public string? OrganizationId { get; set; }
}

public enum DownloadLayout
{
    Flat,
    YearMonth,
    StudentYear,
    StudentYearMonth
}
