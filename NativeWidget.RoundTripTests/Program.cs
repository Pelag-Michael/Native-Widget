using System.IO;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Automation;
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
        if (args.Contains("--selection-harness", StringComparer.Ordinal))
        {
            RunSelectionHarness();
            return;
        }
        if (args.Contains("--drive-selection", StringComparer.Ordinal))
        {
            DriveSelectionAndVerifyPopup();
            return;
        }
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
        TestVocabularyStorage();

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
        if (args.Contains("--translation-smoke", StringComparer.Ordinal))
            TestTranslationAsync().GetAwaiter().GetResult();
        if (args.Contains("--ocr-smoke", StringComparer.Ordinal))
            TestOcrAsync().GetAwaiter().GetResult();
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

    private static void RunSelectionHarness()
    {
        var app = new Application();
        var text = new TextBox
        {
            Text = "Learning a new language opens a new window to the world.",
            FontSize = 24,
            TextWrapping = TextWrapping.Wrap,
            Padding = new Thickness(24),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        var window = new Window
        {
            Title = "NativeWidget Selection Harness",
            Width = 760,
            Height = 220,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = text,
            Topmost = true,
            ShowInTaskbar = true,
        };
        window.Loaded += (_, _) => { window.Activate(); text.Focus(); };
        app.Run(window);
    }

    private static void DriveSelectionAndVerifyPopup()
    {
        var window = IntPtr.Zero;
        for (var attempt = 0; attempt < 30 && window == IntPtr.Zero; attempt++)
        {
            window = FindWindow(null, "NativeWidget Selection Harness");
            if (window == IntPtr.Zero) Thread.Sleep(100);
        }
        if (window == IntPtr.Zero || !GetWindowRect(window, out var rect))
            throw new InvalidOperationException("Selection harness window was not found.");

        SetForegroundWindow(window);
        Thread.Sleep(250);
        if (GetForegroundWindow() != window)
            throw new InvalidOperationException("Selection harness could not become foreground; refusing to drag in another app.");
        var startX = rect.Left + 35;
        var endX = rect.Right - 35;
        var y = rect.Top + (rect.Bottom - rect.Top) / 2;
        SetCursorPos(startX, y);
        mouse_event(MouseeventfLeftdown, 0, 0, 0, UIntPtr.Zero);
        for (var i = 1; i <= 24; i++)
        {
            SetCursorPos(startX + (endX - startX) * i / 24, y);
            Thread.Sleep(15);
        }
        mouse_event(MouseeventfLeftup, 0, 0, 0, UIntPtr.Zero);

        for (var i = 0; i < 60; i++)
        {
            var popup = FindWindow(null, "Translation Result");
            if (popup != IntPtr.Zero)
            {
                var root = AutomationElement.FromHandle(popup);
                var saveButton = root.FindFirst(TreeScope.Descendants, new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                    new PropertyCondition(AutomationElement.NameProperty, "Lưu")))
                    ?? throw new InvalidOperationException("Save button was not found in translation popup.");
                ((InvokePattern)saveButton.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                for (var saveAttempt = 0; saveAttempt < 20; saveAttempt++)
                {
                    var saved = VocabularyService.Load();
                    if (saved.Any(item => item.SourceText.Contains("Learning a new language", StringComparison.Ordinal)))
                    {
                        Console.WriteLine("SELECTION_TRANSLATION_PASS=popup-created-and-saved");
                        return;
                    }
                    Thread.Sleep(100);
                }
                throw new InvalidOperationException("Translation popup appeared, but Save did not persist the entry.");
            }
            Thread.Sleep(200);
        }
        throw new InvalidOperationException("Selecting text did not create the translation popup.");
    }

    private const uint MouseeventfLeftdown = 0x0002;
    private const uint MouseeventfLeftup = 0x0004;
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string windowName);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out WindowRect rect);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

    private static void TestVocabularyStorage()
    {
        var root = Path.Combine(Path.GetTempPath(), "nativewidget-vocabulary-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("NATIVEWIDGET_DATA_DIR", root);
        try
        {
            var added = VocabularyService.Add(new TranslationResult("hello", "xin chào", "en", "vi"),
                "selection", "test-app");
            var loaded = VocabularyService.Load();
            if (loaded.Count != 1 || loaded[0].Id != added.Id || loaded[0].TranslatedText != "xin chào")
                throw new InvalidOperationException("Vocabulary save/load failed.");
            VocabularyService.Delete(added.Id);
            if (VocabularyService.Load().Count != 0)
                throw new InvalidOperationException("Vocabulary delete failed.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("NATIVEWIDGET_DATA_DIR", null);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static async Task TestTranslationAsync()
    {
        var result = await TranslationService.TranslateAsync("Hello, how are you?", "en", "vi");
        if (result.SourceLanguage != "en" || result.TargetLanguage != "vi" ||
            string.IsNullOrWhiteSpace(result.TranslatedText) || result.TranslatedText == result.SourceText)
            throw new InvalidOperationException("Translation provider smoke test failed.");
        Console.WriteLine($"TRANSLATION_PASS={result.TranslatedText}");
    }

    private static async Task TestOcrAsync()
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, 700, 120));
            drawing.DrawText(new FormattedText("HELLO WORLD", CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Arial"), 56, Brushes.Black, 1), new Point(15, 20));
        }
        var bitmap = new RenderTargetBitmap(700, 120, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var text = await ScreenOcrService.ReadAsync(bitmap);
        if (!text.Contains("HELLO", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"OCR smoke test failed. Actual: {text}");
        Console.WriteLine($"OCR_PASS={text}");
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
