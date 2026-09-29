using System;
using System.Collections.Generic;
using System.IO;
using VideoEditor.Core.Model;

namespace VideoEditor.App;

/// <summary>Impostazioni dell'editor (non del progetto), in %APPDATA%\VideoEditor\settings.json.</summary>
public sealed class AppSettings
{
    public List<string> RecentProjects { get; set; } = [];
    public string? ClaudePath { get; set; }
    public string? ClaudeModel { get; set; }
    public string Encoder { get; set; } = "auto";
    public string WhisperModel { get; set; } = "large-v3-turbo";

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VideoEditor", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath) ? Json.Deserialize<AppSettings>(File.ReadAllText(FilePath)) : new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException)
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, Json.Serialize(this));
    }

    public void AddRecent(string dir)
    {
        RecentProjects.RemoveAll(p => string.Equals(p, dir, StringComparison.OrdinalIgnoreCase));
        RecentProjects.Insert(0, dir);
        if (RecentProjects.Count > 10) RecentProjects.RemoveRange(10, RecentProjects.Count - 10);
        Save();
    }
}
