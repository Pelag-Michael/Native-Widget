using System.IO;
using System.Text.Json;
using NativeWidget.Models;

namespace NativeWidget.Services;

/// Google Tasks has no per-list color field at all, so the whole-window tint a list can
/// have is purely local, keyed by the Google Tasklist ID - same pattern as EventColorsService.
public static class TaskListColorsService
{
    private static string FilePath => AppConfig.TokenPath("tasklist-colors.json");

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

    public static void SetColor(string listId, string hex)
    {
        var map = Load();
        if (string.IsNullOrEmpty(hex)) map.Remove(listId);
        else map[listId] = hex;
        AppConfig.EnsureFolder();
        File.WriteAllText(FilePath, JsonSerializer.Serialize(map));
    }
}
