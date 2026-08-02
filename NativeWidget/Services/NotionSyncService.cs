using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using NativeWidget.Models;

namespace NativeWidget.Services;

/// Sync between local Notes and a Notion database, last-write-wins by comparing local
/// NoteMeta.UpdatedAt against Notion's own last_edited_time (so edits made directly in the
/// Notion UI are picked up too, not just ones this service made) - with one asymmetry:
///
/// **Title is 2-way. Body is push-only (local -> Notion) for a note that already exists
/// locally.** Pulling a body change back down used to overwrite the local .xaml file
/// unconditionally - the first real test of this (pasting an image into a note) proved that
/// destructive in practice: the image never reached Notion (plain-text-only sync doesn't
/// capture it), so the very next sync pass pulled Notion's image-less text back down and
/// wiped the image from the local copy too. A brand-new page created directly in Notion
/// (no matching local note yet) still pulls its full body on first sight - there's no local
/// content to destroy at that point.
///
/// Phase 1 scope, deliberately: plain text only (no bold/italic/images - those live in the
/// local .xaml FlowDocument but Notion's block model doesn't map 1:1, so round-tripping rich
/// formatting is left for later). No delete propagation either way - a note deleted on one
/// side is never auto-deleted on the other, since silently destroying data on a timer is a
/// worse failure mode than a stale copy sitting around.
///
/// The note body lives as the Notion page's actual block content (paragraph blocks), not a
/// database property - a property value only renders as a cramped table-cell string in
/// Notion's UI, unreadable for anything beyond a few words. Only Title and LocalId are
/// database properties; opening a row's page in Notion shows the real body like any other
/// Notion page.
public static class NotionSyncService
{
    private const string ApiBase = "https://api.notion.com/v1";
    private const string NotionVersion = "2022-06-28";
    private static readonly HttpClient Http = new();

