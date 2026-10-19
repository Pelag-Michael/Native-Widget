using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using NativeWidget.Services;

internal static class NotionPayloadChecks
{
    public static void Run(string[] notePaths)
    {
        var code = string.Concat(Enumerable.Repeat("let variable_name = \"Tiếng Việt 😀\"; // C:\\code\\file\n", 6000)).TrimEnd('\n');
        var markdown = "```\n" + code + "\n```";
        var blocks = NotionMarkdownConverter.ToBlocks(markdown);
        Require(blocks.Length > 1, "Large code block was not split.");
        VerifyLimits(blocks);
        var remote = AsRemote(blocks);
        var roundTrip = NotionMarkdownConverter.FromBlocks(remote);
        Require(roundTrip.Markdown.Replace("\r\n", "\n") == markdown,
            "Split code changed content, escaping or whitespace on round trip.");
        var distinctCode = "```\nfirst\n```\n```\nsecond\n```";
        Require(NotionMarkdownConverter.FromBlocks(AsRemote(NotionMarkdownConverter.ToBlocks(distinctCode))).Markdown == distinctCode,
            "Ordinary adjacent code blocks were incorrectly merged.");

        foreach (var path in notePaths)
            VerifyLimits(NotionMarkdownConverter.ToBlocks(File.ReadAllText(path)));
        Console.WriteLine($"PASS Notion payload bounds, Unicode code splitting and lossless round-trip ({notePaths.Length} real notes checked offline).");
    }

    private static void VerifyLimits(object[] blocks)
    {
        var json = JsonSerializer.SerializeToElement(blocks);
        foreach (var block in json.EnumerateArray())
        {
            var type = block.GetProperty("type").GetString()!;
            if (!block.GetProperty(type).TryGetProperty("rich_text", out var richText)) continue;
            Require(richText.GetArrayLength() <= 100, "Notion rich_text array exceeds 100 objects.");
            foreach (var text in richText.EnumerateArray())
                Require(text.GetProperty("text").GetProperty("content").GetString()!.Length <= 2000,
                    "Notion rich_text content exceeds 2000 characters.");
        }
        foreach (var batch in NotionSyncService.BatchBlocks(blocks))
        {
            Require(batch.Length <= 100, "Notion children array exceeds 100 blocks.");
            Require(JsonSerializer.SerializeToUtf8Bytes(new { children = batch }).Length <= 400_000,
                "Notion request exceeds conservative byte budget.");
        }
    }

    private static JsonElement AsRemote(object[] blocks)
    {
        var json = JsonSerializer.SerializeToNode(blocks)!.AsArray();
        foreach (var block in json)
        {
            block!["id"] = Guid.NewGuid().ToString("D");
            PopulatePlainText(block);
        }
        return JsonSerializer.SerializeToElement(json);
    }

    private static void PopulatePlainText(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            if (obj["type"]?.GetValue<string>() == "text" && obj["text"] is JsonObject text)
                obj["plain_text"] = text["content"]!.GetValue<string>();
            foreach (var child in obj.ToArray()) PopulatePlainText(child.Value);
        }
        else if (node is JsonArray array)
            foreach (var child in array) PopulatePlainText(child);
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
