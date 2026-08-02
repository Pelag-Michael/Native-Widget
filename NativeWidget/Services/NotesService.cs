using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Documents;
using System.Windows.Markup;
using NativeWidget.Models;

namespace NativeWidget.Services;

public class NoteMeta
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Preview { get; set; } = "";
    public long UpdatedAt { get; set; }

    /// True once the user has renamed the note by hand, which stops the title from being
    /// overwritten with the note's first line on every save.
    public bool TitleIsCustom { get; set; }

    /// Hex color tag (e.g. "#4A7DFF"), or empty for the default look.
    public string Color { get; set; } = "";

    /// Free-form labels, distinct from the project tag (ItemProjectTagsService) - a note can
    /// carry several of these, a project assignment is at most one.
    public List<string> Tags { get; set; } = new();

    /// ID of the CountdownTimer created for this note's reminder, if any - the Timers widget
    /// shows it automatically since it just lists every CountdownTimer, no note-specific code
    /// needed there. Null once the reminder fires and is dismissed, or is cancelled.
    public string? ReminderTimerId { get; set; }
}

public static class NotesService
{
    private static string NotesFolder => AppConfig.TokenPath("notes");
    private static string IndexPath => Path.Combine(NotesFolder, "index.json");
    private static string DocPath(string id) => Path.Combine(NotesFolder, $"{id}.xaml");

    public static List<NoteMeta> LoadIndex()
    {
        EnsureMigrated();
        try
        {
            return JsonSerializer.Deserialize<List<NoteMeta>>(File.ReadAllText(IndexPath)) ?? new();
        }
        catch
        {
            return new();
        }
    }

    private static void SaveIndex(List<NoteMeta> index)
    {
        Directory.CreateDirectory(NotesFolder);
        File.WriteAllText(IndexPath, JsonSerializer.Serialize(index));
    }

    public static FlowDocument LoadNote(string id)
    {
        try
        {
            using var stream = File.OpenRead(DocPath(id));
            if (XamlReader.Load(stream) is FlowDocument doc) return doc;
        }
        catch { }
        return new FlowDocument(new Paragraph());
    }

