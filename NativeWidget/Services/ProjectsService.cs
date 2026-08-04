using System.IO;
using System.Linq;
using System.Text.Json;
using NativeWidget.Models;

namespace NativeWidget.Services;

public class Project
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? FolderPath { get; set; }
    public string? Note { get; set; }
    public string? Color { get; set; }
    public long CreatedAt { get; set; }
}

public class ProjectsData
{
    public string? CurrentId { get; set; }
    public List<Project> Items { get; set; } = new();
}

public static class ProjectsService
{
    private static string FilePath => AppConfig.TokenPath("projects.json");

    public static ProjectsData Load()
    {
        try
        {
            return JsonSerializer.Deserialize<ProjectsData>(File.ReadAllText(FilePath)) ?? new();
        }
        catch
        {
            return new();
        }
    }

    private static void Save(ProjectsData data)
    {
        AppConfig.EnsureFolder();
        File.WriteAllText(FilePath, JsonSerializer.Serialize(data));
    }

    public static string Add(string name, string? folderPath, string? note)
    {
        var data = Load();
        var project = new Project
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = string.IsNullOrWhiteSpace(name) ? "New project" : name.Trim(),
            FolderPath = string.IsNullOrWhiteSpace(folderPath) ? null : folderPath.Trim(),
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        data.Items.Add(project);
        data.CurrentId ??= project.Id;
        Save(data);
        return project.Id;
    }

    public static void SetCurrent(string id)
    {
        var data = Load();
        if (data.Items.Any(p => p.Id == id)) data.CurrentId = id;
        Save(data);
    }

    public static void Delete(string id)
    {
        var data = Load();
        data.Items.RemoveAll(p => p.Id == id);
        if (data.CurrentId == id) data.CurrentId = data.Items.FirstOrDefault()?.Id;
        Save(data);
    }

    public static void Update(string id, string name, string? folderPath, string? note)
    {
        var data = Load();
        var p = data.Items.FirstOrDefault(x => x.Id == id);
        if (p == null) return;
        if (!string.IsNullOrWhiteSpace(name)) p.Name = name.Trim();
        p.FolderPath = string.IsNullOrWhiteSpace(folderPath) ? null : folderPath.Trim();
        p.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        Save(data);
    }

    public static void SetColor(string id, string hex)
    {
        var data = Load();
        var p = data.Items.FirstOrDefault(x => x.Id == id);
        if (p == null) return;
        p.Color = hex;
        Save(data);
    }
}
