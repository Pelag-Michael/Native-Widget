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
    // The dock itself is fixed at 52px; a round Popup holds the 3×3 icon menu on hover.
    private readonly DispatcherTimer _launcherCloseTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };

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
        WindowSessionService.Track(this, _config, "Launcher", "Launcher", trackVisibility: false);
        _launcherCloseTimer.Tick += (_, _) =>
        {
            if (IsMouseOver || LauncherPopupContent.IsMouseOver) return;
            HideLauncher();
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
        if (IsMouseOver || LauncherPopupContent.IsMouseOver) return;
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

    private void DragBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Window_MouseEnter(object sender, MouseEventArgs e)
    {
        ShowLauncher();
    }

    private void Window_MouseLeave(object sender, MouseEventArgs e)
    {
        _launcherCloseTimer.Start();
    }

    private void LauncherPopup_MouseEnter(object sender, MouseEventArgs e) => _launcherCloseTimer.Stop();
    private void LauncherPopup_MouseLeave(object sender, MouseEventArgs e) => _launcherCloseTimer.Start();
    private void ShowLauncher()
    {
        _launcherCloseTimer.Stop();
        LauncherPopup.IsOpen = true;
        LauncherPopupContent.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
        if (LauncherPopupContent.RenderTransform is ScaleTransform scale)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
        }
    }
    private void HideLauncher()
    {
        _launcherCloseTimer.Stop();
        if (!LauncherPopup.IsOpen) return;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(110))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        fade.Completed += (_, _) =>
        {
            if (!IsMouseOver && !LauncherPopupContent.IsMouseOver) LauncherPopup.IsOpen = false;
        };
        LauncherPopupContent.BeginAnimation(OpacityProperty, fade);
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

    private void CloseAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (Window? w in new Window?[] { _projectsWindow, _calendarWindow, _tasksWindow, _notesWindow, _timersWindow, _focusWindow, _translationWindow, _labelsWindow, _settingsWindow })
            w?.Hide();

        foreach (var btn in new[] { BtnProjects, BtnCalendar, BtnTasks, BtnNotes, BtnTimers, BtnFocus, BtnTranslate, BtnLabels, BtnSettings })
        {
            btn.Background = Brushes.Transparent;
            btn.Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x93));
        }
    }
}
