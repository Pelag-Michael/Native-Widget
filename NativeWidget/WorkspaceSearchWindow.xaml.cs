using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using NativeWidget.Services;

namespace NativeWidget;

public partial class WorkspaceSearchWindow : Window
{
    private enum ResultKind { Note, Tag, Project, Folder }
    private sealed record SearchResult(ResultKind Kind, string Id, string Title, string Detail);

    private readonly FolderSearchService _folderSearch = new();
    private readonly Dictionary<object, SearchResult> _results = new();
    private CancellationTokenSource? _refreshCancellation;
    private bool _foldersOnly;
    private bool _folderIndexLoaded;

    public event Action<string>? NoteSelected;
    public event Action<string>? TagSelected;
    public event Action<string>? ProjectSelected;

    public WorkspaceSearchWindow()
    {
        InitializeComponent();
        WindowInterop.HideFromAltTab(this);
        Loaded += Window_Loaded;
    }

    public void OpenAndFocus()
    {
        if (!IsVisible) Show();
        Activate();
        SearchBox.Focus();
        SearchBox.SelectAll();
        RenderResults();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await LoadFolderIndexAsync();

    internal async Task LoadFolderIndexAsync()
    {
        if (_folderIndexLoaded) return;
        _folderIndexLoaded = true;
        SetFolderStatus("Loading folder index…");
        try
        {
            await _folderSearch.LoadAsync();
            RootsButton.IsEnabled = true;
            UpdateFolderStatus();
            RenderResults();
            if (!_folderSearch.HasIndex || DateTime.UtcNow - _folderSearch.UpdatedUtc > TimeSpan.FromDays(7))
                await RefreshFolderIndexAsync();
        }
        catch (Exception ex)
        {
            RootsButton.IsEnabled = true;
            SetFolderStatus($"Folder index unavailable: {ex.Message}");
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RenderResults();

    private void WorkspaceMode_Click(object sender, RoutedEventArgs e) => SetMode(false);
    private void FoldersOnly_Click(object sender, RoutedEventArgs e) => SetMode(true);

    private void SetMode(bool foldersOnly)
    {
        _foldersOnly = foldersOnly;
        WorkspaceModeButton.IsChecked = !foldersOnly;
        FoldersOnlyButton.IsChecked = foldersOnly;
        SearchPlaceholder.Text = foldersOnly ? "Search indexed folder names or paths…" : "Search notes, projects, or folders…";
        RenderResults();
        SearchBox.Focus();
    }

    private void RenderResults()
    {
        if (ResultsList == null) return;
        var query = SearchBox.Text.Trim();
        var results = new List<SearchResult>();
        if (!_foldersOnly)
        {
            var projects = ProjectsService.Load().Items;
            var projectsById = projects.ToDictionary(project => project.Id);
            var assignments = ItemProjectTagsService.Load();
            var notes = NotesService.LoadIndex();
            var tags = LabelsService.LoadAll();
            bool Matches(string? value) => string.IsNullOrEmpty(query) || (!string.IsNullOrEmpty(value) && value.Contains(query, StringComparison.OrdinalIgnoreCase));

            foreach (var project in projects.Where(project => Matches(project.Name) || Matches(project.Note) || Matches(project.FolderPath)))
                results.Add(new SearchResult(ResultKind.Project, project.Id, project.Name,
                    string.IsNullOrWhiteSpace(project.Note) ? "Project" : project.Note));

            foreach (var tag in tags.Where(Matches))
            {
                var noteCount = notes.Count(note => note.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase));
                results.Add(new SearchResult(ResultKind.Tag, tag, tag,
                    noteCount > 0 ? noteCount == 1 ? "Label · 1 note" : $"Label · {noteCount} notes" : "Local label"));
            }

            foreach (var note in notes.Where(note => Matches(note.Title) || Matches(note.Preview) || note.Tags.Any(Matches) ||
                         (assignments.TryGetValue($"note:{note.Id}", out var projectId) && projectsById.TryGetValue(projectId, out var project) && Matches(project.Name))))
            {
                var projectName = assignments.TryGetValue($"note:{note.Id}", out var projectId) && projectsById.TryGetValue(projectId, out var project)
                    ? $" · {project.Name}" : "";
                results.Add(new SearchResult(ResultKind.Note, note.Id, note.Title,
                    string.IsNullOrWhiteSpace(note.Preview) ? $"Note{projectName}" : $"{note.Preview}{projectName}"));
            }
        }

        var folderMatches = _folderSearch.Folders
            .Where(folder => string.IsNullOrWhiteSpace(query) || folder.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || folder.Path.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(folder => new SearchResult(ResultKind.Folder, folder.Path, folder.Name, folder.Path))
            .OrderBy(result => FolderSearchService.RankMatch(result.Title, result.Detail, query))
            .ThenBy(result => result.Title, StringComparer.OrdinalIgnoreCase)
            .Take(_foldersOnly ? 60 : 20)
            .ToList();
        results.AddRange(folderMatches);

        ResultsList.Items.Clear();
        _results.Clear();
        foreach (var result in results.Take(60))
        {
            var row = new Grid { Margin = new Thickness(6), Cursor = Cursors.Hand };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var (glyph, accent) = result.Kind switch
            {
                ResultKind.Note => ("\uE70F", (Brush)FindResource("AccentBrush")),
                ResultKind.Tag => ("\uE8EC", new SolidColorBrush(Color.FromRgb(0x9F, 0xBB, 0xFF))),
                ResultKind.Project => ("\uE8A5", new SolidColorBrush(Color.FromRgb(0x8F, 0xE0, 0xA8))),
                _ => ("\uE8B7", new SolidColorBrush(Color.FromRgb(0xF1, 0xC2, 0x7A))),
            };
            row.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), Foreground = accent, FontSize = 13, Margin = new Thickness(0, 1, 10, 0) });
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = result.Title, Foreground = Brushes.White, FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = result.Detail, Foreground = (Brush)FindResource("MutedBrush"), FontSize = 10.5, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            if (result.Kind == ResultKind.Folder) row.ContextMenu = CreateFolderMenu(result.Id);
            _results[row] = result;
            ResultsList.Items.Add(row);
        }
        if (ResultsList.Items.Count == 0)
        {
            EmptyHint.Text = !_folderSearch.HasIndex
                ? "No folders indexed yet. Add a search root or refresh the folder index."
                : _foldersOnly
                    ? "No match in indexed folders. Search covers configured roots only."
                    : "No matching results. Folder search covers configured roots only.";
            EmptyState.Visibility = Visibility.Visible;
        }
        else EmptyState.Visibility = Visibility.Collapsed;
    }