    // Without this, JsonContent.Create silently lowercases the first letter of every C#
    // anonymous-type property name (Title -> title, LocalId -> localId) - harmless for the
    // snake_case Notion API fields (parent, page_id, rich_text...) since those were already
    // lowercase in the C# source, but it corrupted the *custom* database property names
    // (Title/LocalId) into ones QueryAllAsync's GetProperty("Title") etc. could never find,
    // throwing KeyNotFoundException on every sync pass.
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = null };

    private static HttpRequestMessage Req(HttpMethod method, string path, object? body = null)
    {
        var req = new HttpRequestMessage(method, $"{ApiBase}{path}");
        req.Headers.Add("Notion-Version", NotionVersion);
        if (body != null) req.Content = JsonContent.Create(body, options: JsonOptions);
        return req;
    }

    private static async Task<JsonElement> CallAsync(AppConfig cfg, HttpMethod method, string path, object? body = null)
    {
        var req = Req(method, path, body);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", cfg.NotionToken);
        var res = await Http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException($"Notion {(int)res.StatusCode}: {text}");
        return JsonSerializer.Deserialize<JsonElement>(text);
    }

    private static async Task EnsureDatabaseAsync(AppConfig cfg)
    {
        if (!string.IsNullOrEmpty(cfg.NotionDatabaseId) && await HasExpectedSchemaAsync(cfg)) return;

        if (string.IsNullOrEmpty(cfg.NotionParentPageId))
            throw new InvalidOperationException("Chưa điền Parent Page ID trong Settings.");

        var body = new
        {
            parent = new { type = "page_id", page_id = cfg.NotionParentPageId },
            title = new object[] { new { type = "text", text = new { content = "NativeWidget Notes" } } },
            properties = new
            {
                Title = new { title = new { } },
                LocalId = new { rich_text = new { } },
            },
        };
        var db = await CallAsync(cfg, HttpMethod.Post, "/databases", body);
        cfg.NotionDatabaseId = db.GetProperty("id").GetString()!;
        cfg.Save();
    }

    // Guards against a database that was created with a broken schema surviving in
    // NotionDatabaseId forever (exactly what happened before this method existed - a JSON
    // casing bug created a database with lowercase property keys, and every sync pass after
    // that just kept throwing against the same broken cached ID). A mismatch here means
    // create a fresh database instead of trying to patch the old one's schema.
    private static async Task<bool> HasExpectedSchemaAsync(AppConfig cfg)
    {
        try
        {
            var db = await CallAsync(cfg, HttpMethod.Get, $"/databases/{cfg.NotionDatabaseId}");
            var props = db.GetProperty("properties");
            return props.TryGetProperty("Title", out _) && props.TryGetProperty("LocalId", out _);
        }
        catch
        {
            return false;
        }
    }

    private record RemotePage(string PageId, string LocalId, string Title, long UpdatedAtUnix);

    private static async Task<List<RemotePage>> QueryAllAsync(AppConfig cfg)
    {
        var pages = new List<RemotePage>();
        string? cursor = null;
        do
        {
            object body = cursor == null ? new { } : new { start_cursor = cursor };
            var res = await CallAsync(cfg, HttpMethod.Post, $"/databases/{cfg.NotionDatabaseId}/query", body);
            foreach (var page in res.GetProperty("results").EnumerateArray())
            {
                var props = page.GetProperty("properties");
                var title = TitleText(props.GetProperty("Title"));
                var localId = RichText(props.GetProperty("LocalId"));
                var lastEdited = DateTimeOffset.Parse(page.GetProperty("last_edited_time").GetString()!).ToUnixTimeSeconds();
                pages.Add(new RemotePage(page.GetProperty("id").GetString()!, localId, title, lastEdited));
            }
            cursor = res.TryGetProperty("has_more", out var hm) && hm.GetBoolean()
                ? res.GetProperty("next_cursor").GetString() : null;
        } while (cursor != null);
        return pages;
    }

    private static string TitleText(JsonElement titleProp)
    {
        var arr = titleProp.GetProperty("title");
        return string.Concat(arr.EnumerateArray().Select(t => t.GetProperty("plain_text").GetString()));
    }

    private static string RichText(JsonElement richTextProp)
    {
        var arr = richTextProp.GetProperty("rich_text");
        return string.Concat(arr.EnumerateArray().Select(t => t.GetProperty("plain_text").GetString()));
    }

    // Notion rejects a single rich_text segment over ~2000 chars - split a long line into
    // multiple segments in the same block instead of hitting that limit.
    private static object[] ToRichText(string text)
    {
        if (text.Length == 0) return Array.Empty<object>();
        var chunks = new List<object>();
        for (var i = 0; i < text.Length; i += 1900)
            chunks.Add(new { text = new { content = text.Substring(i, Math.Min(1900, text.Length - i)) } });
        return chunks.ToArray();
    }

    private static object ParagraphBlock(string line) =>
        new { @object = "block", type = "paragraph", paragraph = new { rich_text = ToRichText(line) } };

    private static async Task<string> GetPageBodyAsync(AppConfig cfg, string pageId)
    {
        var lines = new List<string>();
        string? cursor = null;
        do
        {
            var qs = cursor == null ? "" : $"?start_cursor={cursor}";
            var res = await CallAsync(cfg, HttpMethod.Get, $"/blocks/{pageId}/children{qs}");
            foreach (var block in res.GetProperty("results").EnumerateArray())
            {
                if (block.GetProperty("type").GetString() != "paragraph") continue;
                lines.Add(RichText(block.GetProperty("paragraph")));
            }
            cursor = res.TryGetProperty("has_more", out var hm) && hm.GetBoolean()
                ? res.GetProperty("next_cursor").GetString() : null;
        } while (cursor != null);
        return string.Join('\n', lines);
    }

    // Notion has no "replace all children" call - clear the old body block-by-block, then
    // append the new one. Fine for note-sized text; not something you'd want for huge pages.
    private static async Task ReplacePageBodyAsync(AppConfig cfg, string pageId, string content)
    {
        var existing = await CallAsync(cfg, HttpMethod.Get, $"/blocks/{pageId}/children");
        foreach (var block in existing.GetProperty("results").EnumerateArray())
            await CallAsync(cfg, HttpMethod.Delete, $"/blocks/{block.GetProperty("id").GetString()}");

        await AppendBodyAsync(cfg, pageId, content);
    }

    // Notion caps children arrays at 100 blocks per call - chunk long note bodies across
    // multiple append calls instead of failing on anything over ~100 lines.
    private static async Task AppendBodyAsync(AppConfig cfg, string pageId, string content)
    {
        var lines = content.Length == 0 ? new[] { "" } : content.Split('\n');
        foreach (var chunk in lines.Chunk(90))
        {
            var body = new { children = chunk.Select(ParagraphBlock).ToArray() };
            await CallAsync(cfg, HttpMethod.Patch, $"/blocks/{pageId}/children", body);
        }
    }

    private static async Task PushCreateAsync(AppConfig cfg, string localId, string title, string content)
    {
        var body = new
        {
            parent = new { database_id = cfg.NotionDatabaseId },
            properties = new
            {
                Title = new { title = new object[] { new { text = new { content = title } } } },
                LocalId = new { rich_text = new object[] { new { text = new { content = localId } } } },
            },
            children = (content.Length == 0 ? new[] { "" } : content.Split('\n')).Take(90).Select(ParagraphBlock).ToArray(),
        };
        var page = await CallAsync(cfg, HttpMethod.Post, "/pages", body);
        // A note over ~90 lines needs the rest appended after creation - children on the
        // initial POST is capped at 100 blocks same as any other append call.
        var lines = content.Split('\n');
        if (lines.Length > 90)
            await AppendBodyAsync(cfg, page.GetProperty("id").GetString()!, string.Join('\n', lines.Skip(90)));
    }

    private static async Task SetLocalIdAsync(AppConfig cfg, string pageId, string localId)
    {
        var body = new { properties = new { LocalId = new { rich_text = ToRichText(localId) } } };
        await CallAsync(cfg, HttpMethod.Patch, $"/pages/{pageId}", body);
    }

    private static async Task PushUpdateAsync(AppConfig cfg, string pageId, string title, string content)
    {
        var body = new { properties = new { Title = new { title = new object[] { new { text = new { content = title } } } } } };
        await CallAsync(cfg, HttpMethod.Patch, $"/pages/{pageId}", body);
        await ReplacePageBodyAsync(cfg, pageId, content);
    }

    /// Runs one full sync pass. Safe to call on a timer - each pass is independent and
    /// idempotent (re-running with nothing changed does nothing).
    public static async Task SyncOnceAsync(AppConfig cfg)
    {
        if (!cfg.NotionSyncEnabled || string.IsNullOrEmpty(cfg.NotionToken)) return;
        await EnsureDatabaseAsync(cfg);

        var remote = await QueryAllAsync(cfg);
        var remoteByLocalId = remote.Where(r => r.LocalId.Length > 0).ToDictionary(r => r.LocalId);
        var local = NotesService.LoadIndex();
        var localIds = local.Select(m => m.Id).ToHashSet();

        foreach (var meta in local)
        {
            if (remoteByLocalId.TryGetValue(meta.Id, out var match))
            {
                // >2s slack: property write timestamps and local clock ticks can jitter by a
                // second or two even when nothing actually changed on either side.
                if (meta.UpdatedAt > match.UpdatedAtUnix + 2)
                {
                    var text = NotesService.GetPlainText(meta.Id);
                    await PushUpdateAsync(cfg, match.PageId, meta.Title, text);
                }
                else if (match.UpdatedAtUnix > meta.UpdatedAt + 2)
                {
                    // Title-only, deliberately - see ApplyRemoteTitle's doc comment. Pulling
                    // the body back down would overwrite any local rich content (images,
                    // bold/italic) that Notion never received in the first place, since this
                    // sync is plain-text-only going up.
                    NotesService.ApplyRemoteTitle(meta.Id, match.Title, match.UpdatedAtUnix);
                }
            }
            else
            {
                var text = NotesService.GetPlainText(meta.Id);
                await PushCreateAsync(cfg, meta.Id, meta.Title, text);
            }
        }

        // A remote page whose LocalId isn't a note we know about was created straight in
        // Notion - mirror it in as a new local note (see CreateNoteFromRemote's doc comment
        // for why it reuses the Notion page ID as the local note ID), then write that ID back
        // to the page's LocalId property so the next pass matches it by LocalId instead of
        // re-running this branch and duplicating the page.
        foreach (var r in remote)
        {
            if (r.LocalId.Length > 0 && localIds.Contains(r.LocalId)) continue;
            var body = await GetPageBodyAsync(cfg, r.PageId);
            NotesService.CreateNoteFromRemote(r.PageId, r.Title, body, r.UpdatedAtUnix);
            await SetLocalIdAsync(cfg, r.PageId, r.PageId);
        }
    }
}
