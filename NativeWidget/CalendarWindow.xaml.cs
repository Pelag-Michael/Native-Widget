using System.Diagnostics;
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

public partial class CalendarWindow : Window
{
    private readonly AppConfig _config;
    private readonly Dictionary<object, string> _eventLinks = new();
    private readonly DispatcherTimer _autoRefreshTimer = new() { Interval = TimeSpan.FromMinutes(5) };

    public CalendarWindow(AppConfig config)
    {
        InitializeComponent();
        WindowInterop.HideFromAltTab(this);
        _config = config;
        Loaded += async (_, _) => await RefreshEventsAsync();
        Activated += async (_, _) => await RefreshEventsAsync();
        _autoRefreshTimer.Tick += async (_, _) =>
        {
            if (IsVisible) await RefreshEventsAsync();
        };
        _autoRefreshTimer.Start();
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

    private async void GoogleConnectBtn_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_config.GoogleClientId) || string.IsNullOrWhiteSpace(_config.GoogleClientSecret))
        {
            MessageBox.Show("Chưa điền Client ID / Client Secret trong Settings.", "Thiếu cấu hình");
            return;
        }
        GoogleConnectBtn.Content = "Đang mở trình duyệt...";
        try
        {
            await GoogleCalendarService.ConnectAsync(_config);
            await RefreshEventsAsync();
        }
        catch
        {
            GoogleConnectBtn.Content = "Lỗi, thử lại";
        }
    }

    private async void GoogleDisconnectBtn_Click(object sender, RoutedEventArgs e)
    {
        GoogleCalendarService.Disconnect();
        await RefreshEventsAsync();
    }

    private async void RefreshBtn_Click(object sender, RoutedEventArgs e) => await RefreshEventsAsync();

    private async void AddEvent_Click(object sender, RoutedEventArgs e)
    {
        var dialog = AddEventDialog.Show(this);
        if (dialog == null) return;
        try
        {
            await GoogleCalendarService.CreateEventAsync(_config, dialog.EventTitle, dialog.Start, dialog.AllDay, dialog.RecurrenceFreq);
            await RefreshEventsAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Không tạo được sự kiện: {ex.Message}", "Lỗi");
        }
    }

    public async Task RefreshEventsAsync()
    {
        var connected = GoogleCalendarService.IsConnected();
        CalDisconnected.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        CalConnected.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        AddEventBtn.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        RefreshBtn.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        GoogleDisconnectBtn.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        CalendarStatus.Text = connected ? "Đang đồng bộ..." : "Chưa kết nối";
        if (!connected) return;

        LoadingHint.Visibility = Visibility.Visible;
        var events = await GoogleCalendarService.GetUpcomingEventsAsync(_config);
        LoadingHint.Visibility = Visibility.Collapsed;
        EventList.Items.Clear();
        _eventLinks.Clear();

        DateTime? lastDay = null;
        var vi = new System.Globalization.CultureInfo("vi-VN");
        var eventColors = EventColorsService.Load();

        foreach (var ev in events)
        {
            var day = DateTime.Parse(ev.Start).Date;
            if (lastDay != day)
            {
                lastDay = day;
                string label = day == DateTime.Today ? "Hôm nay"
                    : day == DateTime.Today.AddDays(1) ? "Ngày mai"
                    : day.ToString("dddd, dd/MM", vi);
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
                Text = ev.AllDay ? "Cả ngày" : DateTime.Parse(ev.Start).ToString("HH:mm"),
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
                ToolTip = "Gán dự án", Style = (Style)FindResource("IconBtnStyle"), Opacity = 0,
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
                ToolTip = "Nhãn", Style = (Style)FindResource("IconBtnStyle"), Opacity = 0,
                Foreground = labels.Count > 0 ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("MutedBrush"),
            };
            labelBtn.Click += (_, _) =>
            {
                var input = PromptDialog.Show(this, "Nhãn (cách nhau bởi dấu phẩy)", string.Join(", ", labels));
                if (input == null) return;
                ItemTagsService.Set("event", eventId, input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
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
                if (MessageBox.Show($"Xoá sự kiện \"{eventTitle}\"?", "Xác nhận xoá", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                    return;
                try
                {
                    await GoogleCalendarService.DeleteEventAsync(_config, eventId);
                    await RefreshEventsAsync();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Không xoá được: {ex.Message}", "Lỗi");
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

            _eventLinks[card] = ev.Link;
            EventList.Items.Add(card);
        }
        CalendarStatus.Text = $"Đã cập nhật {DateTime.Now:HH:mm}";
    }

    private void EventList_MouseUp(object sender, MouseButtonEventArgs e)
    {
        // The delete/color buttons live inside the row - a click on them still bubbles this
        // generic MouseUp up to the ListBox (Button only marks the more specific
        // MouseLeftButtonUp handled), which would otherwise also open the event link.
        if (e.OriginalSource is DependencyObject d && FindAncestor<ButtonBase>(d) != null) return;

        if (EventList.SelectedItem != null && _eventLinks.TryGetValue(EventList.SelectedItem, out var link) && !string.IsNullOrEmpty(link))
            Process.Start(new ProcessStartInfo(link) { UseShellExecute = true });
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
