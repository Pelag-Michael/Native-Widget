using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NativeWidget.Services;

namespace NativeWidget;

public partial class TimersWindow : Window
{
    private static readonly Regex DigitsOnly = new("^[0-9]+$");
    private static readonly SolidColorBrush Muted = new(Color.FromRgb(0x9B, 0x9B, 0xA6));
    private static readonly SolidColorBrush Accent = new(Color.FromRgb(0x4A, 0x7D, 0xFF));
    private static readonly SolidColorBrush Expired = new(Color.FromRgb(0xC9, 0x8B, 0x8B));

    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<string, TextBlock> _countdownLabels = new();
    private readonly Dictionary<string, ProgressBar> _progressBars = new();

    public TimersWindow()
    {
        InitializeComponent();
        WindowInterop.HideFromAltTab(this);
        Loaded += (_, _) => RenderList();
        _tick.Tick += (_, _) => UpdateCountdowns();
        _tick.Start();
    }

    private void DragBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    public WidgetHeaderControls Header => HeaderControls;

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    private void AddToggle_Click(object sender, RoutedEventArgs e)
    {
        AddForm.Visibility = AddForm.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Digits_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !DigitsOnly.IsMatch(e.Text);
    }

    private bool _deadlineMode;

    private void ToggleMode_Click(object sender, RoutedEventArgs e)
    {
        _deadlineMode = !_deadlineMode;
        DurationMode.Visibility = _deadlineMode ? Visibility.Collapsed : Visibility.Visible;
        DeadlineMode.Visibility = _deadlineMode ? Visibility.Visible : Visibility.Collapsed;
        ModeBtn.Content = _deadlineMode ? "\uE916" : "\uE787";
        ModeBtn.ToolTip = _deadlineMode ? "Switch to duration input" : "Switch to a specific date/time";
        if (_deadlineMode) DeadlineDate.SelectedDate ??= DateTime.Today;
    }

