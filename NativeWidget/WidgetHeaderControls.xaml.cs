using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NativeWidget.Services;

namespace NativeWidget;

/// Shared header button strip for every widget window: opacity, ghost (click-through),
/// pin (always-on-top) and close.
public partial class WidgetHeaderControls : UserControl
{
    private static readonly SolidColorBrush Muted = new(Color.FromRgb(0x77, 0x77, 0x8A));

    // The popup has StaysOpen=true so it doesn't vanish the instant focus shifts; this
    // timer closes it once the pointer has left both the button and the popup itself.
    private readonly DispatcherTimer _popupWatch = new() { Interval = TimeSpan.FromMilliseconds(250) };

    private Window? _owner;

    public WidgetHeaderControls()
    {
        InitializeComponent();
        _popupWatch.Tick += (_, _) =>
        {
            if (OpacityBtn.IsMouseOver || OpacityPopup.IsMouseOver) return;
            OpacityPopup.IsOpen = false;
            _popupWatch.Stop();
        };
        Loaded += (_, _) => _owner = Window.GetWindow(this);
    }

    private void OpacityBtn_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        OpacityPopup.IsOpen = true;
        _popupWatch.Start();
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_owner == null) return;
        _owner.Opacity = e.NewValue;
        OpacityLabel.Text = $"{e.NewValue * 100:0}%";
    }

    private void GhostBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_owner == null) return;

        var enable = !WindowInterop.IsClickThrough(_owner);
        WindowInterop.SetClickThrough(_owner, enable);
        SetGhostVisual(enable);
    }

    /// Called by the launcher when it force-disables ghost mode, so the icon stays in sync.
    public void SetGhostVisual(bool ghosted)
    {
        GhostBtn.Foreground = ghosted ? (Brush)FindResource("AccentBrush") : Muted;
    }

    private void PinBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_owner != null) WindowInterop.TogglePin(_owner, PinBtn);
    }

    // Close() rather than Hide(): every widget cancels Closing and hides itself, and some
    // (Notes) persist their state in that handler - going straight to Hide would skip it.
    private void CloseBtn_Click(object sender, RoutedEventArgs e) => _owner?.Close();
}
