using System.IO;
using System.Text.Json;
using NativeWidget.Models;

namespace NativeWidget.Services;

/// Lightweight project tagging for Tasks and Notes - no structural link to Google Tasks or
/// the notes store, just a local key ("kind:itemId" -> projectId) so tasks/notes can be
/// filtered by project without changing either item's own data model.
public static class ItemProjectTagsService
{
    private static string FilePath => AppConfig.TokenPath("item-project-tags.json");

    public static Dictionary<string, string> Load()
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch
        {
            return new();
        }
    }

    private static void Save(Dictionary<string, string> map)
    {
        AppConfig.EnsureFolder();
        File.WriteAllText(FilePath, JsonSerializer.Serialize(map));
    }

    public static string? Get(string kind, string itemId)
    {
        var map = Load();
        return map.TryGetValue($"{kind}:{itemId}", out var projectId) ? projectId : null;
    }

    public static void Set(string kind, string itemId, string? projectId)
    {
        var map = Load();
        var key = $"{kind}:{itemId}";
        if (string.IsNullOrEmpty(projectId)) map.Remove(key);
        else map[key] = projectId;
        Save(map);
    }
}
