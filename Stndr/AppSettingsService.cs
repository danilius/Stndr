using System;
using System.IO;
using System.Text.Json;

namespace Stndr;

public sealed class AppSettingsService
{
    private const string SettingsFileName = "settings.json";
    private const string LocationPointerFileName = "location.json";

    public AppSettingsService()
    {
        ProjectFolder = ResolveProjectFolder();
        PointerFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Stndr");
        LocationPointerPath = Path.Combine(PointerFolder, LocationPointerFileName);
        Directory.CreateDirectory(PointerFolder);
    }

    public string ProjectFolder { get; }

    /// <summary>
    /// Fixed bootstrap folder (outside the user's Data folder) that only ever holds the
    /// location pointer telling the app where the user's Data folder lives.
    /// </summary>
    public string PointerFolder { get; }

    public string LocationPointerPath { get; }

    /// <summary>
    /// A sensible default location to offer the user when no Data folder is configured yet.
    /// </summary>
    public static string SuggestedDefaultDataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "Stndr");

    /// <summary>
    /// Returns the configured Data folder path, or null if the user has not chosen one yet.
    /// </summary>
    public string? GetConfiguredDataFolder()
    {
        try
        {
            if (!File.Exists(LocationPointerPath))
            {
                return null;
            }

            var json = File.ReadAllText(LocationPointerPath);
            var pointer = JsonSerializer.Deserialize<LocationPointer>(json);
            var folder = pointer?.DataStorageFolder;
            return string.IsNullOrWhiteSpace(folder) ? null : Path.GetFullPath(folder.Trim());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Persists the chosen Data folder path to the bootstrap pointer file.
    /// </summary>
    public void SetConfiguredDataFolder(string dataFolder)
    {
        if (string.IsNullOrWhiteSpace(dataFolder))
        {
            throw new ArgumentException("Data folder path cannot be empty.", nameof(dataFolder));
        }

        var normalized = Path.GetFullPath(dataFolder.Trim());
        Directory.CreateDirectory(PointerFolder);
        var pointer = new LocationPointer { DataStorageFolder = normalized };
        var json = JsonSerializer.Serialize(pointer, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(LocationPointerPath, json);
    }

    public void ClearConfiguredDataFolder()
    {
        try
        {
            if (File.Exists(LocationPointerPath))
            {
                File.Delete(LocationPointerPath);
            }
        }
        catch
        {
            // best effort
        }
    }

    public static string GetSettingsFilePath(string dataFolder)
    {
        return Path.Combine(Path.GetFullPath(dataFolder), SettingsFileName);
    }

    /// <summary>
    /// Loads settings from the given Data folder (settings.json in its root). Creates a fresh
    /// settings file if one does not yet exist.
    /// </summary>
    public AppSettings Load(string dataFolder)
    {
        var normalized = Path.GetFullPath(dataFolder);
        Directory.CreateDirectory(normalized);
        var settingsPath = GetSettingsFilePath(normalized);

        var settings = TryLoadFromFile(settingsPath) ?? new AppSettings();
        settings.DataStorageFolder = normalized;

        if (!File.Exists(settingsPath))
        {
            Save(settings, normalized);
        }

        return settings;
    }

    public void Save(AppSettings settings, string dataFolder)
    {
        var normalized = Path.GetFullPath(dataFolder);
        Directory.CreateDirectory(normalized);
        settings.DataStorageFolder = normalized;
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(GetSettingsFilePath(normalized), json);
    }

    /// <summary>
    /// Convenience save that uses the folder already recorded on the settings object.
    /// No-ops when no Data folder has been configured yet.
    /// </summary>
    public void Save(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.DataStorageFolder))
        {
            return;
        }

        Save(settings, settings.DataStorageFolder);
    }

    private static AppSettings? TryLoadFromFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<AppSettings>(json);
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveProjectFolder()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Stndr.csproj")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        return Path.Combine(Directory.GetCurrentDirectory(), "Stndr");
    }

    private sealed class LocationPointer
    {
        public string DataStorageFolder { get; set; } = string.Empty;
    }
}
