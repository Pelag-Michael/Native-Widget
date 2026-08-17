using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NativeWidget.Services;

namespace NativeWidget;

/// Shared header button strip for every widget window: opacity, ghost (click-through),
/// shelf (click-through + taskbar restore icon), pin (always-on-top) and close.
public partial class WidgetHeaderControls : UserControl
{
    private static readonly SolidColorBrush Muted = new(Color.FromRgb(0x77, 0x77, 0x8A));
    private static readonly SolidColorBrush AccentFallback = new(Color.FromRgb(0x4A, 0x7D, 0xFF));
    private static ImageSource? _appIcon;

    // The popup has StaysOpen=true so it doesn't vanish the instant focus shifts; this
    // timer closes it once the pointer has left both the button and the popup itself.
    private readonly DispatcherTimer _popupWatch = new() { Interval = TimeSpan.FromMilliseconds(250) };

    private Window? _owner;
    private Window? _taskbarProxy;
    private DispatcherTimer? _proxyArmTimer;
    private bool _shelved;
    // True once the proxy is safe to treat Activated as a user taskbar click.
    private bool _proxyArmRestore;

    public bool HasOpenPopup => OpacityPopup.IsOpen;
    public bool IsShelved => _shelved;

    public WidgetHeaderControls()
    {
        InitializeComponent();
        _popupWatch.Tick += (_, _) =>
        {
            if (OpacityBtn.IsMouseOver || OpacityPopup.IsMouseOver) return;
            OpacityPopup.IsOpen = false;
            _popupWatch.Stop();
        };
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _owner = Window.GetWindow(this);
        SetShelfVisual(_shelved);
        if (_shelved)
            SetGhostVisual(false);
    }

    private Window? OwnerWindow => _owner ??= Window.GetWindow(this);

