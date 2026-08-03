using System.IO;
using System.Text.Json;
using NativeWidget.Models;

namespace NativeWidget.Services;

/// Free-form labels for remote-backed items. Google Tasks and Calendar do not expose a
/// portable label field, so this companion store keeps their labels local without changing
/// or overwriting the original Google records.
public static class ItemTagsService
{
    private static string FilePath => AppConfig.TokenPath("item-tags.json");

    public static Dictionary<string, List<string>> Load()
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch
        {
            return new();
        }
    }

    private static void Save(Dictionary<string, List<string>> map)
    {
        AppConfig.EnsureFolder();
        File.WriteAllText(FilePath, JsonSerializer.Serialize(map));
    }

    public static List<string> Get(string kind, string itemId)
    {
        var map = Load();
        return map.TryGetValue($"{kind}:{itemId}", out var tags) ? tags : new List<string>();
    }

    public static void Set(string kind, string itemId, IEnumerable<string> tags)
    {
        var normalized = tags.Select(tag => tag.Trim())
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();

        var map = Load();
        var key = $"{kind}:{itemId}";
        if (normalized.Count == 0) map.Remove(key);
        else map[key] = normalized;
        Save(map);
    }

    public static IEnumerable<string> AllTags() => Load().Values.SelectMany(tags => tags)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase);
}
