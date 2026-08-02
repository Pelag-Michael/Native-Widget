using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FlowList = System.Windows.Documents.List;

namespace NativeWidget.Services;

public sealed record NotionMarkdownDocument(
    string Markdown,
    IReadOnlyList<string> SupportedBlockIds,
    bool HasUnsupportedBlocks);

/// Maps the exact WYSIWYG subset supported by NotesWindow to Notion block JSON. Keeping
/// this separate from HTTP makes the lossy boundary explicit and independently testable.
public static class NotionMarkdownConverter
{
    public static object[] ToBlocks(string markdown, IReadOnlyDictionary<string, string>? uploadedFiles = null)
        => WpfSta.Run(() => ToBlocksCore(markdown, uploadedFiles));

    private static object[] ToBlocksCore(string markdown,
        IReadOnlyDictionary<string, string>? uploadedFiles)
    {
        var document = FlowDocumentMarkdownConverter.FromMarkdown(markdown);
        var blocks = new List<object>();
        foreach (var block in document.Blocks)
            AddBlock(block, blocks, uploadedFiles);
        return blocks.Count == 0 ? new[] { TextBlock("paragraph", Array.Empty<object>()) } : blocks.ToArray();
    }

    public static NotionMarkdownDocument FromBlocks(JsonElement results)
        => FromBlocks(results.EnumerateArray());

    public static NotionMarkdownDocument FromBlocks(IEnumerable<JsonElement> blocks)
    {
        var lines = new List<string>();
        var supportedIds = new List<string>();
        var unsupported = false;
        var numberedIndex = 0;

        foreach (var block in blocks)
        {
            var type = block.GetProperty("type").GetString() ?? "";
            numberedIndex = type == "numbered_list_item" ? numberedIndex + 1 : 0;
            var id = block.GetProperty("id").GetString();
            string? markdown = type switch
            {
                "paragraph" => RichTextMarkdown(block.GetProperty("paragraph").GetProperty("rich_text")),
                "heading_1" => "# " + RichTextMarkdown(block.GetProperty("heading_1").GetProperty("rich_text")),
                "heading_2" => "## " + RichTextMarkdown(block.GetProperty("heading_2").GetProperty("rich_text")),
                "bulleted_list_item" => "- " + RichTextMarkdown(block.GetProperty("bulleted_list_item").GetProperty("rich_text")),
                "numbered_list_item" => $"{numberedIndex}. " + RichTextMarkdown(block.GetProperty("numbered_list_item").GetProperty("rich_text")),
                "to_do" => $"- [{(block.GetProperty("to_do").GetProperty("checked").GetBoolean() ? "x" : " ")}] " +
                           RichTextMarkdown(block.GetProperty("to_do").GetProperty("rich_text")),
                "quote" => "> " + RichTextMarkdown(block.GetProperty("quote").GetProperty("rich_text")),
                "code" => "```\n" + RichTextMarkdown(block.GetProperty("code").GetProperty("rich_text")) + "\n```",
                "image" => ImageMarkdown(block.GetProperty("image")),
                "file" => FileMarkdown(block.GetProperty("file")),
                _ => null,
            };

            if (markdown == null)
            {
                unsupported = true;
                continue;
            }
            lines.Add(markdown);
            if (id != null) supportedIds.Add(id);
        }

        return new NotionMarkdownDocument(string.Join('\n', lines).TrimEnd('\n'), supportedIds, unsupported);
    }

