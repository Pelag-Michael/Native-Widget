using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using NativeWidget.Models;
using NativeWidget.Services;

namespace NativeWidget;

public partial class MainWindow : Window
{
    // The dock itself is fixed at 52px; a round Popup holds the icon menu on hover.
    private readonly DispatcherTimer _launcherCloseTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private readonly DispatcherTimer _hintHideTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private bool _updatingGlobalControls;
    private bool _launcherClosing;
    private bool _draggingDock;
    /// Blocks hover-open while dragging and until the pointer leaves after a drag
    /// (otherwise SnapClose → MouseEnter immediately reopens the menu and hides the dock).
    private bool _suppressMenuOpen;
    private Point _dragCursorOffset;
    private Button? _hintOwner;

    // title, optional shortcut, optional extra line — keyed by button Name
    private static readonly Dictionary<string, (string Title, string? Shortcut, string? Extra)> LauncherHints = new()
    {
        ["BtnSearch"] = ("Search", "Ctrl+Alt+K", null),
        ["BtnProjects"] = ("Projects", null, null),
        ["BtnCalendar"] = ("Calendar", null, "Right-click: quick event"),
        ["BtnTasks"] = ("Tasks", null, "Right-click: quick task"),
        ["BtnNotes"] = ("Notes", null, "Right-click: quick note"),
        ["BtnTimers"] = ("Timers", null, null),
        ["BtnFocus"] = ("Focus session", null, null),
        ["BtnTranslate"] = ("Translate", null, null),
        ["BtnLabels"] = ("Labels", null, null),
        ["BtnSettings"] = ("Settings", null, null),
        ["BtnWindowTools"] = ("Window tools", null, "Pin, ghost, opacity for open widgets"),
    };

