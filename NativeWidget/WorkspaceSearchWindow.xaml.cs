using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NativeWidget.Services;

namespace NativeWidget;

public partial class WorkspaceSearchWindow : Window
{
    private enum ResultKind { Note, Tag, Project }
    private sealed record SearchResult(ResultKind Kind, string Id, string Title, string Detail);

    private readonly Dictionary<object, SearchResult> _results = new();

    public event Action<string>? NoteSelected;
    public event Action<string>? TagSelected;
    public event Action<string>? ProjectSelected;

    public WorkspaceSearchWindow()
    {
        InitializeComponent();
        WindowInterop.HideFromAltTab(this);
        Loaded += (_, _) => RenderResults();
    }

    public void OpenAndFocus()
    {
        if (!IsVisible) Show();
        Activate();
        SearchBox.Focus();
        SearchBox.SelectAll();
        RenderResults();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RenderResults();

    private void RenderResults()
    {
        if (ResultsList == null) return;
        var query = SearchBox.Text.Trim();
        var projects = ProjectsService.Load().Items;
        var projectsById = projects.ToDictionary(project => project.Id);
        var assignments = ItemProjectTagsService.Load();
        var notes = NotesService.LoadIndex();
        var tags = notes.SelectMany(note => note.Tags).Concat(ItemTagsService.AllTags())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase);

        bool Matches(string? value) => string.IsNullOrEmpty(query) || (!string.IsNullOrEmpty(value) && value.Contains(query, StringComparison.OrdinalIgnoreCase));
        var results = new List<SearchResult>();

        foreach (var project in projects.Where(project => Matches(project.Name) || Matches(project.Note) || Matches(project.FolderPath)))
            results.Add(new SearchResult(ResultKind.Project, project.Id, project.Name,
                string.IsNullOrWhiteSpace(project.Note) ? "Dự án" : project.Note));

        foreach (var tag in tags.Where(Matches))
        {
            var noteCount = notes.Count(note => note.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase));
            results.Add(new SearchResult(ResultKind.Tag, tag, tag,
                noteCount > 0 ? $"Nhãn · {noteCount} ghi chú" : "Nhãn cục bộ"));
        }

        foreach (var note in notes.Where(note =>
                     Matches(note.Title) || Matches(note.Preview) || note.Tags.Any(Matches) ||
                     (assignments.TryGetValue($"note:{note.Id}", out var projectId) && projectsById.TryGetValue(projectId, out var project) && Matches(project.Name))))
        {
            var projectName = assignments.TryGetValue($"note:{note.Id}", out var projectId) && projectsById.TryGetValue(projectId, out var project)
                ? $" · {project.Name}" : "";
            results.Add(new SearchResult(ResultKind.Note, note.Id, note.Title,
                string.IsNullOrWhiteSpace(note.Preview) ? $"Ghi chú{projectName}" : $"{note.Preview}{projectName}"));
        }

        ResultsList.Items.Clear();
        _results.Clear();
        foreach (var result in results.Take(40))
        {
            var row = new Grid { Margin = new Thickness(6, 6, 6, 6), Cursor = Cursors.Hand };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var (glyph, accent) = result.Kind switch
            {
                ResultKind.Note => ("\uE70F", (Brush)FindResource("AccentBrush")),
                ResultKind.Tag => ("\uE8EC", new SolidColorBrush(Color.FromRgb(0x9F, 0xBB, 0xFF))),
                _ => ("\uE8A5", new SolidColorBrush(Color.FromRgb(0x8F, 0xE0, 0xA8))),
            };
            row.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), Foreground = accent, FontSize = 13, Margin = new Thickness(0, 1, 10, 0) });
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = result.Title, Foreground = Brushes.White, FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = result.Detail, Foreground = (Brush)FindResource("MutedBrush"), FontSize = 10.5, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            _results[row] = result;
            ResultsList.Items.Add(row);
        }
        EmptyHint.Visibility = ResultsList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ResultsList_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (ResultsList.SelectedItem == null || !_results.TryGetValue(ResultsList.SelectedItem, out var result)) return;
        switch (result.Kind)
        {
            case ResultKind.Note: NoteSelected?.Invoke(result.Id); break;
            case ResultKind.Tag: TagSelected?.Invoke(result.Id); break;
            case ResultKind.Project: ProjectSelected?.Invoke(result.Id); break;
        }
        Hide();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();
    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e) { e.Cancel = true; Hide(); }
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) { Hide(); e.Handled = true; } }
}
