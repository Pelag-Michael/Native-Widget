using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NativeWidget;
using NativeWidget.Models;
using NativeWidget.Services;

internal static class UiRenderSmoke
{
    public static string Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "nativewidget-ui-render-" + Guid.NewGuid().ToString("N"));
        var notes = Path.Combine(root, "notes");
        Directory.CreateDirectory(notes);
        const string id = "ui-render";
        File.WriteAllText(Path.Combine(notes, id + ".md"), string.Join('\n',
            "# Heading one",
            "## Heading two",
            "Plain **bold**, *italic* and ~~strike~~",
            "- Bullet item",
            "1. Numbered item",
            "- [x] Completed to-do",
            "> Quote block",
            "```",
            "var code = true;",
            "```"));
        File.WriteAllText(Path.Combine(notes, "index.json"), JsonSerializer.Serialize(
            new List<NoteMeta>
            {
                new()
                {
                    Id = id,
                    Title = "A deliberately long note title that must not expand the card",
                    TitleIsCustom = true,
                    Preview = "first line\nsecond line\nthird line\nmore content that should be ellipsized",
                },
            }));

        Environment.SetEnvironmentVariable("NATIVEWIDGET_DATA_DIR", root);
        var savedTranslation = VocabularyService.Add(
            new TranslationResult("Hello world", "Xin chào thế giới", "en", "vi"), "clipboard", "Clipboard");
        VocabularyService.SetTags(savedTranslation.Id, new[] { "greeting", "daily" });
        var app = new App();
        app.InitializeComponent();
        // Construct every custom window while the app resources are live. This catches a
        // broken XAML resource/template even when the screenshot below only covers Notes.
        var parserSmoke = new Window[]
        {
            new MainWindow(),
            new CalendarWindow(new AppConfig()),
            new TasksWindow(new AppConfig()),
            new TimersWindow(),
            new FocusWindow(),
            new ProjectsWindow(),
            new LabelsWindow(),
            new SettingsWindow(new AppConfig(), () => Task.CompletedTask),
            new TranslationResultPopup(new TranslationResult("vincea.space", "vincea.space", "en", "vi"), "selection", "Smoke test"),
            new ScreenRegionOverlay(),
            new WorkspaceSearchWindow(),
        };
        var window = new NotesWindow(new AppConfig()) { Width = 420, Height = 540 };
        var translation = new TranslationWindow(new AppConfig { TranslationSelectionTrackingEnabled = false }) { Width = 390, Height = 520 };
        var resultPopup = new TranslationResultPopup(
            new TranslationResult("Visit vincea.space for the complete guide.", "Truy cập vincea.space để xem hướng dẫn đầy đủ.", "en", "vi"),
            "selection", "Smoke test");
        try
        {
            window.Show();
            var path = Path.Combine(root, "notes-list.png");
            Render(window, path);
            translation.Show();
            translation.SetVocabularyExpanded(false);
            translation.SetPanelExpanded(false, animate: false);
            var translationPath = Path.Combine(root, "translation.png");
            Render(translation, translationPath);
            translation.SetVocabularyExpanded(true);
            translation.SetMetadataFiltersVisible(true);
            translation.SetPanelExpanded(true, animate: false);
            var translationVocabularyPath = Path.Combine(root, "translation-vocabulary.png");
            Render(translation, translationVocabularyPath);
            resultPopup.Owner = translation;
            resultPopup.Show();
            var popupPath = Path.Combine(root, "translation-popup.png");
            Render(resultPopup, popupPath);
            return $"{path};{translationPath};{translationVocabularyPath};{popupPath}";
        }
        finally
        {
            window.Close();
            translation.Close();
            resultPopup.Close();
            foreach (var parserWindow in parserSmoke) parserWindow.Close();
            app.Shutdown();
            Environment.SetEnvironmentVariable("NATIVEWIDGET_DATA_DIR", null);
        }
    }

    private static void Render(Window window, string path)
    {
        window.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(window);
        var bitmap = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY)),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(window);
        using var stream = File.Create(path);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(stream);
    }
}
