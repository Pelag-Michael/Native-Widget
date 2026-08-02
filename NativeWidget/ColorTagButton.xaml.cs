using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using NativeWidget.Services;

namespace NativeWidget;

/// A small colored dot that opens a fixed-palette picker; used to color-tag timers, notes
/// and calendar events so the busiest ones stand out at a glance.
public partial class ColorTagButton : UserControl
{
    public event Action<string>? ColorSelected;

    private string _currentHex = "";

    // Picker uses StaysOpen="True" (StaysOpen="False" closes the popup on the same
    // mouse-down that would select a swatch, before the click handler ever runs - a
    // well-known WPF Popup gotcha). This timer closes it manually once the pointer has
    // left both the dot and the popup, mirroring WidgetHeaderControls' opacity slider.
    private readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromMilliseconds(250) };

    public ColorTagButton()
    {
        InitializeComponent();
        BuildSwatches();
        _watch.Tick += (_, _) =>
        {
            if (Dot.IsMouseOver || Picker.IsMouseOver) return;
            Picker.IsOpen = false;
            _watch.Stop();
        };
    }

    public void SetColor(string? hex)
    {
        _currentHex = hex ?? "";
        Dot.ApplyTemplate();
        var circle = (Ellipse)Dot.Template.FindName("Circle", Dot);
        circle.Fill = string.IsNullOrEmpty(_currentHex)
            ? new SolidColorBrush(Color.FromRgb(0x45, 0x45, 0x4E))
            : (Brush)new BrushConverter().ConvertFromString(_currentHex)!;
    }

    private void BuildSwatches()
    {
        foreach (var (name, hex) in ColorTags.Palette)
        {
            var swatch = new Ellipse
            {
                Width = 20,
                Height = 20,
                Margin = new Thickness(4),
                Cursor = System.Windows.Input.Cursors.Hand,
                Stroke = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
                StrokeThickness = 1,
                Fill = string.IsNullOrEmpty(hex)
                    ? new SolidColorBrush(Color.FromRgb(0x45, 0x45, 0x4E))
                    : (Brush)new BrushConverter().ConvertFromString(hex)!,
                ToolTip = name,
            };
            swatch.MouseLeftButtonUp += (_, _) =>
            {
                Picker.IsOpen = false;
                SetColor(hex);
                ColorSelected?.Invoke(hex);
            };
            Swatches.Items.Add(swatch);
        }
    }

    private void Dot_Click(object sender, RoutedEventArgs e)
    {
        Picker.IsOpen = !Picker.IsOpen;
        if (Picker.IsOpen) _watch.Start();
    }
}
