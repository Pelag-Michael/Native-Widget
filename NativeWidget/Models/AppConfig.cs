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
    public double? HoverAiBarTextSize { get; set; }
    public double HoverAiBarIconSize { get; set; } = 11;
    public double? HoverAiFollowUpFontSize { get; set; }
    public double? HoverAiFollowUpOpacity { get; set; }
    public string? HoverAiFollowUpBackground { get; set; }
    public string? HoverAiFollowUpForeground { get; set; }
    public bool HoverAiFollowUpTextOnly { get; set; } = true;
    public bool HoverAiFollowUpAutoHeight { get; set; } = true;
    public double? HoverAiFollowUpLeft { get; set; }
    public double? HoverAiFollowUpTop { get; set; }
    public double HoverAiFollowUpWidth { get; set; } = 260;
    public double HoverAiFollowUpHeight { get; set; } = 70;
    public string HoverAiBackground { get; set; } = "#131217";
    public string HoverAiForeground { get; set; } = "#EEEEF2";
    public string HoverAiIconColor { get; set; } = "#EEEEF2";
    public string? HoverAiDotColor { get; set; }
    public string? HoverAiStatusBackground { get; set; }
    public string? HoverAiResponseBackground { get; set; }
    public string? HoverAiResponseForeground { get; set; }
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
    public double? HoverAiStatusOpacity { get; set; }
    public double? HoverAiResponseOpacity { get; set; }
    public const string DefaultHoverAiSystemPrompt = "Tr\u1EA3 l\u1EDDi ng\u1EAFn g\u1ECDn, \u0111i th\u1EB3ng v\u00E0o \u00FD ch\u00EDnh. M\u1EB7c \u0111\u1ECBnh t\u1ED1i \u0111a 1-3 c\u00E2u. Kh\u00F4ng m\u1EDF \u0111\u1EA7u x\u00E3 giao, kh\u00F4ng l\u1EB7p l\u1EA1i c\u00E2u h\u1ECFi. Ch\u1EC9 gi\u1EA3i th\u00EDch d\u00E0i khi t\u00F4i y\u00EAu c\u1EA7u.";
    public string HoverAiSystemPrompt { get; set; } = DefaultHoverAiSystemPrompt;
    public string HoverAiCodexModel { get; set; } = "";
    public string HoverAiCodexEffort { get; set; } = "low";
    public bool HoverAiScreenshotAutoSend { get; set; }
    public string HoverAiScreenshotPrompt { get; set; } = "\u0110\u1ECDc \u1EA3nh v\u00E0 tr\u1EA3 l\u1EDDi ng\u1EAFn g\u1ECDn b\u1EB1ng ti\u1EBFng Vi\u1EC7t.";
    public bool HoverAiQuicker { get; set; }
    public bool HoverAiCompactBar { get; set; }
    public string HoverAiLocateFirstColor { get; set; } = "#FFFFFF";
    public string HoverAiLocateSecondColor { get; set; } = "#F97316";
    public bool HoverAiDwellButtons { get; set; }
    public string HoverAiScreenshotFolder { get; set; } = "";
    public string HoverAiInputDotColor { get; set; } = "#4ADE80";
    public bool HoverAiResponseTextOnly { get; set; } = true;
    public bool HoverAiResponseAutoHeight { get; set; } = true;


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