    private void AddTimer_Click(object sender, RoutedEventArgs e)
    {
        var duration = _deadlineMode ? ReadDeadlineDuration() : ReadDurationFields();
        if (duration == null) return;

        TimersService.Add(TitleInput.Text, duration.Value);
        TitleInput.Text = "";
        DaysInput.Text = "0";
        HoursInput.Text = "0";
        MinsInput.Text = "30";
        AddForm.Visibility = Visibility.Collapsed;
        RenderList();
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string value } || !int.TryParse(value, out var minutes)) return;
        DaysInput.Text = "0";
        HoursInput.Text = (minutes / 60).ToString();
        MinsInput.Text = (minutes % 60).ToString();
        TitleInput.Focus();
    }

    private TimeSpan? ReadDurationFields()
    {
        var duration = new TimeSpan(ParseBox(DaysInput), ParseBox(HoursInput), ParseBox(MinsInput), 0);
        if (duration > TimeSpan.Zero) return duration;
        MessageBox.Show("Set a duration greater than 0.", "Timer");
        return null;
    }

    private TimeSpan? ReadDeadlineDuration()
    {
        if (DeadlineDate.SelectedDate is not { } date)
        {
            MessageBox.Show("Pick a date first.", "Timer");
            return null;
        }
        if (!TimeSpan.TryParse(DeadlineTime.Text.Trim(), out var timeOfDay))
        {
            MessageBox.Show("Time must be HH:mm, for example 23:59.", "Timer");
            return null;
        }

        var remaining = date.Date.Add(timeOfDay) - DateTime.Now;
        if (remaining > TimeSpan.Zero) return remaining;
        MessageBox.Show("That date and time is already in the past.", "Timer");
        return null;
    }

    private static int ParseBox(TextBox box) => int.TryParse(box.Text, out var v) ? Math.Max(v, 0) : 0;

    // ---- List rendering ----
    // The countdown is the whole point of this widget, so it's the visually dominant
    // element of each card; title/actions are kept small and muted on purpose.
    private void RenderList()
    {
        var timers = TimersService.Load()
            .OrderBy(t => t.IsExpired ? 1 : 0)
            .ThenBy(t => t.EndsAtUnix)
            .ToList();

        TimersList.Items.Clear();
        _countdownLabels.Clear();
        _progressBars.Clear();
        EmptyHint.Visibility = timers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var nextTimerId = timers.FirstOrDefault(timer => !timer.IsExpired)?.Id;

        foreach (var timer in timers)
        {
            var tagBrush = timer.IsExpired ? Expired : (Brush)ColorTags.Resolve(timer.Color, Accent);

            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(timer.Id == nextTimerId ? (byte)0x22 : (byte)0x14, 0xFF, 0xFF, 0xFF)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 8, 8, 8),
                Margin = new Thickness(2, 4, 2, 4),
            };
            var stack = new StackPanel();

            // Top row: color tag, small title, small action icons.
            var topRow = new Grid();
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var colorBtn = new ColorTagButton { Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            colorBtn.SetColor(timer.Color);
            colorBtn.ColorSelected += hex => { TimersService.SetColor(timer.Id, hex); RenderList(); };

            var title = new TextBlock
            {
                Text = timer.Title,
                Foreground = Muted,
                FontSize = 11,
                FontWeight = FontWeights.Medium,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            actions.Children.Add(MakeIconButton("\uE8AC", "Rename", () => RenameTimer(timer.Id, timer.Title)));
            actions.Children.Add(MakeIconButton("\uE72C", "Restart", () => { TimersService.Restart(timer.Id); RenderList(); }));
            actions.Children.Add(MakeIconButton("\uE74D", "Delete", () => { TimersService.Delete(timer.Id); RenderList(); }));
            actions.Opacity = 0;
            card.MouseEnter += (_, _) => actions.Opacity = 1;
            card.MouseLeave += (_, _) => actions.Opacity = 0;

            Grid.SetColumn(colorBtn, 0);
            Grid.SetColumn(title, 1);
            Grid.SetColumn(actions, 2);
            topRow.Children.Add(colorBtn);
            topRow.Children.Add(title);
            topRow.Children.Add(actions);

            var countdown = new TextBlock
            {
                Text = timer.IsExpired ? "Time's up" : TimersService.FormatRemaining(timer.Remaining),
                Foreground = tagBrush,
                FontSize = 26,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Consolas"),
                Margin = new Thickness(0, 4, 0, 0),
            };
            var deadline = new TextBlock
            {
                Text = timer.EndsAt.ToString("HH:mm · dd/MM"),
                Foreground = Muted,
                FontSize = 10,
                Margin = new Thickness(0, 2, 0, 0),
            };
            var progress = new ProgressBar
            {
                Minimum = 0,
                Maximum = 1,
                Value = ProgressFor(timer),
                Style = (Style)FindResource("CompactProgressStyle"),
                Margin = new Thickness(0, 7, 0, 0),
            };

            stack.Children.Add(topRow);
            if (timer.Id == nextTimerId)
                stack.Children.Add(new TextBlock { Text = "UP NEXT", Foreground = tagBrush, FontSize = 9, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 3, 0, -2) });
            stack.Children.Add(countdown);
            stack.Children.Add(progress);
            stack.Children.Add(deadline);
            card.Child = stack;
            _countdownLabels[timer.Id] = countdown;
            _progressBars[timer.Id] = progress;

            TimersList.Items.Add(card);
        }
    }

    private Button MakeIconButton(string glyph, string tooltip, Action onClick)
    {
        var btn = new Button
        {
            Content = glyph,
            ToolTip = tooltip,
            Style = (Style)FindResource("IconBtnStyle"),
            Width = 20,
            Height = 20,
            FontSize = 9,
            Opacity = 0.7,
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private void RenameTimer(string id, string currentTitle)
    {
        var name = PromptDialog.Show(this, "Rename timer", currentTitle);
        if (name == null) return;
        TimersService.Rename(id, name);
        RenderList();
    }

    // Refreshes only the countdown labels once a second; a full re-render would fight
    // with the user scrolling or hovering the list.
    private void UpdateCountdowns()
    {
        if (!IsVisible) return;
        foreach (var timer in TimersService.Load())
        {
            if (!_countdownLabels.TryGetValue(timer.Id, out var label)) continue;
            if (timer.IsExpired)
            {
                label.Text = "Time's up";
                label.Foreground = Expired;
            }
            else
            {
                label.Text = TimersService.FormatRemaining(timer.Remaining);
            }
            if (_progressBars.TryGetValue(timer.Id, out var progress)) progress.Value = ProgressFor(timer);
        }
    }

    private static double ProgressFor(CountdownTimer timer)
    {
        if (timer.DurationSeconds <= 0 || timer.IsExpired) return timer.IsExpired ? 1 : 0;
        return Math.Clamp(1 - timer.Remaining.TotalSeconds / timer.DurationSeconds, 0, 1);
    }

    public void Refresh() => RenderList();
}