    private const int HotkeyId = 0xB001;
    private const int LocateHotkeyId = 0xB002;
    private const int SearchHotkeyId = 0xB003;
    private const uint ModAlt = 0x1, ModControl = 0x2;
    private const uint VkG = 0x47;
    private const uint VkF = 0x46;
    private const uint VkK = 0x4B;
    private const int WmHotkey = 0x0312;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Win32Point point);

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Point
    {
        public int X;
        public int Y;
    }

    private readonly AppConfig _config = AppConfig.Load();

    private ProjectsWindow? _projectsWindow;
    private CalendarWindow? _calendarWindow;
    private TasksWindow? _tasksWindow;
    private NotesWindow? _notesWindow;
    private TimersWindow? _timersWindow;
    private FocusWindow? _focusWindow;
    private TranslationWindow? _translationWindow;
    private LabelsWindow? _labelsWindow;
    private SettingsWindow? _settingsWindow;
    private WorkspaceSearchWindow? _searchWindow;
    private bool _sessionRestored;

    public MainWindow()
    {
        InitializeComponent();
        WindowInterop.HideFromAltTab(this);
        // Position only — never restore size. A poisoned session once stretched this to 80×52
        // and turned the circular dock into a horizontal pill.
        WindowSessionService.Track(this, _config, "Launcher", "Launcher",
            trackVisibility: false, restoreSize: false);
        _launcherCloseTimer.Tick += (_, _) =>
        {
            if (IsMouseOver || LauncherPopupContent.IsMouseOver || WindowToolsPopup.IsOpen || WindowToolsPanel.IsMouseOver) return;
            HideLauncher();
        };
        _hintHideTimer.Tick += (_, _) =>
        {
            _hintHideTimer.Stop();
            // Still hovering the same (or another) action? keep the label.
            if (_hintOwner != null && _hintOwner.IsMouseOver) return;
            _hintOwner = null;
            CloseLauncherHintNow();
        };

        // A ghosted window can't be clicked at all - not even its own un-ghost button - so
        // the only way back is this app-wide hotkey.
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            RegisterHotKey(hwnd, HotkeyId, ModControl | ModAlt, VkG);
            // Ctrl+Alt+F ("Find") - the launcher is a single small floating icon and easy to
            // lose behind other windows, unlike real Alt+Tab entries (it's deliberately
            // hidden from Alt+Tab via WindowInterop.HideFromAltTab, so there's no way to
            // intercept the real Alt+Tab keystroke and still let Windows' own switcher work).
            RegisterHotKey(hwnd, LocateHotkeyId, ModControl | ModAlt, VkF);
            RegisterHotKey(hwnd, SearchHotkeyId, ModControl | ModAlt, VkK);
            HwndSource.FromHwnd(hwnd)?.AddHook(HotkeyHook);
        };
        Closed += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            UnregisterHotKey(hwnd, HotkeyId);
            UnregisterHotKey(hwnd, LocateHotkeyId);
            UnregisterHotKey(hwnd, SearchHotkeyId);
        };
        Closing += (_, _) => WindowSessionService.CaptureForShutdown();
        // Isolated end-to-end tests can open Translate without trying to automate the
        // deliberately tiny, borderless launcher. Production never sets this variable.
        if (Environment.GetEnvironmentVariable("NATIVEWIDGET_UI_TEST") == "1")
        {
            Loaded += (_, _) =>
            {
                _translationWindow ??= (TranslationWindow)GetOrCreateWidget("Translate");
                _translationWindow.Show();
            };
        }
        else
        {
            Loaded += (_, _) => RestoreWindowSession();
        }
    }

    private IntPtr HotkeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            UnghostAll();
            handled = true;
        }
        else if (msg == WmHotkey && wParam.ToInt32() == LocateHotkeyId)
        {
            _ = LocateAsync();
            handled = true;
        }
        else if (msg == WmHotkey && wParam.ToInt32() == SearchHotkeyId)
        {
            OpenGlobalSearch();
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// Pulses a glow around the launcher and briefly opens its round menu, so it's
    /// unmistakable even buried under other windows.
    private async Task LocateAsync()
    {
        Activate();
        Topmost = false;
        Topmost = true;

        var glow = new DropShadowEffect
        {
            Color = (Color)ColorConverter.ConvertFromString("#4A7DFF"),
            BlurRadius = 40,
            ShadowDepth = 0,
            Opacity = 0,
        };
        RootBorder.Effect = glow;
        var pulse = new DoubleAnimation
        {
            From = 0,
            To = 0.9,
            Duration = TimeSpan.FromMilliseconds(350),
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(3),
        };
        pulse.Completed += (_, _) => RootBorder.Effect = null;
        glow.BeginAnimation(DropShadowEffect.OpacityProperty, pulse);

        ShowLauncher();
        await Task.Delay(2200);
        if (IsMouseOver || LauncherPopupContent.IsMouseOver || WindowToolsPopup.IsOpen) return;
        HideLauncher();
    }

    private void UnghostAll()
    {
        foreach (var (window, header) in EnumerateWidgets())
        {
            WindowInterop.SetClickThrough(window, false);
            header.SetGhostVisual(false);
        }
    }

    private IEnumerable<(Window Window, WidgetHeaderControls Header)> EnumerateWidgets()
    {
        if (_projectsWindow != null) yield return (_projectsWindow, _projectsWindow.Header);
        if (_calendarWindow != null) yield return (_calendarWindow, _calendarWindow.Header);
        if (_tasksWindow != null) yield return (_tasksWindow, _tasksWindow.Header);
        if (_notesWindow != null) yield return (_notesWindow, _notesWindow.Header);
        if (_timersWindow != null) yield return (_timersWindow, _timersWindow.Header);
        if (_focusWindow != null) yield return (_focusWindow, _focusWindow.Header);
        if (_translationWindow != null) yield return (_translationWindow, _translationWindow.Header);
        if (_labelsWindow != null) yield return (_labelsWindow, _labelsWindow.Header);
        if (_settingsWindow != null) yield return (_settingsWindow, _settingsWindow.Header);
    }

    private IEnumerable<(Window Window, WidgetHeaderControls Header)> EnumerateVisibleWidgets()
    {
        if (Application.Current == null) yield break;
        foreach (Window window in Application.Current.Windows)
        {
            if (!window.IsVisible || window == this) continue;
            var header = HeaderFor(window);
            if (header != null) yield return (window, header);
        }
    }

    private static WidgetHeaderControls? HeaderFor(Window window) => window switch
    {
        ProjectsWindow widget => widget.Header,
        CalendarWindow widget => widget.Header,
        TasksWindow widget => widget.Header,
        NotesWindow widget => widget.Header,
        TimersWindow widget => widget.Header,
        FocusWindow widget => widget.Header,
        TranslationWindow widget => widget.Header,
        LabelsWindow widget => widget.Header,
        SettingsWindow widget => widget.Header,
        _ => null,
    };

    private void DragBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ButtonState != MouseButtonState.Pressed) return;

        // Do not use DragMove here: after closing the Popup the original mouse-down target is
        // gone, DragMove often fails, and hover immediately re-opens the menu (dock vanishes).
        // Manual capture + move keeps the visible dock glued to the cursor for the whole drag.
        _launcherCloseTimer.Stop();
        _suppressMenuOpen = true;
        _draggingDock = true;
        SnapCloseLauncherForDrag();

        var cursor = GetCursorInDip();
        _dragCursorOffset = new Point(cursor.X - Left, cursor.Y - Top);
        CaptureMouse();
        e.Handled = true;
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_draggingDock || e.LeftButton != MouseButtonState.Pressed) return;
        var cursor = GetCursorInDip();
        Left = cursor.X - _dragCursorOffset.X;
        Top = cursor.Y - _dragCursorOffset.Y;
    }

    private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_draggingDock) return;
        EndDockDrag();
        e.Handled = true;
    }

    private void Window_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_draggingDock) EndDockDrag();
    }

    private void EndDockDrag()
    {
        _draggingDock = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        // Keep _suppressMenuOpen until MouseLeave so the menu does not pop open under a
        // still-hovering cursor and hide the dock again (felt like lag + flicker).
    }

    /// Instantly collapses the radial menu and restores the dock — no close animation.
    private void SnapCloseLauncherForDrag()
    {
        _launcherClosing = false;
        HideLauncherHint();
        if (WindowToolsPopup.IsOpen) WindowToolsPopup.IsOpen = false;

        LauncherPopupContent.BeginAnimation(OpacityProperty, null);
        if (LauncherPopupContent.RenderTransform is ScaleTransform scale)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            scale.ScaleX = LauncherMorphScale;
            scale.ScaleY = LauncherMorphScale;
        }
        LauncherPopupContent.Opacity = 0;
        LauncherPopup.IsOpen = false;

        RootBorder.BeginAnimation(OpacityProperty, null);
        RootBorder.Opacity = 1;
        RootBorder.IsHitTestVisible = true;
    }

    /// Cursor position in WPF device-independent pixels (matches Left/Top).
    private Point GetCursorInDip()
    {
        GetCursorPos(out var pt);
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget != null)
        {
            var fromDevice = source.CompositionTarget.TransformFromDevice;
            return fromDevice.Transform(new Point(pt.X, pt.Y));
        }
        return new Point(pt.X, pt.Y);
    }

    private void Window_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_draggingDock || _suppressMenuOpen) return;
        ShowLauncher();
    }

    private void Window_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_draggingDock) return;
        _suppressMenuOpen = false;
        _launcherCloseTimer.Start();
    }

    private void LauncherPopup_MouseEnter(object sender, MouseEventArgs e) => _launcherCloseTimer.Stop();
    private void LauncherPopup_MouseLeave(object sender, MouseEventArgs e)
    {
        HideLauncherHint();
        _launcherCloseTimer.Start();
    }
    private void WindowToolsPanel_MouseEnter(object sender, MouseEventArgs e) => _launcherCloseTimer.Stop();
    private void WindowToolsPanel_MouseLeave(object sender, MouseEventArgs e) => _launcherCloseTimer.Start();

    // Dock is 52px; radial chrome is 232px — open scale starts at dock/menu so growth
    // reads as the same control expanding, not a second panel popping in.
    private const double LauncherMorphScale = 52.0 / 232.0;

    private void ShowLauncher()
    {
        if (_draggingDock || _suppressMenuOpen) return;
        _launcherCloseTimer.Stop();
        _launcherClosing = false;
        var wasOpen = LauncherPopup.IsOpen;
        LauncherPopup.IsOpen = true;

        // Hide the collapsed dock immediately so the hub inside the menu is the only button.
        RootBorder.Opacity = 0;
        RootBorder.IsHitTestVisible = false;

        var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
        var openMs = 280;

        if (!wasOpen)
        {
            LauncherPopupContent.BeginAnimation(OpacityProperty, null);
            LauncherPopupContent.Opacity = 0;
            if (LauncherPopupContent.RenderTransform is ScaleTransform reset)
            {
                reset.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                reset.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                reset.ScaleX = LauncherMorphScale;
                reset.ScaleY = LauncherMorphScale;
            }
        }

        LauncherPopupContent.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(openMs * 0.7))
        {
            EasingFunction = easeOut,
        });
        if (LauncherPopupContent.RenderTransform is ScaleTransform scale)
        {
            var scaleAnim = new DoubleAnimation(1, TimeSpan.FromMilliseconds(openMs))
            {
                EasingFunction = easeOut,
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
        }
    }

    private void HideLauncher()
    {
        _launcherCloseTimer.Stop();
        HideLauncherHint();
        if (WindowToolsPopup.IsOpen) return;
        if (!LauncherPopup.IsOpen || _launcherClosing) return;
        _launcherClosing = true;

        var easeIn = new CubicEase { EasingMode = EasingMode.EaseIn };
        var closeMs = 180;

        if (LauncherPopupContent.RenderTransform is ScaleTransform scale)
        {
            var scaleAnim = new DoubleAnimation(LauncherMorphScale, TimeSpan.FromMilliseconds(closeMs))
            {
                EasingFunction = easeIn,
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
        }

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(closeMs))
        {
            EasingFunction = easeIn,
        };
        fade.Completed += (_, _) =>
        {
            _launcherClosing = false;
            if (!IsMouseOver && !LauncherPopupContent.IsMouseOver && !WindowToolsPopup.IsOpen)
            {
                LauncherPopup.IsOpen = false;
                // Restore the single collapsed dock after the morph finishes.
                RootBorder.BeginAnimation(OpacityProperty, null);
                RootBorder.Opacity = 1;
                RootBorder.IsHitTestVisible = true;
            }
            else if (IsMouseOver || LauncherPopupContent.IsMouseOver)
            {
                ShowLauncher();
            }
        };
        LauncherPopupContent.BeginAnimation(OpacityProperty, fade);
    }

    private void LauncherAction_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not Button btn) return;
        _launcherCloseTimer.Stop();
        _hintHideTimer.Stop();
        ShowLauncherHint(btn);
    }

    private void LauncherAction_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Button left && _hintOwner == left)
        {
            _hintHideTimer.Stop();
            _hintHideTimer.Start();
        }
    }

    private void ShowLauncherHint(Button btn)
    {
        if (!LauncherHints.TryGetValue(btn.Name, out var hint)) return;
        var switching = LauncherHintPopup.IsOpen && _hintOwner != btn;
        _hintOwner = btn;

        LauncherHintTitle.Text = hint.Title;
        if (string.IsNullOrEmpty(hint.Shortcut))
        {
            LauncherHintShortcut.Visibility = Visibility.Collapsed;
        }
        else
        {
            LauncherHintShortcut.Text = hint.Shortcut;
            LauncherHintShortcut.Visibility = Visibility.Visible;
        }
        if (string.IsNullOrEmpty(hint.Extra))
        {
            LauncherHintExtra.Visibility = Visibility.Collapsed;
        }
        else
        {
            LauncherHintExtra.Text = hint.Extra;
            LauncherHintExtra.Visibility = Visibility.Visible;
        }

        LauncherHintPopup.PlacementTarget = btn;
        LauncherHintPopup.IsOpen = true;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        // Full slide when first shown; lighter fade when moving between icons.
        var fromX = switching ? -4 : -12;
        var duration = switching ? 140 : 220;
        LauncherHintChrome.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(duration - 20))
        {
            EasingFunction = ease,
        });
        if (LauncherHintChrome.RenderTransform is TranslateTransform slide)
        {
            slide.BeginAnimation(TranslateTransform.XProperty, null);
            slide.X = fromX;
            slide.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(0, TimeSpan.FromMilliseconds(duration)) { EasingFunction = ease });
        }
    }

    private void HideLauncherHint()
    {
        _hintOwner = null;
        _hintHideTimer.Stop();
        CloseLauncherHintNow();
    }

    private void CloseLauncherHintNow()
    {
        if (!LauncherHintPopup.IsOpen) return;

        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120)) { EasingFunction = ease };
        fade.Completed += (_, _) =>
        {
            if (_hintOwner == null) LauncherHintPopup.IsOpen = false;
        };
        LauncherHintChrome.BeginAnimation(OpacityProperty, fade);
        if (LauncherHintChrome.RenderTransform is TranslateTransform slide)
        {
            slide.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(-8, TimeSpan.FromMilliseconds(120)) { EasingFunction = ease });
        }
    }

    private void ToggleWidget_Click(object sender, RoutedEventArgs e)
    {
        var btn = (Button)sender;
        var tag = (string)btn.Tag;

        var widget = GetOrCreateWidget(tag);

        if (widget.IsVisible)
        {
            widget.Hide();
        }
        else
        {
            RefreshWidget(widget);
            widget.Show();
            widget.Activate();

            // Bringing a widget back from the launcher also un-ghosts it, so a forgotten
            // ghost toggle can't leave a window permanently unclickable.
            var header = EnumerateWidgets().FirstOrDefault(w => w.Window == widget).Header;
            if (header != null)
            {
                WindowInterop.SetClickThrough(widget, false);
                header.SetGhostVisual(false);
            }
        }

        SetWidgetButtonState(btn, widget.IsVisible);
    }

    private Window GetOrCreateWidget(string tag)
    {
        return tag switch
        {
            "Projects" => _projectsWindow ??= TrackWidget(new ProjectsWindow(), tag),
            "Calendar" => _calendarWindow ??= TrackWidget(new CalendarWindow(_config), tag),
            "Tasks" => _tasksWindow ??= TrackWidget(new TasksWindow(_config), tag),
            "Notes" => _notesWindow ??= TrackWidget(new NotesWindow(_config), tag),
            "Timers" => _timersWindow ??= TrackWidget(new TimersWindow(), tag),
            "Focus" => _focusWindow ??= TrackWidget(new FocusWindow(), tag),
            "Translate" => _translationWindow ??= TrackWidget(new TranslationWindow(_config), tag),
            "Labels" => _labelsWindow ??= TrackWidget(new LabelsWindow(), tag),
            "Settings" => _settingsWindow ??= TrackWidget(new SettingsWindow(_config, async () =>
            {
                if (_calendarWindow != null) await _calendarWindow.RefreshEventsAsync();
            }), tag),
            _ => throw new InvalidOperationException($"Unknown widget: {tag}"),
        };
    }

    private T TrackWidget<T>(T widget, string tag) where T : Window
    {
        WindowSessionService.Track(widget, _config, tag, tag);
        return widget;
    }

    private static void RefreshWidget(Window widget)
    {
        if (widget is TimersWindow timers) timers.Refresh();
        if (widget is ProjectsWindow projects) projects.Render();
        if (widget is TasksWindow tasks) tasks.Refresh();
        if (widget is LabelsWindow labels) labels.Render();
        if (widget is TranslationWindow translation) translation.Refresh();
    }

    private Button? WidgetButton(string tag) => tag switch
    {
        "Projects" => BtnProjects,
        "Calendar" => BtnCalendar,
        "Tasks" => BtnTasks,
        "Notes" => BtnNotes,
        "Timers" => BtnTimers,
        "Focus" => BtnFocus,
        "Translate" => BtnTranslate,
        "Labels" => BtnLabels,
        "Settings" => BtnSettings,
        _ => null,
    };

    private static void SetWidgetButtonState(Button button, bool active)
    {
        button.Background = active
            ? new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF))
            : Brushes.Transparent;
        button.Foreground = active
            ? Brushes.White
            : new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x93));
    }

    private void RestoreWindowSession()
    {
        if (_sessionRestored) return;
        _sessionRestored = true;

        foreach (var entry in WindowSessionService.OpenWindows(_config))
        {
            Window? widget = entry.Kind switch
            {
                "NotesPopout" when !string.IsNullOrWhiteSpace(entry.ContextId) =>
                    RestorePopout(new NotesWindow(_config, entry.ContextId), entry),
                "TasksPopout" when !string.IsNullOrWhiteSpace(entry.ContextId) =>
                    RestorePopout(new TasksWindow(_config, entry.ContextId), entry),
                "Projects" or "Calendar" or "Tasks" or "Notes" or "Timers" or "Focus" or
                    "Translate" or "Labels" or "Settings" => GetOrCreateWidget(entry.Kind),
                _ => null,
            };
            if (widget == null) continue;

            RefreshWidget(widget);
            widget.Show();
            if (WidgetButton(entry.Kind) is { } button) SetWidgetButtonState(button, true);
        }
    }

    private Window RestorePopout(Window widget, WindowSessionEntry entry)
    {
        WindowSessionService.Track(widget, _config, entry.Key, entry.Kind, entry.ContextId);
        return widget;
    }

    private void OpenSearch_Click(object sender, RoutedEventArgs e)
    {
        HideLauncher();
        OpenGlobalSearch();
    }

    private void OpenGlobalSearch()
    {
        if (_searchWindow == null)
        {
            _searchWindow = new WorkspaceSearchWindow();
            _searchWindow.NoteSelected += id =>
            {
                var notes = (NotesWindow)GetOrCreateWidget("Notes");
                notes.Show();
                notes.OpenNoteFromSearch(id);
            };
            _searchWindow.TagSelected += tag =>
            {
                var notes = (NotesWindow)GetOrCreateWidget("Notes");
                notes.Show();
                notes.FilterByTagFromSearch(tag);
            };
            _searchWindow.ProjectSelected += id =>
            {
                var projects = (ProjectsWindow)GetOrCreateWidget("Projects");
                projects.OpenProjectFromSearch(id);
                projects.Show();
                projects.Activate();
            };
        }

        _searchWindow.Left = Left + 12;
        _searchWindow.Top = Top + 58;
        _searchWindow.OpenAndFocus();
    }

    // Right-click on a launcher icon adds an item straight to its default target without
    // opening the full widget - Tasks goes to the first Google Tasklist, Calendar reuses
    // AddEventDialog directly (it already has date/time/recurrence, no reason to duplicate).
    private async void QuickAddTask_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!GoogleTasksService.IsConnected())
        {
            MessageBox.Show("Not connected to Google (open the Calendar widget and connect first).", "Not connected");
            return;
        }
        var title = PromptDialog.Show(this, "Quick task (first list)");
        if (string.IsNullOrWhiteSpace(title)) return;

        var lists = await GoogleTasksService.GetTaskListsAsync(_config);
        if (lists.Count == 0) return;
        await GoogleTasksService.AddTaskAsync(_config, lists[0].Id, title);
        _tasksWindow?.Refresh();
    }

    private void QuickAddNote_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var title = PromptDialog.Show(this, "Quick note");
        if (string.IsNullOrWhiteSpace(title)) return;

        var id = NotesService.CreateNote();
        NotesService.RenameNote(id, title);
        _notesWindow?.Refresh();
    }

    private async void QuickAddCalendar_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!GoogleCalendarService.IsConnected())
        {
            MessageBox.Show("Not connected to Google Calendar (open the Calendar widget and connect first).", "Not connected");
            return;
        }
        var dialog = AddEventDialog.Show(this);
        if (dialog == null) return;
        try
        {
            await GoogleCalendarService.CreateEventAsync(_config, dialog.EventTitle, dialog.Start, dialog.AllDay,
                dialog.RecurrenceFreq, dialog.EventNote);
            if (_calendarWindow != null) await _calendarWindow.RefreshEventsAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not create event: {ex.Message}", "Error");
        }
    }

    private void WindowTools_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        SetWindowToolsOpen(!WindowToolsPopup.IsOpen);
    }

    public void SetWindowToolsOpen(bool open)
    {
        if (open)
        {
            ShowLauncher();
            UpdateGlobalWindowControls();
        }
        WindowToolsPopup.IsOpen = open;
        if (open) _launcherCloseTimer.Stop();
    }

    private void WindowToolsPopup_Closed(object? sender, EventArgs e)
    {
        _launcherCloseTimer.Stop();
        _launcherCloseTimer.Start();
    }

    private void UpdateGlobalWindowControls()
    {
        var widgets = EnumerateVisibleWidgets().ToList();
        GlobalWindowCount.Text = $"{widgets.Count} đang mở";
        var hasWidgets = widgets.Count > 0;
        GlobalPinBtn.IsEnabled = hasWidgets;
        GlobalGhostBtn.IsEnabled = hasWidgets;
        GlobalCloseBtn.IsEnabled = hasWidgets;
        GlobalOpacitySlider.IsEnabled = hasWidgets;

        var muted = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x8A));
        var allPinned = hasWidgets && widgets.All(item => item.Window.Topmost);
        var allGhosted = hasWidgets && widgets.All(item => WindowInterop.IsClickThrough(item.Window));
        GlobalPinBtn.Foreground = allPinned ? (Brush)FindResource("AccentBrush") : muted;
        GlobalGhostBtn.Foreground = allGhosted ? (Brush)FindResource("AccentBrush") : muted;
        GlobalPinBtn.ToolTip = allPinned ? "Bỏ ghim tất cả" : "Ghim tất cả";
        GlobalGhostBtn.ToolTip = allGhosted ? "Tắt ghost cho tất cả" : "Bật ghost cho tất cả";

        _updatingGlobalControls = true;
        try
        {
            var opacity = hasWidgets ? widgets.Average(item => item.Window.Opacity) : 1;
            GlobalOpacitySlider.Value = opacity;
            var mixed = hasWidgets && widgets.Any(item => Math.Abs(item.Window.Opacity - opacity) > 0.01);
            GlobalOpacityLabel.Text = mixed ? $"≈{opacity * 100:0}%" : $"{opacity * 100:0}%";
        }
        finally { _updatingGlobalControls = false; }
    }

    private void GlobalPin_Click(object sender, RoutedEventArgs e)
    {
        var widgets = EnumerateVisibleWidgets().ToList();
        var pin = widgets.Any(item => !item.Window.Topmost);
        foreach (var (window, header) in widgets)
        {
            window.Topmost = pin;
            header.SetPinVisual(pin);
        }
        UpdateGlobalWindowControls();
    }

    private void GlobalGhost_Click(object sender, RoutedEventArgs e)
    {
        var widgets = EnumerateVisibleWidgets().ToList();
        var ghost = widgets.Any(item => !WindowInterop.IsClickThrough(item.Window));
        foreach (var (window, header) in widgets)
        {
            WindowInterop.SetClickThrough(window, ghost);
            header.SetGhostVisual(ghost);
        }
        UpdateGlobalWindowControls();
    }

    private void GlobalOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingGlobalControls || !IsInitialized || GlobalOpacityLabel == null) return;
        foreach (var (window, header) in EnumerateVisibleWidgets())
        {
            window.Opacity = e.NewValue;
            header.SetOpacityValue(e.NewValue);
        }
        GlobalOpacityLabel.Text = $"{e.NewValue * 100:0}%";
    }

    private void GlobalCloseAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var (window, _) in EnumerateVisibleWidgets().ToList()) window.Hide();
        WindowToolsPopup.IsOpen = false;

        foreach (var btn in new[] { BtnProjects, BtnCalendar, BtnTasks, BtnNotes, BtnTimers, BtnFocus, BtnTranslate, BtnLabels, BtnSettings })
        {
            btn.Background = Brushes.Transparent;
            btn.Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x93));
        }
    }
}
