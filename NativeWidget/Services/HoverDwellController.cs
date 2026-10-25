using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace NativeWidget.Services;

// Local to a Quick AI window. No global styles, cursor changes or mouse clicks.
internal sealed class HoverDwellController
{
    [ThreadStatic] private static bool _invoking;
    internal static bool IsInvoking => _invoking;
    private readonly FrameworkElement _root;
    private readonly Func<Button, bool> _enabled;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(25) };
    private readonly Stopwatch _watch = new();
    private Button? _button;
    private ProgressAdorner? _progress;
    private AdornerLayer? _layer;
    private bool _fired;
    internal bool IsActive => _timer.IsEnabled;
    internal double Progress => Math.Clamp(_watch.Elapsed.TotalSeconds / 3, 0, 1);

    internal HoverDwellController(FrameworkElement root, Func<Button, bool> enabled)
    {
        _root = root; _enabled = enabled;
        root.AddHandler(Mouse.MouseMoveEvent, new MouseEventHandler(OnMove), true);
        root.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler((_, _) => Cancel(latch: true)), true);
        root.MouseLeave += (_, _) => Cancel();
        root.IsVisibleChanged += (_, _) => { if (!root.IsVisible) Cancel(); };
        _timer.Tick += (_, _) => Tick();
        if (root is Window window) window.Closed += (_, _) => Cancel();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        DependencyObject? current = e.OriginalSource as DependencyObject;
        while (current != null && current is not Button && current != _root)
            current = current is Visual || current is System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
        var button = current as Button;
        if (button == _button) return;
        Cancel();
        if (button == null || !button.IsEnabled || !_enabled(button)) return;
        _button = button;
        _layer = AdornerLayer.GetAdornerLayer(button);
        if (_layer != null) { _progress = new ProgressAdorner(button); _layer.Add(_progress); }
        _watch.Restart(); _timer.Start();
    }

    private void Tick()
    {
        var button = _button;
        if (button == null || !button.IsVisible || !button.IsEnabled || !button.IsMouseOver || !_enabled(button)) { Cancel(); return; }
        if (_progress != null) { _progress.Progress = Progress; _progress.InvalidateVisual(); }
        if (_fired || Progress < 1) return;
        _fired = true; _timer.Stop(); RemoveProgress();
        // Latch before invoking: Send becoming Stop must not trigger again while
        // the pointer remains on the same button. A physical click still works.
        _invoking = true;
        try
        {
            // This is a managed action event, not native mouse input. The button
            // is never pressed, focused or activated by the dwell controller.
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
            if (button.Command?.CanExecute(button.CommandParameter) == true)
            {
                if (button.Command is RoutedCommand routed) routed.Execute(button.CommandParameter, button.CommandTarget ?? button);
                else button.Command.Execute(button.CommandParameter);
            }
        }
        finally { _invoking = false; }
    }

    internal void Cancel(bool latch = false)
    {
        _timer.Stop(); _watch.Reset(); RemoveProgress();
        _fired = latch;
        if (!latch) _button = null;
    }

    private void RemoveProgress()
    {
        if (_progress != null) _layer?.Remove(_progress);
        _progress = null; _layer = null;
    }

    private sealed class ProgressAdorner : Adorner
    {
        private readonly Button _target;
        internal ProgressAdorner(Button button) : base(button) { _target = button; IsHitTestVisible = false; }
        internal double Progress;
        protected override void OnRender(DrawingContext drawing)
        {
            var brush = _target.Foreground.CloneCurrentValue(); brush.Opacity = .28;
            var bounds = new Rect(_target.RenderSize);
            drawing.PushClip(new RectangleGeometry(new Rect(0, 0, bounds.Width * Progress, bounds.Height)));
            drawing.DrawRoundedRectangle(brush, null, bounds, 6, 6);
            drawing.Pop();
        }
    }
}
