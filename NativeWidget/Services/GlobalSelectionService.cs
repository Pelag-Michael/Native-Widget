using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Automation;

namespace NativeWidget.Services;

public sealed record CapturedSelection(string Text, string SourceApp);

public sealed class GlobalSelectionService : IDisposable
{
    private const int WhMouseLl = 14;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const uint KeyeventfKeyup = 0x0002;
    private const byte VkControl = 0x11;
    private const byte VkC = 0x43;
    private const int VkMenu = 0x12;
    private const int VkLwin = 0x5B;
    private const int VkRwin = 0x5C;

    private readonly Dispatcher _dispatcher;
    private readonly LowLevelMouseProc _hookProc;
    private IntPtr _hook;
    private Point _dragStart;
    private long _dragStartedAt;
    private bool _mouseDown;
    private bool _capturePending;

    public event Func<CapturedSelection, Task>? TextCaptured;
    public bool IsRunning => _hook != IntPtr.Zero;

    public GlobalSelectionService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _hookProc = HookCallback;
    }

    public void Start()
    {
        if (_hook != IntPtr.Zero) return;
        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;
        _hook = SetWindowsHookEx(WhMouseLl, _hookProc, GetModuleHandle(module?.ModuleName), 0);
    }

    public void Stop()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _mouseDown = false;
    }

    public async Task<CapturedSelection?> CaptureCurrentSelectionAsync(IntPtr expectedWindow = default)
    {
        if (_capturePending) return null;
        _capturePending = true;
        try
        {
            var foreground = GetForegroundWindow();
            if (expectedWindow != IntPtr.Zero && foreground != expectedWindow)
            {
                return null;
            }
            GetWindowThreadProcessId(foreground, out var processId);
            if (processId == Environment.ProcessId) return null;
            try
            {
                if (AutomationElement.FocusedElement?.Current.IsPassword == true) return null;
            }
            catch { }

            // Alt/Windows-modified drags often mean a special app gesture, not ordinary
            // text selection. Never synthesize Ctrl+C into those chords.
            if (IsKeyDown(VkMenu) || IsKeyDown(VkLwin) || IsKeyDown(VkRwin)) return null;
            var sourceApp = DescribeWindow(foreground, processId);

            DataObject? previous = null;
            uint beforeSequence = 0;
            try
            {
                previous = SnapshotClipboard();
                beforeSequence = GetClipboardSequenceNumber();
            }
            catch { }

            if (GetForegroundWindow() != foreground) return null;
            var controlWasDown = IsKeyDown(VkControl);
            if (!controlWasDown) keybd_event(VkControl, 0, 0, UIntPtr.Zero);
            keybd_event(VkC, 0, 0, UIntPtr.Zero);
            keybd_event(VkC, 0, KeyeventfKeyup, UIntPtr.Zero);
            if (!controlWasDown) keybd_event(VkControl, 0, KeyeventfKeyup, UIntPtr.Zero);

            string? text = null;
            for (var i = 0; i < 12; i++)
            {
                await Task.Delay(45);
                try
                {
                    if (GetClipboardSequenceNumber() != beforeSequence && Clipboard.ContainsText())
                    {
                        text = Clipboard.GetText().Trim();
                        break;
                    }
                }
                catch { }
            }

            if (previous != null)
            {
                try { Clipboard.SetDataObject(previous, true); } catch { }
            }

            return string.IsNullOrWhiteSpace(text) ? null : new CapturedSelection(text, sourceApp);
        }
        finally
        {
            _capturePending = false;
        }
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var info = Marshal.PtrToStructure<MsllHookStruct>(lParam);
            if (wParam.ToInt32() == WmLButtonDown)
            {
                _dragStart = new Point(info.Point.X, info.Point.Y);
                _dragStartedAt = Environment.TickCount64;
                _mouseDown = true;
            }
            else if (wParam.ToInt32() == WmLButtonUp && _mouseDown)
            {
                _mouseDown = false;
                var dx = Math.Abs(info.Point.X - _dragStart.X);
                var dy = Math.Abs(info.Point.Y - _dragStart.Y);
                var duration = Environment.TickCount64 - _dragStartedAt;
                if ((dx >= 7 || dy >= 7) && duration >= 80)
                {
                    var selectionWindow = GetForegroundWindow();
                    _dispatcher.BeginInvoke(async () => await CaptureAndRaiseAsync(selectionWindow));
                }
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private async Task CaptureAndRaiseAsync(IntPtr selectionWindow)
    {
        await Task.Delay(120);
        var captured = await CaptureCurrentSelectionAsync(selectionWindow);
        if (captured != null && TextCaptured != null) await TextCaptured(captured);
    }

    private static string DescribeWindow(IntPtr hwnd, uint processId)
    {
        try
        {
            var process = Process.GetProcessById((int)processId);
            var titleLength = GetWindowTextLength(hwnd);
            var title = titleLength > 0 ? new System.Text.StringBuilder(titleLength + 1) : null;
            if (title != null) GetWindowText(hwnd, title, title.Capacity);
            return string.IsNullOrWhiteSpace(title?.ToString()) ? process.ProcessName : $"{process.ProcessName} · {title}";
        }
        catch { return ""; }
    }

    public void Dispose() => Stop();

    private static DataObject? SnapshotClipboard()
    {
        var current = Clipboard.GetDataObject();
        if (current == null) return null;
        var snapshot = new DataObject();
        foreach (var format in current.GetFormats())
        {
            try
            {
                var value = current.GetData(format);
                if (value != null) snapshot.SetData(format, value);
            }
            catch { }
        }
        return snapshot;
    }

    private static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsllHookStruct
    {
        public NativePoint Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hookId, LowLevelMouseProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);
    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder text, int count);
    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hwnd);
}
