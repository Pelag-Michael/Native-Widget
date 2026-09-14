using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NativeWidget.Models;
using NativeWidget.Services;

namespace NativeWidget;

public partial class FocusWindow : Window
{
    private static readonly Regex DigitsOnly = new("^[0-9]+$");

    // --- Pomodoro timer state ---
    private int _minutes = 25;
    private int _remainingSeconds;
    private bool _running;

    // --- Time budgets state ---
    private TimeBudgetStore _budgetStore = new();
    private int _budgetSaveCounter;
    private readonly Dictionary<string, (TextBlock TimeText, ProgressBar Bar, TextBlock PctText, Button PlayBtn)> _budgetCardViews = new();

    // Unified 1s tick timer for both modes
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public FocusWindow()
    {
        InitializeComponent();
        WindowInterop.HideFromAltTab(this);
        _timer.Tick += Timer_Tick;

        Loaded += (_, _) =>
        {
            _budgetStore = TimeBudgetsService.Load();
            WeekLabelText.Text = TimeBudgetsService.GetCurrentWeekLabel();
            SwitchPanel(_budgetStore.ActivePanel == "budgets" ? "budgets" : "timer", saveState: false);
            RenderBudgets();

            // If any budget was left running, ensure timer is started
            if (_budgetStore.Items.Any(b => b.IsRunning))
            {
                if (!_timer.IsEnabled) _timer.Start();
            }
        };
    }

    public WidgetHeaderControls Header => HeaderControls;