    private void OpacityBtn_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        OpacityPopup.IsOpen = true;
        _popupWatch.Start();
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var owner = OwnerWindow;
        if (owner == null) return;
        owner.Opacity = e.NewValue;
        OpacityLabel.Text = $"{e.NewValue * 100:0}%";
    }

    private void GhostBtn_Click(object sender, RoutedEventArgs e)
    {
        var owner = OwnerWindow;
        if (owner == null) return;

        // Ghost and shelf both use click-through; picking ghost drops the taskbar button
        // so the only exit is Ctrl+Alt+G / launcher (classic ghost contract).
        if (_shelved)
        {
            ApplyShelf(false, keepClickThrough: true);
            SetGhostVisual(true);
            return;
        }

        var enable = !WindowInterop.IsClickThrough(owner);
        WindowInterop.SetClickThrough(owner, enable);
        SetGhostVisual(enable);
    }

    private void ShelfBtn_Click(object sender, RoutedEventArgs e)
    {
        if (OwnerWindow == null) return;
        ApplyShelf(!_shelved, keepClickThrough: false);
    }

    private void RestoreFromShelf()
    {
        if (!_shelved) return;
        ApplyShelf(false, keepClickThrough: false);
    }

    private Brush AccentBrush()
    {
        if (TryFindResource("AccentBrush") is Brush local) return local;
        if (Application.Current?.TryFindResource("AccentBrush") is Brush app) return app;
        return AccentFallback;
    }

    /// Called by the launcher when it force-disables ghost mode, so the icon stays in sync.
    public void SetGhostVisual(bool ghosted)
    {
        GhostBtn.Foreground = ghosted ? AccentBrush() : Muted;
    }

    public void SetShelfVisual(bool shelved)
    {
        ShelfBtn.Foreground = shelved ? AccentBrush() : Muted;
    }

    /// Forces shelf off (taskbar icon + click-through). Used by unghost-all and launcher reopen.
    public void ClearShelf()
    {
        if (OwnerWindow == null)
        {
            _shelved = false;
            SetShelfVisual(false);
            CloseTaskbarProxy();
            return;
        }
        if (_shelved || _taskbarProxy != null)
            ApplyShelf(false, keepClickThrough: false);
    }

    public void SetPinVisual(bool pinned)
    {
        PinBtn.Foreground = pinned ? AccentBrush() : Muted;
    }

    public void SetOpacityValue(double opacity)
    {
        opacity = Math.Clamp(opacity, OpacitySlider.Minimum, OpacitySlider.Maximum);
        OpacitySlider.Value = opacity;
        OpacityLabel.Text = $"{opacity * 100:0}%";
        var owner = OwnerWindow;
        if (owner != null) owner.Opacity = opacity;
    }

    private void ApplyShelf(bool shelved, bool keepClickThrough)
    {
        var owner = OwnerWindow;
        if (owner == null) return;

        _shelved = shelved;
        SetShelfVisual(shelved);

        if (shelved)
        {
            // Shelf owns click-through; ghost icon is off so the two modes don't look mixed.
            SetGhostVisual(false);
            // Transparent AllowsTransparency windows break if ShowInTaskbar is flipped —
            // use a tiny proxy window that owns the real taskbar button instead.
            OpenTaskbarProxy();
            // Apply after proxy.Show(): creating another top-level window can briefly
            // rewrite the owner's extended styles on some WPF builds.
            WindowInterop.SetClickThrough(owner, true);
            owner.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (_shelved && OwnerWindow is { } live)
                    WindowInterop.SetClickThrough(live, true);
            });
            return;
        }

        CloseTaskbarProxy();
        if (!keepClickThrough)
        {
            WindowInterop.SetClickThrough(owner, false);
            SetGhostVisual(false);
        }
        else
        {
            WindowInterop.SetClickThrough(owner, true);
        }
    }

    private void OpenTaskbarProxy()
    {
        CloseTaskbarProxy();
        var owner = OwnerWindow;
        if (owner == null) return;

        _proxyArmRestore = false;
        _proxyArmTimer?.Stop();
        var proxy = new Window
        {
            Title = string.IsNullOrWhiteSpace(owner.Title) ? "Widget" : owner.Title,
            ShowInTaskbar = true,
            WindowStyle = WindowStyle.ToolWindow,
            ResizeMode = ResizeMode.NoResize,
            ShowActivated = false,
            Width = 1,
            Height = 1,
            Left = -32000,
            Top = -32000,
            // Opaque (non-transparent) so WPF can keep a stable taskbar HWND.
            AllowsTransparency = false,
            Background = Brushes.Black,
            Topmost = false,
        };
        var icon = ResolveAppIcon();
        if (icon != null)
            proxy.Icon = icon;

        proxy.Activated += TaskbarProxy_Activated;
        proxy.Deactivated += TaskbarProxy_Deactivated;
        proxy.Closed += TaskbarProxy_Closed;
        _taskbarProxy = proxy;
        proxy.Show();

        // Ignore activation noise from Show(); after this, any Activated is a taskbar click.
        _proxyArmTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _proxyArmTimer.Tick += (_, _) =>
        {
            _proxyArmTimer?.Stop();
            _proxyArmRestore = true;
        };
        _proxyArmTimer.Start();
    }

    private void CloseTaskbarProxy()
    {
        var proxy = _taskbarProxy;
        _taskbarProxy = null;
        _proxyArmRestore = false;
        _proxyArmTimer?.Stop();
        _proxyArmTimer = null;
        if (proxy == null) return;

        proxy.Activated -= TaskbarProxy_Activated;
        proxy.Deactivated -= TaskbarProxy_Deactivated;
        proxy.Closed -= TaskbarProxy_Closed;
        try
        {
            // Only hide — do not Close(). Closing a transient top-level window has been
            // observed to take hide-on-close widget peers down with it in this app.
            // Hidden + ShowInTaskbar=false removes the taskbar icon immediately.
            proxy.ShowInTaskbar = false;
            if (proxy.IsVisible)
                proxy.Hide();
        }
        catch (InvalidOperationException)
        {
            // Already closing/closed during teardown.
        }
    }

    private void TaskbarProxy_Deactivated(object? sender, EventArgs e)
    {
        // Losing focus also arms restore (covers clicks that never wait for the timer).
        if (_shelved)
            _proxyArmRestore = true;
    }

    private void TaskbarProxy_Activated(object? sender, EventArgs e)
    {
        if (!_shelved || !_proxyArmRestore) return;
        // Taskbar icon click: restore interactivity and drop the icon.
        var owner = OwnerWindow;
        RestoreFromShelf();
        if (owner == null) return;
        try { owner.Activate(); }
        catch (InvalidOperationException) { /* ignore */ }
    }

    private void TaskbarProxy_Closed(object? sender, EventArgs e)
    {
        // Only react when this is still the active proxy (user/OS closed it externally).
        if (!ReferenceEquals(sender, _taskbarProxy)) return;
        _taskbarProxy = null;
        // If the proxy vanished without a restore path, do not leave the widget stuck
        // click-through with no taskbar exit.
        if (_shelved)
            RestoreFromShelf();
    }

    private static ImageSource? ResolveAppIcon()
    {
        if (_appIcon != null) return _appIcon;
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "icon.ico");
            if (File.Exists(path))
            {
                _appIcon = BitmapFrame.Create(new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                return _appIcon;
            }
        }
        catch
        {
            // Fall through — taskbar will use the default executable icon.
        }
        return null;
    }

    private void PinBtn_Click(object sender, RoutedEventArgs e)
    {
        var owner = OwnerWindow;
        if (owner != null) WindowInterop.TogglePin(owner, PinBtn);
    }

    // Close() rather than Hide(): every widget cancels Closing and hides itself, and some
    // (Notes) persist their state in that handler - going straight to Hide would skip it.
    private void CloseBtn_Click(object sender, RoutedEventArgs e) => OwnerWindow?.Close();
}