    private static void AddBlock(Block block, List<object> output,
        IReadOnlyDictionary<string, string>? uploadedFiles)
    {
        if (block is Paragraph paragraph)
        {
            var type = (paragraph.Tag as string) switch
            {
                "heading1" => "heading_1",
                "heading2" => "heading_2",
                "quote" => "quote",
                "code" => "code",
                "todo" => "to_do",
                _ => "paragraph",
            };
            if (type == "code")
            {
                var content = new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text.TrimEnd('\r', '\n');
                output.Add(new
                {
                    @object = "block",
                    type,
                    code = new { rich_text = PlainRichText(content), language = "plain text" },
                });
                return;
            }
            if (type == "to_do")
            {
                var check = paragraph.Inlines.OfType<InlineUIContainer>()
                    .Select(i => i.Child).OfType<CheckBox>().FirstOrDefault();
                output.Add(new
                {
                    @object = "block",
                    type,
                    to_do = new { rich_text = RichText(paragraph.Inlines), @checked = check?.IsChecked == true },
                });
                return;
            }

            var richText = RichText(paragraph.Inlines);
            output.Add(TextBlock(type, richText));
            foreach (var image in paragraph.Inlines.OfType<InlineUIContainer>().Select(i => i.Child).OfType<Image>())
                if (ImageBlock(image, uploadedFiles) is { } imageBlock) output.Add(imageBlock);
            foreach (var link in paragraph.Inlines.OfType<Hyperlink>())
                if (FileBlock(link, uploadedFiles) is { } fileBlock) output.Add(fileBlock);
            return;
        }

        if (block is FlowList list)
        {
            var type = list.MarkerStyle == TextMarkerStyle.Decimal
                ? "numbered_list_item" : "bulleted_list_item";
            foreach (var item in list.ListItems)
                foreach (var listParagraph in item.Blocks.OfType<Paragraph>())
                    output.Add(TextBlock(type, RichText(listParagraph.Inlines)));
            return;
        }

        if (block is Section section)
            foreach (var child in section.Blocks) AddBlock(child, output, uploadedFiles);
        else if (block is BlockUIContainer { Child: Image image } &&
                 ImageBlock(image, uploadedFiles) is { } imageBlock)
            output.Add(imageBlock);
    }

    private static object TextBlock(string type, object[] richText) => type switch
    {
        "heading_1" => new { @object = "block", type, heading_1 = new { rich_text = richText } },
        "heading_2" => new { @object = "block", type, heading_2 = new { rich_text = richText } },
        "quote" => new { @object = "block", type, quote = new { rich_text = richText } },
        "bulleted_list_item" => new { @object = "block", type, bulleted_list_item = new { rich_text = richText } },
        "numbered_list_item" => new { @object = "block", type, numbered_list_item = new { rich_text = richText } },
        _ => new { @object = "block", type = "paragraph", paragraph = new { rich_text = richText } },
    };

    private static object? ImageBlock(Image image, IReadOnlyDictionary<string, string>? uploadedFiles)
    {
        var source = image.Source switch
        {
            BitmapImage { UriSource: not null } bitmap => bitmap.UriSource.AbsoluteUri,
            _ => image.Source?.ToString(),
        };
        if (source == null) return null;
        if (uploadedFiles != null && uploadedFiles.TryGetValue(source, out var uploadId))
            return new { @object = "block", type = "image", image = new { type = "file_upload", file_upload = new { id = uploadId } } };
        if (source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return new { @object = "block", type = "image", image = new { type = "external", external = new { url = source } } };
        return null;
    }

