using System.IO;
using System.Text.Json;
using NativeWidget.Services;

namespace NativeWidget.Models;

public class AppConfig
{
    private static readonly object StorageGate = new();
    public string GoogleClientId { get; set; } = "";
    public string GoogleClientSecret { get; set; } = "";

    public string NotionToken { get; set; } = "";
    /// The Notion page the user shared with the integration - the sync database is created
    /// as a child of this page the first time sync runs, then NotionDatabaseId is cached.
    public string NotionParentPageId { get; set; } = "";
    public string NotionDatabaseId { get; set; } = "";
    public string NotionDataSourceId { get; set; } = "";
    public bool NotionSyncEnabled { get; set; }

    public string TranslationSourceLanguage { get; set; } = "auto";
    public string TranslationTargetLanguage { get; set; } = "vi";
    public bool TranslationSelectionTrackingEnabled { get; set; } = true;
    public bool RestoreWindowSessionEnabled { get; set; }
    public bool InvisibleHoverEnabled { get; set; }
    public bool HoverAiMinimal { get; set; } = true;
    public double HoverAiOpacity { get; set; } = 1;
    public double HoverAiWidth { get; set; } = 260;
    public double HoverAiFontSize { get; set; } = 12;
    public string HoverAiBackground { get; set; } = "#131217";
    public string HoverAiForeground { get; set; } = "#EEEEF2";
    public string HoverAiIconColor { get; set; } = "#EEEEF2";
    public double? HoverAiLeft { get; set; }
    public double? HoverAiTop { get; set; }
    public bool HoverAiCaptureRegionEnabled { get; set; }
    public int HoverAiCaptureX { get; set; }
    public int HoverAiCaptureY { get; set; }
    public int HoverAiCaptureWidth { get; set; } = 800;
    public int HoverAiCaptureHeight { get; set; } = 600;
    public double? HoverAiStatusLeft { get; set; }
    public double? HoverAiStatusTop { get; set; }
    public double? HoverAiResponseLeft { get; set; }
    public double? HoverAiResponseTop { get; set; }
    public double HoverAiResponseWidth { get; set; } = 340;
    public double HoverAiResponseHeight { get; set; } = 240;


    private static string FolderPath =>
        Environment.GetEnvironmentVariable("NATIVEWIDGET_DATA_DIR") is { Length: > 0 } overridePath
            ? overridePath
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NativeWidget");

    private static string FilePath => Path.Combine(FolderPath, "config.json");

    public static AppConfig Load()
    {
        lock (StorageGate)
        {
            if (!File.Exists(FilePath)) return new AppConfig();
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppConfig>(json)
                ?? throw new InvalidDataException("The widget settings are invalid. Restore config.json.bak before saving new settings.");
        }
    }

    public void Save()
    {
        lock (StorageGate)
        {
            Directory.CreateDirectory(FolderPath);
            AtomicFile.WriteText(FilePath, JsonSerializer.Serialize(this));
        }
    }

    public static string TokenPath(string name) => Path.Combine(FolderPath, name);

    public static void EnsureFolder() => Directory.CreateDirectory(FolderPath);
}
