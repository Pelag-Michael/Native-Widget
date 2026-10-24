using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NativeWidget.Models;
using NativeWidget.Services;

namespace NativeWidget;

public partial class HoverAiWindow : Window
{
    private readonly AppConfig _config;
    private readonly List<string> _pastedContext = new();
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private bool _ready;
    private bool _dragging;
    private bool _updatingWheel;
    private string _colorTarget = "Panel";
    private bool _showingResponse;
    private HoverAiFloatingPanel? _statusWindow, _responseWindow;
    private string _responseText = "";
    private bool _selectingCapture;
    private bool _preferencesPinned;
    internal bool EditingLayout => EditLayoutCheck.IsChecked == true;
    internal HoverAiFloatingPanel? ResponseWindowForTests => _responseWindow;
    internal bool OwnsAuxiliaryWindow(Window window) => window == _statusWindow || window == _responseWindow || (_selectingCapture && window is ScreenRegionOverlay);
    internal bool IsOverAuxiliary(Point screen) => new[] { _statusWindow, _responseWindow }.Any(w => w?.IsVisible == true && new Rect(new Point(), w.RenderSize).Contains(w.PointFromScreen(screen)));

    internal BitmapSource? LastScreenshot { get; private set; }
    public event Action<BitmapSource>? ScreenshotReceived;
    private readonly ContextMenu _pasteMenu = new();
    public event Action? SettingsRequested;
    public event Action<string>? QuestionSubmitted;
    public event Action? DismissRequested;
    public event Action? PositionChanged;
    public string LastQuestion { get; private set; } = "";
    public bool IsPointerInteractionActive => _preferencesPinned || EditingLayout || _selectingCapture || _dragging || _pasteMenu.IsOpen || Wheel.IsMouseCaptured;
    internal int PastedItemCount => _pastedContext.Count;