    private void DragBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        TimeBudgetsService.Save(_budgetStore);
        Hide();
    }

    // ==========================================
    // PANEL SWITCHER (Focus Timer vs Time Budgets)
    // ==========================================

    private void TabTimer_Click(object sender, RoutedEventArgs e)
    {
        SwitchPanel("timer", saveState: true);
    }

    private void TabBudgets_Click(object sender, RoutedEventArgs e)
    {
        SwitchPanel("budgets", saveState: true);
    }

    private void SwitchPanel(string panel, bool saveState)
    {
        var accentBrush = (Brush)FindResource("AccentBrush");
        var mutedBrush = (Brush)FindResource("MutedBrush");

        if (panel == "budgets")
        {
            TimerPanel.Visibility = Visibility.Collapsed;
            BudgetsPanel.Visibility = Visibility.Visible;
            TabTimerBtn.Background = Brushes.Transparent;
            TabTimerBtn.Foreground = mutedBrush;
            TabBudgetsBtn.Background = accentBrush;
            TabBudgetsBtn.Foreground = Brushes.White;
        }
        else
        {
            TimerPanel.Visibility = Visibility.Visible;
            BudgetsPanel.Visibility = Visibility.Collapsed;
            TabTimerBtn.Background = accentBrush;
            TabTimerBtn.Foreground = Brushes.White;
            TabBudgetsBtn.Background = Brushes.Transparent;
            TabBudgetsBtn.Foreground = mutedBrush;
        }

        _budgetStore.ActivePanel = panel;
        if (saveState)
        {
            TimeBudgetsService.Save(_budgetStore);
        }
    }

    // ==========================================
    // TIME BUDGETS (Weekly Targets)
    // ==========================================

    private void RenderBudgets()
    {
        BudgetsItemsPanel.Children.Clear();
        _budgetCardViews.Clear();

        if (_budgetStore.Items.Count == 0)
        {
            EmptyBudgetsHint.Visibility = Visibility.Visible;
            return;
        }

        EmptyBudgetsHint.Visibility = Visibility.Collapsed;
        var mutedBrush = (Brush)FindResource("MutedBrush");
        var accentBrush = (Brush)FindResource("AccentBrush");
        var successBrush = new SolidColorBrush(Color.FromRgb(0x8F, 0xE0, 0xA8));

        foreach (var budget in _budgetStore.Items)
        {
            var card = new Border
            {
                Background = budget.IsRunning
                    ? new SolidColorBrush(Color.FromArgb(0x28, 0x4A, 0x7D, 0xFF))
                    : new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
                BorderBrush = budget.IsRunning
                    ? accentBrush
                    : new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(11),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 8),
            };

            var mainGrid = new Grid();
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Row 0: Play/Pause, Title, Time text, More menu
            var topGrid = new Grid();
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Play / Pause button
            var playBtn = new Button
            {
                Width = 26,
                Height = 26,
                Style = (Style)FindResource("IconBtnStyle"),
                Content = budget.IsRunning ? "\uE769" : "\uE768",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 11,
                Foreground = budget.IsRunning ? successBrush : new SolidColorBrush(Color.FromRgb(0xA8, 0xB4, 0xFF)),
                Margin = new Thickness(0, 0, 8, 0),
                ToolTip = budget.IsRunning ? "Pause timer" : "Start tracking",
            };
            var capturedBudget = budget;
            playBtn.Click += (_, _) => ToggleBudgetRunning(capturedBudget);
            Grid.SetColumn(playBtn, 0);
            topGrid.Children.Add(playBtn);

            // Title
            var titleText = new TextBlock
            {
                Text = budget.Title,
                Foreground = Brushes.White,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(titleText, 1);
            topGrid.Children.Add(titleText);

            // Logged / Target time
            var timeText = new TextBlock
            {
                Text = $"{budget.FormattedLoggedTime} / {budget.FormattedTargetTime}" + (budget.ProgressFraction >= 1.0 ? " ✓" : ""),
                Foreground = budget.ProgressFraction >= 1.0 ? successBrush : mutedBrush,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 4, 0),
            };
            Grid.SetColumn(timeText, 2);
            topGrid.Children.Add(timeText);

            // Options menu button
            var menuBtn = new Button
            {
                Content = "\uE712",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 10,
                Style = (Style)FindResource("IconBtnStyle"),
                Width = 20,
                Height = 20,
                ToolTip = "Options",
            };
            menuBtn.Click += (s, _) => ShowBudgetMenu(capturedBudget, s as Button);
            Grid.SetColumn(menuBtn, 3);
            topGrid.Children.Add(menuBtn);

            Grid.SetRow(topGrid, 0);
            mainGrid.Children.Add(topGrid);

            // Row 1: Progress bar + Percentage text
            var progressGrid = new Grid { Margin = new Thickness(34, 6, 0, 0) };
            progressGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            progressGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var bar = new ProgressBar
            {
                Style = (Style)FindResource("CompactProgressStyle"),
                Minimum = 0,
                Maximum = 100,
                Value = budget.ProgressPercent,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(bar, 0);
            progressGrid.Children.Add(bar);

            var pctText = new TextBlock
            {
                Text = $"{budget.ProgressPercent}%",
                FontSize = 10,
                Foreground = budget.ProgressFraction >= 1.0 ? successBrush : new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x99)),
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(pctText, 1);
            progressGrid.Children.Add(pctText);

            Grid.SetRow(progressGrid, 1);
            mainGrid.Children.Add(progressGrid);

            card.Child = mainGrid;
            BudgetsItemsPanel.Children.Add(card);

            _budgetCardViews[budget.Id] = (timeText, bar, pctText, playBtn);
        }
    }

    private void ToggleBudgetRunning(TimeBudget target)
    {
        var now = DateTime.UtcNow;

        if (target.IsRunning)
        {
            // Pause target
            target.IsRunning = false;
            target.LastStartTimeUtc = null;
        }
        else
        {
            // Pause any other currently running budget (single active budget rule)
            foreach (var b in _budgetStore.Items.Where(b => b.IsRunning && b.Id != target.Id))
            {
                b.IsRunning = false;
                b.LastStartTimeUtc = null;
            }

            target.IsRunning = true;
            target.LastStartTimeUtc = now;

            if (!_timer.IsEnabled) _timer.Start();
        }

        TimeBudgetsService.Save(_budgetStore);
        RenderBudgets();
    }

    private void AddBudget_Click(object sender, RoutedEventArgs e)
    {
        var (success, title, hours) = TimeBudgetDialog.Show(this);
        if (!success) return;

        var budget = new TimeBudget
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = title,
            TargetHours = hours,
            LoggedSeconds = 0,
            CurrentWeekKey = TimeBudgetsService.GetCurrentWeekKey(),
            IsRunning = false,
        };

        _budgetStore.Items.Add(budget);
        TimeBudgetsService.Save(_budgetStore);
        RenderBudgets();
    }

    private void ShowBudgetMenu(TimeBudget budget, Button? anchor)
    {
        var menu = new ContextMenu();

        var editItem = new MenuItem { Header = "Edit budget..." };
        editItem.Click += (_, _) =>
        {
            var (success, title, hours) = TimeBudgetDialog.Show(this, budget.Title, budget.TargetHours);
            if (!success) return;
            budget.Title = title;
            budget.TargetHours = hours;
            TimeBudgetsService.Save(_budgetStore);
            RenderBudgets();
        };
        menu.Items.Add(editItem);

        var resetItem = new MenuItem { Header = "Reset this week" };
        resetItem.Click += (_, _) =>
        {
            budget.LoggedSeconds = 0;
            budget.IsRunning = false;
            budget.LastStartTimeUtc = null;
            TimeBudgetsService.Save(_budgetStore);
            RenderBudgets();
        };
        menu.Items.Add(resetItem);

        menu.Items.Add(new Separator());

        var deleteItem = new MenuItem { Header = "Delete" };
        deleteItem.Click += (_, _) =>
        {
            var confirm = MessageBox.Show(
                $"Delete time budget \"{budget.Title}\"?",
                "Delete budget", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            _budgetStore.Items.Remove(budget);
            TimeBudgetsService.Save(_budgetStore);
            RenderBudgets();
        };
        menu.Items.Add(deleteItem);

        if (anchor != null)
        {
            menu.PlacementTarget = anchor;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    // ==========================================
    // POMODORO FOCUS TIMER (Existing features)
    // ==========================================

    private void SetMinutes(int minutes)
    {
        _minutes = Math.Clamp(minutes, 1, 180);
        MinutesInput.Text = _minutes.ToString();
    }

    private void MinutesUp_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        SetMinutes(_minutes + 5);
    }

    private void MinutesDown_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        SetMinutes(_minutes - 5);
    }

    private void MinutesInput_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !DigitsOnly.IsMatch(e.Text);
    }

    private void MinutesInput_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        SetMinutes(int.TryParse(MinutesInput.Text, out var v) ? v : _minutes);
    }

    private void MinutesInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Keyboard.ClearFocus();
    }

    private void PlayBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            _running = false;
            PlayBtn.Content = "\uE768";
            // Do not stop timer if a budget is active
            if (!_budgetStore.Items.Any(b => b.IsRunning))
            {
                _timer.Stop();
            }
        }
        else
        {
            if (!int.TryParse(MinutesInput.Text, out var v) || v < 1) v = _minutes;
            SetMinutes(v);
            if (_remainingSeconds <= 0) _remainingSeconds = _minutes * 60;
            _running = true;
            MinutesInput.IsReadOnly = true;
            MinutesSuffix.Visibility = Visibility.Collapsed;
            if (!_timer.IsEnabled) _timer.Start();
            PlayBtn.Content = "\uE769";
        }
    }

    // ==========================================
    // TICK HANDLER FOR BOTH MODES
    // ==========================================

    private void Timer_Tick(object? sender, EventArgs e)
    {
        // 1. Tick Pomodoro if running
        if (_running)
        {
            _remainingSeconds--;
            if (_remainingSeconds <= 0)
            {
                _running = false;
                MinutesInput.IsReadOnly = false;
                MinutesSuffix.Visibility = Visibility.Visible;
                MinutesInput.Text = _minutes.ToString();
                PlayBtn.Content = "\uE768";

                if (!_budgetStore.Items.Any(b => b.IsRunning))
                {
                    _timer.Stop();
                }

                MessageBox.Show("Focus session complete!", "Focus session");
            }
            else
            {
                var span = TimeSpan.FromSeconds(_remainingSeconds);
                MinutesInput.Text = span.ToString(@"mm\:ss");
            }
        }

        // 2. Tick active time budget if running
        var runningBudgets = _budgetStore.Items.Where(b => b.IsRunning).ToList();
        if (runningBudgets.Count > 0)
        {
            foreach (var b in runningBudgets)
            {
                b.LoggedSeconds++;
                if (_budgetCardViews.TryGetValue(b.Id, out var views))
                {
                    var isCompleted = b.ProgressFraction >= 1.0;
                    views.TimeText.Text = $"{b.FormattedLoggedTime} / {b.FormattedTargetTime}" + (isCompleted ? " ✓" : "");
                    views.Bar.Value = b.ProgressPercent;
                    views.PctText.Text = $"{b.ProgressPercent}%";

                    if (isCompleted)
                    {
                        var successBrush = new SolidColorBrush(Color.FromRgb(0x8F, 0xE0, 0xA8));
                        views.TimeText.Foreground = successBrush;
                        views.PctText.Foreground = successBrush;
                    }
                }
            }

            // Periodic auto-save every 10 seconds
            _budgetSaveCounter++;
            if (_budgetSaveCounter >= 10)
            {
                _budgetSaveCounter = 0;
                TimeBudgetsService.Save(_budgetStore);
            }
        }
        else if (!_running)
        {
            _timer.Stop();
        }
    }
}
