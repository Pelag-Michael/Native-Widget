using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using NativeWidget.Services;

namespace NativeWidget;

public partial class FocusWindow : Window
{
    private static readonly Regex DigitsOnly = new("^[0-9]+$");

    private int _minutes = 25;
    private int _remainingSeconds;
    private bool _running;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public FocusWindow()
    {
        InitializeComponent();
        WindowInterop.HideFromAltTab(this);
        _timer.Tick += Timer_Tick;
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
            _timer.Stop();
            PlayBtn.Content = "";
        }
        else
        {
            if (!int.TryParse(MinutesInput.Text, out var v) || v < 1) v = _minutes;
            SetMinutes(v);
            if (_remainingSeconds <= 0) _remainingSeconds = _minutes * 60;
            _running = true;
            MinutesInput.IsReadOnly = true;
            MinutesSuffix.Visibility = Visibility.Collapsed;
            _timer.Start();
            PlayBtn.Content = "";
        }
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        _remainingSeconds--;
        if (_remainingSeconds <= 0)
        {
            _timer.Stop();
            _running = false;
            MinutesInput.IsReadOnly = false;
            MinutesSuffix.Visibility = Visibility.Visible;
            MinutesInput.Text = _minutes.ToString();
            PlayBtn.Content = "";
            MessageBox.Show("Hết giờ tập trung rồi!", "Focus session");
            return;
        }
        var span = TimeSpan.FromSeconds(_remainingSeconds);
        MinutesInput.Text = span.ToString(@"mm\:ss");
    }
}
