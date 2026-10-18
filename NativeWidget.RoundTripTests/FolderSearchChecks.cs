using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NativeWidget;
using NativeWidget.Services;

internal static class FolderSearchChecks
{
    private sealed record Fixture(string Data, string Root, string Outside)
    {
        public static Fixture Create()
        {
            var basePath = Path.Combine(Path.GetTempPath(), "NativeWidget Folder Search " + Guid.NewGuid().ToString("N"));
            var data = Path.Combine(basePath, "isolated app data");
            var root = Path.Combine(basePath, "root with spaces");
            var outside = Path.Combine(basePath, "outside target");
            Directory.CreateDirectory(data);
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(outside);
            Directory.CreateDirectory(Path.Combine(root, "Alpha Folder", "Deep Match"));
            Directory.CreateDirectory(Path.Combine(root, "Alphabet Soup"));
            Directory.CreateDirectory(Path.Combine(root, "Beta"));
            Directory.CreateDirectory(Path.Combine(root, ".git", "Secret"));
            Directory.CreateDirectory(Path.Combine(root, "node_modules", "Secret"));
            Directory.CreateDirectory(Path.Combine(outside, "Should Not Be Indexed"));
            return new Fixture(data, root, outside);
        }

        public void Delete()
        {
            try { Directory.Delete(Path.GetDirectoryName(Data)!, recursive: true); } catch { }
        }
    }

