using System.Windows;
using System.Windows.Controls;
using NativeWidget.Services;

namespace NativeWidget;

public partial class LabelPickerDialog : Window
{
    private readonly HashSet<string> _selected;
    private readonly Dictionary<string, CheckBox> _checks = new(StringComparer.OrdinalIgnoreCase);
    public List<string> SelectedLabels { get; private set; } = new();

    private LabelPickerDialog(IEnumerable<string> current)
    {
        InitializeComponent();
        _selected = new HashSet<string>(current, StringComparer.OrdinalIgnoreCase);
        Render();
    }

    public static List<string>? Show(Window owner, IEnumerable<string> current)
    {
        var dialog = new LabelPickerDialog(current) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.SelectedLabels : null;
    }

    private void Render()
    {
        LabelsList.Items.Clear();
        _checks.Clear();
        var labels = LabelsService.LoadAll();
        EmptyHint.Visibility = labels.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var label in labels)
        {
            var check = new CheckBox
            {
                Content = label,
                IsChecked = _selected.Contains(label),
                Foreground = System.Windows.Media.Brushes.White,
                FontSize = 12,
                Margin = new Thickness(8, 6, 8, 6),
            };
            _checks[label] = check;
            LabelsList.Items.Add(check);
        }
    }

    private void AddLabel_Click(object sender, RoutedEventArgs e)
    {
        CaptureSelection();
        var label = PromptDialog.Show(this, "Tạo nhãn");
        if (string.IsNullOrWhiteSpace(label)) return;
        LabelsService.Add(label);
        _selected.Add(label.Trim().TrimStart('#'));
        Render();
    }

    private void CaptureSelection()
    {
        _selected.Clear();
        foreach (var (label, check) in _checks)
            if (check.IsChecked == true) _selected.Add(label);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        CaptureSelection();
        SelectedLabels = _selected.OrderBy(label => label, StringComparer.OrdinalIgnoreCase).ToList();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
