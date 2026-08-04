using System.IO;
using System.Text.Json;
using NativeWidget.Models;

namespace NativeWidget.Services;

public sealed class VocabularyEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SourceText { get; set; } = "";
    public string TranslatedText { get; set; } = "";
    public string SourceLanguage { get; set; } = "auto";
    public string TargetLanguage { get; set; } = "vi";
    public string CaptureMethod { get; set; } = "selection";
    public string SourceApp { get; set; } = "";
    public long CreatedAtUnix { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}

public static class VocabularyService
{
    private static string FilePath => AppConfig.TokenPath("translations.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static List<VocabularyEntry> Load()
    {
        try
        {
            return JsonSerializer.Deserialize<List<VocabularyEntry>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch
        {
            return new();
        }
    }

    public static VocabularyEntry Add(TranslationResult result, string captureMethod, string sourceApp)
    {
        var items = Load();
        var existing = items.FirstOrDefault(item =>
            item.SourceText == result.SourceText && item.TranslatedText == result.TranslatedText &&
            item.SourceLanguage == result.SourceLanguage && item.TargetLanguage == result.TargetLanguage);
        if (existing != null) return existing;

        var entry = new VocabularyEntry
        {
            SourceText = result.SourceText,
            TranslatedText = result.TranslatedText,
            SourceLanguage = result.SourceLanguage,
            TargetLanguage = result.TargetLanguage,
            CaptureMethod = captureMethod,
            SourceApp = sourceApp,
        };
        items.Insert(0, entry);
        Save(items);
        return entry;
    }

    public static void Delete(string id)
    {
        var items = Load();
        items.RemoveAll(item => item.Id == id);
        Save(items);
    }

    private static void Save(List<VocabularyEntry> items)
    {
        AppConfig.EnsureFolder();
        File.WriteAllText(FilePath, JsonSerializer.Serialize(items, JsonOptions));
    }
}