    public static async Task RunAsync()
    {
        var oldDataDir = Environment.GetEnvironmentVariable("NATIVEWIDGET_DATA_DIR");
        var fixture = Fixture.Create();
        Environment.SetEnvironmentVariable("NATIVEWIDGET_DATA_DIR", fixture.Data);
        try
        {
            var service = new FolderSearchService();
            await service.LoadAsync();
            await service.SetRootsAsync(new[] { fixture.Root });
            await service.RefreshAsync(CancellationToken.None);

            Assert(service.Folders.Any(folder => folder.Name == "Alpha Folder"), "folder with spaces indexed");
            Assert(service.Folders.Any(folder => folder.Name == "Deep Match" && folder.Path.Contains("root with spaces", StringComparison.Ordinal)), "nested path retained");
            Assert(!service.Folders.Any(folder => folder.Name == "Secret"), "excluded directories skipped");
            Assert(FolderSearchService.RankMatch("Alpha Folder", "C:\\root\\Alpha Folder", "Alpha Folder") == 0, "exact ranking");
            Assert(FolderSearchService.RankMatch("Alpha Folder", "C:\\root\\Alpha Folder", "Alpha") == 1, "prefix ranking");
            Assert(FolderSearchService.RankMatch("Alpha Folder", "C:\\root\\Alpha Folder", "Folder") == 2, "name contains ranking");
            Assert(FolderSearchService.RankMatch("Deep Match", "C:\\root\\Deep Match", "C:\\root") == 3, "path prefix ranking");
            Assert(FolderSearchService.RankMatch("Deep Match", "C:\\root\\Deep Match", "oot") == 4, "path contains ranking");

            var symlinkCreated = false;
            try
            {
                Directory.CreateSymbolicLink(Path.Combine(fixture.Root, "Loop to ancestor"), fixture.Root);
                symlinkCreated = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                Console.WriteLine("SKIP reparse-loop creation (symlink privilege unavailable)");
            }
            if (symlinkCreated)
            {
                await service.RefreshAsync(CancellationToken.None);
                Assert(!service.Folders.Any(folder => folder.Name == "Loop to ancestor" || folder.Name == "Should Not Be Indexed"), "reparse loop was not traversed");
            }

            var cached = new FolderSearchService();
            await cached.LoadAsync();
            Assert(cached.Folders.Any(folder => folder.Name == "Deep Match"), "folder cache persisted and loaded");
            Assert(cached.Roots.Count == 1 && cached.Roots[0].Equals(fixture.Root, StringComparison.OrdinalIgnoreCase), "root persisted and loaded");
            Assert(File.Exists(Path.Combine(fixture.Data, "folder-search.json")), "search cache stored under isolated app data");

            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                var threw = false;
                try { await cached.RefreshAsync(cancelled.Token); }
                catch (OperationCanceledException) { threw = true; }
                Assert(threw, "pre-cancelled refresh stops promptly");
            }
            Assert(cached.Folders.Any(folder => folder.Name == "Deep Match"), "cancelled refresh preserves prior cache");

            await cached.SetRootsAsync(Array.Empty<string>());
            Assert(cached.Folders.Count == 0, "removed root immediately prunes its cached folders");
            var rootsRemoved = new FolderSearchService();
            await rootsRemoved.LoadAsync();
            Assert(rootsRemoved.Roots.Count == 0, "removing final root persists empty root list");
            Console.WriteLine("PASS folder indexing, exclusions, paths with spaces, ranking, cancellation, cache, and root add/remove");
        }
        finally
        {
            Environment.SetEnvironmentVariable("NATIVEWIDGET_DATA_DIR", oldDataDir);
            fixture.Delete();
        }
    }

    public static void Render(string outputPath)
    {
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var oldDataDir = Environment.GetEnvironmentVariable("NATIVEWIDGET_DATA_DIR");
        var oldInstance = Environment.GetEnvironmentVariable("NATIVEWIDGET_INSTANCE_SUFFIX");
        Environment.SetEnvironmentVariable("NATIVEWIDGET_INSTANCE_SUFFIX", "folder-render-" + Guid.NewGuid().ToString("N"));
        var fixture = Fixture.Create();
        Environment.SetEnvironmentVariable("NATIVEWIDGET_DATA_DIR", fixture.Data);
        try
        {
            var service = new FolderSearchService();
            service.LoadAsync().GetAwaiter().GetResult();
            service.SetRootsAsync(new[] { fixture.Root }).GetAwaiter().GetResult();
            service.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();

            var app = new App();
            app.InitializeComponent();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var window = new WorkspaceSearchWindow
            {
                Left = 120,
                Top = 100,
                Width = 560,
                Height = 520,
                Topmost = false,
            };
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
            var load = window.LoadFolderIndexAsync();
            while (!load.IsCompleted) PumpDispatcher(TimeSpan.FromMilliseconds(50));
            load.GetAwaiter().GetResult();
            var folderMode = (ToggleButton)window.FindName("FoldersOnlyButton");
            folderMode.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            var search = (TextBox)window.FindName("SearchBox");
            search.Text = "Alpha Folder";
            var status = (TextBlock)window.FindName("FolderStatus");
            var rootsButton = (Button)window.FindName("RootsButton");
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while ((!rootsButton.IsEnabled || status.Text.StartsWith("Loading folder", StringComparison.Ordinal)) && DateTime.UtcNow < deadline)
                PumpDispatcher(TimeSpan.FromMilliseconds(100));

            var results = (ListBox)window.FindName("ResultsList");
            Assert(!status.Text.StartsWith("Loading folder", StringComparison.Ordinal), "folder cache finished loading before capture");
            Assert(results.Items.Count > 0, "folder results render before capture");
            results.SelectedIndex = 0;
            window.Show();
            window.UpdateLayout();
            PumpDispatcher(TimeSpan.FromMilliseconds(250));
            Assert(((FrameworkElement)window.FindName("FolderActions")).Visibility == Visibility.Visible, "folder actions visible for selected match");
            Console.WriteLine($"Render state: status='{status.Text}', query='{search.Text}', results={results.Items.Count}, selected={results.SelectedIndex}");

            var content = (FrameworkElement)window.Content!;
            content.Measure(new Size(window.Width, window.Height));
            content.Arrange(new Rect(0, 0, window.Width, window.Height));
            InvalidateSubtree(content);
            content.UpdateLayout();
            PumpDispatcher(TimeSpan.FromMilliseconds(300));
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);
            var width = (int)Math.Ceiling(window.ActualWidth * 1.5);
            var height = (int)Math.Ceiling(window.ActualHeight * 1.5);
            var bitmap = new RenderTargetBitmap(width, height, 144, 144, PixelFormats.Pbgra32);
            bitmap.Render(content);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            using var file = File.Create(outputPath);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            encoder.Save(file);
            window.Close();
            app.Shutdown();
            Console.WriteLine($"Rendered folder search UI: {outputPath}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("NATIVEWIDGET_DATA_DIR", oldDataDir);
            Environment.SetEnvironmentVariable("NATIVEWIDGET_INSTANCE_SUFFIX", oldInstance);
            fixture.Delete();
        }
    }

    private static void PumpDispatcher(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void InvalidateSubtree(Visual visual)
    {
        if (visual is FrameworkElement element) element.InvalidateVisual();
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(visual); index++)
            if (VisualTreeHelper.GetChild(visual, index) is Visual child) InvalidateSubtree(child);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Folder search check failed: " + message);
    }
}
