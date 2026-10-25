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
            var launcher = new MainWindow { CodexBackendEnabled = false };
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
            var compactToggle = (CheckBox)panel.FindName("CompactBarCheck");
            compactToggle.IsChecked = true; compactToggle.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            Require(panel.CompactBarCollapsed && panel.Width == 36 && ((Grid)panel.FindName("ActionBar")).Visibility == Visibility.Collapsed,
                "Compact mode shows only the reveal icon and shrinks the actual window");
            ((Button)panel.FindName("CompactRevealButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(!panel.CompactBarCollapsed && panel.Width >= 250, "Reveal restores the action bar");
            panel.Hide(); panel.Show();
            Require(panel.CompactBarCollapsed, "A new hover visit starts compact again");
            compactToggle.IsChecked = false; compactToggle.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            var shortcuts = (WidgetShortcutsView)((Expander)panel.FindName("ShortcutsSection")).Content;
            var shortcutText = string.Join(" ", ((StackPanel)shortcuts.Content).Children.Cast<Border>()
                .SelectMany(row => ((Grid)row.Child).Children.OfType<TextBlock>()).Select(text => text.Text));
            Require(shortcuts.QuickAiOnly && shortcutText.Contains("Ctrl + \u2192") && shortcutText.Contains("Ctrl + \u2191")
                && !shortcutText.Contains("Ctrl + Alt + K") && !shortcutText.Contains("Notion"), "Quick AI shortcut reference must exclude original widget shortcuts");
            Require(panel.IsVisible, "Hovering the anchor must reveal AI panel.");
            launcher.UpdateInvisibleHoverPointer(false, true);
            Require(panel.IsVisible, "Moving into the panel must keep it open.");
            var input = (TextBox)panel.FindName("QuestionInput");
            var clipboardBackup = Clipboard.GetDataObject();
            try
            {
                Clipboard.SetText("shortcut paste");
                Require(launcher.HandleAiInputShortcut(0xB007) && panel.PastedItemCount == 1, "Ctrl+Down pastes into Quick AI");
                Require(launcher.HandleAiInputShortcut(0xB009) && panel.LastQuestion == "shortcut paste", "Ctrl+Right sends the current draft");
                panel.ShowDemoResponse();
                Require(launcher.HandleAiInputShortcut(0xB008) && !panel.HasInput
                    && ((Button)panel.FindName("CopyResponseButton")).IsEnabled, "Ctrl+Alt+Down empties input but keeps response");
                Require(launcher.HandleAiInputShortcut(0xB006) && !panel.HasInput
                    && !((Button)panel.FindName("CopyResponseButton")).IsEnabled, "Ctrl+Up resets into a new session");
            }
            finally { if (clipboardBackup != null) Clipboard.SetDataObject(clipboardBackup, true); else Clipboard.Clear(); }
            Require(Window.GetWindow(input) == panel.InputWindowForTests && Window.GetWindow(input) != panel,
                "Follow-up editor must live in its own floating window.");
            FindButton((FrameworkElement)panel.Content, "+").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(panel.InputWindowForTests?.IsVisible == true, "Plus opens the separate input panel.");
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
            Require(submissions == 0 && panel.LastQuestion.Length == 0, "Paste queues the draft without sending.");
            panel.ReceivePaste("second selection");
            Require(panel.PastedItemCount == 2 && submissions == 0, "Additional paste appends without sending.");
            panel.SubmitQuestion();
            Require(submissions == 1 && panel.LastQuestion.Contains(secret) && panel.LastQuestion.Contains("second selection") && panel.LastQuestion.Contains(input.Text), "Send composes all queued text and the follow-up.");
            Require(input.Text == "Explain briefly" && panel.PastedItemCount == 2, "Pasted context must stay out of the visible editor.");
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
            panel.ReceiveScreenshot(syntheticScreenshot);
            var status = panel.StatusWindowForTests!;
            var statusChrome = (System.Windows.Controls.Border)((Grid)status.Content).Children[0];
            var dot = (System.Windows.Shapes.Ellipse)statusChrome.Child;
            Require(((SolidColorBrush)dot.Fill).Color == (Color)ColorConverter.ConvertFromString("#FACC15"), "A screenshot flashes yellow.");
            var flashFrame = new DispatcherFrame();
            var flashWait = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2100) };
            flashWait.Tick += (_, _) => { flashWait.Stop(); flashFrame.Continue = false; };
            flashWait.Start(); Dispatcher.PushFrame(flashFrame);
            Require(((SolidColorBrush)dot.Fill).Color == (Color)ColorConverter.ConvertFromString("#4ADE80") && status.IsVisible,
                "After two seconds, queued input returns the dot to green even when the input panel hides.");
            var queuedImages = panel.ScreenshotPaths.ToArray();
            Require(queuedImages.Length == 2 && queuedImages.All(File.Exists) && submissions == 1,
                "Images append as PNG attachments in temp without auto sending.");
            HoverAiSubmission? sent = null; panel.SubmissionRequested += request => sent = request;
            panel.SubmitQuestion();
            Require(sent?.ImagePaths.Count == 2 && sent.Text.Contains(secret) && !sent.Text.Contains(queuedImages[0]),
                "Image attachments are carried separately from prompt text.");
            var mainDrag = (System.Windows.Controls.Primitives.Thumb)panel.FindName("MainDragHandle");
            mainDrag.RaiseEvent(new System.Windows.Controls.Primitives.DragStartedEventArgs(0,0) { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragStartedEvent });
            mainDrag.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(70,60) { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragDeltaEvent });
            mainDrag.RaiseEvent(new System.Windows.Controls.Primitives.DragCompletedEventArgs(70,60,false) { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragCompletedEvent });
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
            var appearance = (FrameworkElement)panel.FindName("PanelAppearanceControls");
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
            var layoutForSettings = (CheckBox)panel.FindName("EditLayoutCheck");
            layoutForSettings.IsChecked = true; layoutForSettings.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            ((Button)panel.FindName("BarSettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(panel.PanelSettingsForTests?.IsVisible == true && panel.InputWindowForTests!.VisibleCornerCount == 4 && panel.ResponseWindowForTests!.SettingsButton.IsVisible,
                "Edit layout exposes per-panel settings and all four corners.");
            panel.Opacity = 1;
            panel.Show();
            panel.PanelSettingsForTests!.UpdateLayout();
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
            var settingsPreview = panel.PanelSettingsForTests!; settingsPreview.UpdateLayout();
            var expandedImage = new RenderTargetBitmap((int)(settingsPreview.ActualWidth * 1.5), (int)(settingsPreview.ActualHeight * 1.5), 144, 144, PixelFormats.Pbgra32);
            expandedImage.Render((Visual)settingsPreview.Content);
            using (var file = File.Create(Path.Combine(data, "appearance-preview.png")))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(expandedImage));
                encoder.Save(file);
            }
            panel.EndLayoutEditing();
            Require(panel.InputWindowForTests!.VisibleCornerCount == 0 && !panel.ResponseWindowForTests!.SettingsButton.IsVisible && panel.PanelSettingsForTests?.IsVisible == false, "Leaving edit layout hides gears, visible corners and panel settings.");
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
            ((Slider)panel.FindName("StatusOpacitySlider")).Value = .22;
            ((Slider)panel.FindName("ResponseOpacitySlider")).Value = .43;
            Require(Math.Abs(response.Opacity - .43) < .01 && Math.Abs(panel.Opacity - .65) < .01,
                "Response opacity must be independent from the input bar.");
            var wideWidth = response.Width; var wideHeight = response.Height;
            response.Resize("R", 24 - response.Width, 0); response.UpdateLayout();
            Require(response.Width <= 25 && response.Height > wideHeight,
                "A narrow text box must wrap and expand downwards to retain its entire answer.");
            response.Resize("R", wideWidth - response.Width, 0); response.UpdateLayout();
            var editForResize = (CheckBox)panel.FindName("EditLayoutCheck");
            editForResize.IsChecked = true; editForResize.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            response.SettingsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(((FrameworkElement)panel.FindName("ResponseAppearanceGroup")).Visibility == Visibility.Visible && ((FrameworkElement)panel.FindName("AddAppearanceGroup")).Visibility == Visibility.Collapsed && response.VisibleCornerCount == 4,
                "The response gear opens only response controls while edit corners stay visible.");
            response.Resize("B", 0, 12 - response.Height); response.UpdateLayout();
            Require(response.Height <= 13, $"Layout editing must allow shrinking the frame to 12 pixels high: {response.Height}, editing={panel.EditingLayout}.");
            response.Resize("B", 0, 240 - response.Height); response.UpdateLayout();
            var beforeWidth = response.Width; var beforeHeight = response.Height;
            response.Resize("LT", -30, -25); response.SaveBounds();
            Require(Math.Abs(response.Width - beforeWidth - 30) < 1 && Math.Abs(response.Height - beforeHeight - 25) < 1, $"Left/top edges resize both dimensions: {beforeWidth}x{beforeHeight} -> {response.Width}x{response.Height}.");
            var bounds = AppConfig.Load(); HoverAiAppearanceStore.LoadInto(bounds);
            Require(bounds.HoverAiStatusOpacity == .22 && bounds.HoverAiResponseOpacity == .43,
                "Separate floating panel opacity must survive saving.");
            Require(bounds.HoverAiResponseWidth == response.Width && bounds.HoverAiResponseLeft == response.Left, "Response bounds persist independently.");
            Require(!bounds.HoverAiResponseAutoHeight && ((CheckBox)panel.FindName("AutoHeightCheck")).IsChecked == false,
                "Manual vertical resize disables and persists auto height.");
            panel.EndLayoutEditing();
            var fixedResponseHeight = response.Height;
            response.SetAnswer("AI", string.Join(" ", Enumerable.Repeat("Long answer wraps but stays inside the chosen height.", 80)));
            response.Resize("R", -40, 0); response.UpdateLayout();
            Require(Math.Abs(response.Height - fixedResponseHeight) < 1, "Long answers and horizontal resize must respect the manually set height.");
            editForResize.IsChecked = true; editForResize.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            ((TextBox)panel.FindName("CaptureX")).Text = "10";
            ((TextBox)panel.FindName("CaptureY")).Text = "20";
            ((TextBox)panel.FindName("CaptureWidth")).Text = "320";
            ((TextBox)panel.FindName("CaptureHeight")).Text = "180";
            var captureToggle = (CheckBox)panel.FindName("CaptureRegionCheck");
            captureToggle.IsChecked = true; captureToggle.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            var captureConfig = AppConfig.Load(); HoverAiAppearanceStore.LoadInto(captureConfig);
            Require(captureConfig.HoverAiCaptureRegionEnabled && captureConfig.HoverAiCaptureX == 10 && captureConfig.HoverAiCaptureHeight == 180, "Capture rectangle persists.");
            var addInput = panel.InputWindowForTests!;
            var previousResponseWidth = response.Width; var previousBarWidth = panel.Width;
            addInput.Resize("R", -60, 0); addInput.Left += 45; addInput.Top += 35; addInput.SaveBounds();
            Require(response.Width == previousResponseWidth && panel.Width == previousBarWidth, "Input resize must not change response or bar bounds.");
            ((Slider)panel.FindName("BarTextSlider")).Value = 15;
            ((Slider)panel.FindName("BarIconSlider")).Value = 17;
            ((Slider)panel.FindName("FollowUpFontSlider")).Value = 18;
            ((Slider)panel.FindName("FollowUpOpacitySlider")).Value = .4;
            Require(((Button)panel.FindName("PasteButton")).FontSize == 17 && ((Button)panel.FindName("SendButton")).FontSize == 17 && input.FontSize == 18 && Math.Abs(addInput.Opacity - .4) < .01,
                "Bar text, icons and follow-up typography/opacity must be independent.");
            var inputTextOnly = (CheckBox)panel.FindName("FollowUpTextOnlyCheck");
            inputTextOnly.IsChecked = false; inputTextOnly.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            panel.OpenPanelAppearance("Add input");
            panel.PanelSettingsForTests!.UpdateLayout();
            Require(((FrameworkElement)panel.FindName("BarAppearanceGroup")).Visibility == Visibility.Collapsed && ((FrameworkElement)panel.FindName("AddAppearanceGroup")).Visibility == Visibility.Visible, "Each gear exposes only its own panel settings.");
            FindButton(appearance, "Add panel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            wheel.Brightness = 1; wheel.Pick(new Point(wheel.ActualWidth - 5, wheel.ActualHeight / 2));
            Require(((SolidColorBrush)((System.Windows.Controls.Border)((Grid)addInput.Content).Children[0]).Background).Color == Colors.Red && ((SolidColorBrush)((System.Windows.Controls.Border)panel.Content).Background).Color != Colors.Red,
                "Follow-up panel color must not recolor the action bar.");
            panel.SelectColor(true, "#16161C");
            var inputPrefs = AppConfig.Load(); HoverAiAppearanceStore.LoadInto(inputPrefs);
            Require(inputPrefs.HoverAiFollowUpLeft == addInput.Left && inputPrefs.HoverAiFollowUpWidth == addInput.Width && inputPrefs.HoverAiFollowUpFontSize == 18 && inputPrefs.HoverAiBarIconSize == 17,
                "Follow-up bounds, typography and bar icon settings persist separately.");
            var capture = SilentScreenCapture.Capture(captureConfig);
            Require(capture.PixelWidth == 320 && capture.PixelHeight == 180, "Fixed capture uses exact physical pixel dimensions.");
            var edit = (CheckBox)panel.FindName("EditLayoutCheck");
            edit.IsChecked = true; edit.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            launcher.UpdateInvisibleHoverPointer(false, false);
            Require(panel.IsVisible && response.IsVisible, "Layout editing keeps demo panels visible.");
            panel.EndLayoutEditing();
            launcher.UpdateInvisibleHoverPointer(false, false);
            Require(!panel.IsVisible && !response.IsVisible && input.Text.Length > 0, "Leaving hides the panel and keeps its draft.");
            var beforeQuick = submissions; var beforeQuickCount = panel.PastedItemCount;
            panel.ReceiveQuickCopy("ignored while quicker is off");
            Require(submissions == beforeQuick && panel.PastedItemCount == beforeQuickCount, "Normal copy must not enqueue or send.");
            var quicker = (CheckBox)panel.FindName("QuickerCheck");
            quicker.IsChecked = true; quicker.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            panel.ReceiveQuickCopy("new quick selection");
            Require(submissions == beforeQuick + 1 && panel.LastQuestion.Contains("new quick selection") && panel.PastedItemCount == beforeQuickCount + 1,
                "Quicker enqueues a fresh copy and sends the complete draft once.");
            quicker.IsChecked = false; quicker.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            HoverAiSubmission? captureRequest = null; panel.SubmissionRequested += request => captureRequest = request;
            var shotMode = (CheckBox)panel.FindName("ScreenshotAutoSendCheck");
            shotMode.IsChecked = true; shotMode.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            ((TextBox)panel.FindName("ScreenshotPromptInput")).Text = "Read this screenshot only";
            var beforeCapture = submissions;
            panel.ReceiveScreenshot(syntheticScreenshot);
            Require(submissions == beforeCapture, "Clipboard image paste still queues in screenshot mode.");
            panel.ReceiveScreenshot(syntheticScreenshot, fromCapture: true);
            Require(submissions == beforeCapture + 1 && captureRequest?.Text == "Read this screenshot only" && captureRequest.ImagePaths.Count == 1 && captureRequest.ImagePaths[0] == panel.ScreenshotPaths.Last() && captureRequest.QueueIndividually,
                "Capture mode sends only the new image with its configured prompt, independently of queued draft text.");
            shotMode.IsChecked = false; shotMode.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            var sibling = Path.Combine(Path.GetDirectoryName(queuedImages[0])!, "unowned-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(sibling, "keep");
            ((Button)panel.FindName("ResetButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(panel.PastedItemCount == 0 && panel.ScreenshotPaths.Count == 0 && panel.LastScreenshot == null && input.Text.Length == 0 && !panel.HasInput,
                "Reset clears all draft text and image attachments.");
            Require(queuedImages.All(path => !File.Exists(path)) && File.Exists(sibling), "Reset deletes only this draft's image files.");
            Require(panel.StatusWindowForTests?.IsVisible == false, "An empty reset draft hides the dot.");
            File.Delete(sibling);
            launcher.SetInvisibleHoverMode(false);
            Require(launcher.Opacity == 1 && peer.IsVisible, "Disabling must restore launcher and peer.");
            var config = new AppConfig { InvisibleHoverEnabled = true };
            config.Save();
            Require(AppConfig.Load().InvisibleHoverEnabled, "Mode preference must survive restart.");
            peer.Close();
            launcher.Close();
            RunDwellChecks(data, (Style)panel.FindResource("IconHoverButton"));
            app.Shutdown();
            Console.WriteLine("PASS hover mode: queued paste/images, temp-file reset, Quicker dispatch, timed yellow/green receipt, flexible text frame, separate opacity, isolated preferences and mode restoration.");
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
    private static void RunDwellChecks(string data, Style iconStyle)
    {
        var clicks = 0;
        var nativeClickState = false;
        var enabled = true;
        var button = new Button { Content = "\u2191", Width = 80, Height = 40, Foreground = Brushes.White, Style = iconStyle };
        var decorator = new System.Windows.Documents.AdornerDecorator { Child = button };
        var window = new Window { Title = "Dwell test", Content = decorator, Width = 180, Height = 100,
            Left = SystemParameters.WorkArea.Right - 260, Top = SystemParameters.WorkArea.Bottom - 160, Topmost = true, ShowInTaskbar = false };
        var dwell = new HoverDwellController(window, _ => enabled);
        window.ShowActivated = false;
        window.SourceInitialized += (_, _) => WindowInterop.SetNoActivate(window, true);
        var foreground = new Window { Title = "Dwell foreground", Width = 200, Height = 100, Left = 20, Top = 500, ShowInTaskbar = false };
        button.Click += (_, _) => { clicks++; nativeClickState |= button.IsPressed || button.IsKeyboardFocused || !HoverDwellController.IsInvoking; };
        GetCursorPos(out var originalCursor);
        try
        {
            foreground.Show(); foreground.Activate();
            var activeWindow = GetForegroundWindow();
            window.Show(); window.UpdateLayout();
            var center = button.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
            void Enter() { SetCursorPos((int)center.X, (int)center.Y); Pump(100); }
            void Leave() { SetCursorPos((int)window.Left - 20, (int)window.Top - 20); Pump(100); }
            Leave(); Enter(); Pump(1300);
            GetCursorPos(out var currentCursor);
            Require(dwell.IsActive && dwell.Progress > .3 && clicks == 0, $"Dwell must show progress before its 3s trigger: active={dwell.IsActive}, progress={dwell.Progress}, clicks={clicks}, over={button.IsMouseOver}, target={System.Windows.Input.Mouse.DirectlyOver}, center={center}, cursor={currentCursor.X},{currentCursor.Y}");
            Require(System.Windows.Documents.AdornerLayer.GetAdornerLayer(button)?.GetAdorners(button)?.Length == 1, "Hovered button must have a visible progress adorner");
            var render = new RenderTargetBitmap((int)decorator.ActualWidth, (int)decorator.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            render.Render(decorator);
            using (var file = File.Create(Path.Combine(data, "dwell-progress.png"))) { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(render)); encoder.Save(file); }
            Leave(); Require(!dwell.IsActive && dwell.Progress == 0, "Leaving a button cancels and resets its fill");
            Enter(); Pump(3100); Require(clicks == 1 && !dwell.IsActive, "Holding for 3s invokes exactly one action");
            Require(!nativeClickState && !window.IsActive && GetForegroundWindow() == activeWindow,
                "Dwell must invoke directly without pressing/focusing the button or changing the foreground window");
            Pump(3100); Require(clicks == 1, "Holding after activation must not repeat or trigger Stop");
            Leave(); enabled = false; Enter(); Require(!dwell.IsActive, "Disabled hover setting must preserve click-only behavior");
            Leave(); enabled = true; button.IsEnabled = false; Enter(); Require(!dwell.IsActive, "Disabled buttons must never dwell-trigger");
        }
        finally { window.Close(); foreground.Close(); SetCursorPos(originalCursor.X, originalCursor.Y); }
    }
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct CursorPoint { public int X, Y; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPoint point);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
