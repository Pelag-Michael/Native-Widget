using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
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
        new AppConfig { RestoreWindowSessionEnabled = true }.Save();
        File.WriteAllText(AppConfig.TokenPath("window-session.json"), JsonSerializer.Serialize(
            new WindowSessionSnapshot
            {
                Windows =
                {
                    new WindowSessionEntry
                    {
                        Key = "Notes", Kind = "Notes", IsOpen = true,
                        Left = SystemParameters.VirtualScreenLeft + 80,
                        Top = SystemParameters.VirtualScreenTop + 80,
                        Width = 410, Height = 530,
                    },
                },
            }));
        var savedTranslation = VocabularyService.Add(
            new TranslationResult("Hello world", "Xin chào thế giới", "en", "vi"), "clipboard", "Clipboard");
        VocabularyService.SetTags(savedTranslation.Id, new[] { "greeting", "daily" });
        var app = new App();
        app.InitializeComponent();
        // Construct every custom window while the app resources are live. This catches a
        // broken XAML resource/template even when the screenshot below only covers Notes.
        var launcher = new MainWindow();
        launcher.Show();
        var restoredNotes = app.Windows.OfType<NotesWindow>()
            .FirstOrDefault(candidate => candidate.IsVisible);
        if (restoredNotes == null || Math.Abs(restoredNotes.Width - 410) > 1 ||
            Math.Abs(restoredNotes.Height - 530) > 1)
            throw new InvalidOperationException("Startup did not restore the saved Notes window bounds.");

        var topmostChallenger = new Window
        {
            Width = 80, Height = 80, Left = launcher.Left + 70, Top = launcher.Top,
            Topmost = true, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
        };
        topmostChallenger.Show();
        topmostChallenger.Activate();
        launcher.EnsureLauncherTopmostForTests();
        var launcherHandle = (PresentationSource.FromVisual(launcher) as HwndSource)?.Handle ?? IntPtr.Zero;
        var challengerHandle = (PresentationSource.FromVisual(topmostChallenger) as HwndSource)?.Handle ?? IntPtr.Zero;
        if (!IsAbove(launcherHandle, challengerHandle))
        {
            var zOrder = TopLevelWindows();
            throw new InvalidOperationException(
                $"Launcher did not reclaim the front of the topmost z-order (launcherHandle={launcherHandle}, challengerHandle={challengerHandle}, launcher={zOrder.IndexOf(launcherHandle)}, challenger={zOrder.IndexOf(challengerHandle)}, windows={zOrder.Count}, launcherTopmost={launcher.Topmost}).");
        }
        launcher.SetWindowToolsOpen(false);
        var windowToolsButton = (Button)launcher.FindName("BtnWindowTools");
        windowToolsButton.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
        {
            RoutedEvent = Mouse.MouseEnterEvent,
        });
        var windowToolsPopup = (Popup)launcher.FindName("WindowToolsPopup");
        if (!windowToolsPopup.IsOpen)
            throw new InvalidOperationException("Hovering Window Tools did not open its panel.");
        topmostChallenger.Close();
        restoredNotes.Topmost = false;
        var globalPin = (Button)launcher.FindName("GlobalPinBtn");
        globalPin.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (!restoredNotes.Topmost) throw new InvalidOperationException("Global pin did not affect the visible widget.");
        var globalOpacity = (Slider)launcher.FindName("GlobalOpacitySlider");
        globalOpacity.Value = 0.72;
        if (Math.Abs(restoredNotes.Opacity - 0.72) > 0.01)
            throw new InvalidOperationException("Global opacity did not affect the visible widget.");
        var globalGhost = (Button)launcher.FindName("GlobalGhostBtn");
        globalGhost.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (!WindowInterop.IsClickThrough(restoredNotes))
            throw new InvalidOperationException("Global ghost did not affect the visible widget.");
        globalGhost.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (WindowInterop.IsClickThrough(restoredNotes))
            throw new InvalidOperationException("Global ghost could not restore the visible widget.");

        // Shelf on the already-live restored Notes window (proxy owns the taskbar button).
        var shelfWin = restoredNotes;
        if (new WindowInteropHelper(shelfWin).Handle == IntPtr.Zero)
            throw new InvalidOperationException("Notes HWND missing before shelf test.");
        var shelfTitle = shelfWin.Title;
        var shelfBtn = (Button)shelfWin.Header.FindName("ShelfBtn")
            ?? throw new InvalidOperationException("ShelfBtn not found on Notes header.");
        shelfBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (!WindowInterop.IsClickThrough(shelfWin) || !shelfWin.Header.IsShelved)
            throw new InvalidOperationException(
                $"Shelf did not enable click-through (clickThrough={WindowInterop.IsClickThrough(shelfWin)}, " +
                $"shelved={shelfWin.Header.IsShelved}, hwnd={new WindowInteropHelper(shelfWin).Handle}).");
        if (!shelfWin.IsLoaded)
            throw new InvalidOperationException("Notes died immediately after shelf enable.");
        var proxy = Application.Current.Windows.OfType<Window>()
            .FirstOrDefault(w => !ReferenceEquals(w, shelfWin) && w.ShowInTaskbar && w.Title == shelfTitle);
        if (proxy == null)
            throw new InvalidOperationException("Shelf did not create a taskbar proxy window.");
        // Restore via ClearShelf (Ctrl+Alt+G / launcher path). Taskbar-icon click is the
        // same RestoreFromShelf() used when the proxy raises Activated after arming.
        shelfWin.Header.ClearShelf();
        if (shelfWin.Header.IsShelved || WindowInterop.IsClickThrough(shelfWin))
            throw new InvalidOperationException("ClearShelf did not fully restore the widget.");
        if (proxy.IsVisible || proxy.ShowInTaskbar)
            throw new InvalidOperationException("ClearShelf did not dismiss the taskbar proxy.");
        if (!shelfWin.IsLoaded || !shelfWin.IsVisible)
            throw new InvalidOperationException("ClearShelf destroyed Notes.");

        var windowToolsPath = Path.Combine(root, "global-window-tools.png");
        Render((FrameworkElement)launcher.FindName("WindowToolsPanel"), windowToolsPath);
        globalOpacity.Value = 1;
        var globalClose = (Button)launcher.FindName("GlobalCloseBtn");
        globalClose.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (restoredNotes.IsVisible) throw new InvalidOperationException("Global close did not hide the visible widget.");
        restoredNotes.Show();

        var parserSmoke = new Window[]
        {
            launcher,
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
            new TranslationResult("engage", "đính hôn", "en", "vi",
                new[] { new TranslationMeaningGroup("verb", new[] { "thuê", "bận việc", "giao ước", "hứa hẹn", "mướn" }) },
                new[] { "they attempted to engage Anthony in conversation", "the teams needed to engage with local communities", "the clutch will not engage" }),
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
            return $"{path};{translationPath};{translationVocabularyPath};{popupPath};{windowToolsPath}";
        }
        finally
        {
            window.Close();
            translation.Close();
            resultPopup.Close();
            restoredNotes.Close();
            foreach (var parserWindow in parserSmoke) parserWindow.Close();
            app.Shutdown();
            Environment.SetEnvironmentVariable("NATIVEWIDGET_DATA_DIR", null);
        }
    }

    private static void Render(FrameworkElement window, string path)
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

    private static bool IsAbove(IntPtr expectedUpper, IntPtr expectedLower)
    {
        var windows = TopLevelWindows();
        var upperIndex = windows.IndexOf(expectedUpper);
        var lowerIndex = windows.IndexOf(expectedLower);
        return upperIndex >= 0 && lowerIndex >= 0 && upperIndex < lowerIndex;
    }

    private static int ZOrderIndex(IntPtr target) => TopLevelWindows().IndexOf(target);

    private static List<IntPtr> TopLevelWindows()
    {
        var windows = new List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            windows.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
}
