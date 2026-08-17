using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace NativeWidget.Services;

public static class WindowInterop
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_APPWINDOW = 0x40000;
    private const int WS_EX_TRANSPARENT = 0x20;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndTop = IntPtr.Zero;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter,
        int x, int y, int width, int height, uint flags);

    // Removes the window from the Alt-Tab switcher (it's a small widget, not a real app window).
    public static void HideFromAltTab(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            var style = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, (style | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW);
        };
    }

    private static readonly SolidColorBrush MutedBrush = new(Color.FromRgb(0x77, 0x77, 0x8A));

    // Toggles the window's always-on-top state and reflects it on the pin icon's color.
    public static void TogglePin(Window window, Button pinButton)
    {
        window.Topmost = !window.Topmost;
        pinButton.Foreground = window.Topmost
            ? (Brush)window.FindResource("AccentBrush")
            : MutedBrush;
    }

    /// Ghost mode: mouse clicks pass straight through the window to whatever is behind it.
    /// The window stays visible but becomes entirely non-interactive - including its own
    /// buttons - so there must always be an external way back out (see the Ctrl+Alt+G
    /// hotkey on the launcher).
    public static void SetClickThrough(Window window, bool enabled)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        var style = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE,
            enabled ? style | WS_EX_TRANSPARENT : style & ~WS_EX_TRANSPARENT);
    }

    public static bool IsClickThrough(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return false;
        return (GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TRANSPARENT) != 0;
    }

    /// Reasserts the window at the front of the topmost band without stealing keyboard
    /// focus. Setting WPF Topmost=true alone does not reorder it above another topmost
    /// window that was activated later.
    public static void EnsureTopmost(Window window)
    {
        window.Topmost = true;
        var hwnd = new WindowInteropHelper(window).Handle;
        EnsureTopmost(hwnd);
    }

    /// WPF Popup owns a separate native HWND, so the launcher's radial menu and tool
    /// panel need their own z-order assertion after opening.
    public static void EnsureTopmost(Visual visual)
    {
        if (PresentationSource.FromVisual(visual) is HwndSource source)
            EnsureTopmost(source.Handle);
    }

    private static void EnsureTopmost(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
        // HWND_TOPMOST preserves membership in the topmost band but Windows may retain
        // the previous order when the HWND was already topmost. HWND_TOP performs the
        // actual reorder within that band.
        SetWindowPos(hwnd, HwndTop, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
    }
}
