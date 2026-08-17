using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace NativeWidget.Services;

/// App-wide shelf: click-through + unpin every open widget and the launcher, with a single
/// taskbar proxy icon to restore. The proxy is a real (non-ToolWindow) window minimized to
/// the taskbar — ToolWindow style is intentionally avoided because Windows hides it there.
public sealed class AppShelf
{
    private static ImageSource? _appIcon;

    private readonly Window _launcher;
    private readonly Func<IEnumerable<Window>> _enumerateTargets;
    private readonly Action<bool> _onPinVisuals;
    private readonly Action<bool> _onGhostVisuals;
    private readonly Action _onStateChanged;

    private Window? _proxy;
    private DispatcherTimer? _armTimer;
    private bool _armRestore;
    private bool _shelved;

    public bool IsShelved => _shelved;

    public AppShelf(
        Window launcher,
        Func<IEnumerable<Window>> enumerateTargets,
        Action<bool> onPinVisuals,
        Action<bool> onGhostVisuals,
        Action onStateChanged)
    {
        _launcher = launcher;
        _enumerateTargets = enumerateTargets;
        _onPinVisuals = onPinVisuals;
        _onGhostVisuals = onGhostVisuals;
        _onStateChanged = onStateChanged;
    }

    public void Toggle() => SetShelved(!_shelved);

    public void Clear() => SetShelved(false);

    public void SetShelved(bool shelved)
    {
        // No-op when already clear (do not force re-pin on unrelated hotkeys).
        if (!shelved && !_shelved) return;
        // Already shelved with a live proxy.
        if (shelved && _shelved && _proxy != null) return;

        _shelved = shelved;
        if (shelved)
            EnterShelf();
        else
            ExitShelf();
        _onStateChanged();
    }

    private void EnterShelf()
    {
        foreach (var window in TargetsIncludingLauncher())
        {
            window.Topmost = false;
            WindowInterop.SetClickThrough(window, true);
        }
        _onPinVisuals(false);
        _onGhostVisuals(false);
        OpenProxy();
        // Re-apply after proxy.Show may shuffle Z-order / styles.
        _launcher.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (!_shelved) return;
            foreach (var window in TargetsIncludingLauncher())
                WindowInterop.SetClickThrough(window, true);
        });
    }

    private void ExitShelf()
    {
        CloseProxy();
        ApplyRestoredState();
    }

    private void ApplyRestoredState()
    {
        foreach (var window in TargetsIncludingLauncher())
        {
            WindowInterop.SetClickThrough(window, false);
            window.Topmost = true;
        }
        _onPinVisuals(true);
        _onGhostVisuals(false);
    }

    private IEnumerable<Window> TargetsIncludingLauncher()
    {
        yield return _launcher;
        foreach (var w in _enumerateTargets())
        {
            if (w != null && !ReferenceEquals(w, _launcher) && w.IsVisible)
                yield return w;
        }
    }

    private void OpenProxy()
    {
        CloseProxy();
        _armRestore = false;
        _armTimer?.Stop();

        // Must NOT use ToolWindow — that style suppresses the taskbar button.
        var proxy = new Window
        {
            Title = "Widgets",
            ShowInTaskbar = true,
            WindowStyle = WindowStyle.SingleBorderWindow,
            ResizeMode = ResizeMode.CanMinimize,
            ShowActivated = false,
            // Real size so Windows treats it as a normal app window; parked off-screen
            // then minimized so only the taskbar button remains visible.
            Width = 280,
            Height = 120,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            AllowsTransparency = false,
            Background = Brushes.Black,
            Topmost = false,
            Content = new System.Windows.Controls.TextBlock
            {
                Text = "Widgets (shelved)\nClick the taskbar icon to restore.",
                Foreground = Brushes.White,
                Margin = new Thickness(16),
                TextWrapping = TextWrapping.Wrap,
            },
        };
        var icon = ResolveAppIcon();
        if (icon != null) proxy.Icon = icon;

        proxy.SourceInitialized += (_, _) => WindowInterop.ForceTaskbarButton(proxy);
        proxy.StateChanged += Proxy_StateChanged;
        proxy.Activated += Proxy_Activated;
        proxy.Deactivated += Proxy_Deactivated;
        proxy.Closed += Proxy_Closed;
        _proxy = proxy;
        proxy.Show();
        WindowInterop.ForceTaskbarButton(proxy);
        // Minimize after Show so the button appears and the frame stays off the desktop.
        proxy.WindowState = WindowState.Minimized;

        _armTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _armTimer.Tick += (_, _) =>
        {
            _armTimer?.Stop();
            _armRestore = true;
            // Keep force-applied in case WPF rewrote styles on minimize.
            if (_proxy != null) WindowInterop.ForceTaskbarButton(_proxy);
        };
        _armTimer.Start();
    }

    private void CloseProxy()
    {
        var proxy = _proxy;
        _proxy = null;
        _armRestore = false;
        _armTimer?.Stop();
        _armTimer = null;
        if (proxy == null) return;

        proxy.StateChanged -= Proxy_StateChanged;
        proxy.Activated -= Proxy_Activated;
        proxy.Deactivated -= Proxy_Deactivated;
        proxy.Closed -= Proxy_Closed;
        try
        {
            proxy.ShowInTaskbar = false;
            if (proxy.IsVisible) proxy.Hide();
        }
        catch (InvalidOperationException) { /* teardown */ }
    }

    private void Proxy_StateChanged(object? sender, EventArgs e)
    {
        if (!_shelved || _proxy == null || !_armRestore) return;
        // Taskbar click often restores from Minimized → Normal before/with Activated.
        if (_proxy.WindowState != WindowState.Minimized)
            SetShelved(false);
    }

    private void Proxy_Deactivated(object? sender, EventArgs e)
    {
        if (_shelved) _armRestore = true;
    }

    private void Proxy_Activated(object? sender, EventArgs e)
    {
        if (!_shelved || !_armRestore) return;
        // Taskbar icon click: restore everything and drop the button.
        SetShelved(false);
        try { _launcher.Activate(); }
        catch (InvalidOperationException) { /* ignore */ }
    }

    private void Proxy_Closed(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _proxy)) return;
        _proxy = null;
        if (_shelved) SetShelved(false);
    }

    private static ImageSource? ResolveAppIcon()
    {
        if (_appIcon != null) return _appIcon;
        try
        {
            // Prefer the deployed app folder (same place as NativeWidget.exe / Start Menu).
            foreach (var dir in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            {
                var path = Path.Combine(dir, "icon.ico");
                if (!File.Exists(path)) continue;
                _appIcon = BitmapFrame.Create(new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                return _appIcon;
            }
        }
        catch { /* default exe icon */ }
        return null;
    }
}
