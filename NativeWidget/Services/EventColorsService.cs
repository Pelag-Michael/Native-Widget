using System.IO;
using System.Text.Json;
using NativeWidget.Models;

namespace NativeWidget.Services;

/// Calendar events come fresh from Google on every fetch (no local record to attach a
/// color to), so tags are kept here as a small local map keyed by the Google event ID.
public static class EventColorsService
{
    private static string FilePath => AppConfig.TokenPath("event-colors.json");

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

    public static void SetColor(string eventId, string hex)
    {
        var map = Load();
        if (string.IsNullOrEmpty(hex)) map.Remove(eventId);
        else map[eventId] = hex;
        AppConfig.EnsureFolder();
        File.WriteAllText(FilePath, JsonSerializer.Serialize(map));
    }
}
