using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProcareDownloader.Services;

public static class AppLog
{
    private static readonly object Sync = new();
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ProcareDownloader",
        "logs");

    public static string LogPath => Path.Combine(LogDirectory, "app.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null)
    {
        var builder = new StringBuilder(message);
        if (ex != null)
        {
            builder.AppendLine();
            builder.Append(ex);
        }

        Write("ERROR", builder.ToString());
    }

    public static string DescribeNode(JsonNode? node)
    {
        if (node == null)
        {
            return "null";
        }

        return node switch
        {
            JsonArray => "array",
            JsonObject => "object",
            JsonValue => "value",
            _ => node.GetType().Name
        };
    }

    public static string SerializeNode(JsonNode? node)
    {
        return node?.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = false
        }) ?? "null";
    }

    public static string Truncate(string? value, int maxLength = 2000)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value ?? "";
        }

        return value[..maxLength] + "...";
    }

    private static void Write(string level, string message)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);

            var entry = $"""
                [{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] {level}
                {message}

                """;

            lock (Sync)
            {
                File.AppendAllText(LogPath, entry);
            }
        }
        catch
        {
            // Logging must never break the app.
        }
    }
}
