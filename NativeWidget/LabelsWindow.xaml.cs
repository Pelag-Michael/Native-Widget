using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NativeWidget.Services;

namespace NativeWidget;

public partial class LabelsWindow : Window
{
    public LabelsWindow()
    {
        InitializeComponent();
        WindowInterop.HideFromAltTab(this);
        Loaded += (_, _) => Render();
    }

    public WidgetHeaderControls Header => HeaderControls;

    public void Render()
    {
        var labels = LabelsService.LoadAll();
        LabelsList.Items.Clear();
        CountText.Text = labels.Count == 0 ? "" : labels.Count.ToString();
        EmptyHint.Visibility = labels.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var label in labels)
        {
            var row = new Grid { Margin = new Thickness(6, 5, 6, 5) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x22, 0x4A, 0x7D, 0xFF)),
                CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 2, 8, 2),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock { Text = label, Foreground = new SolidColorBrush(Color.FromRgb(0x9F, 0xBB, 0xFF)), FontSize = 11.5 },
            });
            var uses = LabelsService.UsageCount(label);
            text.Children.Add(new TextBlock
            {
                Text = uses == 0 ? "Not used yet" : uses == 1 ? "Used on 1 item" : $"Used on {uses} items",
                Foreground = (Brush)FindResource("MutedBrush"), FontSize = 10, Margin = new Thickness(2, 3, 0, 0),
            });

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Opacity = 0 };
            var rename = MakeIcon("\uE8AC", "Rename");
            rename.Click += (_, _) =>
            {
                var value = PromptDialog.Show(this, "Rename label", label);
                if (string.IsNullOrWhiteSpace(value)) return;
                LabelsService.Rename(label, value);
                Render();
            };
            var delete = MakeIcon("\uE74D", "Delete label");
            delete.Click += (_, _) =>
            {
                if (MessageBox.Show(this, $"Remove label \"{label}\" from every note, task, and event?",
                        "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                LabelsService.Delete(label);
                Render();
            };
            actions.Children.Add(rename);
            actions.Children.Add(delete);
            row.MouseEnter += (_, _) => actions.Opacity = 1;
            row.MouseLeave += (_, _) => actions.Opacity = 0;

            Grid.SetColumn(text, 0);
            Grid.SetColumn(actions, 1);
            row.Children.Add(text);
            row.Children.Add(actions);
            LabelsList.Items.Add(row);
        }
    }

    private Button MakeIcon(string glyph, string tooltip) => new()
    {
        Content = glyph, ToolTip = tooltip, Style = (Style)FindResource("IconBtnStyle"), Width = 24, Height = 24, FontSize = 10,
    };

    private void AddLabel_Click(object sender, RoutedEventArgs e)
    {
        var label = PromptDialog.Show(this, "Create label");
        if (string.IsNullOrWhiteSpace(label)) return;
        LabelsService.Add(label);
        Render();
    }

    private void DragBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}
