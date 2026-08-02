using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using NativeWidget.Services;

namespace NativeWidget;

public partial class ProjectPickerDialog : Window
{
    public string? SelectedProjectId { get; private set; }
    private readonly Dictionary<object, string?> _rowProjectIds = new();

    private ProjectPickerDialog(string? currentProjectId)
    {
        InitializeComponent();

        var noneRow = BuildRow("Không gắn", null, currentProjectId == null);
        ProjectListBox.Items.Add(noneRow);
        _rowProjectIds[noneRow] = null;

        foreach (var p in ProjectsService.Load().Items)
        {
            var row = BuildRow(p.Name, p.Color, p.Id == currentProjectId);
            ProjectListBox.Items.Add(row);
            _rowProjectIds[row] = p.Id;
        }
    }

    /// Returns null if the user cancelled - otherwise the picked project ID, or "" for
    /// "Không gắn" (explicit clear).
    public static string? Show(Window owner, string? currentProjectId)
    {
        var dialog = new ProjectPickerDialog(currentProjectId) { Owner = owner };
        if (dialog.ShowDialog() != true) return null;
        return dialog.SelectedProjectId ?? "";
    }

    private UIElement BuildRow(string name, string? colorHex, bool isCurrent)
    {
        var row = new Grid { Margin = new Thickness(6, 4, 6, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var dot = new Ellipse
        {
            Width = 8,
            Height = 8,
            Margin = new Thickness(0, 0, 8, 0),
            Fill = ColorTags.Resolve(colorHex, new SolidColorBrush(Color.FromRgb(0x45, 0x45, 0x4E))),
        };
        var text = new TextBlock
        {
            Text = name,
            Foreground = isCurrent ? (Brush)FindResource("AccentBrush") : Brushes.White,
            FontWeight = isCurrent ? FontWeights.SemiBold : FontWeights.Normal,
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
        };

        Grid.SetColumn(dot, 0);
        Grid.SetColumn(text, 1);
        row.Children.Add(dot);
        row.Children.Add(text);
        return row;
    }

    private void ProjectListBox_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (ProjectListBox.SelectedItem == null) return;
        SelectedProjectId = _rowProjectIds.TryGetValue(ProjectListBox.SelectedItem, out var id) ? id : null;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
