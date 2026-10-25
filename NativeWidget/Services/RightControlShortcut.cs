using System.ComponentModel;
using System.Runtime.InteropServices;

namespace NativeWidget.Services;

// A bare modifier cannot be registered with RegisterHotKey. Observe it without
// swallowing key events, so Right Ctrl still works in normal Ctrl combinations.
internal sealed class RightControlShortcut : IDisposable
{
    private readonly HookProc _callback;
    private readonly HookProc _mouseCallback;
    private readonly Action _send;
    private IntPtr _hook;
    private IntPtr _mouseHook;
    private bool _held, _combined;

    internal RightControlShortcut(Action send)
    {
        _send = send; _callback = OnKey; _mouseCallback = OnMouse;
        _hook = SetWindowsHookEx(13, _callback, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        _mouseHook = SetWindowsHookEx(14, _mouseCallback, GetModuleHandle(null), 0);
        if (_mouseHook == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error(); Dispose(); throw new Win32Exception(error);
        }
    }

    internal bool ProcessKey(uint key, bool down, bool injected, bool otherModifier = false)
    {
        if (injected) return false;
        if (key != 0xA3) { if (_held) _combined = true; return false; }
        if (down)
        {
            if (!_held) { _held = true; _combined = otherModifier; }
            return false;
        }
        var send = _held && !_combined && !otherModifier;
        _held = false; _combined = false;
        return send;
    }

    private IntPtr OnKey(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && message.ToInt32() is 0x100 or 0x101 or 0x104 or 0x105)
        {
            var input = Marshal.PtrToStructure<KeyboardInput>(data);
            var otherModifier = new[] { 0xA2, 0x10, 0x12, 0x5B, 0x5C }.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0);
            if (input.Key == 0xA3 && !_held && message.ToInt32() is 0x100 or 0x104)
                otherModifier |= Enumerable.Range(1, 254).Any(key => key is not (0x11 or 0xA3) && (GetAsyncKeyState(key) & 0x8000) != 0);
            if (ProcessKey(input.Key, message.ToInt32() is 0x100 or 0x104, (input.Flags & 0x10) != 0, otherModifier)) _send();
        }
        return CallNextHookEx(_hook, code, message, data);
    }

    private IntPtr OnMouse(int code, IntPtr message, IntPtr data)
    {
        // Ctrl+click / Ctrl+wheel is also a combination, never a Send gesture.
        if (code >= 0 && _held && message.ToInt32() is 0x201 or 0x204 or 0x207 or 0x20B or 0x20A or 0x20E) _combined = true;
        return CallNextHookEx(_mouseHook, code, message, data);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
        if (_mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
        _held = false; _combined = false;
    }

    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput { public uint Key, Scan, Flags, Time; public UIntPtr Extra; }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
