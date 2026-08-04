using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using NativeWidget.Models;

namespace NativeWidget.Services;

public sealed class WindowSessionEntry
{
    public string Key { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? ContextId { get; set; }
    public bool IsOpen { get; set; }
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

public sealed class WindowSessionSnapshot
{
    public List<WindowSessionEntry> Windows { get; set; } = new();
}

/// Persists visibility and bounds continuously so session restoration also survives crashes or
/// power loss. All calls originate on the WPF dispatcher; a short per-window debounce avoids a
/// disk write for every pixel while the user drags or resizes a widget.
public static class WindowSessionService
{
    private sealed record Tracked(Window Window, AppConfig Config, WindowSessionEntry Entry,
        DispatcherTimer BoundsTimer, bool TrackVisibility, bool RestoreSize,
        double DesignWidth, double DesignHeight);

    private static readonly Dictionary<string, Tracked> TrackedWindows = new(StringComparer.Ordinal);
    private static WindowSessionSnapshot? _snapshot;
    private static string? _snapshotFilePath;
    private static bool _capturingShutdown;
    private static string FilePath => AppConfig.TokenPath("window-session.json");

    /// <param name="restoreSize">
    /// When false (the fixed-size launcher), only Left/Top are restored and captured size is
    /// forced to the window's design Width/Height so a poisoned session cannot flatten the circle.
    /// </param>
    public static void Track(Window window, AppConfig config, string key, string kind,
        string? contextId = null, bool trackVisibility = true, bool restoreSize = true)
    {
        if (TrackedWindows.ContainsKey(key)) return;
        // Snapshot design size before any session restore can stretch the window.
        var designWidth = IsFinite(window.Width) && window.Width > 0 ? window.Width : 52;
        var designHeight = IsFinite(window.Height) && window.Height > 0 ? window.Height : 52;

        var snapshot = Snapshot();
        var entry = snapshot.Windows.FirstOrDefault(item => item.Key == key) ?? new WindowSessionEntry
        {
            Key = key,
            Kind = kind,
            ContextId = contextId,
        };
        entry.Kind = kind;
        entry.ContextId = contextId;
        if (!snapshot.Windows.Contains(entry)) snapshot.Windows.Add(entry);

        if (config.RestoreWindowSessionEnabled)
            ApplySafeBounds(window, entry, restoreSize, designWidth, designHeight);
        else if (!restoreSize)
            LockDesignSize(window, designWidth, designHeight);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
        var tracked = new Tracked(window, config, entry, timer, trackVisibility, restoreSize,
            designWidth, designHeight);
        TrackedWindows[key] = tracked;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            CaptureWindow(tracked, save: true);
        };
        window.LocationChanged += (_, _) => ScheduleBoundsCapture(tracked);
        window.SizeChanged += (_, _) => ScheduleBoundsCapture(tracked);
        window.IsVisibleChanged += (_, _) =>
        {
            if (_capturingShutdown || !tracked.Config.RestoreWindowSessionEnabled) return;
            CaptureWindow(tracked, save: true);
        };
        window.Closed += (_, _) =>
        {
            timer.Stop();
            if (!_capturingShutdown && tracked.Config.RestoreWindowSessionEnabled)
            {
                tracked.Entry.IsOpen = false;
                Save();
            }
            TrackedWindows.Remove(key);
        };
    }

    public static string TrackNewPopout(Window window, AppConfig config, string kind, string contextId)
    {
        var key = $"{kind}:{contextId}:{Guid.NewGuid():N}";
        Track(window, config, key, kind, contextId);
        return key;
    }

    public static IReadOnlyList<WindowSessionEntry> OpenWindows(AppConfig config)
    {
        if (!config.RestoreWindowSessionEnabled) return Array.Empty<WindowSessionEntry>();
        return Snapshot().Windows.Where(entry => entry.IsOpen && entry.Kind != "Launcher")
            .Select(Clone).ToList();
    }

    public static void SaveCurrentSession()
    {
        foreach (var tracked in TrackedWindows.Values) CaptureWindow(tracked, save: false);
        Save();
    }

    public static void CaptureForShutdown()
    {
        if (_capturingShutdown) return;
        _capturingShutdown = true;
        foreach (var tracked in TrackedWindows.Values)
        {
            tracked.BoundsTimer.Stop();
            CaptureWindow(tracked, save: false);
        }
        Save();
    }

    private static void ScheduleBoundsCapture(Tracked tracked)
    {
        if (_capturingShutdown || !tracked.Config.RestoreWindowSessionEnabled) return;
        tracked.BoundsTimer.Stop();
        tracked.BoundsTimer.Start();
    }

    private static void CaptureWindow(Tracked tracked, bool save)
    {
        if (!tracked.Config.RestoreWindowSessionEnabled) return;
        var window = tracked.Window;
        var bounds = window.RestoreBounds;
        if (IsFinite(bounds.Left) && IsFinite(bounds.Top))
        {
            tracked.Entry.Left = bounds.Left;
            tracked.Entry.Top = bounds.Top;
        }
        if (tracked.RestoreSize)
        {
            if (IsFinite(bounds.Width) && bounds.Width > 0) tracked.Entry.Width = bounds.Width;
            if (IsFinite(bounds.Height) && bounds.Height > 0) tracked.Entry.Height = bounds.Height;
        }
        else
        {
            // Fixed-size chrome (launcher): always persist design size — RestoreBounds can
            // report a stretched width on some DPI/session paths and would re-poison restore.
            tracked.Entry.Width = tracked.DesignWidth;
            tracked.Entry.Height = tracked.DesignHeight;
            LockDesignSize(window, tracked.DesignWidth, tracked.DesignHeight);
        }
        if (tracked.TrackVisibility) tracked.Entry.IsOpen = window.IsVisible;
        else tracked.Entry.IsOpen = true;
        if (save) Save();
    }

    private static void ApplySafeBounds(Window window, WindowSessionEntry entry, bool restoreSize,
        double designWidth, double designHeight)
    {
        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualWidth = Math.Max(200, SystemParameters.VirtualScreenWidth);
        var virtualHeight = Math.Max(120, SystemParameters.VirtualScreenHeight);

        double width;
        double height;
        if (restoreSize)
        {
            if (!IsFinite(entry.Width) || !IsFinite(entry.Height) || entry.Width <= 0 || entry.Height <= 0)
                return;
            var minWidth = IsFinite(window.MinWidth) && window.MinWidth > 0 ? window.MinWidth : 80;
            var minHeight = IsFinite(window.MinHeight) && window.MinHeight > 0 ? window.MinHeight : 50;
            width = Math.Clamp(entry.Width, minWidth, virtualWidth);
            height = Math.Clamp(entry.Height, minHeight, virtualHeight);
            window.Width = width;
            window.Height = height;
        }
        else
        {
            LockDesignSize(window, designWidth, designHeight);
            width = designWidth;
            height = designHeight;
            // Heal any previously saved non-square launcher size in the snapshot.
            entry.Width = width;
            entry.Height = height;
        }

        if (!IsFinite(entry.Left) || !IsFinite(entry.Top)) return;
        const double visibleEdge = 56;
        var left = Math.Clamp(entry.Left, virtualLeft - width + visibleEdge,
            virtualLeft + virtualWidth - visibleEdge);
        var top = Math.Clamp(entry.Top, virtualTop,
            virtualTop + virtualHeight - Math.Min(visibleEdge, height));
        window.Left = left;
        window.Top = top;
    }

    private static void LockDesignSize(Window window, double designWidth, double designHeight)
    {
        window.Width = designWidth;
        window.Height = designHeight;
    }

    private static WindowSessionSnapshot Snapshot()
    {
        var filePath = FilePath;
        if (_snapshot != null && string.Equals(_snapshotFilePath, filePath, StringComparison.OrdinalIgnoreCase))
            return _snapshot;
        try
        {
            _snapshot = JsonSerializer.Deserialize<WindowSessionSnapshot>(File.ReadAllText(filePath)) ?? new();
            _snapshot.Windows ??= new();
        }
        catch { _snapshot = new(); }
        _snapshotFilePath = filePath;
        return _snapshot;
    }

    private static void Save()
    {
        try
        {
            AppConfig.EnsureFolder();
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Snapshot(), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* Session restore must never take down the widget process. */ }
    }

    private static WindowSessionEntry Clone(WindowSessionEntry entry) => new()
    {
        Key = entry.Key,
        Kind = entry.Kind,
        ContextId = entry.ContextId,
        IsOpen = entry.IsOpen,
        Left = entry.Left,
        Top = entry.Top,
        Width = entry.Width,
        Height = entry.Height,
    };

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
