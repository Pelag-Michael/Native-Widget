using System.Windows;
using System.Windows.Controls.Primitives;
using NativeWidget.Services;

namespace NativeWidget;

public partial class VocabularyTagPickerDialog : Window
{
    private readonly HashSet<string> _selected;
    private readonly Dictionary<string, ToggleButton> _buttons = new(StringComparer.OrdinalIgnoreCase);
    public List<string> SelectedTags { get; private set; } = new();

    private VocabularyTagPickerDialog(IEnumerable<string> current)
    {
        InitializeComponent();
        WindowInterop.HideFromAltTab(this);
        _selected = new HashSet<string>(current, StringComparer.OrdinalIgnoreCase);
        Render();
    }

    public static List<string>? Show(Window owner, IEnumerable<string> current)
    {
        var dialog = new VocabularyTagPickerDialog(current) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.SelectedTags : null;
    }

    private void Render()
    {
        TagsPanel.Children.Clear();
        _buttons.Clear();
        var tags = VocabularyTagsService.LoadAll();
        EmptyHint.Visibility = tags.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var tag in tags)
        {
            var button = new ToggleButton
            {
                Content = $"#{tag}",
                IsChecked = _selected.Contains(tag),
                Style = (Style)FindResource("TagChoiceStyle"),
            };
            _buttons[tag] = button;
            TagsPanel.Children.Add(button);
        }
    }

    private void AddTag_Click(object sender, RoutedEventArgs e)
    {
        CaptureSelection();
        var tag = PromptDialog.Show(this, "Create vocabulary tag");
        if (string.IsNullOrWhiteSpace(tag)) return;
        tag = tag.Trim().TrimStart('#');
        if (tag.Length == 0) return;
        VocabularyTagsService.Add(tag);
        _selected.Add(tag);
        Render();
    }

    private void CaptureSelection()
    {
        _selected.Clear();
        foreach (var (tag, button) in _buttons)
            if (button.IsChecked == true) _selected.Add(tag);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        CaptureSelection();
        SelectedTags = VocabularyTagsService.Normalize(_selected).ToList();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