    public HoverAiWindow() : this(AppConfig.Load()) { }
    public HoverAiWindow(AppConfig config)
    {
        _config = config;
        HoverAiAppearanceStore.LoadInto(config);
        InitializeComponent();
        WindowInterop.HideFromAltTab(this);
        CaptureRegionCheck.IsChecked = config.HoverAiCaptureRegionEnabled;
        CaptureX.Text = config.HoverAiCaptureX.ToString(); CaptureY.Text = config.HoverAiCaptureY.ToString();
        CaptureWidth.Text = config.HoverAiCaptureWidth.ToString(); CaptureHeight.Text = config.HoverAiCaptureHeight.ToString();
        MinimalCheck.IsChecked = config.HoverAiMinimal;
        OpacitySlider.Value = Safe(config.HoverAiOpacity, .15, 1, 1);
        WidthSlider.Value = Safe(config.HoverAiWidth, 180, 600, 260);
        FontSlider.Value = Safe(config.HoverAiFontSize, 9, 20, 12);
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
            if (text == null) return;
            e.CancelCommand(); // Never insert clipboard content into the visible editor.
            ReceivePaste(text);
        });
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SavePreferences(); };
        Closed += (_, _) => { _saveTimer.Stop(); _statusWindow?.Close(); _responseWindow?.Close(); if (_ready) SavePreferences(); };
        IsVisibleChanged += (_, _) => RefreshFloatingPanels();
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
        _pastedContext.Add(text);
        PasteDot.Visibility = Visibility.Collapsed;
        RefreshFloatingPanels();
        SubmitQuestion();
    }

    internal void ReceiveScreenshot(BitmapSource screenshot)
    {
        LastScreenshot = screenshot; // Keep only the newest image, in memory only.
        PasteDot.Visibility = Visibility.Collapsed;
        RefreshFloatingPanels();
        ScreenshotReceived?.Invoke(screenshot);
        SubmitQuestion();
    }

    private void PasteClipboard()
    {
        try
        {
            if (Clipboard.ContainsText()) ReceivePaste(Clipboard.GetText());
            else ShowStatus("Clipboard has no text.");
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
        _statusWindow.BoundsSaved += SavePreferences;
        _responseWindow.BoundsSaved += SavePreferences;
    }
    internal void RefreshFloatingPanels()
    {
        if (!_ready) return;
        EnsureFloatingPanels();
        _statusWindow!.RefreshTheme(); _responseWindow!.RefreshTheme();
        _responseWindow.SetAnswer(ResponseLabel.Text, AnswerText.Text);
        var show = IsVisible || EditingLayout;
        if (show && (EditingLayout || _pastedContext.Count > 0 || LastScreenshot != null)) _statusWindow.Show(); else _statusWindow.Hide();
        if (show && (EditingLayout || _showingResponse)) _responseWindow.Show(); else _responseWindow.Hide();
    }
    private void EditLayout_Click(object sender, RoutedEventArgs e)
    {
        if (EditingLayout && !_showingResponse) ShowDemoResponse();
        RefreshFloatingPanels();
    }
    internal void EndLayoutEditing() { _preferencesPinned = false; EditLayoutCheck.IsChecked = false; RefreshFloatingPanels(); }
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
        _statusWindow?.Hide(); _responseWindow?.Hide();
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
            var hex = _colorTarget == "Panel" ? _config.HoverAiBackground : _colorTarget == "Text" ? _config.HoverAiForeground : _config.HoverAiIconColor;
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
        var parts = _pastedContext.ToList();
        if (LastScreenshot != null) parts.Add("[Screenshot attached]");
        if (!string.IsNullOrWhiteSpace(QuestionInput.Text)) parts.Add(QuestionInput.Text.Trim());
        if (parts.Count == 0) return;
        LastQuestion = string.Join("\n\n", parts);
        if (_responseText.Length == 0) { ResponseLabel.Text = "AI"; AnswerText.Text = "AI is not connected yet."; }
        ResponsePanel.Visibility = Visibility.Collapsed;
        RefreshFloatingPanels();
        QuestionSubmitted?.Invoke(LastQuestion);
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

    internal void SavePosition()
    {
        _config.HoverAiLeft = Left;
        _config.HoverAiTop = Top;
        SavePreferences();
        PositionChanged?.Invoke();
    }

    private void ApplyAppearance()
    {
        if (!_ready) return;
        _config.HoverAiMinimal = MinimalCheck.IsChecked == true;
        _config.HoverAiOpacity = OpacitySlider.Value;
        _config.HoverAiWidth = WidthSlider.Value;
        _config.HoverAiFontSize = FontSlider.Value;
        Opacity = _config.HoverAiOpacity;
        Width = _config.HoverAiWidth;
        FontSize = _config.HoverAiFontSize;
        TitleText.FontSize = FontSize;
        PasteButton.FontSize = FontSize;
        QuestionInput.FontSize = FontSize;
        Chrome.Padding = new Thickness(_config.HoverAiMinimal ? 7 : 12);
        if (!_config.HoverAiMinimal) FollowUpPanel.Visibility = Visibility.Visible;
        else if (string.IsNullOrEmpty(QuestionInput.Text)) FollowUpPanel.Visibility = Visibility.Collapsed;
        ResponsePanel.Visibility = Visibility.Collapsed;
        RefreshFloatingPanels();
        _saveTimer.Stop();
        _saveTimer.Start();
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
            Resources["HoverIcons"] = new SolidColorBrush(icons);
            PasteDot.Fill = new SolidColorBrush(icons);
            Chrome.Background = new SolidColorBrush(background);
            Foreground = new SolidColorBrush(foreground);
            ApplyLocalTheme(Chrome, foreground, background, icons);
            _pasteMenu.Background = new SolidColorBrush(background);
            _pasteMenu.Foreground = new SolidColorBrush(foreground);
            foreach (var item in _pasteMenu.Items.OfType<MenuItem>()) item.Foreground = new SolidColorBrush(icons);
            QuestionInput.Background = new SolidColorBrush(background);
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
        FollowUpPanel.Visibility = FollowUpPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (FollowUpPanel.IsVisible) { Activate(); QuestionInput.Focus(); }
    }
    internal void OpenPreferences() { _preferencesPinned = true; AppearancePanel.Visibility = Visibility.Visible; Activate(); }
    private void Options_Click(object sender, RoutedEventArgs e)
    {
        AppearancePanel.Visibility = AppearancePanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (AppearancePanel.Visibility == Visibility.Collapsed) { _preferencesPinned = false; EndLayoutEditing(); }
    }
    private void Appearance_Changed(object sender, RoutedEventArgs e) => ApplyAppearance();
    private void Appearance_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => ApplyAppearance();
    private void Colors_Click(object sender, RoutedEventArgs e) => ApplyColors(true);
    private void Paste_Click(object sender, RoutedEventArgs e) => PasteClipboard();
    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _pastedContext.Clear(); LastQuestion = ""; QuestionInput.Clear(); LastScreenshot = null;
        PasteDot.Visibility = Visibility.Collapsed;
        AnswerText.Text = ""; ResponsePanel.Visibility = Visibility.Collapsed; _showingResponse = false;
        _responseText = ""; CopyResponseButton.IsEnabled = false; RefreshFloatingPanels();
    }
    private void Ask_Click(object sender, RoutedEventArgs e)
    {
        SubmitQuestion();
        ShowStatus(LastQuestion.Length == 0 ? "Paste or add a message first." : "AI is not connected yet.");
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
