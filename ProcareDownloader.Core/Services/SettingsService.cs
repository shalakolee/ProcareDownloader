using System;
using System.IO;
using System.Text.Json;
using ProcareDownloader.Models;

namespace ProcareDownloader.Services;

public sealed class AppSettings
{
    public DownloadLayout DownloadLayout { get; set; } = DownloadLayout.StudentYearMonth;
}

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _settingsPath;
    private readonly object _sync = new();

    public SettingsService()
    {
        var settingsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProcareDownloader");
        Directory.CreateDirectory(settingsFolder);
        _settingsPath = Path.Combine(settingsFolder, "settings.json");
    }

    public AppSettings Load()
    {
        lock (_sync)
        {
            try
            {
                if (!File.Exists(_settingsPath))
                {
                    return new AppSettings();
                }

                var json = File.ReadAllText(_settingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Failed to load settings, using defaults. {ex.Message}");
                return new AppSettings();
            }
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_sync)
        {
            try
            {
                var json = JsonSerializer.Serialize(settings, JsonOptions);
                File.WriteAllText(_settingsPath, json);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Failed to save settings. {ex.Message}");
            }
        }
    }
}
