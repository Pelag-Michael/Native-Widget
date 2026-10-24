using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NativeWidget;
using NativeWidget.Models;
using NativeWidget.Services;

internal static class HoverModeChecks
{
    public static void Run()
    {
        var data = Path.Combine(Path.GetTempPath(), "nativewidget-hover-" + Guid.NewGuid().ToString("N"));
        var previous = Environment.GetEnvironmentVariable("NATIVEWIDGET_DATA_DIR");
        var previousInstance = Environment.GetEnvironmentVariable("NATIVEWIDGET_INSTANCE_SUFFIX");
        Directory.CreateDirectory(data);
        Environment.SetEnvironmentVariable("NATIVEWIDGET_DATA_DIR", data);
        Environment.SetEnvironmentVariable("NATIVEWIDGET_INSTANCE_SUFFIX", Guid.NewGuid().ToString("N"));
        try
        {
            var app = new App();
            app.InitializeComponent();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var launcher = new MainWindow();
            launcher.Show();
            var peer = new Window { Width = 100, Height = 100, ShowInTaskbar = false };
            peer.Show();
            var originalLauncherPosition = new Point(launcher.Left, launcher.Top);
            var originalAppAccent = ((SolidColorBrush)app.Resources["AccentBrush"]).Color;
            var originalSettingsBytes = File.Exists(Path.Combine(data, "config.json")) ? File.ReadAllText(Path.Combine(data, "config.json")) : null;
            launcher.SetInvisibleHoverMode(true);
            Require(launcher.Opacity == 0 && !peer.IsVisible, "Idle launcher and peer must be invisible.");
            launcher.ShowHoverAnchorFrame();
            Require(launcher.AnchorFrameVisibleForTests && launcher.HoverPanelForTests?.IsVisible != true,
                "Locate must outline the hotspot without opening the AI panel.");
            launcher.UpdateInvisibleHoverPointer(false, false);
            launcher.UpdateInvisibleHoverPointer(true, false);
            var panel = launcher.HoverPanelForTests!;
            Require(panel.IsVisible, "Hovering the anchor must reveal AI panel.");
            launcher.UpdateInvisibleHoverPointer(false, true);
            Require(panel.IsVisible, "Moving into the panel must keep it open.");
            var input = (TextBox)panel.FindName("QuestionInput");
            input.Text = "Explain briefly";
            var submissions = 0;
            panel.QuestionSubmitted += _ => submissions++;
            const string secret = "Private pasted content that must never be shown";
            var pasteData = new DataObject(DataFormats.UnicodeText, secret);
            input.RaiseEvent(new DataObjectPastingEventArgs(pasteData, false, DataFormats.UnicodeText));
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Require(submissions == 1 && panel.LastQuestion.Contains(secret) && panel.LastQuestion.Contains(input.Text), "Paste must automatically submit once with optional message.");
            Require(input.Text == "Explain briefly" && panel.PastedItemCount == 1, "Pasted context must stay out of the visible editor.");
            Require(((FrameworkElement)panel.FindName("PasteDot")).Visibility == Visibility.Collapsed, "Receipt dot must live in its independent panel.");
            Require(!((Button)panel.FindName("CopyResponseButton")).IsEnabled, "Backend status must not enable response copying.");
            Require(((Button)panel.FindName("SendButton")).Visibility == Visibility.Visible, "Send must be present in minimal mode.");
            Require(panel.FindName("Wheel") is ColorWheel, "Appearance must provide a circular HSV picker.");
            Require(((TextBlock)panel.FindName("AnswerText")).Text.Contains("not connected"), "UI must identify missing backend.");
            Require(panel.ForceCursor && panel.Cursor == System.Windows.Input.Cursors.Arrow,
                "Mode must keep the arrow cursor over buttons.");
            var syntheticScreenshot = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgr32, null,
                new byte[16], 8);
            var wasVisible = panel.IsVisible;
            panel.ReceiveScreenshot(syntheticScreenshot);
            Require(panel.LastScreenshot == syntheticScreenshot && panel.IsVisible == wasVisible && input.Text == "Explain briefly",
                "Receiving a screenshot must store it privately without showing a window or changing input text.");
            panel.Left += 70;
            panel.Top += 60;
            panel.SavePosition();
            Require(Math.Abs(launcher.HoverAnchorForTests.X - panel.Left - 12) < 1 && Math.Abs(launcher.HoverAnchorForTests.Y - panel.Top - 12) < 1,
                "Dragging must move the invisible hover anchor along with the panel.");
            Require(new Point(launcher.Left, launcher.Top) == originalLauncherPosition, "AI dragging must not move the original launcher.");
            var savedPosition = AppConfig.Load();
            HoverAiAppearanceStore.LoadInto(savedPosition);
            Require(savedPosition.HoverAiLeft == panel.Left, "Panel position must persist separately.");
            // Pointer polling can hide the panel while the dispatcher runs; show once for render.
            launcher.UpdateInvisibleHoverPointer(false, false);
            launcher.UpdateInvisibleHoverPointer(true, false);
            panel.UpdateLayout();
            Require(((Button)panel.FindName("SendButton")).IsVisible, "Send must be visible when the minimal panel is shown.");
            var image = new RenderTargetBitmap((int)(panel.ActualWidth * 1.5), (int)(panel.ActualHeight * 1.5), 144, 144, PixelFormats.Pbgra32);
            image.Render((Visual)panel.Content);
            var preview = Path.Combine(data, "hover-ai-preview.png");
            using (var file = File.Create(preview))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                encoder.Save(file);
            }
            ((Slider)panel.FindName("OpacitySlider")).Value = .65;
            ((Slider)panel.FindName("WidthSlider")).Value = 260;
            ((Slider)panel.FindName("FontSlider")).Value = 14;
            ((TextBox)panel.FindName("BackgroundInput")).Text = "#202030";
            ((TextBox)panel.FindName("ForegroundInput")).Text = "#AABBCC";
            // Exercise color validation/save through the actual button event.
            var appearance = (FrameworkElement)panel.FindName("AppearancePanel");
            panel.SelectColor(true, "#202030");
            panel.SelectColor(false, "#AABBCC");
            var saved = AppConfig.Load();
            HoverAiAppearanceStore.LoadInto(saved);
            Require(saved.HoverAiOpacity == .65 && saved.HoverAiWidth == 260 && saved.HoverAiFontSize == 14 &&
                saved.HoverAiBackground == "#202030" && saved.HoverAiForeground == "#AABBCC", "Appearance settings must persist.");
            ((TextBox)panel.FindName("BackgroundInput")).Text = "invalid";
            panel.SelectColor(true, "invalid");
            var savedAfterInvalid = AppConfig.Load();
            HoverAiAppearanceStore.LoadInto(savedAfterInvalid);
            Require(savedAfterInvalid.HoverAiBackground == "#202030", "Invalid colors must not replace saved preferences.");
            Require(((SolidColorBrush)app.Resources["AccentBrush"]).Color == originalAppAccent,
                "AI colors must not modify global widget colors.");
            Require((File.Exists(Path.Combine(data, "config.json")) ? File.ReadAllText(Path.Combine(data, "config.json")) : null) == originalSettingsBytes,
                "AI appearance must not write the original settings file.");
            Require(ColorWheel.FromHsv(0, 1, 1) == Colors.Red && ColorWheel.FromHsv(120, 1, 1) == Colors.Lime &&
                    ColorWheel.FromHsv(240, 1, 1) == Colors.Blue, "Color wheel hue mapping must be correct.");
            panel.SelectColor(true, "#202030");
            appearance.Visibility = Visibility.Visible;
            panel.Opacity = 1;
            panel.Show();
            panel.UpdateLayout();
            FindButton(appearance, "Icons").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var wheel = (ColorWheel)panel.FindName("Wheel");
            wheel.Brightness = 1;
            wheel.Pick(new Point(wheel.ActualWidth - 5, wheel.ActualHeight / 2));
            Require(((SolidColorBrush)((Button)panel.FindName("SendButton")).Foreground).Color == Colors.Red &&
                    ((SolidColorBrush)((Button)panel.FindName("PasteButton")).Foreground).Color == Colors.Red,
                "Wheel selection must recolor all AI action buttons together.");
            Require(((SolidColorBrush)app.Resources["AccentBrush"]).Color == originalAppAccent,
                "Icon recoloring must not touch the original app accent.");
            var colorRenderFrame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
                new Action(() => colorRenderFrame.Continue = false));
            Dispatcher.PushFrame(colorRenderFrame);
            panel.Show();
            panel.UpdateLayout();
            var expandedImage = new RenderTargetBitmap((int)(panel.ActualWidth * 1.5), (int)(panel.ActualHeight * 1.5), 144, 144, PixelFormats.Pbgra32);
            expandedImage.Render((Visual)panel.Content);
            using (var file = File.Create(Path.Combine(data, "appearance-preview.png")))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(expandedImage));
                encoder.Save(file);
            }
            panel.Width = 340;
            panel.SelectColor(true, "#16161C");
            panel.SelectColor(false, "#EEEEF2");
            panel.ShowDemoResponse();
            panel.UpdateLayout();
            var demoFrame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => demoFrame.Continue = false));
            Dispatcher.PushFrame(demoFrame);
            panel.Show(); panel.UpdateLayout();
            Require(((TextBlock)panel.FindName("ResponseLabel")).Text.StartsWith("DEMO"), "Demo response must be labelled clearly.");
            var floating = panel.ResponseWindowForTests!; floating.Show(); floating.UpdateLayout();
            var demoImage = new RenderTargetBitmap((int)(floating.ActualWidth * 1.5), (int)(floating.ActualHeight * 1.5), 144, 144, PixelFormats.Pbgra32);
            demoImage.Render((Visual)floating.Content);
            using (var file = File.Create(Path.Combine(data, "ai-answer-demo.png")))
            {
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(demoImage)); encoder.Save(file);
            }
            Require(((Button)panel.FindName("CopyResponseButton")).IsEnabled, "Demo answer enables copy.");
            var response = panel.ResponseWindowForTests!;
            response.Show(); response.UpdateLayout();
            var beforeWidth = response.Width; var beforeHeight = response.Height;
            response.Resize("LT", -30, -25); response.SaveBounds();
            Require(Math.Abs(response.Width - beforeWidth - 30) < 1 && Math.Abs(response.Height - beforeHeight - 25) < 1, $"Left/top edges resize both dimensions: {beforeWidth}x{beforeHeight} -> {response.Width}x{response.Height}.");
            var bounds = AppConfig.Load(); HoverAiAppearanceStore.LoadInto(bounds);
            Require(bounds.HoverAiResponseWidth == response.Width && bounds.HoverAiResponseLeft == response.Left, "Response bounds persist independently.");
            ((TextBox)panel.FindName("CaptureX")).Text = "10";
            ((TextBox)panel.FindName("CaptureY")).Text = "20";
            ((TextBox)panel.FindName("CaptureWidth")).Text = "320";
            ((TextBox)panel.FindName("CaptureHeight")).Text = "180";
            var captureToggle = (CheckBox)panel.FindName("CaptureRegionCheck");
            captureToggle.IsChecked = true; captureToggle.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            var captureConfig = AppConfig.Load(); HoverAiAppearanceStore.LoadInto(captureConfig);
            Require(captureConfig.HoverAiCaptureRegionEnabled && captureConfig.HoverAiCaptureX == 10 && captureConfig.HoverAiCaptureHeight == 180, "Capture rectangle persists.");
            var capture = SilentScreenCapture.Capture(captureConfig);
            Require(capture.PixelWidth == 320 && capture.PixelHeight == 180, "Fixed capture uses exact physical pixel dimensions.");
            var edit = (CheckBox)panel.FindName("EditLayoutCheck");
            edit.IsChecked = true; edit.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            launcher.UpdateInvisibleHoverPointer(false, false);
            Require(panel.IsVisible && response.IsVisible, "Layout editing keeps demo panels visible.");
            panel.EndLayoutEditing();
            launcher.UpdateInvisibleHoverPointer(false, false);
            Require(!panel.IsVisible && !response.IsVisible && input.Text.Length > 0, "Leaving hides the panel and keeps its draft.");
            launcher.SetInvisibleHoverMode(false);
            Require(launcher.Opacity == 1 && peer.IsVisible, "Disabling must restore launcher and peer.");
            var config = new AppConfig { InvisibleHoverEnabled = true };
            config.Save();
            Require(AppConfig.Load().InvisibleHoverEnabled, "Mode preference must survive restart.");
            peer.Close();
            launcher.Close();
            app.Shutdown();
            Console.WriteLine("PASS hover mode: private paste, white dot, follow-up composition, anchor movement, appearance persistence, invalid color safety, invisible idle and peer restore.");
            Console.WriteLine("PREVIEW=" + preview);
            Console.WriteLine("DEMO=" + Path.Combine(data, "ai-answer-demo.png"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("NATIVEWIDGET_DATA_DIR", previous);
            Environment.SetEnvironmentVariable("NATIVEWIDGET_INSTANCE_SUFFIX", previousInstance);
        }
    }
    private static Button FindButton(FrameworkElement parent, string content)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<FrameworkElement>())
        {
            if (child is Button button && Equals(button.Content, content)) return button;
            try { return FindButton(child, content); } catch (InvalidOperationException) { }
        }
        throw new InvalidOperationException("Button not found: " + content);
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
