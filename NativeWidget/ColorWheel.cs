using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NativeWidget;

/// Local HSV picker: drag inside the hue/saturation disc; brightness is controlled separately.
public sealed class ColorWheel : FrameworkElement
{
    private double _hue, _saturation, _value = 1;
    private BitmapSource? _disc;
    public event Action<Color>? ColorChanged;
    public Color SelectedColor => FromHsv(_hue, _saturation, _value);
    public double Brightness
    {
        get => _value;
        set { _value = Math.Clamp(value, 0, 1); _disc = null; InvalidateVisual(); ColorChanged?.Invoke(SelectedColor); }
    }
    public ColorWheel()
    {
        Width = Height = 156;
        Cursor = Cursors.Cross;
        MouseLeftButtonDown += (_, e) => { CaptureMouse(); Pick(e.GetPosition(this)); e.Handled = true; };
        MouseMove += (_, e) => { if (IsMouseCaptured) Pick(e.GetPosition(this)); };
        MouseLeftButtonUp += (_, e) => { ReleaseMouseCapture(); e.Handled = true; };
    }
    public void SetColor(Color color)
    {
        var r = color.R / 255d; var g = color.G / 255d; var b = color.B / 255d;
        var max = Math.Max(r, Math.Max(g, b)); var min = Math.Min(r, Math.Min(g, b)); var delta = max - min;
        if (delta > 0) _hue = ((max == r ? (g - b) / delta : max == g ? (b - r) / delta + 2 : (r - g) / delta + 4) * 60 + 360) % 360;
        _saturation = max == 0 ? 0 : delta / max; _value = max;
        _disc = null; InvalidateVisual();
    }
    internal void Pick(Point point)
    {
        var x = point.X - ActualWidth / 2; var y = point.Y - ActualHeight / 2;
        _hue = (Math.Atan2(y, x) * 180 / Math.PI + 360) % 360;
        _saturation = Math.Clamp(Math.Sqrt(x * x + y * y) / (Math.Min(ActualWidth, ActualHeight) / 2 - 5), 0, 1);
        InvalidateVisual(); ColorChanged?.Invoke(SelectedColor);
    }
    protected override void OnRender(DrawingContext drawing)
    {
        const int size = 180;
        if (_disc == null)
        {
            var pixels = new byte[size * size * 4];
            for (var y = 0; y < size; y++) for (var x = 0; x < size; x++)
            {
                var dx = (x - (size - 1) / 2d) / (size / 2d); var dy = (y - (size - 1) / 2d) / (size / 2d);
                var saturation = Math.Sqrt(dx * dx + dy * dy);
                if (saturation > 1) continue;
                var color = FromHsv((Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360, saturation, 1);
                var offset = (y * size + x) * 4;
                pixels[offset] = color.B; pixels[offset + 1] = color.G; pixels[offset + 2] = color.R; pixels[offset + 3] = 255;
            }
            _disc = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
            _disc.Freeze();
        }
        var radius = Math.Min(ActualWidth, ActualHeight) / 2 - 5;
        drawing.DrawImage(_disc, new Rect(ActualWidth / 2 - radius, ActualHeight / 2 - radius, radius * 2, radius * 2));
        var angle = _hue * Math.PI / 180;
        var marker = new Point(ActualWidth / 2 + radius * _saturation * Math.Cos(angle), ActualHeight / 2 + radius * _saturation * Math.Sin(angle));
        drawing.DrawEllipse(null, new Pen(Brushes.Black, 4), marker, 5, 5);
        drawing.DrawEllipse(null, new Pen(Brushes.White, 2), marker, 5, 5);
    }
    internal static Color FromHsv(double hue, double saturation, double value)
    {
        var c = value * saturation; var x = c * (1 - Math.Abs(hue / 60 % 2 - 1)); var m = value - c;
        var (r, g, b) = hue switch { < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x), < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x) };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}
