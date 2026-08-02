using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NativeWidget.Models;
using NativeWidget.Services;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var imagePath = Path.Combine(Path.GetTempPath(), "nativewidget-markdown-test.png");
        File.WriteAllBytes(imagePath, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
        var attachmentPath = Path.Combine(Path.GetTempPath(), "nativewidget-attachment-test.pdf");
        File.WriteAllBytes(attachmentPath, "%PDF-1.4\n% NativeWidget attachment smoke"u8.ToArray());

        var document = new FlowDocument();
        document.Blocks.Add(Styled("heading1", new Run("Main heading")));
        document.Blocks.Add(Styled("heading2", new Run("Sub heading")));
        var rich = new Paragraph();
        rich.Inlines.Add(new Run("Plain "));
        rich.Inlines.Add(new Bold(new Run("bold")));
        rich.Inlines.Add(new Run(" "));
        rich.Inlines.Add(new Italic(new Run("italic")));
        rich.Inlines.Add(new Span(new Run("strike")) { TextDecorations = TextDecorations.Strikethrough });
        rich.Inlines.Add(new Run(" font") { FontFamily = new FontFamily("Georgia"), FontSize = 16 });
        rich.Inlines.Add(new InlineUIContainer(new Image
        {
            Source = new BitmapImage(new Uri(imagePath)),
            MaxWidth = 260,
        }));
        document.Blocks.Add(rich);
        document.Blocks.Add(new List(new ListItem(new Paragraph(new Run("bullet"))))
            { MarkerStyle = TextMarkerStyle.Disc });
        document.Blocks.Add(new List(new ListItem(new Paragraph(new Run("number"))))
            { MarkerStyle = TextMarkerStyle.Decimal });
        var todo = new Paragraph { Tag = "todo" };
        todo.Inlines.Add(new InlineUIContainer(new CheckBox { IsChecked = true }));
        todo.Inlines.Add(new Run("done"));
        document.Blocks.Add(todo);
        document.Blocks.Add(Styled("quote", new Run("quoted")));
        document.Blocks.Add(Styled("code", new Run("var x = 1;\nreturn x;")));
        document.Blocks.Add(new Paragraph(new Hyperlink(new Run("📎 sample.pdf"))
        {
            NavigateUri = new Uri(attachmentPath),
        }));

        var markdown = FlowDocumentMarkdownConverter.ToMarkdown(document);
        var roundTrip = FlowDocumentMarkdownConverter.ToMarkdown(
            FlowDocumentMarkdownConverter.FromMarkdown(markdown));
        AssertEqual(markdown, roundTrip, "Markdown round-trip");

        var xaml = XamlWriter.Save(document);
        var xamlRoundTrip = (FlowDocument)XamlReader.Parse(xaml);
        AssertEqual(markdown, FlowDocumentMarkdownConverter.ToMarkdown(xamlRoundTrip), "XAML structural round-trip");

        var expectedTokens = new[]
        {
            "# Main heading", "## Sub heading", "**bold**", "*italic*", "~~strike~~",
            "data-font=\"Georgia\"", "- bullet", "1. number", "- [x] done",
            "> quoted", "```", "![](file:", "[📎 sample.pdf](file:",
        };
        foreach (var token in expectedTokens)
            if (!markdown.Contains(token, StringComparison.Ordinal))
                throw new InvalidOperationException($"Missing expected token: {token}\n{markdown}");

        AssertEqual(NotionSyncService.SyncResolution.PushLocal,
            NotionSyncService.ResolveChanges(localChanged: true, remoteChanged: false),
            "Local-only sync resolution");
        AssertEqual(NotionSyncService.SyncResolution.PullRemote,
            NotionSyncService.ResolveChanges(localChanged: false, remoteChanged: true),
            "Remote-only sync resolution");
        AssertEqual(NotionSyncService.SyncResolution.PullRemoteWithLocalConflict,
            NotionSyncService.ResolveChanges(localChanged: true, remoteChanged: true),
            "Two-sided sync conflict resolution");
        TestBareDomainLinkify();

        using var notionJson = JsonDocument.Parse("""
        [
          {"id":"p","type":"paragraph","paragraph":{"rich_text":[{"plain_text":"plain","href":null,"annotations":{"bold":false,"italic":false,"strikethrough":false}}]}},
          {"id":"h1","type":"heading_1","heading_1":{"rich_text":[{"plain_text":"head","href":null,"annotations":{"bold":false,"italic":false,"strikethrough":false}}]}},
          {"id":"b","type":"bulleted_list_item","bulleted_list_item":{"rich_text":[{"plain_text":"bold","href":null,"annotations":{"bold":true,"italic":false,"strikethrough":false}}]}},
          {"id":"n","type":"numbered_list_item","numbered_list_item":{"rich_text":[{"plain_text":"number","href":null,"annotations":{"bold":false,"italic":true,"strikethrough":false}}]}},
          {"id":"n2","type":"numbered_list_item","numbered_list_item":{"rich_text":[{"plain_text":"number2","href":null,"annotations":{"bold":false,"italic":false,"strikethrough":false}}]}},
          {"id":"t","type":"to_do","to_do":{"checked":true,"rich_text":[{"plain_text":"todo","href":null,"annotations":{"bold":false,"italic":false,"strikethrough":true}}]}},
          {"id":"q","type":"quote","quote":{"rich_text":[{"plain_text":"quote","href":null,"annotations":{"bold":false,"italic":false,"strikethrough":false}}]}},
          {"id":"c","type":"code","code":{"rich_text":[{"plain_text":"code","href":null,"annotations":{"bold":false,"italic":false,"strikethrough":false}}]}},
          {"id":"i","type":"image","image":{"type":"external","external":{"url":"https://example.com/image.png"}}},
          {"id":"f","type":"file","file":{"type":"external","external":{"url":"https://example.com/sample.pdf"},"name":"sample.pdf","caption":[{"plain_text":"📎 sample.pdf","href":null,"annotations":{"bold":false,"italic":false,"strikethrough":false}}]}},
          {"id":"u","type":"toggle","toggle":{"rich_text":[]}}
        ]
        """);
        var notionDocument = NotionMarkdownConverter.FromBlocks(notionJson.RootElement);
        if (!notionDocument.HasUnsupportedBlocks || notionDocument.SupportedBlockIds.Count != 10 ||
            !notionDocument.Markdown.Contains("2. number2", StringComparison.Ordinal))
            throw new InvalidOperationException("Notion unsupported-block preservation metadata failed.");
        var notionRoundTripJson = JsonSerializer.Serialize(
            NotionMarkdownConverter.ToBlocks(notionDocument.Markdown));
        foreach (var token in new[]
                 {
                     "heading_1", "bulleted_list_item", "numbered_list_item", "to_do",
                     "quote", "code", "image", "\"type\":\"file\"", "\"bold\":true", "\"italic\":true",
                     "\"strikethrough\":true",
                 })
            if (!notionRoundTripJson.Contains(token, StringComparison.Ordinal))
                throw new InvalidOperationException($"Notion round-trip missing token: {token}");

        var uploadedAttachmentJson = JsonSerializer.Serialize(NotionMarkdownConverter.ToBlocks(markdown,
            new Dictionary<string, string> { [new Uri(attachmentPath).AbsoluteUri] = "upload-id" }));
        if (!uploadedAttachmentJson.Contains("\"file_upload\"", StringComparison.Ordinal) ||
            !uploadedAttachmentJson.Contains("\"upload-id\"", StringComparison.Ordinal))
            throw new InvalidOperationException("Local attachment did not map to a Notion file-upload block.");

        var realNotePaths = args.Where(File.Exists).ToArray();
        foreach (var path in realNotePaths)
        {
            using var stream = File.OpenRead(path);
            var realDocument = (FlowDocument)XamlReader.Load(stream);
            var realMarkdown = FlowDocumentMarkdownConverter.ToMarkdown(realDocument);
            AssertEqual(realMarkdown,
                FlowDocumentMarkdownConverter.ToMarkdown(
                    FlowDocumentMarkdownConverter.FromMarkdown(realMarkdown)),
                $"Real-note round-trip: {Path.GetFileName(path)}");
        }

        if (realNotePaths.Length > 0) TestStorageMigration(realNotePaths[0]);

        Console.WriteLine($"PASS FlowDocument <-> Markdown and XAML structural round-trips ({realNotePaths.Length} real notes)");
        if (args.Contains("--render-ui", StringComparer.Ordinal))
            Console.WriteLine("UI_RENDER=" + UiRenderSmoke.Run());
        if (args.Contains("--notion-smoke", StringComparer.Ordinal))
            NotionSmoke.Run(imagePath).GetAwaiter().GetResult();
    }

    private static Paragraph Styled(string tag, Inline inline)
    {
        var paragraph = new Paragraph(inline) { Tag = tag };
        if (tag == "heading1") { paragraph.FontSize = 24; paragraph.FontWeight = FontWeights.Bold; }
        if (tag == "heading2") { paragraph.FontSize = 18; paragraph.FontWeight = FontWeights.Bold; }
        if (tag == "quote") paragraph.Margin = new System.Windows.Thickness(14, 4, 0, 4);
        if (tag == "code") paragraph.FontFamily = new FontFamily("Consolas");
        return paragraph;
    }

    private static void TestBareDomainLinkify()
    {
        var targets = LinkDetection.Find("vincea.space and vincea.com")
            .Select(link => link.Target.AbsoluteUri).ToArray();
        AssertEqual("https://vincea.space/", targets.ElementAtOrDefault(0) ?? "",
            "Bare .space link detection");
        AssertEqual("https://vincea.com/", targets.ElementAtOrDefault(1) ?? "",
            "Bare .com link detection");
    }

    private static void TestStorageMigration(string sourceXaml)
    {
        var root = Path.Combine(Path.GetTempPath(), "nativewidget-migration-test-" + Guid.NewGuid().ToString("N"));
        var notes = Path.Combine(root, "notes");
        Directory.CreateDirectory(notes);
        const string id = "migration-note";
        var backup = Path.Combine(notes, id + ".xaml");
        File.Copy(sourceXaml, backup);
        File.WriteAllText(Path.Combine(notes, "index.json"), JsonSerializer.Serialize(
            new List<NoteMeta> { new() { Id = id, Title = "Migration test" } }));
        Environment.SetEnvironmentVariable("NATIVEWIDGET_DATA_DIR", root);
        try
        {
            NotesService.LoadIndex();
            if (!File.Exists(Path.Combine(notes, id + ".md")) || !File.Exists(backup))
                throw new InvalidOperationException("XAML -> Markdown migration or backup retention failed.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("NATIVEWIDGET_DATA_DIR", null);
        }
    }

    private static void AssertEqual(string expected, string actual, string label)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new InvalidOperationException($"{label} failed.\nEXPECTED:\n{expected}\nACTUAL:\n{actual}");
    }

    private static void AssertEqual<T>(T expected, T actual, string label) where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{label} failed. Expected {expected}, actual {actual}.");
    }
}