    private static object? FileBlock(Hyperlink link,
        IReadOnlyDictionary<string, string>? uploadedFiles)
    {
        var source = link.NavigateUri?.AbsoluteUri;
        var label = new TextRange(link.ContentStart, link.ContentEnd).Text.Trim();
        if (source == null || !label.StartsWith("📎 ", StringComparison.Ordinal)) return null;
        var caption = PlainRichText(label);
        if (uploadedFiles != null && uploadedFiles.TryGetValue(source, out var uploadId))
            return new
            {
                @object = "block",
                type = "file",
                file = new { type = "file_upload", file_upload = new { id = uploadId }, caption },
            };
        if (source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return new
            {
                @object = "block",
                type = "file",
                file = new { type = "external", external = new { url = source }, caption },
            };
        return null;
    }

    private static object[] RichText(InlineCollection inlines)
    {
        var segments = new List<RichSegment>();
        foreach (var inline in inlines) AddInline(inline, segments, false, false, false, null);
        return segments.SelectMany(ToRichTextObjects).ToArray();
    }

    private static object[] PlainRichText(string text) =>
        ToRichTextObjects(new RichSegment(text, false, false, false, null)).ToArray();

    private static void AddInline(Inline inline, List<RichSegment> output,
        bool bold, bool italic, bool strike, string? link)
    {
        if (inline is Hyperlink attachment &&
            new TextRange(attachment.ContentStart, attachment.ContentEnd).Text.Trim()
                .StartsWith("📎 ", StringComparison.Ordinal))
            return;

        var localWeight = inline.ReadLocalValue(TextElement.FontWeightProperty);
        var localStyle = inline.ReadLocalValue(TextElement.FontStyleProperty);
        var localDecorations = inline.ReadLocalValue(Inline.TextDecorationsProperty);
        bold |= inline is Bold || localWeight is FontWeight weight && weight == FontWeights.Bold;
        italic |= inline is Italic || localStyle is FontStyle style && style == FontStyles.Italic;
        strike |= localDecorations is TextDecorationCollection decorations &&
                  decorations.Any(d => d.Location == TextDecorationLocation.Strikethrough);
        if (inline is Hyperlink hyperlink) link = hyperlink.NavigateUri?.AbsoluteUri;

        switch (inline)
        {
            case Run run when run.Text.Length > 0:
                output.Add(new RichSegment(run.Text, bold, italic, strike, link));
                break;
            case LineBreak:
                output.Add(new RichSegment("\n", bold, italic, strike, link));
                break;
            case Span span:
                foreach (var child in span.Inlines) AddInline(child, output, bold, italic, strike, link);
                break;
        }
    }

    private static IEnumerable<object> ToRichTextObjects(RichSegment segment)
    {
        for (var i = 0; i < segment.Text.Length; i += 1900)
        {
            var content = segment.Text.Substring(i, Math.Min(1900, segment.Text.Length - i));
            yield return new
            {
                type = "text",
                text = new
                {
                    content,
                    link = segment.Link == null ? null : new { url = segment.Link },
                },
                annotations = new
                {
                    bold = segment.Bold,
                    italic = segment.Italic,
                    strikethrough = segment.Strike,
                    underline = false,
                    code = false,
                    color = "default",
                },
            };
        }
    }

    private static string RichTextMarkdown(JsonElement richText)
    {
        var output = new StringBuilder();
        foreach (var item in richText.EnumerateArray())
        {
            var content = Escape(item.GetProperty("plain_text").GetString() ?? "");
            if (item.TryGetProperty("annotations", out var annotations))
            {
                if (annotations.TryGetProperty("bold", out var bold) && bold.GetBoolean()) content = $"**{content}**";
                if (annotations.TryGetProperty("italic", out var italic) && italic.GetBoolean()) content = $"*{content}*";
                if (annotations.TryGetProperty("strikethrough", out var strike) && strike.GetBoolean()) content = $"~~{content}~~";
            }
            if (item.TryGetProperty("href", out var href) && href.ValueKind == JsonValueKind.String)
                content = $"[{content}]({href.GetString()})";
            output.Append(content);
        }
        return output.ToString();
    }

    private static string? ImageMarkdown(JsonElement image)
    {
        var type = image.GetProperty("type").GetString();
        if (type == null || !image.TryGetProperty(type, out var value) ||
            !value.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String)
            return null;
        return $"![]({url.GetString()})";
    }

    private static string? FileMarkdown(JsonElement file)
    {
        var type = file.GetProperty("type").GetString();
        if (type == null || !file.TryGetProperty(type, out var value) ||
            !value.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String)
            return null;
        var caption = file.TryGetProperty("caption", out var captionValue)
            ? RichTextMarkdown(captionValue).Trim() : "";
        var name = file.TryGetProperty("name", out var nameValue) && nameValue.ValueKind == JsonValueKind.String
            ? nameValue.GetString() ?? "file" : "file";
        const string attachmentPrefix = "📎 ";
        var label = caption.StartsWith(attachmentPrefix, StringComparison.Ordinal)
            ? caption[attachmentPrefix.Length..] : name;
        label = label.Replace(']', ')');
        return $"[📎 {label}]({url.GetString()})";
    }

    private static string Escape(string text) => text
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("*", "\\*", StringComparison.Ordinal)
        .Replace("~", "\\~", StringComparison.Ordinal)
        .Replace("[", "\\[", StringComparison.Ordinal)
        .Replace("<", "\\<", StringComparison.Ordinal);

    private sealed record RichSegment(string Text, bool Bold, bool Italic, bool Strike, string? Link);
}
