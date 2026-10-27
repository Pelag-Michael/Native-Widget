using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace NativeWidget;

public partial class HoverAiWindow
{
    internal Rect CompactIconBounds
    {
        get
        {
            // Use the button's actual layout, including the chrome padding.
            if (CompactBarCollapsed && CompactRevealButton.ActualWidth > 0)
            {
                var offset = CompactRevealButton.TranslatePoint(new Point(), this);
                return new Rect(Left + offset.X, Top + offset.Y,
                    CompactRevealButton.ActualWidth, CompactRevealButton.ActualHeight);
            }
            return new Rect(Left + 3, Top + 3, CompactRevealButton.Width, CompactRevealButton.Height);
        }
    }
    private readonly DispatcherTimer _compactMoveTimer = new() { Interval = TimeSpan.FromMilliseconds(20) };
    private bool _compactMoving;
    private Point _compactMoveCursor;

    private void InitializeCompactMove()
    {
        PreviewMouseMove += (_, _) =>
        {
            if (_compactMoving || !CompactBarCollapsed || !CompactRevealButton.IsMouseOver || !CompactCtrlHeld()) return;
            if (CompactGetCursorPos(out var cursor)) BeginCompactMove(new Point(cursor.X, cursor.Y));
        };
        _compactMoveTimer.Tick += (_, _) =>
        {
            if (!CompactGetCursorPos(out var cursor)) { EndCompactMove(); return; }
            UpdateCompactMove(new Point(cursor.X, cursor.Y), CompactCtrlHeld() && _modeActive && IsVisible);
        };
        IsVisibleChanged += (_, _) => { if (!IsVisible) EndCompactMove(); };
        Closing += (_, _) => EndCompactMove();
    }

    internal void BeginCompactMove(Point cursor)
    {
        if (!CompactBarCollapsed || _compactMoving) return;
        _compactMoving = _dragging = true;
        _compactMoveCursor = cursor;
        _buttonDwell?.Cancel(latch: true);
        _compactMoveTimer.Start();
    }

    internal void UpdateCompactMove(Point cursor, bool ctrlHeld)
    {
        if (!_compactMoving) return;
        if (!ctrlHeld) { EndCompactMove(); return; }
        var dpi = VisualTreeHelper.GetDpi(this);
        Left += (cursor.X - _compactMoveCursor.X) / dpi.DpiScaleX;
        Top += (cursor.Y - _compactMoveCursor.Y) / dpi.DpiScaleY;
        _compactMoveCursor = cursor;
        _buttonDwell?.Cancel(latch: true);
    }

    private void EndCompactMove()
    {
        _compactMoveTimer.Stop();
        if (!_compactMoving) return;
        _compactMoving = _dragging = false;
        SavePosition();
    }

    private static bool CompactCtrlHeld() => (CompactGetAsyncKeyState(0x11) & 0x8000) != 0
        && (CompactGetAsyncKeyState(0x12) & 0x8000) == 0 && (CompactGetAsyncKeyState(0x10) & 0x8000) == 0;
    [StructLayout(LayoutKind.Sequential)] private struct CompactCursor { public int X, Y; }
    [DllImport("user32.dll", EntryPoint = "GetCursorPos")] private static extern bool CompactGetCursorPos(out CompactCursor point);
    [DllImport("user32.dll", EntryPoint = "GetAsyncKeyState")] private static extern short CompactGetAsyncKeyState(int key);
}
