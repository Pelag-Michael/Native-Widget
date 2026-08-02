using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Windows.Documents;
using NativeWidget.Models;
using NativeWidget.Services;

internal static class NotionSmoke
{
    private const string ApiBase = "https://api.notion.com/v1";
    private const string Version = "2026-03-11";
    private static readonly HttpClient Http = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = null };

    public static async Task Run(string imagePath)
    {
        var real = AppConfig.Load();
        if (real.NotionToken.Length == 0 || real.NotionParentPageId.Length == 0)
        {
            Console.WriteLine("SKIP Notion smoke test: token or parent page is not configured.");
            return;
        }

        var testRoot = Path.Combine(Path.GetTempPath(), "nativewidget-notion-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(testRoot, "notes"));
        Environment.SetEnvironmentVariable("NATIVEWIDGET_DATA_DIR", testRoot);
        var noteId = "smoke-" + Guid.NewGuid().ToString("N");
        var markdown = string.Join('\n',
            "# Smoke heading",
            "Paragraph with **bold**, *italic* and ~~strike~~.",
            "- bullet",
            "1. number",
            "- [x] todo",
            "> quote",
            "```",
            "var smoke = true;",
            "```",
            $"![]({new Uri(imagePath).AbsoluteUri})");
        File.WriteAllText(Path.Combine(testRoot, "notes", noteId + ".md"), markdown);
        File.WriteAllText(Path.Combine(testRoot, "notes", "index.json"), JsonSerializer.Serialize(
            new List<NoteMeta>
            {
                new()
                {
                    Id = noteId,
                    Title = "NativeWidget sync smoke",
                    TitleIsCustom = true,
                    UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                },
            }));

        var config = new AppConfig
        {
            NotionToken = real.NotionToken,
            NotionParentPageId = real.NotionParentPageId,
            NotionSyncEnabled = true,
        };

        try
        {
            await NotionSyncService.SyncOnceAsync(config);
            await NotionSyncService.SyncOnceAsync(config);
            var meta = NotesService.LoadIndex().Single(note => note.Id == noteId);
            if (meta.LastSyncedHash.Length == 0)
                throw new InvalidOperationException("Smoke sync did not store a content hash.");

            var pageId = await GetOnlyPageId(config);
            var initialBlocks = await GetBlocks(config, pageId);
            RequireTypes(initialBlocks, "heading_1", "paragraph", "bulleted_list_item",
                "numbered_list_item", "to_do", "quote", "code", "image");

            await Call(config, HttpMethod.Patch, $"/blocks/{pageId}/children", new
            {
                children = new object[]
                {
                    new
                    {
                        @object = "block",
                        type = "heading_2",
                        heading_2 = new { rich_text = RichText("Remote edit") },
                    },
                    new
                    {
                        @object = "block",
                        type = "toggle",
                        toggle = new { rich_text = RichText("Keep this unsupported block") },
                    },
                },
            });

            await NotionSyncService.SyncOnceAsync(config);
            var pulled = NotesService.GetMarkdown(noteId);
            if (!pulled.Contains("## Remote edit", StringComparison.Ordinal) ||
                !pulled.Contains("![](file:", StringComparison.Ordinal))
                throw new InvalidOperationException("Remote block/image pull did not localize correctly.");

            NotesService.ApplyRemoteUpdate(noteId, meta.Title, pulled + "\n> Local edit",
                DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await NotionSyncService.SyncOnceAsync(config);
            var finalBlocks = await GetBlocks(config, pageId);
            RequireTypes(finalBlocks, "toggle", "quote", "image");
            var finalText = string.Join(' ', finalBlocks.Select(BlockText));
            if (!finalText.Contains("Local edit", StringComparison.Ordinal) ||
                !finalText.Contains("Keep this unsupported block", StringComparison.Ordinal))
                throw new InvalidOperationException("Push or unsupported-block preservation failed.");

            // Change both sides from the same shared hash. The remote version must survive,
            // while the local draft is retained as a conflict copy instead of overwriting it.
            await Call(config, HttpMethod.Patch, $"/blocks/{pageId}/children", new
            {
                children = new object[]
                {
                    new
                    {
                        @object = "block",
                        type = "paragraph",
                        paragraph = new { rich_text = RichText("Remote conflict survives") },
                    },
                },
            });
            NotesService.ApplyRemoteUpdate(noteId, meta.Title, pulled + "\n> Local conflict retained",
                DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await NotionSyncService.SyncOnceAsync(config);

            var conflictResolved = NotesService.GetMarkdown(noteId);
            if (!conflictResolved.Contains("Remote conflict survives", StringComparison.Ordinal) ||
                conflictResolved.Contains("Local conflict retained", StringComparison.Ordinal))
                throw new InvalidOperationException("Two-sided conflict did not preserve the remote version.");
            var conflictCopy = Directory.GetFiles(Path.Combine(testRoot, "notes"),
                noteId + ".conflict-*.md").SingleOrDefault();
            if (conflictCopy == null ||
                !File.ReadAllText(conflictCopy).Contains("Local conflict retained", StringComparison.Ordinal))
                throw new InvalidOperationException("Two-sided conflict did not retain the local draft.");

            Console.WriteLine("PASS live Notion throwaway push/pull, conflict, image and preservation smoke test");
        }
        finally
        {
            if (config.NotionDatabaseId.Length > 0)
            {
                try { await Call(config, HttpMethod.Patch, $"/databases/{config.NotionDatabaseId}", new { in_trash = true }); }
                catch (Exception ex) { Console.WriteLine("WARN throwaway database cleanup failed: " + ex.Message); }
            }
            Environment.SetEnvironmentVariable("NATIVEWIDGET_DATA_DIR", null);
        }
    }

    private static async Task<string> GetOnlyPageId(AppConfig config)
    {
        var response = await Call(config, HttpMethod.Post,
            $"/data_sources/{config.NotionDataSourceId}/query", new { });
        var results = response.GetProperty("results").EnumerateArray().ToList();
        if (results.Count != 1) throw new InvalidOperationException($"Expected 1 smoke page, found {results.Count}.");
        return results[0].GetProperty("id").GetString()!;
    }

    private static async Task<List<JsonElement>> GetBlocks(AppConfig config, string pageId)
    {
        var output = new List<JsonElement>();
        string? cursor = null;
        do
        {
            var query = cursor == null ? "" : $"?start_cursor={Uri.EscapeDataString(cursor)}";
            var response = await Call(config, HttpMethod.Get, $"/blocks/{pageId}/children{query}");
            output.AddRange(response.GetProperty("results").EnumerateArray());
            cursor = response.TryGetProperty("has_more", out var more) && more.GetBoolean()
                ? response.GetProperty("next_cursor").GetString() : null;
        } while (cursor != null);
        return output;
    }

    private static void RequireTypes(IEnumerable<JsonElement> blocks, params string[] expected)
    {
        var types = blocks.Select(block => block.GetProperty("type").GetString()).ToHashSet();
        foreach (var type in expected)
            if (!types.Contains(type))
                throw new InvalidOperationException($"Smoke page is missing block type {type}.");
    }

    private static string BlockText(JsonElement block)
    {
        var type = block.GetProperty("type").GetString()!;
        if (!block.TryGetProperty(type, out var value) ||
            !value.TryGetProperty("rich_text", out var richText)) return "";
        return string.Concat(richText.EnumerateArray().Select(item =>
            item.TryGetProperty("plain_text", out var text) ? text.GetString() : ""));
    }

    private static object[] RichText(string text) =>
        new object[] { new { type = "text", text = new { content = text } } };

    private static async Task<JsonElement> Call(AppConfig config, HttpMethod method,
        string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, ApiBase + path);
        request.Headers.Add("Notion-Version", Version);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.NotionToken);
        if (body != null) request.Content = JsonContent.Create(body, options: JsonOptions);
        using var response = await Http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Notion smoke {(int)response.StatusCode}: {text}");
        return JsonSerializer.Deserialize<JsonElement>(text);
    }
}
