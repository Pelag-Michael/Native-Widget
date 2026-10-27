using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NativeWidget.Models;
using NativeWidget.Services;

namespace NativeWidget;

public sealed record HoverAiSubmission(string Text, IReadOnlyList<string> ImagePaths, bool QueueIndividually = false)
{
    internal long DraftPasteThrough { get; init; }
    internal long DraftQuestionRevision { get; init; }
    internal string[] DraftImages { get; init; } = Array.Empty<string>();
    internal string DraftQuestionText { get; init; } = "";
    internal string? PresetText { get; init; }
    internal string? SystemPrompt { get; init; }
    internal string? Model { get; init; }
    internal string? Effort { get; init; }
}

public partial class HoverAiWindow : Window
{
    private readonly AppConfig _config;
    private readonly List<(long Id, string Text)> _pastedContext = new();
    private long _pasteSequence, _questionRevision;
    private HoverDwellController? _buttonDwell;
    private bool _compactExpanded;
    private double _expandedMinimumWidth = 250;
    internal bool CompactBarCollapsed => CompactRevealButton.Visibility == Visibility.Visible;
    private readonly HoverAiScreenshotStore _screenshots = new();
    private readonly DispatcherTimer _screenshotFlashTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _screenshotFlash;
    private bool _modeActive = true;
    internal bool HasInput => _pastedContext.Count > 0 || _screenshots.Files.Count > 0 || !string.IsNullOrWhiteSpace(QuestionInput.Text);
    internal IReadOnlyList<string> ScreenshotPaths => _screenshots.Files;
    public event Action<HoverAiSubmission>? SubmissionRequested;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private bool _ready;
    private bool _dragging;
    private bool _updatingWheel;
    private string _colorTarget = "Panel";
    private bool _showingResponse;
    private HoverAiFloatingPanel? _statusWindow, _responseWindow, _inputWindow;
    private bool _followUpOpen, _applyingAppearance;
    private HoverPanelSettingsWindow? _panelSettings;
    private HoverAiShortcutsWindow? _shortcutsWindow;
    internal HoverAiShortcutsWindow? ShortcutsWindowForTests => _shortcutsWindow;
    private string _responseText = "";
    private bool _selectingCapture;
    private bool _preferencesPinned;
    internal bool EditingLayout => EditLayoutCheck.IsChecked == true;
    internal HoverAiFloatingPanel? ResponseWindowForTests => _responseWindow;
    internal HoverAiFloatingPanel? StatusWindowForTests => _statusWindow;
    internal HoverAiFloatingPanel? InputWindowForTests => _inputWindow;
    internal bool OwnsAuxiliaryWindow(Window window) => window == _statusWindow || window == _responseWindow || window == _inputWindow || window == _panelSettings || window == _shortcutsWindow || (_selectingCapture && window is ScreenRegionOverlay);
    internal bool IsOverAuxiliary(Point screen) => new[] { _statusWindow, _responseWindow, _inputWindow }.Any(w => w?.IsVisible == true && new Rect(new Point(), w.RenderSize).Contains(w.PointFromScreen(screen)));

    internal BitmapSource? LastScreenshot { get; private set; }
    public event Action<BitmapSource>? ScreenshotReceived;
    private readonly ContextMenu _pasteMenu = new();
    public event Action? SettingsRequested;
    public event Action<string>? QuestionSubmitted;
    public event Action? DismissRequested;
    public event Action? PositionChanged;
    public string LastQuestion { get; private set; } = "";
    public bool IsPointerInteractionActive => _preferencesPinned || EditingLayout || _selectingCapture || _dragging || _pasteMenu.IsOpen || _buttonDwell?.IsActive == true || Wheel.IsMouseCaptured || _inputWindow?.IsInteracting == true || _responseWindow?.IsInteracting == true || _statusWindow?.IsInteracting == true || CodexModelPicker.IsDropDownOpen || CodexEffortPicker.IsDropDownOpen;
    internal int PastedItemCount => _pastedContext.Count;

