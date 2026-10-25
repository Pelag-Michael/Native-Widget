using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NativeWidget.Services;

namespace NativeWidget;

internal sealed class HoverPanelSettingsWindow : Window, IDisposable
{
    private readonly Border _chrome;
    private readonly TextBlock _title;
    private bool _disposing;
    internal HoverPanelSettingsWindow(Window owner, StackPanel controls, ResourceDictionary resources)
    {
        Owner = owner; Width = 260; SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        Topmost = true; ShowActivated = false; ShowInTaskbar = false; Cursor = Cursors.Arrow; ForceCursor = true;
        ResizeMode = ResizeMode.NoResize;
        Resources.MergedDictionaries.Add(resources);
        var body = new StackPanel();
        var header = new DockPanel { Margin = new Thickness(0,0,0,8), Background = Brushes.Transparent };
        var close = new Button { Content = "\u00D7", Padding = new Thickness(7,1,7,1), Style = (Style)owner.FindResource("SecondaryBtnStyle") };
        DockPanel.SetDock(close, Dock.Right); header.Children.Add(close); close.Click += (_, _) => Hide();
        _title = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(_title); header.MouseLeftButtonDown += (_, e) => { if (e.ChangedButton == MouseButton.Left && e.OriginalSource is not Button) DragMove(); };
        body.Children.Add(header);
        controls.Visibility = Visibility.Visible;
        body.Children.Add(new ScrollViewer { Content = controls, MaxHeight = Math.Max(180, Math.Min(560, SystemParameters.WorkArea.Height - 100)), VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        _chrome = new Border { Child = body, Padding = new Thickness(12), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(45,255,255,255)) };
        Content = _chrome;
        WindowInterop.HideFromAltTab(this);
        Closing += (_, e) => { if (!_disposing) { e.Cancel = true; Hide(); } };
    }
    internal void Open(string title, Window anchor)
    {
        _title.Text = title;
        Left = Math.Clamp(anchor.Left + anchor.Width + 8, SystemParameters.VirtualScreenLeft, Math.Max(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width));
        Top = Math.Clamp(anchor.Top, SystemParameters.VirtualScreenTop, Math.Max(SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 600));
        Show(); UpdateLayout();
        Top = Math.Clamp(anchor.Top, SystemParameters.VirtualScreenTop, Math.Max(SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - ActualHeight));
        if (!HoverDwellController.IsInvoking) Activate();
    }
    internal void RefreshTheme(Brush background, Brush foreground)
    { _chrome.Background = background; _title.Foreground = foreground; }
    public void Dispose() { _disposing = true; Close(); }
}