    public static string CreateNote()
    {
        var id = Guid.NewGuid().ToString("N");
        var index = LoadIndex();
        index.Insert(0, new NoteMeta { Id = id, Title = "Ghi chú mới", Preview = "", UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
        SaveIndex(index);
        SaveNote(id, new FlowDocument(new Paragraph()));
        return id;
    }

    public static void SaveNote(string id, FlowDocument doc)
    {
        Directory.CreateDirectory(NotesFolder);
        File.WriteAllText(DocPath(id), XamlWriter.Save(doc));

        var plain = new TextRange(doc.ContentStart, doc.ContentEnd).Text.Trim();
        var firstLine = plain.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        var title = firstLine.Length > 40 ? firstLine[..40] + "…" : firstLine;
        if (string.IsNullOrWhiteSpace(title)) title = "Ghi chú trống";
        var preview = plain.Length > 80 ? plain[..80] : plain;

        var index = LoadIndex();
        var meta = index.FirstOrDefault(m => m.Id == id);
        if (meta == null)
        {
            meta = new NoteMeta { Id = id };
            index.Insert(0, meta);
        }
        if (!meta.TitleIsCustom) meta.Title = title;
        meta.Preview = preview;
        meta.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        SaveIndex(index.OrderByDescending(m => m.UpdatedAt).ToList());
    }

    public static string GetPlainText(string id)
    {
        var doc = LoadNote(id);
        return new TextRange(doc.ContentStart, doc.ContentEnd).Text.TrimEnd();
    }

    /// Used by NotionSyncService to pull a change made on the Notion side. Unlike SaveNote,
    /// this does NOT recompute the title from the body's first line - the remote title is
    /// authoritative here, since it may already differ from the body (renamed on either side).
    public static void ApplyRemoteUpdate(string id, string title, string plainBody, long updatedAtUnix)
    {
        WritePlainTextBody(id, plainBody);
        var index = LoadIndex();
        var meta = index.FirstOrDefault(m => m.Id == id);
        if (meta == null) return;
        meta.Title = title;
        meta.TitleIsCustom = true;
        meta.Preview = plainBody.Length > 80 ? plainBody[..80] : plainBody;
        meta.UpdatedAt = updatedAtUnix;
        SaveIndex(index.OrderByDescending(m => m.UpdatedAt).ToList());
    }

    /// Title-only counterpart to ApplyRemoteUpdate - used for a note that already exists
    /// locally, where overwriting the body would destroy local-only rich content (images,
    /// bold/italic) that never made it to Notion in the first place, since Notion sync is
    /// plain-text-only. Renaming carries no such risk, so it stays 2-way; the body sync
    /// direction was made push-only (local -> Notion) after exactly this happened once.
    public static void ApplyRemoteTitle(string id, string title, long updatedAtUnix)
    {
        var index = LoadIndex();
        var meta = index.FirstOrDefault(m => m.Id == id);
        if (meta == null) return;
        meta.Title = title;
        meta.TitleIsCustom = true;
        meta.UpdatedAt = updatedAtUnix;
        SaveIndex(index.OrderByDescending(m => m.UpdatedAt).ToList());
    }

    /// A page created directly in Notion (not yet known locally) becomes a new local note -
    /// reusing the Notion page ID as the local note ID keeps the two sides mapped 1:1 without
    /// a separate lookup table.
    public static void CreateNoteFromRemote(string id, string title, string plainBody, long updatedAtUnix)
    {
        var index = LoadIndex();
        if (index.Any(m => m.Id == id))
        {
            ApplyRemoteUpdate(id, title, plainBody, updatedAtUnix);
            return;
        }
        index.Insert(0, new NoteMeta
        {
            Id = id,
            Title = title,
            TitleIsCustom = true,
            Preview = plainBody.Length > 80 ? plainBody[..80] : plainBody,
            UpdatedAt = updatedAtUnix,
        });
        SaveIndex(index.OrderByDescending(m => m.UpdatedAt).ToList());
        WritePlainTextBody(id, plainBody);
    }

    private static void WritePlainTextBody(string id, string plainBody)
    {
        var doc = new FlowDocument();
        var lines = plainBody.Length == 0 ? new[] { "" } : plainBody.Split('\n');
        foreach (var line in lines) doc.Blocks.Add(new Paragraph(new Run(line.TrimEnd('\r'))));
        Directory.CreateDirectory(NotesFolder);
        File.WriteAllText(DocPath(id), XamlWriter.Save(doc));
    }

    public static void RenameNote(string id, string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        var index = LoadIndex();
        var meta = index.FirstOrDefault(m => m.Id == id);
        if (meta == null) return;
        meta.Title = title.Trim();
        meta.TitleIsCustom = true;
        SaveIndex(index);
    }

    public static string GetTitle(string id) =>
        LoadIndex().FirstOrDefault(m => m.Id == id)?.Title ?? "";

    public static void SetColor(string id, string hex)
    {
        var index = LoadIndex();
        var meta = index.FirstOrDefault(m => m.Id == id);
        if (meta == null) return;
        meta.Color = hex;
        SaveIndex(index);
    }

    public static void SetTags(string id, List<string> tags)
    {
        var index = LoadIndex();
        var meta = index.FirstOrDefault(m => m.Id == id);
        if (meta == null) return;
        meta.Tags = tags;
        SaveIndex(index);
    }

    public static void SetReminderTimerId(string id, string? timerId)
    {
        var index = LoadIndex();
        var meta = index.FirstOrDefault(m => m.Id == id);
        if (meta == null) return;
        meta.ReminderTimerId = timerId;
        SaveIndex(index);
    }

    public static void DeleteNote(string id)
    {
        var index = LoadIndex();
        index.RemoveAll(m => m.Id == id);
        SaveIndex(index);
        try { File.Delete(DocPath(id)); } catch { }
    }

    // Migrates the old single-note format (notes.xaml, or legacy notes.txt) into the
    // first entry of the new multi-note index, the first time this runs after upgrading.
    private static void EnsureMigrated()
    {
        if (File.Exists(IndexPath)) return;
        Directory.CreateDirectory(NotesFolder);

        FlowDocument? legacyDoc = null;
        var legacyXaml = AppConfig.TokenPath("notes.xaml");
        var legacyTxt = AppConfig.TokenPath("notes.txt");
        try
        {
            if (File.Exists(legacyXaml))
            {
                using var stream = File.OpenRead(legacyXaml);
                legacyDoc = XamlReader.Load(stream) as FlowDocument;
            }
            else if (File.Exists(legacyTxt))
            {
                legacyDoc = new FlowDocument(new Paragraph(new Run(File.ReadAllText(legacyTxt))));
            }
        }
        catch { }

        if (legacyDoc != null)
        {
            var id = Guid.NewGuid().ToString("N");
            SaveIndex(new List<NoteMeta> { new() { Id = id, Title = "Ghi chú", UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() } });
            SaveNote(id, legacyDoc);
        }
        else
        {
            SaveIndex(new List<NoteMeta>());
        }
    }
}
