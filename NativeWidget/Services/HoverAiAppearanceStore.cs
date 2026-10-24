using System.IO;
using System.Reflection;
using System.Text.Json;
using NativeWidget.Models;

namespace NativeWidget.Services;

/// Mode appearance is stored independently of the original widget settings.
internal static class HoverAiAppearanceStore
{
    private static string FilePath => AppConfig.TokenPath("hover-ai-appearance.json");
    private static readonly PropertyInfo[] Properties = typeof(AppConfig).GetProperties()
        .Where(property => property.Name.StartsWith("HoverAi", StringComparison.Ordinal)).ToArray();

    public static void LoadInto(AppConfig config)
    {
        if (!File.Exists(FilePath)) return; // Legacy HoverAi fields are migration defaults.
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(FilePath));
            foreach (var property in Properties)
                if (document.RootElement.TryGetProperty(property.Name, out var value))
                    property.SetValue(config, value.Deserialize(property.PropertyType));
        }
        catch (JsonException) { }
        catch (IOException) { }
    }

    public static void Save(AppConfig config) => AtomicFile.WriteText(FilePath,
        JsonSerializer.Serialize(Properties.ToDictionary(property => property.Name, property => property.GetValue(config))));
}
