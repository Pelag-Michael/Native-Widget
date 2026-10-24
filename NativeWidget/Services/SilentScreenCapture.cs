using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace NativeWidget.Services;

internal static class SilentScreenCapture
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public uint Size; public Rect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    public static BitmapSource Capture(NativeWidget.Models.AppConfig config)
    {
        if (!config.HoverAiCaptureRegionEnabled) return CapturePointerMonitor();
        var left = GetSystemMetrics(76); var top = GetSystemMetrics(77);
        var right = left + GetSystemMetrics(78); var bottom = top + GetSystemMetrics(79);
        var x = Math.Max(left, config.HoverAiCaptureX); var y = Math.Max(top, config.HoverAiCaptureY);
        var r = Math.Min(right, (long)config.HoverAiCaptureX + config.HoverAiCaptureWidth);
        var b = Math.Min(bottom, (long)config.HoverAiCaptureY + config.HoverAiCaptureHeight);
        if (r <= x || b <= y) throw new InvalidOperationException("Capture area is outside the desktop.");
        return ScreenOcrService.Capture(x, y, (int)(r - x), (int)(b - y));
    }

    public static BitmapSource CapturePointerMonitor()
    {
        if (!GetCursorPos(out var pointer)) throw new InvalidOperationException("Screen capture could not find the pointer.");
        var monitor = MonitorFromPoint(pointer, 2);
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) throw new InvalidOperationException("Screen capture could not read the monitor.");
        // Capture physical pixels directly: no selection window, dimming, flash or clipboard change.
        return ScreenOcrService.Capture(info.Monitor.Left, info.Monitor.Top,
            info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top);
    }
}
