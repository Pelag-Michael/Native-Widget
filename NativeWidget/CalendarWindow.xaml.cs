using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Shapes;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NativeWidget.Models;
using NativeWidget.Services;

namespace NativeWidget;

internal interface ICalendarEventSource
{
    bool IsConnected { get; }
    Task<List<CalendarEvent>> GetUpcomingEventsAsync(AppConfig config, CancellationToken cancellationToken);
}

internal sealed class GoogleCalendarEventSource : ICalendarEventSource
{
    public bool IsConnected => GoogleCalendarService.IsConnected();

    public Task<List<CalendarEvent>> GetUpcomingEventsAsync(AppConfig config, CancellationToken cancellationToken) =>
        GoogleCalendarService.GetUpcomingEventsAsync(config, cancellationToken);
}

public partial class CalendarWindow : Window
{
    private readonly AppConfig _config;
    private readonly ICalendarEventSource _eventSource;
    private readonly Dictionary<object, CalendarEvent> _eventItems = new();
    private readonly DispatcherTimer _autoRefreshTimer = new() { Interval = TimeSpan.FromMinutes(5) };
    private readonly DispatcherTimer _retryTimer = new();
    private CancellationTokenSource _visibilityCts = new();
    private int _scheduledRetryAttempt;
    private bool _requiresReconnect;
    private bool _stopping;
    private const int MaxScheduledRetries = 3;

    // Loaded, Activated, the 5-minute timer and the refresh button can all fire while a
    // previous load is still awaiting the network. Without this guard the passes interleave:
    // a later pass flips LoadingHint back on over the events an earlier pass already
    // rendered (looks like "still loading" on a fully-loaded list), and both passes mutate
    // EventList.Items concurrently. Same fix TasksWindow already carries.
    private bool _isBusy;

    public CalendarWindow(AppConfig config) : this(config, new GoogleCalendarEventSource())
    {
    }

    internal CalendarWindow(AppConfig config, ICalendarEventSource eventSource)
    {
        InitializeComponent();
        WindowInterop.HideFromAltTab(this);
        _config = config;
        _eventSource = eventSource;
        Loaded += CalendarWindow_Loaded;
        Activated += CalendarWindow_Activated;
        IsVisibleChanged += CalendarWindow_IsVisibleChanged;
        _autoRefreshTimer.Tick += AutoRefreshTimer_Tick;
        _retryTimer.Tick += RetryTimer_Tick;
        if (Application.Current != null) Application.Current.Exit += Application_Exit;
    }

    internal bool IsBusyForTests => _isBusy;
    internal Visibility LoadingVisibilityForTests => LoadingHint.Visibility;
    internal int DisplayedEventCountForTests => _eventItems.Count;
    internal string StatusForTests => CalendarStatus.Text;

    private async void CalendarWindow_Loaded(object sender, RoutedEventArgs e) =>
        await RunEventBoundaryAsync(() => RefreshEventsAsync());

    private async void CalendarWindow_Activated(object? sender, EventArgs e)
    {
        if (IsVisible) await RunEventBoundaryAsync(() => RefreshEventsAsync());
    }

    private async void AutoRefreshTimer_Tick(object? sender, EventArgs e)
    {
        if (IsVisible) await RunEventBoundaryAsync(() => RefreshEventsAsync());
    }

    private async void RetryTimer_Tick(object? sender, EventArgs e)
    {
        _retryTimer.Stop();
        if (IsVisible && !_stopping) await RunEventBoundaryAsync(() => RefreshEventsAsync());
    }

