using System.IO;
using System.Text.Json;

namespace GhostHunterPC.Services;

public sealed class UserSettings
{
    public string Language { get; set; } = "ar";
    public bool LightTheme { get; set; }
    public string MonitorIntervalSeconds { get; set; } = "2";
    public List<string> ExcludedFolders { get; set; } = [];
    public bool SaveHistory { get; set; } = true;
}

public sealed class UserSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public string StoragePath { get; }
    public UserSettings Settings { get; }
    public string StorageFolder => Path.GetDirectoryName(StoragePath)!;

    public UserSettingsService(string? storagePath = null)
    {
        StoragePath = storagePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GhostHunterPC",
            "settings.json");
        Settings = Load(StoragePath);
    }

    public void Save()
    {
        Directory.CreateDirectory(StorageFolder);
        var temporaryPath = StoragePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(Settings, JsonOptions));
        File.Move(temporaryPath, StoragePath, overwrite: true);
    }

    private static UserSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            return new UserSettings();
        }

        try
        {
            var settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException("Settings file contains no settings.");
            if (settings.Language is not ("ar" or "en"))
            {
                settings.Language = "ar";
            }

            if (settings.MonitorIntervalSeconds is not ("1" or "2" or "5" or "10" or "30"))
            {
                settings.MonitorIntervalSeconds = "2";
            }

            settings.ExcludedFolders = settings.ExcludedFolders
                .Where(Directory.Exists)
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return settings;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Could not read settings file '{path}'.", exception);
        }
    }
}

public static class FolderExclusion
{
    public static bool Contains(string path, IEnumerable<string> excludedFolders)
    {
        var fullPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var excluded in excludedFolders)
        {
            var fullExcluded = Path.GetFullPath(excluded)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(fullPath, fullExcluded, StringComparison.OrdinalIgnoreCase)
                || fullPath.StartsWith(
                    fullExcluded + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
