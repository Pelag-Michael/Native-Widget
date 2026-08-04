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
    public List<string> Tags { get; set; } = new();
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
            var items = JsonSerializer.Deserialize<List<VocabularyEntry>>(File.ReadAllText(FilePath)) ?? new();
            foreach (var item in items) item.Tags ??= new();
            return items;
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

    public static void SetTags(string id, IEnumerable<string> tags)
    {
        var items = Load();
        var item = items.FirstOrDefault(entry => entry.Id == id);
        if (item == null) return;
        item.Tags = VocabularyTagsService.Normalize(tags).ToList();
        VocabularyTagsService.Register(item.Tags);
        Save(items);
    }

    private static void Save(List<VocabularyEntry> items)
    {
        AppConfig.EnsureFolder();
        File.WriteAllText(FilePath, JsonSerializer.Serialize(items, JsonOptions));
    }
}

/// A registry used only by the translation notebook. It intentionally does not read from or
/// write to the shared LabelsService registry used by Notes, Tasks, Calendar, and other widgets.
public static class VocabularyTagsService
{
    private static string FilePath => AppConfig.TokenPath("translation-tags.json");

    public static List<string> LoadAll()
    {
        var tags = new List<string>();
        try { tags = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath)) ?? new(); }
        catch { /* A missing or malformed registry is recovered from saved entries below. */ }
        tags.AddRange(VocabularyService.Load().SelectMany(item => item.Tags));
        return Normalize(tags).ToList();
    }

    public static void Add(string tag) => Register(new[] { tag });

    public static void Register(IEnumerable<string> tags)
    {
        var merged = LoadRegistry();
        merged.AddRange(tags);
        AppConfig.EnsureFolder();
        File.WriteAllText(FilePath, JsonSerializer.Serialize(Normalize(merged),
            new JsonSerializerOptions { WriteIndented = true }));
    }

    internal static IEnumerable<string> Normalize(IEnumerable<string> tags) => tags
        .Select(tag => tag.Trim().TrimStart('#'))
        .Where(tag => tag.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase);

    private static List<string> LoadRegistry()
    {
        try { return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }
}
