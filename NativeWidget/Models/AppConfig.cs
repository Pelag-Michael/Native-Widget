using System.IO;
using System.Text.Json;

namespace NativeWidget.Models;

public class AppConfig
{
    public string GoogleClientId { get; set; } = "";
    public string GoogleClientSecret { get; set; } = "";

    public string NotionToken { get; set; } = "";
    /// The Notion page the user shared with the integration - the sync database is created
    /// as a child of this page the first time sync runs, then NotionDatabaseId is cached.
    public string NotionParentPageId { get; set; } = "";
    public string NotionDatabaseId { get; set; } = "";
    public string NotionDataSourceId { get; set; } = "";
    public bool NotionSyncEnabled { get; set; }

    private static string FolderPath =>
        Environment.GetEnvironmentVariable("NATIVEWIDGET_DATA_DIR") is { Length: > 0 } overridePath
            ? overridePath
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NativeWidget");

    private static string FilePath => Path.Combine(FolderPath, "config.json");

    public static AppConfig Load()
    {
        try
        {
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
        }
        catch
        {
            return new AppConfig();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(FolderPath);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
    }

    public static string TokenPath(string name) => Path.Combine(FolderPath, name);

    public static void EnsureFolder() => Directory.CreateDirectory(FolderPath);
}
