using System.IO;
using System.Text.Json;
using NativeWidget.Models;

namespace NativeWidget.Services;

/// Canonical label registry. Labels may exist before they are assigned; legacy labels embedded
/// in notes/tasks/events are always folded into the visible registry on load.
public static class LabelsService
{
    private static string FilePath => AppConfig.TokenPath("labels.json");

    private static List<string> LoadRegistry()
    {
        try { return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }

    private static void SaveRegistry(IEnumerable<string> labels)
    {
        AppConfig.EnsureFolder();
        var normalized = labels.Select(Normalize).Where(label => label.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(label => label, StringComparer.OrdinalIgnoreCase)
            .ToList();
        File.WriteAllText(FilePath, JsonSerializer.Serialize(normalized));
    }

    public static List<string> LoadAll()
    {
        var labels = new HashSet<string>(LoadRegistry(), StringComparer.OrdinalIgnoreCase);
        labels.UnionWith(NotesService.LoadIndex().SelectMany(note => note.Tags));
        labels.UnionWith(ItemTagsService.AllTags());
        return labels.OrderBy(label => label, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static void Register(IEnumerable<string> labels)
    {
        var registry = LoadRegistry();
        registry.AddRange(labels);
        SaveRegistry(registry);
    }

    public static void Add(string label) => Register(new[] { label });

    public static void Rename(string oldLabel, string newLabel)
    {
        oldLabel = Normalize(oldLabel);
        newLabel = Normalize(newLabel);
        if (oldLabel.Length == 0 || newLabel.Length == 0 ||
            string.Equals(oldLabel, newLabel, StringComparison.OrdinalIgnoreCase)) return;

        var registry = LoadRegistry().Where(label => !Same(label, oldLabel)).ToList();
        registry.Add(newLabel);
        SaveRegistry(registry);
        NotesService.RenameTag(oldLabel, newLabel);
        ItemTagsService.RenameTag(oldLabel, newLabel);
    }

    public static void Delete(string label)
    {
        label = Normalize(label);
        SaveRegistry(LoadRegistry().Where(existing => !Same(existing, label)));
        NotesService.RemoveTag(label);
        ItemTagsService.RemoveTag(label);
    }

    public static int UsageCount(string label)
    {
        var noteUses = NotesService.LoadIndex().Count(note => note.Tags.Any(tag => Same(tag, label)));
        var itemUses = ItemTagsService.Load().Values.Count(tags => tags.Any(tag => Same(tag, label)));
        return noteUses + itemUses;
    }

    private static string Normalize(string label) => label.Trim().TrimStart('#');
    private static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
