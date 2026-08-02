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

    /// SHA-256 of the canonical Markdown at the last successful Notion sync. This lets the
    /// sync distinguish a one-sided edit from a true conflict without trusting clocks alone.
    public string LastSyncedHash { get; set; } = "";

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
    private static string MarkdownPath(string id) => Path.Combine(NotesFolder, $"{id}.md");
    private static string LegacyXamlPath(string id) => Path.Combine(NotesFolder, $"{id}.xaml");

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
        EnsureMigrated();
        try
        {
            if (File.Exists(MarkdownPath(id)))
                return FlowDocumentMarkdownConverter.FromMarkdown(File.ReadAllText(MarkdownPath(id)));

            using var stream = File.OpenRead(LegacyXamlPath(id));
            if (XamlReader.Load(stream) is FlowDocument legacy) return legacy;
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
        File.WriteAllText(MarkdownPath(id), FlowDocumentMarkdownConverter.ToMarkdown(doc));

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

    public static string GetMarkdown(string id)
    {
        EnsureMigrated();
        try { return File.ReadAllText(MarkdownPath(id)); }
        catch { return FlowDocumentMarkdownConverter.ToMarkdown(LoadNote(id)); }
    }

    /// Used by NotionSyncService to pull a change made on the Notion side. Unlike SaveNote,
    /// this does NOT recompute the title from the body's first line - the remote title is
    /// authoritative here, since it may already differ from the body (renamed on either side).
    public static void ApplyRemoteUpdate(string id, string title, string markdownBody, long updatedAtUnix,
        string syncedHash = "", bool backupLocal = false)
    {
        if (backupLocal && File.Exists(MarkdownPath(id)))
        {
            var backup = Path.Combine(NotesFolder,
                $"{id}.conflict-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.md");
            try { File.Copy(MarkdownPath(id), backup, overwrite: false); } catch { }
        }
        WriteMarkdownBody(id, markdownBody);
        var plainBody = PlainText(markdownBody);
        var index = LoadIndex();
        var meta = index.FirstOrDefault(m => m.Id == id);
        if (meta == null) return;
        meta.Title = title;
        meta.TitleIsCustom = true;
        meta.Preview = plainBody.Length > 80 ? plainBody[..80] : plainBody;
        meta.UpdatedAt = updatedAtUnix;
        if (syncedHash.Length > 0) meta.LastSyncedHash = syncedHash;
        SaveIndex(index.OrderByDescending(m => m.UpdatedAt).ToList());
    }

    /// Title-only counterpart to ApplyRemoteUpdate, used when body hashes already match and
    /// only the Notion title won the timestamp comparison.
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
    public static void CreateNoteFromRemote(string id, string title, string markdownBody, long updatedAtUnix,
        string syncedHash = "")
    {
        var index = LoadIndex();
        if (index.Any(m => m.Id == id))
        {
            ApplyRemoteUpdate(id, title, markdownBody, updatedAtUnix, syncedHash);
            return;
        }
        var plainBody = PlainText(markdownBody);
        index.Insert(0, new NoteMeta
        {
            Id = id,
            Title = title,
            TitleIsCustom = true,
            Preview = plainBody.Length > 80 ? plainBody[..80] : plainBody,
            UpdatedAt = updatedAtUnix,
            LastSyncedHash = syncedHash,
        });
        SaveIndex(index.OrderByDescending(m => m.UpdatedAt).ToList());
        WriteMarkdownBody(id, markdownBody);
    }

    private static void WriteMarkdownBody(string id, string markdownBody)
    {
        Directory.CreateDirectory(NotesFolder);
        File.WriteAllText(MarkdownPath(id), markdownBody);
    }

    private static string PlainText(string markdown)
        => WpfSta.Run(() =>
    {
        var document = FlowDocumentMarkdownConverter.FromMarkdown(markdown);
        return new TextRange(document.ContentStart, document.ContentEnd).Text.Trim();
    });

    public static void RenameNote(string id, string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        var index = LoadIndex();
        var meta = index.FirstOrDefault(m => m.Id == id);
        if (meta == null) return;
        meta.Title = title.Trim();
        meta.TitleIsCustom = true;
        meta.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        SaveIndex(index);
    }

    public static string GetTitle(string id) =>
        LoadIndex().FirstOrDefault(m => m.Id == id)?.Title ?? "";

    public static void MarkSynced(string id, string hash)
    {
        var index = LoadIndex();
        var meta = index.FirstOrDefault(m => m.Id == id);
        if (meta == null) return;
        meta.LastSyncedHash = hash;
        SaveIndex(index);
    }

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
        try { File.Delete(MarkdownPath(id)); } catch { }
    }

    // Migrates the old single-note format (notes.xaml, or legacy notes.txt) into the
    // first entry of the new multi-note index, the first time this runs after upgrading.
    private static void EnsureMigrated()
    {
        Directory.CreateDirectory(NotesFolder);
        if (!File.Exists(IndexPath)) MigrateSingleLegacyNote();
        MigrateXamlNotes();
    }

    private static void MigrateSingleLegacyNote()
    {
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

    private static void MigrateXamlNotes()
    {
        List<NoteMeta> index;
        try { index = JsonSerializer.Deserialize<List<NoteMeta>>(File.ReadAllText(IndexPath)) ?? new(); }
        catch { return; }

        // Existence of the .md is the migration marker, so an interrupted pass safely
        // resumes next launch. The original .xaml remains a byte-for-byte backup.
        foreach (var meta in index)
        {
            var markdownPath = MarkdownPath(meta.Id);
            var xamlPath = LegacyXamlPath(meta.Id);
            if (File.Exists(markdownPath) || !File.Exists(xamlPath)) continue;
            try
            {
                using var stream = File.OpenRead(xamlPath);
                if (XamlReader.Load(stream) is FlowDocument document)
                    File.WriteAllText(markdownPath, FlowDocumentMarkdownConverter.ToMarkdown(document));
            }
            catch { }
        }
    }
}
