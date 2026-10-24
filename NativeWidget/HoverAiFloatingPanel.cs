using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using NativeWidget.Models;
using NativeWidget.Services;

namespace NativeWidget;

// These windows belong only to Quick AI; no shared widget resources are modified.
internal sealed class HoverAiFloatingPanel : Window
{
    private readonly AppConfig _config;
    private readonly bool _status;
    private readonly TextBox? _editor;
    private bool IsInput => _editor != null;
    internal bool IsInteracting { get; private set; }
    private bool _loaded;
    private readonly Border _chrome;
    private Border? _header;
    private ScrollViewer? _scroll;
    private bool _editing;
    private double _frameHeight;
    private bool _screenshotFlash;
    public void SetScreenshotFlash(bool flash) => _screenshotFlash = flash;
    private readonly TextBlock _label = new() { FontSize = 10, Opacity = .65 };
    private readonly TextBlock _answer = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Ellipse _dot = new() { Width = 6, Height = 6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    public event Action? BoundsSaved;
    public HoverAiFloatingPanel(AppConfig config, bool status, double left, double top, TextBox? editor = null)
    {
        _config = config; _status = status; _editor = editor;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        Topmost = true; ShowInTaskbar = false; ShowActivated = false; Cursor = Cursors.Arrow; ForceCursor = true;
        ResizeMode = ResizeMode.NoResize;
        Width = status ? 28 : Math.Clamp(IsInput ? config.HoverAiFollowUpWidth : config.HoverAiResponseWidth, 12, 32000);
        Height = status ? 28 : Math.Clamp(IsInput ? config.HoverAiFollowUpHeight : config.HoverAiResponseHeight, 12, 32000);
        _frameHeight = Height;
        Left = status ? config.HoverAiStatusLeft ?? left : (IsInput ? config.HoverAiFollowUpLeft : config.HoverAiResponseLeft) ?? left;
        Top = status ? config.HoverAiStatusTop ?? top : (IsInput ? config.HoverAiFollowUpTop : config.HoverAiResponseTop) ?? top;
        _chrome = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(60,255,255,255)), Padding = new Thickness(status ? 0 : 12) };
        var root = new Grid(); root.Children.Add(_chrome); Content = root;
        if (status) { _chrome.Child = _dot; _chrome.MouseLeftButtonDown += Drag; }
        else
        {
            var body = new DockPanel(); _chrome.Child = body;
            _header = new Border { Background = Brushes.Transparent, Padding = new Thickness(0,0,0,8), Child = _label };
            DockPanel.SetDock(_header, Dock.Top); body.Children.Add(_header); _header.MouseLeftButtonDown += Drag;
            _answer.MouseLeftButtonDown += (_, e) => { if (_editing) Drag(_answer, e); };
            if (_editor != null)
            {
                body.Children.Add(_editor);
                _editor.TextChanged += (_, _) => ReflowText();
                _label.Text = "Add input";
            }
            else
            {
                _scroll = new ScrollViewer { Content = _answer, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
                body.Children.Add(_scroll);
            }
            SizeChanged += (_, e) => { if (e.WidthChanged) ReflowText(); };
            foreach (var edge in new[] { "L", "R", "T", "B", "LT", "RT", "LB", "RB" })
            {
                var factory = new FrameworkElementFactory(typeof(Border));
                factory.SetValue(Border.BackgroundProperty, Brushes.Transparent);
                var thumb = new Thumb { Width = edge.Length == 2 ? 14 : edge is "L" or "R" ? 5 : double.NaN,
                    Height = edge.Length == 2 ? 14 : edge is "T" or "B" ? 5 : double.NaN,
                    HorizontalAlignment = edge.Contains('L') ? HorizontalAlignment.Left : edge.Contains('R') ? HorizontalAlignment.Right : HorizontalAlignment.Stretch,
                    VerticalAlignment = edge.Contains('T') ? VerticalAlignment.Top : edge.Contains('B') ? VerticalAlignment.Bottom : VerticalAlignment.Stretch,
                    Cursor = Cursors.Arrow, Template = new ControlTemplate(typeof(Thumb)) { VisualTree = factory } };
                thumb.DragDelta += (_, e) =>
                {
                    if (edge.Length == 2 && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { Left += e.HorizontalChange; Top += e.VerticalChange; }
                    else Resize(edge, e.HorizontalChange, e.VerticalChange);
                };
                thumb.DragStarted += (_, _) => IsInteracting = true;
                thumb.DragCompleted += (_, _) => { IsInteracting = false; SaveBounds(); }; root.Children.Add(thumb);
            }
        }
        Loaded += (_, _) => _loaded = true;
        WindowInterop.HideFromAltTab(this); RefreshTheme();
    }
    private void Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        IsInteracting = true;
        try { DragMove(); }
        finally { IsInteracting = false; SaveBounds(); }
    }
    internal void Resize(string edge, double x, double y)
    {
        if (edge.Contains('L')) { var w = Math.Clamp(Width - x, 12, 32000); Left += Width - w; Width = w; }
        if (edge.Contains('R')) Width = Math.Clamp(Width + x, 12, 32000);
        if (edge.Contains('T')) { var h = Math.Clamp(Height - y, 12, 32000); Top += Height - h; _frameHeight = h; Height = h; }
        if (edge.Contains('B')) { _frameHeight = Math.Clamp(Height + y, 12, 32000); Height = _frameHeight; }
    }
    internal void SaveBounds()
    {
        if (!_loaded) return;
        if (_status) { _config.HoverAiStatusLeft = Left; _config.HoverAiStatusTop = Top; }
        else if (IsInput) { _config.HoverAiFollowUpLeft = Left; _config.HoverAiFollowUpTop = Top; _config.HoverAiFollowUpWidth = Width; _config.HoverAiFollowUpHeight = _frameHeight; }
        else { _config.HoverAiResponseLeft = Left; _config.HoverAiResponseTop = Top; _config.HoverAiResponseWidth = Width; _config.HoverAiResponseHeight = _frameHeight; }
        BoundsSaved?.Invoke();
    }
    public void SetAnswer(string label, string text) { _label.Text = label; _answer.Text = text; ReflowText(); }
    public void RefreshTheme(bool editing = false)
    {
        _editing = editing;
        _chrome.BorderThickness = new Thickness(0);
        var textOnly = IsInput ? _config.HoverAiFollowUpTextOnly : _config.HoverAiResponseTextOnly;
        _chrome.Padding = new Thickness(_status || textOnly ? 0 : 12);
        var background = _status ? _config.HoverAiStatusBackground : IsInput ? _config.HoverAiFollowUpBackground : _config.HoverAiResponseBackground;
        var foreground = IsInput ? _config.HoverAiFollowUpForeground : _config.HoverAiResponseForeground;
        _chrome.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(background ?? _config.HoverAiBackground));
        if (!_status && textOnly) _chrome.Background = Brushes.Transparent;
        if (_header != null) _header.Visibility = editing || !textOnly ? Visibility.Visible : Visibility.Collapsed;
        var textBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(foreground ?? _config.HoverAiForeground));
        _answer.Foreground = _label.Foreground = textBrush;
        _dot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_screenshotFlash ? "#FACC15" : _config.HoverAiInputDotColor));
        var fontSize = IsInput ? _config.HoverAiFollowUpFontSize ?? _config.HoverAiFontSize : _config.HoverAiFontSize;
        _answer.FontSize = fontSize;
        if (_editor != null)
        {
            _editor.FontSize = fontSize; _editor.Foreground = textBrush; _editor.CaretBrush = textBrush;
            _editor.Background = Brushes.Transparent; _editor.BorderThickness = new Thickness(0);
        }
        var opacity = (_status ? _config.HoverAiStatusOpacity : IsInput ? _config.HoverAiFollowUpOpacity : _config.HoverAiResponseOpacity) ?? _config.HoverAiOpacity;
        Opacity = editing ? Math.Max(.25, opacity) : opacity;
        ReflowText();
    }
    private void ReflowText()
    {
        if (_status) return;
        var auto = (IsInput ? _config.HoverAiFollowUpAutoHeight : _config.HoverAiResponseAutoHeight) && !_editing;
        if (_scroll != null) _scroll.VerticalScrollBarVisibility = auto ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden;
        if (_editor != null) _editor.VerticalScrollBarVisibility = auto ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden;
        if (!auto) { Height = _frameHeight; return; }
        var padding = _chrome.Padding;
        var contentWidth = Math.Max(1, Width - padding.Left - padding.Right - _chrome.BorderThickness.Left - _chrome.BorderThickness.Right);
        FrameworkElement content = _editor is null ? _answer : _editor;
        content.Measure(new Size(contentWidth, double.PositiveInfinity));
        var headerHeight = _header?.Visibility == Visibility.Visible ? _config.HoverAiFontSize + 12 : 0;
        Height = Math.Clamp(Math.Max(_frameHeight, content.DesiredSize.Height + padding.Top + padding.Bottom + headerHeight + 2), 12, 32000);
    }
}