    public HoverAiWindow() : this(AppConfig.Load()) { }
    public HoverAiWindow(AppConfig config)
    {
        _config = config;
        HoverAiAppearanceStore.LoadInto(config);
        config.HoverAiShortcutsBackground ??= config.HoverAiBackground;
        config.HoverAiShortcutsForeground ??= config.HoverAiForeground;
        config.HoverAiShortcutsOpacity ??= config.HoverAiOpacity;
        config.HoverAiBarTextSize ??= config.HoverAiFontSize;
        config.HoverAiFollowUpFontSize ??= config.HoverAiFontSize;
        config.HoverAiFollowUpOpacity ??= config.HoverAiOpacity;
        config.HoverAiFollowUpBackground ??= config.HoverAiBackground;
        config.HoverAiFollowUpForeground ??= config.HoverAiForeground;
        _followUpOpen = !config.HoverAiMinimal;
        config.HoverAiStatusOpacity ??= config.HoverAiOpacity;
        config.HoverAiResponseOpacity ??= config.HoverAiOpacity;
        config.HoverAiDotColor ??= config.HoverAiIconColor;
        config.HoverAiStatusBackground ??= config.HoverAiBackground;
        config.HoverAiResponseBackground ??= config.HoverAiBackground;
        config.HoverAiResponseForeground ??= config.HoverAiForeground;
        InitializeComponent();
        _buttonDwell = new HoverDwellController(this, button => _modeActive &&
            !_compactMoving && (button == CompactRevealButton || _config.HoverAiDwellButtons));
        InitializeCompactMove();
        InitializeLocateColors();
        InitializePromptPresets();
        SourceInitialized += (_, _) => RefreshCompactBar();
        QuestionInput.TextChanged += (_, _) => ++_questionRevision;
        WindowInterop.HideFromAltTab(this);
        InitializeCodexControls();
        ScreenshotAutoSendCheck.IsChecked = config.HoverAiScreenshotAutoSend;
        ScreenshotPromptInput.Text = config.HoverAiScreenshotPrompt;
        QuickerCheck.IsChecked = config.HoverAiQuicker;
        CompactBarCheck.IsChecked = config.HoverAiCompactBar;
        DwellButtonsCheck.IsChecked = config.HoverAiDwellButtons;
        try { ScreenshotFolderInput.Text = HoverAiScreenshotStore.ResolveFolder(config.HoverAiScreenshotFolder); }
        catch (ArgumentException) { config.HoverAiScreenshotFolder = ""; ScreenshotFolderInput.Text = HoverAiScreenshotStore.DefaultFolder; }
        _screenshotFlashTimer.Tick += (_, _) => FinishScreenshotFlash();
        CaptureRegionCheck.IsChecked = config.HoverAiCaptureRegionEnabled;
        CaptureX.Text = config.HoverAiCaptureX.ToString(); CaptureY.Text = config.HoverAiCaptureY.ToString();
        CaptureWidth.Text = config.HoverAiCaptureWidth.ToString(); CaptureHeight.Text = config.HoverAiCaptureHeight.ToString();
        MinimalCheck.IsChecked = config.HoverAiMinimal;
        OpacitySlider.Value = Safe(config.HoverAiOpacity, .15, 1, 1);
        StatusOpacitySlider.Value = Safe(config.HoverAiStatusOpacity.Value, 0, 1, 1);
        ResponseOpacitySlider.Value = Safe(config.HoverAiResponseOpacity.Value, 0, 1, 1);
        ShortcutsOpacitySlider.Value = Safe(config.HoverAiShortcutsOpacity.Value, 0, 1, 1);
        ShortcutsFontSlider.Value = Safe(config.HoverAiShortcutsFontSize, 6, 24, 10.5);
        ShortcutsTextOnlyCheck.IsChecked = config.HoverAiShortcutsTextOnly;
        TextOnlyCheck.IsChecked = config.HoverAiResponseTextOnly;
        AutoHeightCheck.IsChecked = config.HoverAiResponseAutoHeight;
        WidthSlider.Value = Safe(config.HoverAiWidth, 250, 600, 260);
        FontSlider.Value = Safe(config.HoverAiFontSize, 9, 48, 12);
        BarTextSlider.Value = Safe(config.HoverAiBarTextSize.Value, 6, 32, 12);
        BarIconSlider.Value = Safe(config.HoverAiBarIconSize, 6, 32, 11);
        FollowUpFontSlider.Value = Safe(config.HoverAiFollowUpFontSize.Value, 6, 48, 12);
        FollowUpOpacitySlider.Value = Safe(config.HoverAiFollowUpOpacity.Value, 0, 1, 1);
        FollowUpTextOnlyCheck.IsChecked = config.HoverAiFollowUpTextOnly;
        FollowUpAutoHeightCheck.IsChecked = config.HoverAiFollowUpAutoHeight;
        BackgroundInput.Text = config.HoverAiBackground;
        ForegroundInput.Text = config.HoverAiForeground;
        var paste = new MenuItem { Header = "Paste privately" };
        paste.Click += (_, _) => PasteClipboard();
        _pasteMenu.Items.Add(paste);
        Chrome.ContextMenu = _pasteMenu;
        DataObject.AddPastingHandler(QuestionInput, (_, e) =>
        {
            var text = e.SourceDataObject.GetData(DataFormats.UnicodeText) as string
                ?? e.SourceDataObject.GetData(DataFormats.Text) as string;
            if (text == null)
            {
                if (e.SourceDataObject.GetData(DataFormats.Bitmap) is BitmapSource image) { e.CancelCommand(); ReceiveScreenshot(image); }
                return;
            }
            e.CancelCommand(); // Never insert clipboard content into the visible editor.
            ReceivePaste(text);
        });
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SavePreferences(); };
        Closing += (_, _) => { _panelSettings?.Dispose(); CloseCodexBackend(); _modeActive = false; _screenshotFlashTimer.Stop(); _statusWindow?.Hide(); _responseWindow?.Hide(); _inputWindow?.Hide(); };
        Closed += (_, _) => { _saveTimer.Stop(); _screenshotFlashTimer.Stop(); _screenshots.Clear(); _statusWindow?.Close(); _responseWindow?.Close(); _inputWindow?.Close(); _shortcutsWindow?.Dispose(); if (_ready) SavePreferences(); };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) { _compactExpanded = false; _buttonDwell?.Cancel(); }
            RefreshFloatingPanels();
        };
        _ready = true;
        ApplyAppearance();
        ApplyColors(false);
        Wheel.ColorChanged += Wheel_ColorChanged;
        LoadWheel();
    }

    private static double Safe(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    internal void ReceivePaste(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        _pastedContext.Add((++_pasteSequence, text));
        PasteDot.Visibility = Visibility.Collapsed;
        RefreshFloatingPanels();
    }

    internal void ReceiveQuickCopy(string text)
    {
        if (!_config.HoverAiQuicker || string.IsNullOrWhiteSpace(text)) return;
        ReceivePaste(text); SubmitQuestion();
    }

    internal void ReceiveScreenshot(BitmapSource screenshot, bool fromCapture = false)
    {
        string path;
        try { path = _screenshots.Add(screenshot, _config.HoverAiScreenshotFolder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { ShowStatus("Could not save screenshot. Check the temp folder."); return; }
        LastScreenshot = screenshot;
        _screenshotFlash = true;
        _screenshotFlashTimer.Stop(); _screenshotFlashTimer.Start();
        RefreshFloatingPanels();
        ScreenshotReceived?.Invoke(screenshot);
        if (fromCapture && _config.HoverAiScreenshotAutoSend)
        {
            var prompt = _config.HoverAiScreenshotPrompt.Trim();
            DispatchSubmission(new HoverAiSubmission(prompt, new[] { path }, QueueIndividually: true));
        }
    }
    private void ScreenshotMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _config.HoverAiScreenshotAutoSend = ScreenshotAutoSendCheck.IsChecked == true;
        _config.HoverAiScreenshotPrompt = ScreenshotPromptInput.Text;
        _saveTimer.Stop(); _saveTimer.Start();
    }
    internal void FinishScreenshotFlash()
    {
        _screenshotFlashTimer.Stop(); _screenshotFlash = false; RefreshFloatingPanels();
    }
    internal void SetModeActive(bool active) { _modeActive = active; if (!active) _shortcutsWindow?.Hide(); RefreshFloatingPanels(); }
    private Point ShortcutsAnchor => _config.HoverAiLeft is double x && _config.HoverAiTop is double y
        ? new Point(x, y) : new Point(SystemParameters.WorkArea.Left + 30, SystemParameters.WorkArea.Top + 30);
    private HoverAiShortcutsWindow EnsureShortcutsPanel()
    {
        if (_shortcutsWindow == null)
        {
            _shortcutsWindow = new HoverAiShortcutsWindow(_config);
            _shortcutsWindow.BoundsChanged += () => { _saveTimer.Stop(); _saveTimer.Start(); };
            _shortcutsWindow.SettingsRequested += () => OpenPanelAppearance("Shortcuts");
        }
        return _shortcutsWindow;
    }
    internal void ShowShortcutsPanel() => EnsureShortcutsPanel().ShowWhileHeld(ShortcutsAnchor);
    private void Question_Changed(object sender, TextChangedEventArgs e) { if (_ready) RefreshFloatingPanels(); }
    private void ScreenshotFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a screenshot folder inside Temp", InitialDirectory = Path.GetTempPath() };
        if (dialog.ShowDialog(this) != true) return;
        try { _config.HoverAiScreenshotFolder = HoverAiScreenshotStore.ResolveFolder(dialog.FolderName); ScreenshotFolderInput.Text = _config.HoverAiScreenshotFolder; SavePreferences(); }
        catch (ArgumentException ex) { AppearanceError.Text = ex.Message; }
    }

    private void PasteClipboard()
    {
        try
        {
            if (Clipboard.ContainsImage()) { var image = Clipboard.GetImage(); if (image != null) ReceiveScreenshot(image); }
            else if (Clipboard.ContainsText()) ReceivePaste(Clipboard.GetText());
            else ShowStatus("Clipboard has no text or image.");
        }
        catch (System.Runtime.InteropServices.ExternalException)
        { ShowStatus("Clipboard is busy. Tap again."); }
    }

    internal void ShowStatus(string text)
    {
        if (_responseText.Length > 0) { AppearanceError.Text = text; return; }
        AnswerText.Text = text;
        ResponseLabel.Text = "AI";
        ResponsePanel.Visibility = Visibility.Collapsed;
        _showingResponse = true;
        RefreshFloatingPanels();
    }

    internal void ShowDemoResponse()
    {
        ResponseLabel.Text = "DEMO \u00B7 sample response";
        AnswerText.Text = "M\u00ECnh \u0111\u1EC1 xu\u1EA5t c\u00E1ch n\u00E0y\n\nChia c\u00F4ng vi\u1EC7c th\u00E0nh 3 b\u01B0\u1EDBc nh\u1ECF:\n\n1. Ki\u1EC3m tra k\u1EBFt qu\u1EA3 hi\u1EC7n t\u1EA1i.\n2. Thay \u0111\u1ED5i t\u1EEBng ph\u1EA7n m\u1ED9t.\n3. So s\u00E1nh tr\u01B0\u1EDBc v\u00E0 sau khi s\u1EEDa.\n\n\u0110\u00E2y l\u00E0 c\u00E2u tr\u1EA3 l\u1EDDi m\u1EABu \u0111\u1EC3 xem giao di\u1EC7n, ch\u01B0a g\u1EEDi y\u00EAu c\u1EA7u t\u1EDBi AI.";
        _responseText = AnswerText.Text;
        CopyResponseButton.IsEnabled = true;
        _showingResponse = true;
        ResponsePanel.Visibility = Visibility.Collapsed;
        RefreshFloatingPanels();
    }
    private void Demo_Click(object sender, RoutedEventArgs e) => ShowDemoResponse();
    private void CloseResponse_Click(object sender, RoutedEventArgs e) { _showingResponse = false; RefreshFloatingPanels(); }

    private void EnsureFloatingPanels()
    {
        if (_statusWindow != null) return;
        var left = double.IsFinite(Left) ? Left : 100;
        var top = double.IsFinite(Top) ? Top : 100;
        _statusWindow = new HoverAiFloatingPanel(_config, true, left + Width + 8, top);
        _responseWindow = new HoverAiFloatingPanel(_config, false, left, top + 50);
        FollowUpPanel.Children.Remove(QuestionInput);
        _inputWindow = new HoverAiFloatingPanel(_config, false, left + Width + 16, top + 45, QuestionInput);
        _inputWindow.PreviewKeyDown += Window_PreviewKeyDown;
        _inputWindow.BoundsSaved += () => { FollowUpAutoHeightCheck.IsChecked = _config.HoverAiFollowUpAutoHeight; SavePreferences(); };
        _inputWindow.SettingsRequested += () => OpenPanelAppearance("Add input");
        _responseWindow.SettingsRequested += () => OpenPanelAppearance("Response");
        _statusWindow.SettingsRequested += () => OpenPanelAppearance("Dot");
        _statusWindow.BoundsSaved += SavePreferences;
        _responseWindow.BoundsSaved += () => { AutoHeightCheck.IsChecked = _config.HoverAiResponseAutoHeight; SavePreferences(); };
    }
    internal void RefreshFloatingPanels()
    {
        if (!_ready) return;
        EnsureFloatingPanels();
        if (_modeActive && EditingLayout) EnsureShortcutsPanel();
        _shortcutsWindow?.SetEditing(_modeActive && EditingLayout, ShortcutsAnchor);
        RefreshCompactBar();
        BarSettingsButton.Visibility = EditingLayout ? Visibility.Visible : Visibility.Collapsed;
        if (!EditingLayout) _panelSettings?.Hide();
        _statusWindow!.SetScreenshotFlash(_screenshotFlash);
        _statusWindow.RefreshTheme(EditingLayout); _responseWindow!.RefreshTheme(EditingLayout); _inputWindow!.RefreshTheme(EditingLayout);
        _responseWindow.SetAnswer(ResponseLabel.Text, AnswerText.Text);
        var show = _modeActive && (IsVisible || EditingLayout);
        if (_modeActive && (EditingLayout || HasInput || _screenshotFlash)) _statusWindow.Show(); else _statusWindow.Hide();
        if (show && (EditingLayout || _showingResponse)) _responseWindow.Show(); else _responseWindow.Hide();
        if (show && (EditingLayout || _followUpOpen)) _inputWindow!.Show(); else _inputWindow!.Hide();
    }
    private void EditLayout_Click(object sender, RoutedEventArgs e)
    {
        if (EditingLayout && !_showingResponse) ShowDemoResponse();
        ApplyAppearance();
    }
    internal void EndLayoutEditing() { _preferencesPinned = false; EditLayoutCheck.IsChecked = false; RefreshFloatingPanels(); }
    private void BarSettings_Click(object sender, RoutedEventArgs e) => OpenPanelAppearance("Bar");
    internal void OpenPanelAppearance(string kind)
    {
        if (!EditingLayout) return;
        if (_panelSettings == null)
        {
            AppearancePanel.Children.Remove(PanelAppearanceControls);
            _panelSettings = new HoverPanelSettingsWindow(this, PanelAppearanceControls, Resources);
            _ = new HoverDwellController(_panelSettings, _ => _modeActive && _config.HoverAiDwellButtons);
        }
        BarAppearanceGroup.Visibility = kind == "Bar" ? Visibility.Visible : Visibility.Collapsed;
        DotAppearanceGroup.Visibility = kind == "Dot" ? Visibility.Visible : Visibility.Collapsed;
        ResponseAppearanceGroup.Visibility = kind == "Response" ? Visibility.Visible : Visibility.Collapsed;
        AddAppearanceGroup.Visibility = kind == "Add input" ? Visibility.Visible : Visibility.Collapsed;
        ShortcutsAppearanceGroup.Visibility = kind == "Shortcuts" ? Visibility.Visible : Visibility.Collapsed;
        var targets = kind switch
        {
            "Bar" => new[] { "Panel", "Text", "Icons" },
            "Dot" => new[] { "Dot", "Dot panel" },
            "Response" => new[] { "AI panel", "AI text" },
            "Shortcuts" => new[] { "Shortcuts panel", "Shortcuts text" },
            _ => new[] { "Add panel", "Add text" }
        };
        foreach (var button in PanelColorTargets.Children.OfType<Button>()) button.Visibility = targets.Contains((string)button.Tag) ? Visibility.Visible : Visibility.Collapsed;
        _colorTarget = targets[0]; LoadWheel();
        ApplyLocalTheme(PanelAppearanceControls, ((SolidColorBrush)Foreground).Color, ((SolidColorBrush)Resources["HoverPanel"]).Color, ((SolidColorBrush)Resources["HoverIcons"]).Color);
        _panelSettings.RefreshTheme((Brush)Resources["HoverPanel"], Foreground);
        Window anchor = kind == "Shortcuts" ? _shortcutsWindow! : kind == "Response" ? _responseWindow! : kind == "Add input" ? _inputWindow! : kind == "Dot" ? _statusWindow! : this;
        _panelSettings.Open(kind + " settings", anchor);
    }
    internal Window? PanelSettingsForTests => _panelSettings;
    private void CopyResponse_Click(object sender, RoutedEventArgs e)
    {
        if (_responseText.Length == 0) return;
        try { Clipboard.SetText(_responseText); }
        catch (System.Runtime.InteropServices.ExternalException) { AppearanceError.Text = "Clipboard is busy. Tap again."; }
    }
    private void Capture_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        if (CaptureRegionCheck.IsChecked != true)
        { _config.HoverAiCaptureRegionEnabled = false; SavePreferences(); return; }
        if (!int.TryParse(CaptureX.Text, out var x) || !int.TryParse(CaptureY.Text, out var y) ||
            !int.TryParse(CaptureWidth.Text, out var w) || !int.TryParse(CaptureHeight.Text, out var h) || w < 8 || h < 8 || w > 32000 || h > 32000)
        { AppearanceError.Text = "Enter X, Y and a size of at least 8 ? 8 pixels."; return; }
        _config.HoverAiCaptureRegionEnabled = CaptureRegionCheck.IsChecked == true;
        _config.HoverAiCaptureX = x; _config.HoverAiCaptureY = y;
        _config.HoverAiCaptureWidth = w; _config.HoverAiCaptureHeight = h;
        AppearanceError.Text = ""; SavePreferences();
    }
    private void ChooseCapture_Click(object sender, RoutedEventArgs e)
    {
        _selectingCapture = true;
        _statusWindow?.Hide(); _responseWindow?.Hide(); _inputWindow?.Hide();
        try
        {
            var region = ScreenRegionOverlay.Select(this);
            if (region is not Rect r) return;
            _ready = false;
            CaptureX.Text = ((int)r.X).ToString(); CaptureY.Text = ((int)r.Y).ToString();
            CaptureWidth.Text = ((int)r.Width).ToString(); CaptureHeight.Text = ((int)r.Height).ToString();
            CaptureRegionCheck.IsChecked = true;
            _ready = true; Capture_Changed(this, new RoutedEventArgs());
        }
        finally { _ready = true; _selectingCapture = false; RefreshFloatingPanels(); }
    }

    internal void SelectColor(bool background, string hex)
    {
        if (background) BackgroundInput.Text = hex;
        else ForegroundInput.Text = hex;
        ApplyColors(true);
    }

    private void LoadWheel()
    {
        _updatingWheel = true;
        try
        {
            var hex = _colorTarget switch
            {
                "Panel" => _config.HoverAiBackground,
                "Text" => _config.HoverAiForeground,
                "Dot" => _config.HoverAiInputDotColor,
                "Dot panel" => _config.HoverAiStatusBackground!,
                "AI panel" => _config.HoverAiResponseBackground!,
                "AI text" => _config.HoverAiResponseForeground!,
                "Add panel" => _config.HoverAiFollowUpBackground!,
                "Add text" => _config.HoverAiFollowUpForeground!,
                "Shortcuts panel" => _config.HoverAiShortcutsBackground!,
                "Shortcuts text" => _config.HoverAiShortcutsForeground!,
                _ => _config.HoverAiIconColor
            };
            Wheel.SetColor((Color)ColorConverter.ConvertFromString(hex));
            BrightnessSlider.Value = Wheel.Brightness;
            ColorTargetLabel.Text = _colorTarget + " color";
        }
        finally { _updatingWheel = false; }
    }
    private void ColorTarget_Click(object sender, RoutedEventArgs e)
    {
        _colorTarget = (string)((Button)sender).Tag;
        LoadWheel();
    }
    private void Wheel_ColorChanged(Color color)
    {
        if (_updatingWheel) return;
        var hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        if (_colorTarget == "Panel") BackgroundInput.Text = hex;
        else if (_colorTarget == "Text") ForegroundInput.Text = hex;
        else if (_colorTarget == "Dot") _config.HoverAiInputDotColor = hex;
        else if (_colorTarget == "Dot panel") _config.HoverAiStatusBackground = hex;
        else if (_colorTarget == "AI panel") _config.HoverAiResponseBackground = hex;
        else if (_colorTarget == "AI text") _config.HoverAiResponseForeground = hex;
        else if (_colorTarget == "Add panel") _config.HoverAiFollowUpBackground = hex;
        else if (_colorTarget == "Add text") _config.HoverAiFollowUpForeground = hex;
        else if (_colorTarget == "Shortcuts panel") _config.HoverAiShortcutsBackground = hex;
        else if (_colorTarget == "Shortcuts text") _config.HoverAiShortcutsForeground = hex;
        else _config.HoverAiIconColor = hex;
        ApplyColors(false);
        _saveTimer.Stop(); _saveTimer.Start();
    }
    private void Brightness_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_ready && !_updatingWheel) Wheel.Brightness = e.NewValue;
    }

    internal void SubmitQuestion()
    {
        var parts = _pastedContext.Select(item => item.Text).ToList();

        if (!string.IsNullOrWhiteSpace(QuestionInput.Text)) parts.Add(QuestionInput.Text.Trim());
        if (parts.Count == 0 && _screenshots.Files.Count == 0) { LastQuestion = ""; return; }
        var text = string.Join("\n\n", parts);
        LastQuestion = text;
        if (_screenshots.Files.Count > 0) LastQuestion += $"\n\n[{_screenshots.Files.Count} image attachment(s)]";
        if (!_codexEnabled && _responseText.Length == 0) { ResponseLabel.Text = "AI"; AnswerText.Text = "AI is not connected yet."; }
        ResponsePanel.Visibility = Visibility.Collapsed;
        RefreshFloatingPanels();
        DispatchSubmission(new HoverAiSubmission(text, _screenshots.Files.ToArray()));
    }

    private void DispatchSubmission(HoverAiSubmission request)
    {
        var preset = SelectedPromptPresetText();
        request = request with { DraftPasteThrough = request.QueueIndividually ? 0 : _pasteSequence,
            DraftQuestionRevision = request.QueueIndividually ? -1 : _questionRevision,
            DraftQuestionText = request.QueueIndividually ? "" : QuestionInput.Text.Trim(),
            DraftImages = request.ImagePaths.ToArray(), Text = ApplyPromptPreset(request.Text, preset), PresetText = preset,
            SystemPrompt = SystemPromptInput.Text, Model = CodexModelPicker.Text.Trim(),
            Effort = (CodexEffortPicker.SelectedItem as ComboBoxItem)?.Content as string ?? "low" };
        LastQuestion = request.Text;
        if (request.ImagePaths.Count > 0) LastQuestion += $"\n\n[{request.ImagePaths.Count} image attachment(s)]";
        SubmissionRequested?.Invoke(request); QuestionSubmitted?.Invoke(LastQuestion);
    }

    private void ClearCompletedDraft(HoverAiSubmission request)
    {
        if (request.DraftQuestionText.Length > 0) _completedQuestionRevision = Math.Max(_completedQuestionRevision, request.DraftQuestionRevision);
        _pastedContext.RemoveAll(item => item.Id <= request.DraftPasteThrough);
        if (_questionRevision == request.DraftQuestionRevision) QuestionInput.Clear();
        // Queued turns still need their original files, even after they leave the draft.
        var retained = _queuedSubmissions.SelectMany(item => item.ImagePaths).ToHashSet();
        _screenshots.Remove(request.DraftImages, retained);
        if (_screenshots.Files.Count == 0)
        {
            LastScreenshot = null; _screenshotFlashTimer.Stop(); _screenshotFlash = false;
        }
        LastQuestion = "";
        RefreshFloatingPanels();
    }

    private void Chrome_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == Chrome) Title_Drag(sender, e);
    }
    private void Title_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        _dragging = true;
        try { DragMove(); }
        finally
        {
            _dragging = false;
            SavePosition();
        }
    }

    private void MainDrag_Started(object sender, DragStartedEventArgs e) => _dragging = true;
    private void MainDrag_Delta(object sender, DragDeltaEventArgs e)
    {
        Left += e.HorizontalChange; Top += e.VerticalChange;
    }
    private void MainDrag_Completed(object sender, DragCompletedEventArgs e)
    {
        _dragging = false; SavePosition();
    }

    internal void SavePosition()
    {
        _config.HoverAiLeft = Left;
        _config.HoverAiTop = Top;
        SavePreferences();
        PositionChanged?.Invoke();
    }

    private void ApplyAppearance()
    {
        if (!_ready || _applyingAppearance) return;
        _applyingAppearance = true;
        try
        {
            _config.HoverAiQuicker = QuickerCheck.IsChecked == true;
            _config.HoverAiCompactBar = CompactBarCheck.IsChecked == true;
            _config.HoverAiDwellButtons = DwellButtonsCheck.IsChecked == true;
            _config.HoverAiMinimal = MinimalCheck.IsChecked == true;
            _config.HoverAiOpacity = OpacitySlider.Value;
            _config.HoverAiStatusOpacity = StatusOpacitySlider.Value;
            _config.HoverAiResponseOpacity = ResponseOpacitySlider.Value;
            _config.HoverAiShortcutsOpacity = ShortcutsOpacitySlider.Value;
            _config.HoverAiShortcutsFontSize = ShortcutsFontSlider.Value;
            _config.HoverAiShortcutsTextOnly = ShortcutsTextOnlyCheck.IsChecked == true;
            _config.HoverAiResponseTextOnly = TextOnlyCheck.IsChecked == true;
            _config.HoverAiResponseAutoHeight = AutoHeightCheck.IsChecked == true;
            _config.HoverAiFontSize = FontSlider.Value;
            _config.HoverAiBarTextSize = BarTextSlider.Value;
            _config.HoverAiBarIconSize = BarIconSlider.Value;
            _config.HoverAiFollowUpFontSize = FollowUpFontSlider.Value;
            _config.HoverAiFollowUpOpacity = FollowUpOpacitySlider.Value;
            _config.HoverAiFollowUpTextOnly = FollowUpTextOnlyCheck.IsChecked == true;
            _config.HoverAiFollowUpAutoHeight = FollowUpAutoHeightCheck.IsChecked == true;
            Opacity = _config.HoverAiOpacity;
            Chrome.Padding = new Thickness(_config.HoverAiMinimal ? 7 : 12);
            foreach (var button in ActionBar.Children.OfType<Button>())
                button.FontSize = BarIconSlider.Value;
            MainDragHandle.FontSize = BarIconSlider.Value;
            MainDragHandle.Width = Math.Max(18, BarIconSlider.Value + 8);
            var label = new TextBlock { Text = "Paste", FontFamily = PasteButton.FontFamily, FontSize = BarTextSlider.Value };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _expandedMinimumWidth = Math.Max(250, Math.Ceiling(label.DesiredSize.Width + 16 + (EditingLayout ? 6 : 5) * (BarIconSlider.Value + 14) + 20 + MainDragHandle.Width + 4 + Chrome.Padding.Left * 2));
            WidthSlider.Minimum = _expandedMinimumWidth;
            _config.HoverAiWidth = WidthSlider.Value;
            FollowUpPanel.Visibility = Visibility.Collapsed;
            ResponsePanel.Visibility = Visibility.Collapsed;
            RefreshFloatingPanels();
            _saveTimer.Stop(); _saveTimer.Start();
        }
        finally { _applyingAppearance = false; }
    }

    private bool ApplyColors(bool save)
    {
        try
        {
            Color Parse(string value)
            {
                if (value.Length != 7 || value[0] != '#') throw new FormatException();
                return (Color)ColorConverter.ConvertFromString(value);
            }
            var background = Parse(BackgroundInput.Text.Trim());
            var foreground = Parse(ForegroundInput.Text.Trim());
            var icons = Parse(_config.HoverAiIconColor);
            Resources["HoverText"] = new SolidColorBrush(foreground);
            Resources["HoverPanel"] = new SolidColorBrush(background);
            Resources["HoverIcons"] = new SolidColorBrush(icons);
            PasteDot.Fill = new SolidColorBrush(icons);
            Chrome.Background = new SolidColorBrush(background);
            Foreground = new SolidColorBrush(foreground);
            ApplyLocalTheme(Chrome, foreground, background, icons);
            CompactRevealButton.Foreground = LocateBrush(_config.HoverAiCompactIconColor, Colors.White);
            if (_panelSettings != null)
            {
                ApplyLocalTheme(PanelAppearanceControls, foreground, background, icons);
                _panelSettings.RefreshTheme(new SolidColorBrush(background), new SolidColorBrush(foreground));
            }
            _pasteMenu.Background = new SolidColorBrush(background);
            _pasteMenu.Foreground = new SolidColorBrush(foreground);
            foreach (var item in _pasteMenu.Items.OfType<MenuItem>()) item.Foreground = new SolidColorBrush(icons);
            QuestionInput.Background = Brushes.Transparent;
            QuestionInput.CaretBrush = new SolidColorBrush(foreground);
            _config.HoverAiBackground = BackgroundInput.Text.Trim();
            _config.HoverAiForeground = ForegroundInput.Text.Trim();
            AppearanceError.Text = "";
            RefreshFloatingPanels();
            if (save) SavePreferences();
            return true;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or NotSupportedException)
        { AppearanceError.Text = "Use a color such as #131217 or #FFFFFF."; return false; }
    }

    private static void ApplyLocalTheme(DependencyObject parent, Color text, Color background, Color icons)
    {
        // Touch only controls owned by this window. Never mutate shared app brushes/styles.
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is Button button)
            {
                button.Foreground = new SolidColorBrush(icons);
                button.Background = new SolidColorBrush(Color.FromRgb(
                    (byte)(background.R * .85 + 20), (byte)(background.G * .85 + 20), (byte)(background.B * .85 + 20)));
            }
            else if (child is TextBlock label) label.Foreground = new SolidColorBrush(text);
            else if (child is Control control) control.Foreground = new SolidColorBrush(text);
            if (child is Slider slider) slider.Foreground = new SolidColorBrush(icons);
            ApplyLocalTheme(child, text, background, icons);
        }
    }

    private void SavePreferences()
    {
        try { HoverAiAppearanceStore.Save(_config); }
        catch (System.IO.IOException) { AppearanceError.Text = "Could not save appearance. Try again."; }
        catch (UnauthorizedAccessException) { AppearanceError.Text = "Could not save appearance. Check folder access."; }
    }

    private void FollowUp_Click(object sender, RoutedEventArgs e)
    {
        _followUpOpen = !_followUpOpen;
        RefreshFloatingPanels();
        if (_inputWindow?.IsVisible == true && !HoverDwellController.IsInvoking) { _inputWindow.Activate(); QuestionInput.Focus(); }
    }
    private void CompactReveal_Click(object sender, RoutedEventArgs e)
    {
        _compactExpanded = true;
        RefreshCompactBar();
    }
    private void RefreshCompactBar()
    {
        if (!_ready) return;
        var collapsed = _config.HoverAiCompactBar && !_compactExpanded && !EditingLayout
            && !_preferencesPinned && AppearancePanel.Visibility != Visibility.Visible;
        ActionBar.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        CompactRevealButton.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        CompactRevealButton.FontSize = _config.HoverAiBarIconSize;
        MinWidth = collapsed ? 36 : _expandedMinimumWidth;
        Width = collapsed ? 36 : Math.Max(_expandedMinimumWidth, _config.HoverAiWidth);
        Chrome.Padding = new Thickness(collapsed ? 3 : _config.HoverAiMinimal ? 7 : 12);
        Chrome.Background = AppearancePanel.Visibility == Visibility.Visible ? (Brush)Resources["HoverPanel"] : Brushes.Transparent;
        WindowInterop.SetNoActivate(this, AppearancePanel.Visibility != Visibility.Visible && (_config.HoverAiCompactBar || _config.HoverAiDwellButtons));
        foreach (var button in ActionBar.Children.OfType<Button>()) button.Background = Brushes.Transparent;
        CompactRevealButton.Background = Brushes.Transparent;
    }
    internal void OpenPreferences() { _preferencesPinned = true; AppearancePanel.Visibility = Visibility.Visible; RefreshCompactBar(); Activate(); }
    private void Options_Click(object sender, RoutedEventArgs e)
    {
        AppearancePanel.Visibility = AppearancePanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (AppearancePanel.Visibility == Visibility.Collapsed) { _preferencesPinned = false; EndLayoutEditing(); }
        RefreshCompactBar();
    }
    private void Appearance_Changed(object sender, RoutedEventArgs e)
    { if (sender == MinimalCheck) _followUpOpen = MinimalCheck.IsChecked != true; ApplyAppearance(); }
    private void Appearance_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => ApplyAppearance();
    private void Colors_Click(object sender, RoutedEventArgs e) => ApplyColors(true);
    private void Paste_Click(object sender, RoutedEventArgs e) => PasteClipboard();
    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        ClearInputFromShortcut();
        AnswerText.Text = ""; ResponsePanel.Visibility = Visibility.Collapsed; _showingResponse = false;
        _responseText = ""; CopyResponseButton.IsEnabled = false; RefreshFloatingPanels();
    }
    internal void PasteInputFromShortcut() => PasteClipboard();
    internal void NewSessionFromShortcut()
    {
        if (!_changingChat) NewChat_Click(this, new RoutedEventArgs());
    }
    internal void ClearInputFromShortcut()
    {
        ResetCodexDraft();
        _screenshots.Clear(); _screenshotFlashTimer.Stop(); _screenshotFlash = false;
        _pastedContext.Clear(); LastQuestion = ""; QuestionInput.Clear(); LastScreenshot = null;
        PasteDot.Visibility = Visibility.Collapsed;
        RefreshFloatingPanels();
    }
    private void Ask_Click(object sender, RoutedEventArgs e)
    {
        if (_codexCancellation != null) { StopCodex(); return; }
        SubmitQuestion();
        if (LastQuestion.Length == 0) ShowStatus("Paste or add a message first.");
        else if (!_codexEnabled) ShowStatus("AI is not connected yet.");
    }
    private void Settings_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.V && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && Keyboard.FocusedElement is not TextBox)
        { PasteClipboard(); e.Handled = true; }
        else if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        { SubmitQuestion(); e.Handled = true; }
        else if (e.Key == Key.Escape) { EndLayoutEditing(); DismissRequested?.Invoke(); e.Handled = true; }
    }
}
