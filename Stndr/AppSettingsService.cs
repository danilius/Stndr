using System;
using System.IO;
using System.Text.Json;

namespace Stndr;

public sealed class AppSettingsService
{
    public AppSettingsService()
    {
        ProjectFolder = ResolveProjectFolder();
        SettingsFolder = Path.Combine(ProjectFolder, "Data");
        SettingsFilePath = Path.Combine(SettingsFolder, "settings.json");
        Directory.CreateDirectory(SettingsFolder);
    }

    public string ProjectFolder { get; }
    public string SettingsFolder { get; }
    public string SettingsFilePath { get; }

    public AppSettings Load()
    {
        if (!File.Exists(SettingsFilePath))
        {
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(SettingsFilePath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsFilePath, json);
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