    private void CalendarWindow_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            if (_visibilityCts.IsCancellationRequested)
            {
                _visibilityCts.Dispose();
                _visibilityCts = new CancellationTokenSource();
            }
            _stopping = false;
            _autoRefreshTimer.Start();
        }
        else
        {
            StopBackgroundRefresh();
        }
    }

    private void Application_Exit(object? sender, ExitEventArgs e) => StopBackgroundRefresh();

    private void DragBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    public WidgetHeaderControls Header => HeaderControls;


    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        StopBackgroundRefresh();
        e.Cancel = true;
        Hide();
    }

    private async void GoogleConnectBtn_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_config.GoogleClientId) || string.IsNullOrWhiteSpace(_config.GoogleClientSecret))
        {
            MessageBox.Show("Client ID / Client Secret not set in Settings.", "Missing configuration");
            return;
        }
        GoogleConnectBtn.Content = "Opening browser...";
        try
        {
            await GoogleCalendarService.ConnectAsync(_config, _visibilityCts.Token);
            _requiresReconnect = false;
            _scheduledRetryAttempt = 0;
            await RefreshEventsAsync();
        }
        catch (OperationCanceledException) when (_visibilityCts.IsCancellationRequested)
        {
        }
        catch (GoogleCalendarTransientException)
        {
            CalendarStatus.Text = "Offline — try reconnect";
        }
        catch (GoogleCalendarAuthenticationException)
        {
            _requiresReconnect = true;
            ShowReconnectState();
        }
        catch (Exception)
        {
            CalendarStatus.Text = "Could not connect";
        }
        finally
        {
            GoogleConnectBtn.Content = _requiresReconnect ? "Reconnect Google Calendar" : "Connect Google Calendar";
        }
    }

    private async void GoogleDisconnectBtn_Click(object sender, RoutedEventArgs e)
    {
        await RunEventBoundaryAsync(async () =>
        {
            CancelScheduledRetry(resetAttempts: true);
            _requiresReconnect = false;
            GoogleCalendarService.Disconnect();
            await RefreshEventsAsync();
        });
    }

    private async void RefreshBtn_Click(object sender, RoutedEventArgs e)
    {
        CancelScheduledRetry(resetAttempts: true);
        await RunEventBoundaryAsync(() => RefreshEventsAsync());
    }

    private async void AddEvent_Click(object sender, RoutedEventArgs e)
    {
        var dialog = AddEventDialog.Show(this);
        if (dialog == null) return;
        try
        {
            await GoogleCalendarService.CreateEventAsync(_config, dialog.EventTitle, dialog.Start, dialog.AllDay,
                dialog.RecurrenceFreq, dialog.EventNote, _visibilityCts.Token);
            CancelScheduledRetry(resetAttempts: true);
            await RefreshEventsAsync();
        }
        catch (OperationCanceledException) when (_visibilityCts.IsCancellationRequested)
        {
        }
        catch (GoogleCalendarAuthenticationException)
        {
            _requiresReconnect = true;
            ShowReconnectState();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not create event: {ex.Message}", "Error");
        }
    }

    public async Task RefreshEventsAsync()
    {
        if (_isBusy) return;
        _isBusy = true;
        _retryTimer.Stop();
        try
        {
            if (_requiresReconnect)
            {
                ShowReconnectState();
                return;
            }

            var connected = _eventSource.IsConnected;
            CalDisconnected.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
            CalConnected.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
            AddEventBtn.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
            RefreshBtn.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
            GoogleDisconnectBtn.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
            if (!connected)
            {
                CalendarStatus.Text = "Not connected";
                DisconnectedMessage.Text = "Not connected to Google Calendar.";
                GoogleConnectBtn.Content = "Connect Google Calendar";
                CancelScheduledRetry(resetAttempts: true);
                return;
            }

            CalendarStatus.Text = "Syncing...";
            LoadingHint.Visibility = Visibility.Visible;
            var events = await _eventSource.GetUpcomingEventsAsync(_config, _visibilityCts.Token);
            EventList.Items.Clear();
            _eventItems.Clear();

            DateTime? lastDay = null;
            var en = new System.Globalization.CultureInfo("en-US");
            var eventColors = EventColorsService.Load();

            foreach (var ev in events)
            {
            var day = DateTime.Parse(ev.Start).Date;
            if (lastDay != day)
            {
                lastDay = day;
                string label = day == DateTime.Today ? "Today"
                    : day == DateTime.Today.AddDays(1) ? "Tomorrow"
                    : day.ToString("dddd, dd/MM", en);
                EventList.Items.Add(new TextBlock
                {
                    Text = label,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x77, 0x78, 0x8A)),
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(6, 10, 0, 2),
                });
            }

            eventColors.TryGetValue(ev.Id, out var tagHex);
            var defaultDotColor = ev.AllDay ? Color.FromRgb(0x77, 0x78, 0x8A) : Color.FromRgb(0x4A, 0x7D, 0xFF);
            var tagColor = string.IsNullOrEmpty(tagHex) ? defaultDotColor : (Color)ColorConverter.ConvertFromString(tagHex);

            // A whole pastel-tinted card (Fantastical-style) reads faster at a glance than a
            // thin color bar - the low alpha keeps it a "wash", not a solid block.
            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x1E, tagColor.R, tagColor.G, tagColor.B)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 8, 6, 8),
                Margin = new Thickness(2, 3, 2, 3),
                Cursor = Cursors.Hand,
            };

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            card.Child = row;

            var dot = new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = new SolidColorBrush(tagColor),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
            };

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var time = new TextBlock
            {
                Text = ev.AllDay ? "All day" : DateTime.Parse(ev.Start).ToString("HH:mm"),
                Foreground = new SolidColorBrush(Color.FromRgb(0x9B, 0x9B, 0xA6)),
                FontSize = 11,
                FontWeight = FontWeights.Medium,
                Margin = new Thickness(0, 0, 0, 2),
            };
            var title = new TextBlock
            {
                Text = ev.Title,
                Foreground = Brushes.White,
                FontSize = 13.5,
                TextWrapping = TextWrapping.Wrap,
            };
            text.Children.Add(time);
            text.Children.Add(title);
            if (!string.IsNullOrWhiteSpace(ev.Description))
            {
                text.Children.Add(new TextBlock
                {
                    Text = ev.Description.Trim(),
                    Foreground = (Brush)FindResource("MutedBrush"),
                    FontSize = 10.5,
                    TextWrapping = TextWrapping.Wrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxHeight = 34,
                    Margin = new Thickness(0, 3, 0, 0),
                });
            }

            var eventId = ev.Id;
            var labels = ItemTagsService.Get("event", eventId);
            if (labels.Count > 0)
            {
                var chips = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
                foreach (var label in labels)
                {
                    chips.Children.Add(new Border
                    {
                        Background = new SolidColorBrush(Color.FromArgb(0x22, 0x4A, 0x7D, 0xFF)),
                        CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 0, 4, 2),
                        Child = new TextBlock { Text = label, FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(0x9F, 0xBB, 0xFF)) },
                    });
                }
                text.Children.Add(chips);
            }

            // Secondary actions (color tag, delete) only appear on hover - showing them on
            // every row at once is what made the list feel busy/cluttered.
            var colorBtn = new ColorTagButton { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), Opacity = 0 };
            colorBtn.SetColor(tagHex);
            colorBtn.ColorSelected += hex => { EventColorsService.SetColor(eventId, hex); _ = RefreshEventsAsync(); };

            var projectBtn = new Button
            {
                Content = "\uE8A5", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 11,
                Width = 22, Height = 22, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0),
                ToolTip = "Assign project", Style = (Style)FindResource("IconBtnStyle"), Opacity = 0,
                Foreground = ItemProjectTagsService.Get("event", eventId) != null ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("MutedBrush"),
            };
            projectBtn.Click += (_, _) =>
            {
                var picked = ProjectPickerDialog.Show(this, ItemProjectTagsService.Get("event", eventId));
                if (picked == null) return;
                ItemProjectTagsService.Set("event", eventId, picked == "" ? null : picked);
                _ = RefreshEventsAsync();
            };

            var labelBtn = new Button
            {
                Content = "\uE8EC", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 11,
                Width = 22, Height = 22, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0),
                ToolTip = "Labels", Style = (Style)FindResource("IconBtnStyle"), Opacity = 0,
                Foreground = labels.Count > 0 ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("MutedBrush"),
            };
            labelBtn.Click += (_, _) =>
            {
                var picked = LabelPickerDialog.Show(this, labels);
                if (picked == null) return;
                ItemTagsService.Set("event", eventId, picked);
                _ = RefreshEventsAsync();
            };

            var delBtn = new Button
            {
                Content = "\uE74D",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 11,
                Width = 22,
                Height = 22,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 0, 0),
                Style = (Style)FindResource("IconBtnStyle"),
                Opacity = 0,
            };
            var eventTitle = ev.Title;
            delBtn.Click += async (_, _) =>
            {
                if (MessageBox.Show($"Delete event \"{eventTitle}\"?", "Confirm delete", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                    return;
                try
                {
                    await GoogleCalendarService.DeleteEventAsync(_config, eventId);
                    await RefreshEventsAsync();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Could not delete: {ex.Message}", "Error");
                }
            };

            card.MouseEnter += (_, _) => { colorBtn.Opacity = 1; projectBtn.Opacity = 1; labelBtn.Opacity = 1; delBtn.Opacity = 1; };
            card.MouseLeave += (_, _) => { colorBtn.Opacity = 0; projectBtn.Opacity = 0; labelBtn.Opacity = 0; delBtn.Opacity = 0; };

            Grid.SetColumn(dot, 0);
            Grid.SetColumn(text, 1);
            Grid.SetColumn(colorBtn, 2);
            Grid.SetColumn(projectBtn, 3);
            Grid.SetColumn(labelBtn, 4);
            Grid.SetColumn(delBtn, 5);
            row.Children.Add(dot);
            row.Children.Add(text);
            row.Children.Add(colorBtn);
            row.Children.Add(projectBtn);
            row.Children.Add(labelBtn);
            row.Children.Add(delBtn);

            _eventItems[card] = ev;
            EventList.Items.Add(card);
        }
            CalendarStatus.Text = $"Updated {DateTime.Now:HH:mm}";
            CancelScheduledRetry(resetAttempts: true);
        }
        catch (OperationCanceledException) when (_visibilityCts.IsCancellationRequested)
        {
            // Hiding the widget or shutting down cancels in-flight HTTP without turning
            // normal window lifecycle into an error state.
        }
        catch (GoogleCalendarTransientException)
        {
            ScheduleRetry();
        }
        catch (GoogleCalendarAuthenticationException)
        {
            _requiresReconnect = true;
            CancelScheduledRetry(resetAttempts: true);
            ShowReconnectState();
        }
        catch (Exception)
        {
            CancelScheduledRetry(resetAttempts: false);
            CalendarStatus.Text = "Calendar unavailable — press Refresh";
        }
        finally
        {
            LoadingHint.Visibility = Visibility.Collapsed;
            _isBusy = false;
        }
    }

    private void ScheduleRetry()
    {
        if (_stopping || !IsVisible)
        {
            CalendarStatus.Text = "Offline — press Refresh";
            return;
        }

        if (_scheduledRetryAttempt >= MaxScheduledRetries)
        {
            CalendarStatus.Text = "Offline — press Refresh";
            return;
        }

        _scheduledRetryAttempt++;
        var seconds = 5 * (1 << (_scheduledRetryAttempt - 1));
        _retryTimer.Interval = TimeSpan.FromMilliseconds(seconds * 1000 + Random.Shared.Next(250, 1000));
        CalendarStatus.Text = "Offline — will retry";
        _retryTimer.Stop();
        _retryTimer.Start();
    }

    private void CancelScheduledRetry(bool resetAttempts)
    {
        _retryTimer.Stop();
        if (resetAttempts) _scheduledRetryAttempt = 0;
    }

    private void ShowReconnectState()
    {
        CalendarStatus.Text = "Reconnect Google";
        DisconnectedMessage.Text = "Google authorization expired. Reconnect to continue syncing.";
        GoogleConnectBtn.Content = "Reconnect Google Calendar";
        CalDisconnected.Visibility = Visibility.Visible;
        CalConnected.Visibility = Visibility.Collapsed;
        AddEventBtn.Visibility = Visibility.Collapsed;
        RefreshBtn.Visibility = Visibility.Collapsed;
        GoogleDisconnectBtn.Visibility = Visibility.Visible;
    }

    private async Task RunEventBoundaryAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException) when (_visibilityCts.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            // This is the final WPF async-void boundary. Network and API failures are
            // handled more specifically inside RefreshEventsAsync, but no unexpected
            // exception is allowed to reach DispatcherUnhandledException.
            LoadingHint.Visibility = Visibility.Collapsed;
            CalendarStatus.Text = "Calendar unavailable — press Refresh";
            _isBusy = false;
        }
    }

    private void StopBackgroundRefresh()
    {
        _stopping = true;
        _autoRefreshTimer.Stop();
        _retryTimer.Stop();
        if (!_visibilityCts.IsCancellationRequested) _visibilityCts.Cancel();
    }

    internal void StopForTests() => StopBackgroundRefresh();

    private void EventList_MouseUp(object sender, MouseButtonEventArgs e)
    {
        // The delete/color buttons live inside the row - a click on them still bubbles this
        // generic MouseUp up to the ListBox (Button only marks the more specific
        // MouseLeftButtonUp handled), which would otherwise also open the event link.
        if (e.OriginalSource is DependencyObject d && FindAncestor<ButtonBase>(d) != null) return;

        if (EventList.SelectedItem == null || !_eventItems.TryGetValue(EventList.SelectedItem, out var ev)) return;

        var start = DateTime.Parse(ev.Start);
        var metadata = ev.AllDay
            ? $"All day · {start:dd/MM/yyyy}"
            : $"{start:HH:mm} · {start:dd/MM/yyyy}";
        ItemDetailsDialog.Show(this, ev.Title, metadata, ev.Description,
            "Open Google Calendar", ev.Link, canEditDescription: false);
    }

    private static T? FindAncestor<T>(DependencyObject d) where T : DependencyObject
    {
        while (d is not null)
        {
            if (d is T match) return match;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }
}
