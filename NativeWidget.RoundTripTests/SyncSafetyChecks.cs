using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using NativeWidget;
using NativeWidget.Models;
using NativeWidget.Services;

internal static class SyncSafetyChecks
{
    public static async Task RunAsync()
    {
        var previous = Environment.GetEnvironmentVariable("NATIVEWIDGET_DATA_DIR");
        var root = Path.Combine(Path.GetTempPath(), "nativewidget-sync-safety-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("NATIVEWIDGET_DATA_DIR", root);
        try
        {
            foreach (var status in new[] { 403, 404, 429, 502 })
            {
                foreach (var failSchema in new[] { false, true })
                {
                    var config = Config();
                    using var handler = new FakeHandler(request =>
                    {
                        Require(request.Method == HttpMethod.Get, "An API error must never create a replacement table.");
                        return !failSchema || request.RequestUri!.AbsolutePath.Contains("data_sources")
                            ? Json("{}", status) : Database();
                    });
                    using var client = new HttpClient(handler);
                    await ExpectFailure(() => NotionSyncService.WithTransportAsync(client,
                        () => NotionSyncService.EnsureDataSourceAsync(config)));
                    Require(config.NotionDatabaseId == "saved-db" && config.NotionDataSourceId == "saved-source",
                        "Read failure changed table identity.");
                }
            }

            await VerifyDiscoveryAsync();
            await VerifyCreationIdentityAsync();
            await VerifyRemoteIdentityAsync();
            await VerifyStorageAsync(root);
            VerifyNestedBlocks();
            Require(SettingsWindow.NormalizeNotionId("0123456789abcdef0123456789abcdef") ==
                    SettingsWindow.NormalizeNotionId("01234567-89ab-cdef-0123-456789abcdef"),
                "Equivalent Notion IDs reset database identity.");
            Console.WriteLine("PASS sync safety: API failures, database discovery, creation identity, atomic storage, concurrent index updates, corrupt index, missing bodies, stale pulls, nested-block preservation.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("NATIVEWIDGET_DATA_DIR", previous);
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task VerifyDiscoveryAsync()
    {
        var config = Config();
        config.NotionDatabaseId = config.NotionDataSourceId = "";
        using var handler = new FakeHandler(request =>
        {
            Require(request.Method == HttpMethod.Get, "Discovery created a duplicate database.");
            if (request.RequestUri!.AbsolutePath.Contains("children"))
                return request.RequestUri.Query.Contains("start_cursor")
                    ? Json("""{"results":[{"id":"saved-db","child_database":{"title":"NativeWidget Notes"}}],"has_more":false,"next_cursor":null}""")
                    : Json("""{"results":[],"has_more":true,"next_cursor":"next"}""");
            return request.RequestUri.AbsolutePath.Contains("data_sources") ? Schema() : Database();
        });
        using var client = new HttpClient(handler);
        await NotionSyncService.WithTransportAsync(client, () => NotionSyncService.EnsureDataSourceAsync(config));
        Require(config.NotionDatabaseId == "saved-db" && AppConfig.Load().NotionDataSourceId == "saved-source",
            "Existing database wasn't reused and persisted.");

        config.NotionDatabaseId = config.NotionDataSourceId = "";
        using var duplicates = new HttpClient(new FakeHandler(_ => Json("""
            {"results":[{"id":"one","child_database":{"title":"NativeWidget Notes"}},
                        {"id":"two","child_database":{"title":"NativeWidget Notes"}}],"has_more":false}
            """)));
        await ExpectFailure(() => NotionSyncService.WithTransportAsync(duplicates,
            () => NotionSyncService.EnsureDataSourceAsync(config)));
        Require(config.NotionDatabaseId == "", "Ambiguous discovery chose a table silently.");

        var wrongSchemaConfig = Config();
        using var wrongSchema = new HttpClient(new FakeHandler(request =>
        {
            Require(request.Method == HttpMethod.Get, "Schema mismatch created a new database.");
            return request.RequestUri!.AbsolutePath.Contains("data_sources")
                ? Json("""{"properties":{"Title":{"type":"rich_text"},"LocalId":{"type":"rich_text"}}}""")
                : Database();
        }));
        await ExpectFailure(() => NotionSyncService.WithTransportAsync(wrongSchema,
            () => NotionSyncService.EnsureDataSourceAsync(wrongSchemaConfig)));
    }

    private static async Task VerifyCreationIdentityAsync()
    {
        var config = Config();
        config.NotionDatabaseId = config.NotionDataSourceId = "";
        var creates = 0;
        using var client = new HttpClient(new FakeHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("children"))
                return Json("""{"results":[],"has_more":false}""");
            if (request.Method == HttpMethod.Post)
            {
                creates++;
                return Json("""{"id":"new-db"}""");
            }
            return Json("{}", 502);
        }));
        await ExpectFailure(() => NotionSyncService.WithTransportAsync(client,
            () => NotionSyncService.EnsureDataSourceAsync(config)));
        Require(creates == 1 && AppConfig.Load().NotionDatabaseId == "new-db",
            "Created database identity was lost when its follow-up read failed.");
        await ExpectFailure(() => NotionSyncService.WithTransportAsync(client,
            () => NotionSyncService.EnsureDataSourceAsync(config)));
        Require(creates == 1, "Retry created another table.");
    }

    private static async Task VerifyStorageAsync(string root)
    {
        NotesService.CreateNoteFromRemote("first", "First", "Original content", 1);
        NotesService.CreateNoteFromRemote("second", "Second", "Other content", 1);
        await Task.WhenAll(Enumerable.Range(0, 30).Select(i => Task.Run(() =>
            NotesService.CreateNoteFromRemote("parallel-" + i, "Parallel", "Body " + i, i + 2))));
        Require(NotesService.LoadIndex().Count == 33, "Concurrent saves lost notes from the index.");
        var indexPath = Path.Combine(root, "notes", "index.json");
        Require(File.Exists(indexPath + ".bak"), "Atomic index backup wasn't retained.");
        var originalIndex = File.ReadAllText(indexPath);
        File.WriteAllText(indexPath, "{broken");
        await ExpectFailure(() => Task.Run(() => NotesService.MarkSynced("first", "hash")));
        Require(File.ReadAllText(indexPath) == "{broken", "Corrupt index was replaced with partial data.");
        File.WriteAllText(indexPath, originalIndex);
        File.Delete(Path.Combine(root, "notes", "second.md"));
        await ExpectFailure(() => Task.Run(() => NotesService.GetMarkdown("second")));

        NotesService.ApplyRemoteUpdate("first", "First", "Remote body", 50, expectedMarkdown: "Original content");
        Require(Directory.GetFiles(Path.Combine(root, "notes"), "first.conflict-*.md").Any(file =>
            File.ReadAllText(file) == "Original content"), "Remote pull discarded original local content.");
        await ExpectFailure(() => Task.Run(() => NotesService.ApplyRemoteUpdate("first", "First", "Stale remote body", 51,
            expectedMarkdown: "Original content")));
        Require(NotesService.GetMarkdown("first") == "Remote body", "A stale sync overwrote newer local content.");

        var configPath = Path.Combine(root, "config.json");
        File.WriteAllText(configPath, "{broken");
        await ExpectFailure(() => Task.Run(AppConfig.Load));
        Require(File.ReadAllText(configPath) == "{broken", "Invalid config reset database identity.");
    }

    private static async Task VerifyRemoteIdentityAsync()
    {
        const string localId = "0123456789abcdef0123456789abcdef";
        var config = Config();
        var simulatePartialUpload = false;
        var recoveryWrites = 0;
        using var client = new HttpClient(new FakeHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/query"))
                return Json("""
                    {"results":[{"id":"page-id","last_edited_time":"2026-10-19T00:00:00Z",
                    "properties":{"Title":{"title":[{"plain_text":"Imported"}]},
                    "LocalId":{"rich_text":[{"plain_text":"0123456789abcdef0123456789abcdef"}]}}}],"has_more":false}
                    """);
            if (simulatePartialUpload && (request.Method == HttpMethod.Patch || request.Method == HttpMethod.Delete))
            {
                recoveryWrites++;
                return Json("{}");
            }
            Require(request.Method == HttpMethod.Get, "Re-import rewrote stable remote identity or content.");
            if (path.Contains("children"))
            {
                if (simulatePartialUpload)
                    return Json("""{"results":[{"id":"partial-block","type":"paragraph","paragraph":{"rich_text":[{"plain_text":"Partial body","annotations":{},"href":null}]}}],"has_more":false}""");
                return Json("""
                    {"results":[{"id":"block-id","type":"paragraph","has_children":false,
                    "paragraph":{"rich_text":[{"plain_text":"Recovered remote body","annotations":{"bold":false,"italic":false,"strikethrough":false},"href":null}]}}],"has_more":false}
                    """);
            }
            return path.Contains("data_sources") ? Schema() : Database();
        }));
        await NotionSyncService.WithTransportAsync(client, () => NotionSyncService.SyncOnceAsync(config));
        Require(NotesService.LoadIndex().Single().Id == localId &&
                NotesService.GetMarkdown(localId) == "Recovered remote body", "Re-import lost the original LocalId.");
        await NotionSyncService.WithTransportAsync(client, () => NotionSyncService.SyncOnceAsync(config));
        Require(NotesService.LoadIndex().Count == 1, "Re-import created a duplicate local note.");
        NotesService.MarkUploadPending(localId, "pending-hash");
        simulatePartialUpload = true;
        await NotionSyncService.WithTransportAsync(client, () => NotionSyncService.SyncOnceAsync(config));
        Require(recoveryWrites >= 2 && NotesService.GetMarkdown(localId) == "Recovered remote body" &&
                NotesService.LoadIndex().Single().PendingNotionUploadHash.Length == 0,
            "An incomplete upload was pulled over the full local note instead of completed.");
    }

    private static void VerifyNestedBlocks()
    {
        using var json = JsonDocument.Parse("""
            [{"id":"parent","type":"bulleted_list_item","has_children":true,
              "bulleted_list_item":{"rich_text":[{"plain_text":"Parent","annotations":{"bold":false,"italic":false,"strikethrough":false},"href":null}]}}]
            """);
        var converted = NotionMarkdownConverter.FromBlocks(json.RootElement);
        Require(converted.Markdown.Contains("Parent") && converted.HasUnsupportedBlocks && !converted.SupportedBlockIds.Contains("parent"),
            "Nested parent could be archived along with its children.");
    }

    private static AppConfig Config() => new()
    {
        NotionToken = "offline-test-token", NotionParentPageId = "parent",
        NotionDatabaseId = "saved-db", NotionDataSourceId = "saved-source", NotionSyncEnabled = true,
    };
    private static HttpResponseMessage Database() => Json("""{"id":"saved-db","data_sources":[{"id":"saved-source"}]}""");
    private static HttpResponseMessage Schema() => Json("""{"properties":{"Title":{"type":"title"},"LocalId":{"type":"rich_text"}}}""");
    private static HttpResponseMessage Json(string body, int status = 200) => new((HttpStatusCode)status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };
    private static async Task ExpectFailure(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException) { return; }
        throw new Exception("Expected a protective failure.");
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
