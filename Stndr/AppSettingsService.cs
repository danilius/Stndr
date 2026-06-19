using System;
using System.IO;
using System.Text.Json;

namespace Stndr;

public sealed class AppSettingsService
{
    public const string DefaultDataStorageFolder = @"F:\Git Repos\Stendr\Stndr\Data";

    public AppSettingsService()
    {
        ProjectFolder = ResolveProjectFolder();
        SettingsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Stndr");
        SettingsFilePath = Path.Combine(SettingsFolder, "settings.json");
        LegacySettingsFilePath = Path.Combine(ProjectFolder, "Data", "settings.json");
        Directory.CreateDirectory(SettingsFolder);
    }

    public string ProjectFolder { get; }
    public string SettingsFolder { get; }
    public string SettingsFilePath { get; }
    public string LegacySettingsFilePath { get; }

    public AppSettings Load()
    {
        var settings = TryLoadFromFile(SettingsFilePath);
        var loadedFromLegacy = false;
        if (settings is null)
        {
            settings = TryLoadFromFile(LegacySettingsFilePath);
            loadedFromLegacy = settings is not null;
        }

        try
        {
            settings ??= new AppSettings();
            settings.DataStorageFolder = NormalizeDataStorageFolder(settings.DataStorageFolder);
            Directory.CreateDirectory(settings.DataStorageFolder);

            if (!File.Exists(SettingsFilePath) || loadedFromLegacy)
            {
                Save(settings);
            }

            return settings;
        }
        catch
        {
            return new AppSettings
            {
                DataStorageFolder = NormalizeDataStorageFolder(null)
            };
        }
    }

    public void Save(AppSettings settings)
    {
        settings.DataStorageFolder = NormalizeDataStorageFolder(settings.DataStorageFolder);
        Directory.CreateDirectory(SettingsFolder);
        Directory.CreateDirectory(settings.DataStorageFolder);
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsFilePath, json);
    }

    public static string NormalizeDataStorageFolder(string? folderPath)
    {
        var candidate = string.IsNullOrWhiteSpace(folderPath)
            ? DefaultDataStorageFolder
            : folderPath.Trim();
        return Path.GetFullPath(candidate);
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
}
