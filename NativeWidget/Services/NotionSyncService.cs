using System.IO;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NativeWidget.Models;

namespace NativeWidget.Services;

/// Safe two-way Markdown/block sync. Supported Notion blocks round-trip structurally;
/// unsupported blocks are never archived, and local conflict copies are kept before a
/// last-write-wins remote pull. Deletion still does not propagate in either direction.
public static partial class NotionSyncService
{
    public enum SyncResolution
    {
        PushLocal,
        PullRemote,
        PullRemoteWithLocalConflict,
    }

    private const string ApiBase = "https://api.notion.com/v1";
    private const string NotionVersion = "2026-03-11";
    private static readonly HttpClient Http = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = null };
    private static readonly ConcurrentDictionary<string, string> RemoteAssetHashCache = new();

    [GeneratedRegex(@"!\[\]\(([^)]+)\)")]
    private static partial Regex ImageMarkdownRegex();

    [GeneratedRegex(@"\[(📎 [^\]]+)\]\(([^)]+)\)")]
    private static partial Regex AttachmentMarkdownRegex();

    private static HttpRequestMessage Req(HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, $"{ApiBase}{path}");
        request.Headers.Add("Notion-Version", NotionVersion);
        if (body != null) request.Content = JsonContent.Create(body, options: JsonOptions);
        return request;
    }

    private static async Task<JsonElement> CallAsync(AppConfig config, HttpMethod method,
        string path, object? body = null)
    {
        using var request = Req(method, path, body);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.NotionToken);
        using var response = await Http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Notion {(int)response.StatusCode}: {text}");
        return JsonSerializer.Deserialize<JsonElement>(text);
    }

    private static async Task<JsonElement> SendFileAsync(AppConfig config, string uploadId,
        string path, string contentType)
    {
        using var request = Req(HttpMethod.Post, $"/file_uploads/{uploadId}/send");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.NotionToken);
        using var form = new MultipartFormDataContent();
        await using var stream = File.OpenRead(path);
        using var file = new StreamContent(stream);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", Path.GetFileName(path));
        request.Content = form;
        using var response = await Http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Notion upload {(int)response.StatusCode}: {text}");
        return JsonSerializer.Deserialize<JsonElement>(text);
    }

    private static async Task EnsureDataSourceAsync(AppConfig config)
    {
        if (!string.IsNullOrEmpty(config.NotionDatabaseId))
        {
            try
            {
                var database = await CallAsync(config, HttpMethod.Get,
                    $"/databases/{config.NotionDatabaseId}");
                var dataSourceId = FindDataSourceId(database, config.NotionDataSourceId);
                if (dataSourceId != null && await HasExpectedSchemaAsync(config, dataSourceId))
                {
                    if (config.NotionDataSourceId != dataSourceId)
                    {
                        config.NotionDataSourceId = dataSourceId;
                        config.Save();
                    }
                    return;
                }
            }
            catch { }
        }

        if (string.IsNullOrEmpty(config.NotionParentPageId))
            throw new InvalidOperationException("Parent Page ID is not set in Settings.");

        var body = new
        {
            parent = new { type = "page_id", page_id = config.NotionParentPageId },
            title = new object[] { new { type = "text", text = new { content = "NativeWidget Notes" } } },
            initial_data_source = new
            {
                properties = new
                {
                    Title = new { title = new { } },
                    LocalId = new { rich_text = new { } },
                },
            },
        };
        var created = await CallAsync(config, HttpMethod.Post, "/databases", body);
        config.NotionDatabaseId = created.GetProperty("id").GetString()!;
        var createdDataSourceId = FindDataSourceId(created, null);
        if (createdDataSourceId == null)
        {
            var database = await CallAsync(config, HttpMethod.Get,
                $"/databases/{config.NotionDatabaseId}");
            createdDataSourceId = FindDataSourceId(database, null);
        }
        config.NotionDataSourceId = createdDataSourceId ??
            throw new InvalidOperationException("Notion did not return the initial data source ID.");
        config.Save();
    }

    private static string? FindDataSourceId(JsonElement database, string? preferred)
    {
        if (!database.TryGetProperty("data_sources", out var sources)) return null;
        var ids = sources.EnumerateArray()
            .Select(s => s.TryGetProperty("id", out var id) ? id.GetString() : null)
            .Where(id => !string.IsNullOrEmpty(id)).ToList();
        return preferred != null && ids.Contains(preferred) ? preferred : ids.FirstOrDefault();
    }

    private static async Task<bool> HasExpectedSchemaAsync(AppConfig config, string dataSourceId)
    {
        try
        {
            var source = await CallAsync(config, HttpMethod.Get, $"/data_sources/{dataSourceId}");
            var properties = source.GetProperty("properties");
            return properties.TryGetProperty("Title", out _) &&
                   properties.TryGetProperty("LocalId", out _);
        }
        catch { return false; }
    }

    private sealed record RemotePage(string PageId, string LocalId, string Title, long UpdatedAtUnix);

    private static async Task<List<RemotePage>> QueryAllAsync(AppConfig config)
    {
        var pages = new List<RemotePage>();
        string? cursor = null;
        do
        {
            object body = cursor == null ? new { } : new { start_cursor = cursor };
            var response = await CallAsync(config, HttpMethod.Post,
                $"/data_sources/{config.NotionDataSourceId}/query", body);
            foreach (var page in response.GetProperty("results").EnumerateArray())
            {
                var properties = page.GetProperty("properties");
                var title = PropertyText(properties.GetProperty("Title"), "title");
                var localId = PropertyText(properties.GetProperty("LocalId"), "rich_text");
                var updated = DateTimeOffset.Parse(page.GetProperty("last_edited_time").GetString()!)
                    .ToUnixTimeSeconds();
                pages.Add(new RemotePage(page.GetProperty("id").GetString()!, localId, title, updated));
            }
            cursor = response.TryGetProperty("has_more", out var more) && more.GetBoolean()
                ? response.GetProperty("next_cursor").GetString() : null;
        } while (cursor != null);
        return pages;
    }

    private static string PropertyText(JsonElement property, string arrayName) =>
        string.Concat(property.GetProperty(arrayName).EnumerateArray()
            .Select(item => item.GetProperty("plain_text").GetString()));

    private static object[] PlainRichText(string text)
    {
        if (text.Length == 0) return Array.Empty<object>();
        var parts = new List<object>();
        for (var i = 0; i < text.Length; i += 1900)
            parts.Add(new { type = "text", text = new
            {
                content = text.Substring(i, Math.Min(1900, text.Length - i)),
            }});
        return parts.ToArray();
    }

    private static async Task<NotionMarkdownDocument> GetPageBodyAsync(AppConfig config,
        string pageId)
    {
        var blocks = new List<JsonElement>();
        string? cursor = null;
        do
        {
            var query = cursor == null ? "" : $"?start_cursor={Uri.EscapeDataString(cursor)}";
            var response = await CallAsync(config, HttpMethod.Get,
                $"/blocks/{pageId}/children{query}");
            blocks.AddRange(response.GetProperty("results").EnumerateArray());
            cursor = response.TryGetProperty("has_more", out var more) && more.GetBoolean()
                ? response.GetProperty("next_cursor").GetString() : null;
        } while (cursor != null);
        return NotionMarkdownConverter.FromBlocks(blocks);
    }

    private static async Task ReplaceSupportedBodyAsync(AppConfig config, string pageId,
        NotionMarkdownDocument existing, string markdown)
    {
        // Preserve every unsupported block. This prevents a local edit from destroying a
        // toggle/embed or future Notion block type the bounded converter cannot represent.
        // Append first, archive second: a mid-request failure can leave duplicates but can
        // never leave the page empty.
        await AppendBodyAsync(config, pageId, markdown);
        foreach (var blockId in existing.SupportedBlockIds)
            await CallAsync(config, HttpMethod.Delete, $"/blocks/{blockId}");
    }

    private static async Task AppendBodyAsync(AppConfig config, string pageId, string markdown,
        object[]? preparedBlocks = null)
    {
        var blocks = preparedBlocks ?? await BuildBlocksAsync(config, markdown);
        foreach (var chunk in blocks.Chunk(90))
            await CallAsync(config, HttpMethod.Patch, $"/blocks/{pageId}/children",
                new { children = chunk });
    }

    private static async Task<object[]> BuildBlocksAsync(AppConfig config, string markdown)
    {
        var uploads = await UploadLocalFilesAsync(config, markdown);
        return NotionMarkdownConverter.ToBlocks(markdown, uploads);
    }

    private static async Task<string> PushCreateAsync(AppConfig config, string localId,
        string title, string markdown)
    {
        var blocks = await BuildBlocksAsync(config, markdown);
        var body = new
        {
            parent = new { type = "data_source_id", data_source_id = config.NotionDataSourceId },
            properties = new
            {
                Title = new { title = PlainRichText(title) },
                LocalId = new { rich_text = PlainRichText(localId) },
            },
            children = blocks.Take(90).ToArray(),
        };
        var page = await CallAsync(config, HttpMethod.Post, "/pages", body);
        var pageId = page.GetProperty("id").GetString()!;
        if (blocks.Length > 90)
            foreach (var chunk in blocks.Skip(90).Chunk(90))
                await CallAsync(config, HttpMethod.Patch, $"/blocks/{pageId}/children",
                    new { children = chunk });
        return pageId;
    }

    private static async Task SetLocalIdAsync(AppConfig config, string pageId, string localId)
    {
        var body = new { properties = new { LocalId = new { rich_text = PlainRichText(localId) } } };
        await CallAsync(config, HttpMethod.Patch, $"/pages/{pageId}", body);
    }

    private static async Task SetTitleAsync(AppConfig config, string pageId, string title)
    {
        var body = new { properties = new { Title = new { title = PlainRichText(title) } } };
        await CallAsync(config, HttpMethod.Patch, $"/pages/{pageId}", body);
    }

    private static async Task PushUpdateAsync(AppConfig config, RemotePage page,
        NotionMarkdownDocument remoteBody, string title, string markdown)
    {
        await SetTitleAsync(config, page.PageId, title);
        await ReplaceSupportedBodyAsync(config, page.PageId, remoteBody, markdown);
    }

    public static SyncResolution ResolveChanges(bool localChanged, bool remoteChanged)
    {
        if (localChanged && remoteChanged) return SyncResolution.PullRemoteWithLocalConflict;
        if (localChanged) return SyncResolution.PushLocal;
        return SyncResolution.PullRemote;
    }

    public static async Task SyncOnceAsync(AppConfig config, string? skipLocalId = null)
    {
        if (!config.NotionSyncEnabled || string.IsNullOrEmpty(config.NotionToken)) return;
        await EnsureDataSourceAsync(config);

        var remote = await QueryAllAsync(config);
        var remoteByLocalId = remote.Where(page => page.LocalId.Length > 0)
            .GroupBy(page => page.LocalId).ToDictionary(group => group.Key, group => group.First());
        var local = NotesService.LoadIndex();
        var localIds = local.Select(note => note.Id).ToHashSet();

        foreach (var meta in local)
        {
            // An actively edited note is skipped when its in-memory document is dirty. Other
            // notes still sync normally, and this one is reconsidered after the user saves it.
            if (string.Equals(meta.Id, skipLocalId, StringComparison.Ordinal)) continue;

            var localMarkdown = NotesService.GetMarkdown(meta.Id);
            var localHash = await CanonicalHashAsync(localMarkdown);
            if (!remoteByLocalId.TryGetValue(meta.Id, out var match))
            {
                await PushCreateAsync(config, meta.Id, meta.Title, localMarkdown);
                NotesService.MarkSynced(meta.Id, localHash);
                continue;
            }

            var remoteBody = await GetPageBodyAsync(config, match.PageId);
            var remoteHash = await CanonicalHashAsync(remoteBody.Markdown);
            var localChanged = meta.LastSyncedHash.Length == 0
                ? meta.UpdatedAt > match.UpdatedAtUnix + 2
                : localHash != meta.LastSyncedHash;
            var remoteChanged = meta.LastSyncedHash.Length == 0
                ? match.UpdatedAtUnix > meta.UpdatedAt + 2
                : remoteHash != meta.LastSyncedHash;

            if (localHash == remoteHash)
            {
                if (!string.Equals(meta.Title, match.Title, StringComparison.Ordinal))
                {
                    if (meta.UpdatedAt > match.UpdatedAtUnix + 2)
                        await SetTitleAsync(config, match.PageId, meta.Title);
                    else
                        NotesService.ApplyRemoteTitle(meta.Id, match.Title, match.UpdatedAtUnix);
                }
                NotesService.MarkSynced(meta.Id, localHash);
                continue;
            }

            var resolution = ResolveChanges(localChanged, remoteChanged);
            if (resolution == SyncResolution.PushLocal)
            {
                await PushUpdateAsync(config, match, remoteBody, meta.Title, localMarkdown);
                NotesService.MarkSynced(meta.Id, localHash);
            }
            else
            {
                var localized = await LocalizeRemoteFilesAsync(remoteBody.Markdown);
                var localizedHash = await CanonicalHashAsync(localized);
                NotesService.ApplyRemoteUpdate(meta.Id, match.Title, localized,
                    match.UpdatedAtUnix, localizedHash,
                    backupLocal: resolution == SyncResolution.PullRemoteWithLocalConflict);
            }
        }

        foreach (var page in remote)
        {
            if (page.LocalId.Length > 0 && localIds.Contains(page.LocalId)) continue;
            var body = await GetPageBodyAsync(config, page.PageId);
            var localized = await LocalizeRemoteFilesAsync(body.Markdown);
            var hash = await CanonicalHashAsync(localized);
            NotesService.CreateNoteFromRemote(page.PageId, page.Title, localized,
                page.UpdatedAtUnix, hash);
            await SetLocalIdAsync(config, page.PageId, page.PageId);
        }
    }

    private static async Task<Dictionary<string, string>> UploadLocalFilesAsync(
        AppConfig config, string markdown)
    {
        var uploads = new Dictionary<string, string>(StringComparer.Ordinal);
        var sources = ImageMarkdownRegex().Matches(markdown).Select(match => match.Groups[1].Value)
            .Concat(AttachmentMarkdownRegex().Matches(markdown).Select(match => match.Groups[2].Value))
            .Distinct(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            if (uploads.ContainsKey(source) || !Uri.TryCreate(source, UriKind.Absolute, out var uri) ||
                !uri.IsFile || !File.Exists(uri.LocalPath))
                continue;

            var info = new FileInfo(uri.LocalPath);
            if (info.Length > 20 * 1024 * 1024)
                throw new InvalidOperationException($"File is too large to upload to Notion: {info.Name}");
            var contentType = ContentType(uri.LocalPath);
            var created = await CallAsync(config, HttpMethod.Post, "/file_uploads", new
            {
                mode = "single_part",
                filename = info.Name,
                content_type = contentType,
            });
            var uploadId = created.GetProperty("id").GetString()!;
            await SendFileAsync(config, uploadId, uri.LocalPath, contentType);
            uploads[source] = uploadId;
        }
        return uploads;
    }

    private static async Task<string> LocalizeRemoteFilesAsync(string markdown)
    {
        markdown = await LocalizeRemoteImagesAsync(markdown);
        return await LocalizeRemoteAttachmentsAsync(markdown);
    }

    private static async Task<string> LocalizeRemoteImagesAsync(string markdown)
    {
        var matches = ImageMarkdownRegex().Matches(markdown);
        if (matches.Count == 0) return markdown;
        var output = new StringBuilder();
        var offset = 0;
        foreach (Match match in matches)
        {
            output.Append(markdown, offset, match.Index - offset);
            var source = match.Groups[1].Value;
            if (Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
                uri.Scheme is "http" or "https")
            {
                try
                {
                    var (bytes, contentType) = await DownloadAsync(uri);
                    var extension = Extension(contentType, uri.AbsolutePath);
                    var name = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() + extension;
                    var folder = Path.Combine(AppConfig.TokenPath("notes"), "images");
                    Directory.CreateDirectory(folder);
                    var path = Path.Combine(folder, name);
                    if (!File.Exists(path)) await File.WriteAllBytesAsync(path, bytes);
                    source = new Uri(path).AbsoluteUri;
                }
                catch { }
            }
            output.Append($"![]({source})");
            offset = match.Index + match.Length;
        }
        output.Append(markdown, offset, markdown.Length - offset);
        return output.ToString();
    }

    private static async Task<string> LocalizeRemoteAttachmentsAsync(string markdown)
    {
        var matches = AttachmentMarkdownRegex().Matches(markdown);
        if (matches.Count == 0) return markdown;
        var output = new StringBuilder();
        var offset = 0;
        foreach (Match match in matches)
        {
            output.Append(markdown, offset, match.Index - offset);
            var label = match.Groups[1].Value;
            var source = match.Groups[2].Value;
            if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                try
                {
                    var (bytes, contentType) = await DownloadAsync(uri);
                    var extension = Extension(contentType, uri.AbsolutePath);
                    var name = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() + extension;
                    var folder = Path.Combine(AppConfig.TokenPath("notes"), "attachments", "remote");
                    Directory.CreateDirectory(folder);
                    var path = Path.Combine(folder, name);
                    if (!File.Exists(path)) await File.WriteAllBytesAsync(path, bytes);
                    source = new Uri(path).AbsoluteUri;
                }
                catch { }
            }
            output.Append($"[{label}]({source})");
            offset = match.Index + match.Length;
        }
        output.Append(markdown, offset, markdown.Length - offset);
        return output.ToString();
    }

    private static async Task<string> CanonicalHashAsync(string markdown)
    {
        // Font family/size are retained in local Markdown as private span extensions, but
        // Notion cannot represent them. Exclude only the wrapper tags from sync comparison
        // so an otherwise-equal remote body never strips local typography.
        var comparable = Regex.Replace(markdown,
            @"</?span(?:\s+data-font=""[^""]*"")?(?:\s+data-size=""[^""]*"")?>", "");
        comparable = await CanonicalizeAssetsAsync(comparable, ImageMarkdownRegex(), 1,
            (match, hash) => $"![](sha256:{hash})");
        comparable = await CanonicalizeAssetsAsync(comparable, AttachmentMarkdownRegex(), 2,
            (match, hash) => $"[{match.Groups[1].Value}](sha256:{hash})");
        var normalized = Regex.Replace(
            comparable.Replace("\r\n", "\n", StringComparison.Ordinal),
            "\\n+", "\n").Trim('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))
            .ToLowerInvariant();
    }

    private static async Task<string> CanonicalizeAssetsAsync(string markdown, Regex regex,
        int sourceGroup, Func<Match, string, string> replacement)
    {
        var matches = regex.Matches(markdown);
        var canonical = new StringBuilder();
        var offset = 0;
        foreach (Match match in matches)
        {
            canonical.Append(markdown, offset, match.Index - offset);
            var source = match.Groups[sourceGroup].Value;
            string? hash = null;
            try
            {
                if (Uri.TryCreate(source, UriKind.Absolute, out var uri))
                {
                    if (uri.IsFile && File.Exists(uri.LocalPath))
                        hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(uri.LocalPath)));
                    else if (uri.Scheme is "http" or "https")
                    {
                        if (!RemoteAssetHashCache.TryGetValue(source, out hash))
                        {
                            hash = Convert.ToHexString(SHA256.HashData((await DownloadAsync(uri)).Bytes));
                            RemoteAssetHashCache[source] = hash;
                        }
                    }
                }
            }
            catch { }
            canonical.Append(hash == null ? match.Value : replacement(match, hash));
            offset = match.Index + match.Length;
        }
        canonical.Append(markdown, offset, markdown.Length - offset);
        return canonical.ToString();
    }

    private static async Task<(byte[] Bytes, string? ContentType)> DownloadAsync(Uri uri)
    {
        using var response = await Http.GetAsync(uri);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadAsByteArrayAsync(),
            response.Content.Headers.ContentType?.MediaType);
    }

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".pdf" => "application/pdf",
        ".txt" or ".md" => "text/plain",
        ".csv" => "text/csv",
        ".json" => "application/json",
        ".zip" => "application/zip",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        _ => "application/octet-stream",
    };

    private static string Extension(string? contentType, string sourcePath) => contentType switch
    {
        "image/jpeg" => ".jpg",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "image/svg+xml" => ".svg",
        "application/pdf" => ".pdf",
        "text/plain" => ".txt",
        "text/csv" => ".csv",
        "application/json" => ".json",
        "application/zip" => ".zip",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => ".docx",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => ".xlsx",
        "application/vnd.openxmlformats-officedocument.presentationml.presentation" => ".pptx",
        _ => Path.GetExtension(sourcePath) is { Length: > 0 and <= 5 } ext ? ext : ".png",
    };
}