    private ContextMenu CreateFolderMenu(string path)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("Open folder", (_, _) => OpenFolder(path)));
        menu.Items.Add(MenuItem("Copy path", (_, _) => CopyText(path, "Folder path copied.")));
        menu.Items.Add(MenuItem("Copy CMD command", (_, _) => CopyText($"cd /d \"{path}\"", "CMD command copied.")));
        return menu;
    }

    private static MenuItem MenuItem(string text, RoutedEventHandler action)
    {
        var item = new MenuItem { Header = text };
        item.Click += action;
        return item;
    }

    private void ResultsList_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || ResultsList.SelectedItem == null || !_results.TryGetValue(ResultsList.SelectedItem, out var result)) return;
        if (result.Kind == ResultKind.Folder)
        {
            if (e.ClickCount > 1) OpenFolder(result.Id);
            return;
        }
        switch (result.Kind)
        {
            case ResultKind.Note: NoteSelected?.Invoke(result.Id); break;
            case ResultKind.Tag: TagSelected?.Invoke(result.Id); break;
            case ResultKind.Project: ProjectSelected?.Invoke(result.Id); break;
        }
        Hide();
    }

    private void ResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = ResultsList.SelectedItem;
        if (selected != null && _results.TryGetValue(selected, out var result) && result.Kind == ResultKind.Folder)
        {
            SelectedFolderPath.Text = result.Id;
            SelectedFolderPath.ToolTip = result.Id;
            FolderActions.Visibility = Visibility.Visible;
        }
        else FolderActions.Visibility = Visibility.Collapsed;
    }

    private string? SelectedFolderPathValue => ResultsList.SelectedItem is { } selected &&
        _results.TryGetValue(selected, out var result) && result.Kind == ResultKind.Folder ? result.Id : null;

    private void CopySelectedPath_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFolderPathValue is { } path) CopyText(path, "Folder path copied.");
    }

    private void CopySelectedCommand_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFolderPathValue is { } path) CopyText($"cd /d \"{path}\"", "CMD command copied.");
    }

    private void OpenSelectedFolder_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFolderPathValue is { } path) OpenFolder(path);
    }

    private void OpenFolder(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                SetFolderStatus("Folder no longer exists. Refresh the folder index.");
                return;
            }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { SetFolderStatus($"Could not open folder: {ex.Message}"); }
    }

    private void CopyText(string text, string successStatus = "Copied to clipboard.")
    {
        try
        {
            Clipboard.SetText(text);
            SetFolderStatus(successStatus);
        }
        catch (Exception ex) { SetFolderStatus($"Clipboard copy failed: {ex.Message}"); }
    }

    private void Roots_Click(object sender, RoutedEventArgs e)
    {
        if (!_folderIndexLoaded || !RootsButton.IsEnabled) return;
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("Add folder…", async (_, _) => await AddRootAsync()));
        if (_folderSearch.Roots.Count > 0)
        {
            var remove = new MenuItem { Header = "Remove root" };
            foreach (var root in _folderSearch.Roots.ToArray()) remove.Items.Add(MenuItem(root, async (_, _) => await RemoveRootAsync(root)));
            menu.Items.Add(remove);
        }
        RootsButton.ContextMenu = menu;
        menu.PlacementTarget = RootsButton;
        menu.IsOpen = true;
    }

    private async Task AddRootAsync()
    {
        var dialog = new OpenFolderDialog { Title = "Add a folder to folder search" };
        if (dialog.ShowDialog(this) != true) return;
        await _folderSearch.SetRootsAsync(_folderSearch.Roots.Append(dialog.FolderName));
        UpdateFolderStatus();
        await RefreshFolderIndexAsync();
    }

    private async void AddRootEmpty_Click(object sender, RoutedEventArgs e) => await AddRootAsync();

    private async void RefreshEmpty_Click(object sender, RoutedEventArgs e) => await RefreshFolderIndexAsync();

    private async Task RemoveRootAsync(string root)
    {
        await _folderSearch.SetRootsAsync(_folderSearch.Roots.Where(item => !item.Equals(root, StringComparison.OrdinalIgnoreCase)));
        UpdateFolderStatus();
        await RefreshFolderIndexAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_refreshCancellation != null)
        {
            _refreshCancellation.Cancel();
            return;
        }
        await RefreshFolderIndexAsync();
    }

    private async Task RefreshFolderIndexAsync()
    {
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _refreshCancellation = cancellation;
        RefreshButton.IsEnabled = true;
        RefreshButton.Content = "\uE711";
        RefreshButton.ToolTip = "Cancel folder index refresh";
        SetFolderStatus("Indexing folders… click cancel to stop");
        try
        {
            await _folderSearch.RefreshAsync(cancellation.Token);
            UpdateFolderStatus();
            RenderResults();
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_refreshCancellation, cancellation)) SetFolderStatus("Index refresh cancelled.");
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_refreshCancellation, cancellation)) SetFolderStatus($"Index refresh failed: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_refreshCancellation, cancellation))
            {
                RefreshButton.IsEnabled = true;
                RefreshButton.Content = "\uE72C";
                RefreshButton.ToolTip = "Refresh folder index";
                _refreshCancellation = null;
                cancellation.Dispose();
            }
        }
    }

    private void UpdateFolderStatus()
    {
        var updated = _folderSearch.UpdatedUtc == default ? "not indexed" : $"updated {_folderSearch.UpdatedUtc.ToLocalTime():g}";
        var status = $"{_folderSearch.Folders.Count:N0} folders · {_folderSearch.Roots.Count} configured roots · {updated}";
        if (_folderSearch.Truncated) status += " · limit reached";
        SetFolderStatus(status);
    }

    private void SetFolderStatus(string text) => FolderStatus.Text = text;

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();
    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e) { e.Cancel = true; Hide(); }
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && SearchBox.IsKeyboardFocusWithin && ResultsList.Items.Count > 0)
        {
            ResultsList.SelectedIndex = Math.Max(0, ResultsList.SelectedIndex);
            ResultsList.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && !SearchBox.IsKeyboardFocusWithin &&
                 SelectedFolderPathValue is { } selectedPath)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) CopyText($"cd /d \"{selectedPath}\"", "CMD command copied.");
            else CopyText(selectedPath, "Folder path copied.");
            e.Handled = true;
        }
        else if (e.Key == Key.Escape) { Hide(); e.Handled = true; }
        else if (e.Key == Key.Enter && ResultsList.SelectedItem is Grid row && _results.TryGetValue(row, out var result) && result.Kind == ResultKind.Folder)
        { OpenFolder(result.Id); e.Handled = true; }
        else if (e.Key == Key.F5) { _ = RefreshFolderIndexAsync(); e.Handled = true; }
    }
}
