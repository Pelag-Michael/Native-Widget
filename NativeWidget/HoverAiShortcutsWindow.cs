using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NativeWidget.Models;
using NativeWidget.Services;

namespace NativeWidget;

internal sealed class HoverAiShortcutsWindow : Window, IDisposable
{
    private readonly AppConfig _config;
    private readonly Border _chrome;
    private readonly Grid _layout = new() { Width = 336, Height = 416 };
    private readonly WidgetShortcutsView _shortcuts = new() { QuickAiOnly = true, Focusable = false };
    private readonly DockPanel _header = new() { Background = Brushes.Transparent, Margin = new Thickness(0,0,0,8) };
    private readonly TextBlock _title = new() { Text = "Phím tắt", FontWeight = FontWeights.SemiBold, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly DispatcherTimer _holdWatch = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private bool _disposing, _ready, _editing;
    internal Button SettingsButton { get; }
    internal event Action? BoundsChanged;
    internal event Action? SettingsRequested;
    internal Func<bool> ShortcutHeld { get; set; } = () => (GetAsyncKeyState(0x11) & 0x8000) != 0
        && (GetAsyncKeyState(0x12) & 0x8000) != 0 && (GetAsyncKeyState(0xBF) & 0x8000) != 0;
    internal double ContentScaleForTests => (_layout.TransformToAncestor(_chrome).Transform(new Point(1, 0)) - _layout.TransformToAncestor(_chrome).Transform(new Point())).Length;

    internal HoverAiShortcutsWindow(AppConfig config)
    {
        _config = config;
        config.HoverAiShortcutsBackground ??= config.HoverAiBackground;
        config.HoverAiShortcutsForeground ??= config.HoverAiForeground;
        config.HoverAiShortcutsOpacity ??= config.HoverAiOpacity;
        _ = new HoverDwellController(this, _ => config.HoverAiDwellButtons);
        Title = "Quick AI shortcuts";
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        Topmost = true; ShowInTaskbar = false; ShowActivated = false;
        Cursor = Cursors.Arrow; ForceCursor = true; ResizeMode = ResizeMode.CanResizeWithGrip;
        MinWidth = MinHeight = 36;
        Width = Math.Clamp(config.HoverAiShortcutsWidth, 36, 32000); Height = Math.Clamp(config.HoverAiShortcutsHeight, 36, 32000);
        var body = new DockPanel();
        _header.Children.Add(_title);
        _header.MouseLeftButtonDown += (_, e) => { if (e.ChangedButton == MouseButton.Left) { DragMove(); e.Handled = true; } };
        DockPanel.SetDock(_header, Dock.Top); body.Children.Add(_header);
        body.Children.Add(new ScrollViewer { Content = _shortcuts, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false });
        _layout.Children.Add(body);
        _chrome = new Border { Child = new Viewbox { Child = _layout, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both }, Padding = new Thickness(8), CornerRadius = new CornerRadius(10) };
        var root = new Grid(); root.Children.Add(_chrome);
        SettingsButton = new Button { Content = "\uE713", FontFamily = new FontFamily("Segoe MDL2 Assets"), Width = 22, Height = 22,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Focusable = false,
            Style = (Style)Application.Current.FindResource("SecondaryBtnStyle") };
        SettingsButton.Click += (_, _) => SettingsRequested?.Invoke(); root.Children.Add(SettingsButton); Content = root;
        WindowInterop.HideFromAltTab(this);
        SourceInitialized += (_, _) => WindowInterop.SetNoActivate(this, true);
        Loaded += (_, _) => _ready = true;
        LocationChanged += (_, _) => SaveBounds(); SizeChanged += (_, _) => SaveBounds();
        _holdWatch.Tick += (_, _) => PollHold();
        IsVisibleChanged += (_, _) => { if (!IsVisible) _holdWatch.Stop(); };
        Closing += (_, e) => { if (!_disposing) { e.Cancel = true; Hide(); } };
        RefreshTheme();
    }

    internal void ShowWhileHeld(Point anchor) { Open(anchor); _holdWatch.Start(); }
    internal void PollHold() { if (!ShortcutHeld()) { _holdWatch.Stop(); if (!_editing) Hide(); } }
    internal void SetEditing(bool editing, Point anchor)
    {
        var wasEditing = _editing; _editing = editing;
        RefreshTheme();
        if (editing) Open(anchor);
        else if (wasEditing) { if (ShortcutHeld()) _holdWatch.Start(); else Hide(); }
    }
    private void Open(Point anchor)
    {
        RefreshTheme();
        if (!_ready)
        {
            Left = _config.HoverAiShortcutsLeft ?? anchor.X; Top = _config.HoverAiShortcutsTop ?? anchor.Y;
            Left = Math.Clamp(Left, SystemParameters.VirtualScreenLeft, Math.Max(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width));
            Top = Math.Clamp(Top, SystemParameters.VirtualScreenTop, Math.Max(SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - Height));
        }
        Show(); WindowInterop.EnsureTopmost(this);
    }
    internal void RefreshTheme()
    {
        Brush ColorBrush(string value, Color fallback)
        {
            try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)); }
            catch (FormatException) { return new SolidColorBrush(fallback); }
        }
        _chrome.Background = _config.HoverAiShortcutsTextOnly ? Brushes.Transparent : ColorBrush(_config.HoverAiShortcutsBackground!, Colors.Black);
        _chrome.Padding = new Thickness(_config.HoverAiShortcutsTextOnly ? 0 : 8);
        _shortcuts.Foreground = _title.Foreground = ColorBrush(_config.HoverAiShortcutsForeground!, Colors.White);
        SettingsButton.Foreground = _shortcuts.Foreground;
        _shortcuts.ShortcutFontSize = _config.HoverAiShortcutsFontSize;
        SettingsButton.Visibility = _editing ? Visibility.Visible : Visibility.Collapsed;
        _header.Visibility = _editing ? Visibility.Visible : Visibility.Collapsed;
        Opacity = Math.Clamp(_config.HoverAiShortcutsOpacity ?? 1, 0, 1);
    }
    private void SaveBounds()
    {
        if (!_ready) return;
        _config.HoverAiShortcutsLeft = Left; _config.HoverAiShortcutsTop = Top;
        _config.HoverAiShortcutsWidth = Width; _config.HoverAiShortcutsHeight = Height;
        BoundsChanged?.Invoke();
    }
    public void Dispose() { _holdWatch.Stop(); _disposing = true; Close(); }
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}
