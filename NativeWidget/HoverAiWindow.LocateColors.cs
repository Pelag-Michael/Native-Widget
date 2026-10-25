using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NativeWidget;

public partial class HoverAiWindow
{
    private int _locateColorPhase = 1;
    private bool _loadingLocateColor;

    private void InitializeLocateColors()
    {
        UpdateLocateSwatches();
        LocateWheel.ColorChanged += color =>
        {
            if (_loadingLocateColor) return;
            var value = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            if (_locateColorPhase == 1) _config.HoverAiLocateFirstColor = value; else _config.HoverAiLocateSecondColor = value;
            UpdateLocateSwatches();
            _saveTimer.Stop(); _saveTimer.Start();
        };
    }

    private void UpdateLocateSwatches()
    {
        LocateFirstSwatch.Background = LocateBrush(_config.HoverAiLocateFirstColor, Colors.White);
        LocateSecondSwatch.Background = LocateBrush(_config.HoverAiLocateSecondColor, Colors.Orange);
    }

    private static SolidColorBrush LocateBrush(string value, Color fallback)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)); }
        catch (FormatException) { return new SolidColorBrush(fallback); }
    }

    private void LocateColor_Click(object sender, RoutedEventArgs e)
    {
        _locateColorPhase = (string)((Button)sender).Tag == "1" ? 1 : 2;
        _loadingLocateColor = true;
        try
        {
            LocatePicker.Visibility = Visibility.Visible;
            LocateWheel.SetColor(LocateBrush(_locateColorPhase == 1 ? _config.HoverAiLocateFirstColor : _config.HoverAiLocateSecondColor, Colors.White).Color);
            LocateBrightness.Value = LocateWheel.Brightness;
        }
        finally { _loadingLocateColor = false; }
    }

    private void LocateBrightness_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_ready && !_loadingLocateColor) LocateWheel.Brightness = e.NewValue;
    }
}
