using System.Windows;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using NativeWidget.Services;
using NativeWidget.Models;

namespace NativeWidget;

public partial class MainWindow
{
    internal bool CodexBackendEnabled { get; set; } = true;
    private readonly DispatcherTimer _hoverWatch = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly HashSet<Window> _hoverHiddenWindows = new();
    private HoverAiWindow? _hoverAi;
    private bool _hoverModeActive;
    private bool _hoverSuppressed;
    private bool _hoverSettingsOpen;
    private Window? _anchorFrame;
    private readonly DispatcherTimer _anchorFrameTimer = new() { Interval = TimeSpan.FromMilliseconds(1800) };
    private bool _copyShortcutBusy;
    private bool _copyShortcutAvailable;
    private bool _screenshotShortcutAvailable;
    private string? _aiInputShortcutError;
    private int _hoverModeGeneration;
    private Point _hoverAnchor;
    internal Point HoverAnchorForTests => _hoverAnchor;

    private void RegisterLocateShortcut()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        UnregisterHotKey(hwnd, LocateHotkeyId);
        var mode = _config.InvisibleHoverEnabled;
        var registered = RegisterHotKey(hwnd, LocateHotkeyId, ModControl | ModAlt | 0x4000, mode ? VkLeft : 0x46);
        try
        {
            System.IO.File.WriteAllText(AppConfig.TokenPath("hotkey-status.log"),
                $"{DateTime.Now:O} {(mode ? "Ctrl+Alt+Left" : "Ctrl+Alt+F")} registered={registered}\n");
        }
        catch (System.IO.IOException) { }
    }

    internal void ShowHoverAnchorFrame()
    {
        if (_anchorFrame == null)
        {
            _anchorFrame = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                ShowActivated = false, ShowInTaskbar = false, Topmost = true,
                ResizeMode = ResizeMode.NoResize, IsHitTestVisible = false,
                Content = new System.Windows.Shapes.Rectangle
                {
                    Stroke = System.Windows.Media.Brushes.White, StrokeThickness = 2,
                    StrokeDashArray = new System.Windows.Media.DoubleCollection { 3, 3 },
                    RadiusX = 3, RadiusY = 3, Margin = new Thickness(1),
                },
            };
            WindowInterop.HideFromAltTab(_anchorFrame);
        }
        _anchorFrame.Left = Math.Clamp(_hoverModeActive ? _hoverAnchor.X : Left, SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width);
        _anchorFrame.Top = Math.Clamp(_hoverModeActive ? _hoverAnchor.Y : Top, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - Height);
        _anchorFrame.Width = Width;
        _anchorFrame.Height = Height;
        _anchorFrame.Show();
        _anchorFrame.Opacity = 1;
        WindowInterop.EnsureTopmost(_anchorFrame);
        WindowInterop.SetClickThrough(_anchorFrame, true);
        _anchorFrameTimer.Stop();
        _anchorFrameTimer.Start();
    }
    internal bool AnchorFrameVisibleForTests => _anchorFrame?.IsVisible == true;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, System.Text.StringBuilder name, int count);
    [DllImport("user32.dll")]
    private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);

    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();

    private async Task CopyWithAlternateShortcutAsync()
    {
        if (_copyShortcutBusy) return;
        _copyShortcutBusy = true;
        var generation = _hoverModeGeneration;
        var foreground = GetForegroundWindow();
        try
        {
            // Wait for the real keys to be released. Never release the user's held Ctrl
            // or copy into a different window after focus has changed.
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while ((GetAsyncKeyState(0x11) & 0x8000) != 0 || (GetAsyncKeyState(0x25) & 0x8000) != 0)
            {
                if (!_hoverModeActive || generation != _hoverModeGeneration || DateTime.UtcNow > deadline || GetForegroundWindow() != foreground) return;
                await Task.Delay(15);
            }
            if (!_hoverModeActive || generation != _hoverModeGeneration || GetForegroundWindow() != foreground) return;
            if ((GetAsyncKeyState(0x10) & 0x8000) != 0 || (GetAsyncKeyState(0x12) & 0x8000) != 0) return;
            var windowClass = new System.Text.StringBuilder(256);
            GetClassName(foreground, windowClass, windowClass.Capacity);
            GetWindowThreadProcessId(foreground, out var processId);
            string processName;
            try { using var process = System.Diagnostics.Process.GetProcessById((int)processId); processName = process.ProcessName; }
            catch (ArgumentException) { return; }
            var terminal = windowClass.ToString() is "ConsoleWindowClass" or "CASCADIA_HOSTING_WINDOW_CLASS" ||
                processName is "WindowsTerminal" or "wezterm-gui" or "mintty" or "ConEmu64" or "ConEmu";
            var beforeClipboard = GetClipboardSequenceNumber();
            keybd_event(0x11, 0, 0, UIntPtr.Zero);
            try
            {
                if (terminal) keybd_event(0x10, 0, 0, UIntPtr.Zero);
                keybd_event(0x43, 0, 0, UIntPtr.Zero);
                keybd_event(0x43, 0, 2, UIntPtr.Zero);
            }
            finally
            {
                if (terminal) keybd_event(0x10, 0, 2, UIntPtr.Zero);
                keybd_event(0x11, 0, 2, UIntPtr.Zero);
            }
            if (_config.HoverAiQuicker)
            {
                var copyDeadline = DateTime.UtcNow.AddMilliseconds(900);
                while (DateTime.UtcNow < copyDeadline)
                {
                    await Task.Delay(25);
                    if (!_hoverModeActive || generation != _hoverModeGeneration || !_config.HoverAiQuicker) return;
                    if (GetClipboardSequenceNumber() == beforeClipboard) continue;
                    try
                    {
                        if (!Clipboard.ContainsText()) return;
                        var copied = Clipboard.GetText();
                        if (string.IsNullOrWhiteSpace(copied)) return;
                        EnsureHoverAiPanel(); _hoverAi!.ReceiveQuickCopy(copied);
                        return;
                    }
                    catch (System.Runtime.InteropServices.ExternalException) { }
                }
            }
        }
        finally { _copyShortcutBusy = false; }
    }

    private void InitializeInvisibleHover()
    {
        HoverAiAppearanceStore.LoadInto(_config);
        _anchorFrameTimer.Tick += (_, _) => { _anchorFrameTimer.Stop(); _anchorFrame?.Hide(); };
        _hoverWatch.Tick += (_, _) =>
        {
            if (!_hoverModeActive || !GetCursorPos(out var cursor)) return;
            HideOtherHoverWindows();
            var screenPoint = new Point(cursor.X, cursor.Y);
            var overAnchor = new Rect(_hoverAnchor, RenderSize).Contains(GetCursorInDip());
            var overPanel = _hoverAi?.IsVisible == true &&
                new Rect(new Point(), _hoverAi.RenderSize).Contains(_hoverAi.PointFromScreen(screenPoint));
            UpdateInvisibleHoverPointer(overAnchor, overPanel || _hoverAi?.IsOverAuxiliary(screenPoint) == true);
        };
        Closed += (_, _) => { _hoverWatch.Stop(); _anchorFrameTimer.Stop(); _anchorFrame?.Close(); _hoverAi?.Close(); };
    }

    internal void SetInvisibleHoverMode(bool enabled)
    {
        if (_hoverModeActive == enabled) return;
        _hoverModeActive = enabled;
        _hoverModeGeneration++;
        _config.InvisibleHoverEnabled = enabled;
        RegisterLocateShortcut();
        _hoverSettingsOpen = false;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (enabled)
        {
            _copyShortcutAvailable = RegisterHotKey(hwnd, CopyHotkeyId, ModControl | 0x4000, 0x25);
            _screenshotShortcutAvailable = RegisterHotKey(hwnd, ScreenshotHotkeyId, ModControl | 0x4000, 0xBC);
            RegisterAiInputShortcuts(hwnd);
            try
            {
                System.IO.File.AppendAllText(AppConfig.TokenPath("hotkey-status.log"),
                    $"{DateTime.Now:O} Ctrl+Left registered={_copyShortcutAvailable}\nCtrl+Comma screenshot registered={_screenshotShortcutAvailable}\n");
            }
            catch (System.IO.IOException) { }
            _hoverAnchor = new Point(
                Math.Clamp(_config.HoverAiLeft is double savedLeft && double.IsFinite(savedLeft) ? savedLeft + 12 : Left,
                    SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width),
                Math.Clamp(_config.HoverAiTop is double savedTop && double.IsFinite(savedTop) ? savedTop + 12 : Top,
                    SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - Height));
            _hoverAi?.SetModeActive(true);
            _shelf?.Clear();
            SnapCloseLauncherForDrag();
            _launcherCloseTimer.Stop();
            _hintHideTimer.Stop();
            _windowToolsCloseTimer.Stop();
            LauncherHintPopup.IsOpen = false;
            _hoverSuppressed = true; // Enabling never pops the input under a stationary cursor.
            RootBorder.Opacity = 0;
            Opacity = 0;
            WindowInterop.SetClickThrough(this, true);
            HideOtherHoverWindows();
            _hoverWatch.Start();
        }
        else
        {
            UnregisterHotKey(hwnd, CopyHotkeyId);
            UnregisterHotKey(hwnd, ScreenshotHotkeyId);
            UnregisterAiInputShortcuts(hwnd);
            _hoverWatch.Stop();
            _hoverAi?.SetModeActive(false);
            _hoverAi?.EndLayoutEditing();
            _hoverAi?.Hide();
            Opacity = 1;
            RootBorder.Opacity = 1;
            WindowInterop.SetClickThrough(this, false);
            foreach (var window in _hoverHiddenWindows.ToArray())
            {
                WindowSessionService.SetTemporarilyHidden(window, false);
                if (Application.Current.Windows.Cast<Window>().Contains(window)) window.Show();
            }
            _hoverHiddenWindows.Clear();
            UpdateGlobalWindowControls();
        }
    }

    private void HideOtherHoverWindows()
    {
        foreach (var window in Application.Current.Windows.Cast<Window>().ToArray())
        {
            if (!window.IsVisible || window == this || window == _hoverAi || window == _anchorFrame || _hoverAi?.OwnsAuxiliaryWindow(window) == true ||
                (_hoverSettingsOpen && window == _settingsWindow)) continue;
            _hoverHiddenWindows.Add(window);
            WindowSessionService.SetTemporarilyHidden(window, true);
            window.Hide();
        }
    }

    internal void UpdateInvisibleHoverPointer(bool overAnchor, bool overPanel)
    {
        if (!_hoverModeActive) return;
        if (_hoverAi?.IsPointerInteractionActive == true) return;
        if (_hoverSuppressed)
        {
            if (!overAnchor && !overPanel) _hoverSuppressed = false;
            return;
        }
        if (overAnchor && _hoverAi?.IsVisible != true) ShowInvisibleHoverPanel();
        else if (!overAnchor && !overPanel) _hoverAi?.Hide();
    }

    private void CaptureInvisibleScreenshot()
    {
        if (!_hoverModeActive) return;
        try
        {
            var screenshot = SilentScreenCapture.Capture(_config);
            EnsureHoverAiPanel();
            _hoverAi!.ReceiveScreenshot(screenshot, fromCapture: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            EnsureHoverAiPanel();
            _hoverAi!.ShowStatus("Screen capture failed. Try again.");
        }
    }

    private void EnsureHoverAiPanel()
    {
        if (_hoverAi == null)
        {
            _hoverAi = new HoverAiWindow(_config);
            if (CodexBackendEnabled) _hoverAi.EnableCodexBackend();
            _hoverAi.PositionChanged += () =>
            {
                _hoverAnchor = new Point(_hoverAi.Left + 12, _hoverAi.Top + 12);
            };
            _hoverAi.SettingsRequested += () =>
            {
                _hoverSettingsOpen = true;
                var settings = GetOrCreateWidget("Settings");
                settings.Show();
                if (!HoverDwellController.IsInvoking) settings.Activate();
            };
            _hoverAi.DismissRequested += () => { _hoverAi.Hide(); _hoverSuppressed = true; };
        }
    }

    private void ShowInvisibleHoverPanel()
    {
        EnsureHoverAiPanel();
        if (_hoverAi == null) return;
        // Overlap the original 52px hotspot, so there is no gap between it and the input.
        var dip = _hoverAnchor;
        _hoverAi.Left = Math.Clamp(dip.X - 12, SystemParameters.VirtualScreenLeft,
            Math.Max(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - _hoverAi.Width));
        var panelHeight = _hoverAi.ActualHeight > 0 ? _hoverAi.ActualHeight : 100;
        _hoverAi.Top = Math.Clamp(dip.Y - 12, SystemParameters.VirtualScreenTop,
            Math.Max(SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - panelHeight));
        _hoverAi.SetModeActive(true);
        _hoverAi.Show();
        if (!_copyShortcutAvailable)
            _hoverAi.ShowStatus("Ctrl+Left is in use by another app. Ctrl+C still works.");
        else if (!_screenshotShortcutAvailable)
            _hoverAi.ShowStatus("Ctrl+, screenshot shortcut is in use by another app.");
        else if (_aiInputShortcutError != null) _hoverAi.ShowStatus(_aiInputShortcutError);
        WindowInterop.EnsureTopmost(_hoverAi);
    }

    internal HoverAiWindow? HoverPanelForTests => _hoverAi;

    private void RegisterAiInputShortcuts(IntPtr hwnd)
    {
        _aiInputShortcutError = null;
        var status = new System.Text.StringBuilder();
        foreach (var (id, modifiers, key, name) in new[] {
            (NewSessionHotkeyId, ModControl, 0x26u, "Ctrl+Up"),
            (PasteInputHotkeyId, ModControl, 0x28u, "Ctrl+Down"),
            (SendInputHotkeyId, ModControl, 0x27u, "Ctrl+Right"),
            (ClearInputHotkeyId, ModControl | ModAlt, 0x28u, "Ctrl+Alt+Down") })
        {
            var registered = RegisterHotKey(hwnd, id, modifiers | 0x4000, key);
            status.AppendLine($"{name} registered={registered}");
            if (!registered) _aiInputShortcutError = name + " is in use by another app.";
        }
        try { System.IO.File.AppendAllText(AppConfig.TokenPath("hotkey-status.log"), status.ToString()); }
        catch (System.IO.IOException) { }
    }

    private void UnregisterAiInputShortcuts(IntPtr hwnd)
    {
        foreach (var id in new[] { NewSessionHotkeyId, PasteInputHotkeyId, ClearInputHotkeyId, SendInputHotkeyId }) UnregisterHotKey(hwnd, id);
    }

    internal bool HandleAiInputShortcut(int id)
    {
        if (!_hoverModeActive || id is not (NewSessionHotkeyId or PasteInputHotkeyId or ClearInputHotkeyId or SendInputHotkeyId)) return false;
        EnsureHoverAiPanel();
        if (id == NewSessionHotkeyId) _hoverAi!.NewSessionFromShortcut();
        else if (id == PasteInputHotkeyId) _hoverAi!.PasteInputFromShortcut();
        else if (id == SendInputHotkeyId) _hoverAi!.SubmitQuestion();
        else _hoverAi!.ClearInputFromShortcut();
        return true;
    }
}
