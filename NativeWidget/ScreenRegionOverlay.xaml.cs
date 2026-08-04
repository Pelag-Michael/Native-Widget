using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace NativeWidget;

public partial class ScreenRegionOverlay : Window
{
    private Point _start;
    private bool _dragging;
    public Rect? ScreenRegion { get; private set; }

    public ScreenRegionOverlay()
    {
        InitializeComponent();
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        Loaded += (_, _) => { Activate(); Focus(); };
    }

    public static Rect? Select(Window owner)
    {
        var overlay = new ScreenRegionOverlay { Owner = owner };
        overlay.ShowDialog();
        return overlay.ScreenRegion;
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(this);
        _dragging = true;
        SelectionBorder.Visibility = Visibility.Visible;
        CaptureMouse();
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var current = e.GetPosition(this);
        var left = Math.Min(_start.X, current.X);
        var top = Math.Min(_start.Y, current.Y);
        Canvas.SetLeft(SelectionBorder, left);
        Canvas.SetTop(SelectionBorder, top);
        SelectionBorder.Width = Math.Abs(current.X - _start.X);
        SelectionBorder.Height = Math.Abs(current.Y - _start.Y);
    }

    private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        var end = e.GetPosition(this);
        if (Math.Abs(end.X - _start.X) < 8 || Math.Abs(end.Y - _start.Y) < 8)
        {
            DialogResult = false;
            return;
        }

        // PointToScreen converts WPF device-independent coordinates to the physical pixels
        // expected by BitBlt, including per-monitor DPI scaling.
        var startPx = PointToScreen(_start);
        var endPx = PointToScreen(end);
        ScreenRegion = new Rect(Math.Min(startPx.X, endPx.X), Math.Min(startPx.Y, endPx.Y),
            Math.Abs(endPx.X - startPx.X), Math.Abs(endPx.Y - startPx.Y));
        Visibility = Visibility.Hidden;
        DialogResult = true;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) DialogResult = false;
    }
}
