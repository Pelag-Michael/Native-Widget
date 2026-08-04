using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NativeWidget.Models;
using NativeWidget.Services;

namespace NativeWidget;

public partial class TasksWindow : Window
{
    private readonly AppConfig _config;
    private readonly DispatcherTimer _autoRefreshTimer = new() { Interval = TimeSpan.FromMinutes(5) };
    private List<GoogleTaskList> _lists = new();

    // Set when this window was popped out to show a single list (see PopOut_Click /
    // NotesWindow's identical pattern) - the list picker is locked and Closing really closes
    // the window instead of hiding it, since nothing else tracks a pop-out instance.
    private readonly string? _lockedListId;

    // Parent task IDs currently collapsed (children hidden). Re-render is done from the
    // last-fetched task list so toggling doesn't need a network round-trip.
    private readonly HashSet<string> _collapsedParents = new();
    private readonly HashSet<string> _seenParents = new();
    private string? _lastRenderedListId;
    private List<GoogleTaskItem>? _lastRenderedTasks;
    private string _searchText = "";

    // "" (index 0, "All projects") = no filter. Index-to-projectId map rebuilt whenever the
    // combo is repopulated, since Google Tasks IDs and local project GUIDs share no order.
    private readonly List<string?> _projectFilterIds = new();

    // Loaded, Activated, the launcher's Refresh() and the auto-refresh timer can all fire
    // within the same instant (the launcher calls Refresh() before Show(), so Loaded and
    // Activated land right after it). Without these guards the overlapping loads clear and
    // repopulate ListSelect underneath each other - and every Items.Clear() raises
    // SelectionChanged, which re-entered the load again and left the widget blank.
    private bool _isBusy;
    private bool _suppressSelectionChanged;

    private string? CurrentListId => _lockedListId ??
        (ListSelect.SelectedIndex >= 0 && ListSelect.SelectedIndex < _lists.Count ? _lists[ListSelect.SelectedIndex].Id : null);

    public TasksWindow(AppConfig config) : this(config, null) { }

    public TasksWindow(AppConfig config, string? lockedListId)
    {
        InitializeComponent();
        WindowInterop.HideFromAltTab(this);
        _config = config;
        _lockedListId = lockedListId;
        if (_lockedListId != null)
        {
            ListSelect.IsEnabled = false;
            PopOutBtn.Visibility = Visibility.Collapsed;
            CreateListBtn.Visibility = Visibility.Collapsed;
        }

        ListColorBtn.ColorSelected += hex =>
        {
            var listId = CurrentListId;
            if (listId == null) return;
            TaskListColorsService.SetColor(listId, hex);
            ApplyTint();
        };

        PopulateProjectFilter();
        Loaded += async (_, _) => await ReloadAsync();
        _autoRefreshTimer.Tick += async (_, _) => { if (IsVisible) await ReloadAsync(); };
        _autoRefreshTimer.Start();
    }

    public WidgetHeaderControls Header => HeaderControls;

    private void DragBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // A pop-out window isn't tracked by anything (unlike the launcher's singleton
        // reference to the main Tasks window), so it has to actually close for real or it
        // would leak as an invisible, unreachable window - see NotesWindow's identical rule.
        if (_lockedListId != null) return;

        e.Cancel = true;
        Hide();
    }

    private void PopOut_Click(object sender, RoutedEventArgs e)
    {
        var listId = CurrentListId;
        if (listId == null) return;
        var popped = new TasksWindow(_config, listId);
        WindowSessionService.TrackNewPopout(popped, _config, "TasksPopout", listId);
        popped.Left = Left + 30;
        popped.Top = Top + 30;
        popped.Show();
    }

    /// Loads the tasklists, then the tasks of whichever one ends up selected.
    private async Task ReloadAsync(string? preferredListId = null)
    {
        if (_isBusy) return;
        _isBusy = true;
        LoadingHint.Visibility = Visibility.Visible;
        try
        {
            if (!GoogleTasksService.IsConnected())
            {
                Disconnected.Visibility = Visibility.Visible;
                return;
            }
            Disconnected.Visibility = Visibility.Collapsed;
            PopulateProjectFilter();

            var selectedId = _lockedListId ?? preferredListId ?? CurrentListId;
            _lists = await GoogleTasksService.GetTaskListsAsync(_config);

            _suppressSelectionChanged = true;
            ListSelect.Items.Clear();
            foreach (var l in _lists) ListSelect.Items.Add(l.Title);
            var restoreIndex = selectedId != null ? _lists.FindIndex(l => l.Id == selectedId) : -1;
            ListSelect.SelectedIndex = restoreIndex >= 0 ? restoreIndex : (_lists.Count > 0 ? 0 : -1);
            _suppressSelectionChanged = false;

            ApplyTint();
            await LoadTasksForCurrentListAsync();
        }
        catch (Exception ex)
        {
            ShowError("Failed to load lists", ex);
        }
        finally
        {
            _suppressSelectionChanged = false;
            _isBusy = false;
            LoadingHint.Visibility = Visibility.Collapsed;
        }
    }

    public async void Refresh() => await ReloadAsync();

    private async void RefreshLists_Click(object sender, RoutedEventArgs e) => await ReloadAsync();

    private async void CreateList_Click(object sender, RoutedEventArgs e)
    {
        var title = PromptDialog.Show(this, "New task list", "");
        if (string.IsNullOrWhiteSpace(title)) return;

        try
        {
            var created = await GoogleTasksService.CreateTaskListAsync(_config, title.Trim());
            if (created == null)
            {
                MessageBox.Show("Could not create task list. Check your Google connection.", "Error");
                return;
            }
            await ReloadAsync(created.Id);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not create task list: {ex.Message}", "Error");
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchText = SearchBox.Text.Trim();
        if (_lastRenderedListId != null && _lastRenderedTasks != null)
            RenderTasks(_lastRenderedListId, _lastRenderedTasks);
    }

    private void PopulateProjectFilter()
    {
        var selected = ProjectFilter.SelectedIndex >= 0 && ProjectFilter.SelectedIndex < _projectFilterIds.Count
            ? _projectFilterIds[ProjectFilter.SelectedIndex] : null;

        ProjectFilter.Items.Clear();
        _projectFilterIds.Clear();
        ProjectFilter.Items.Add("All projects");
        _projectFilterIds.Add(null);
        foreach (var p in ProjectsService.Load().Items)
        {
            ProjectFilter.Items.Add(p.Name);
            _projectFilterIds.Add(p.Id);
        }

        var restoreIndex = _projectFilterIds.IndexOf(selected);
        ProjectFilter.SelectedIndex = restoreIndex >= 0 ? restoreIndex : 0;
    }

    private void ProjectFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_lastRenderedListId != null && _lastRenderedTasks != null)
            RenderTasks(_lastRenderedListId, _lastRenderedTasks);
    }

    private async void ListSelect_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionChanged) return;
        ApplyTint();
        await RefreshTasksAsync();
    }

    private async Task RefreshTasksAsync()
    {
        if (_isBusy) return;
        _isBusy = true;
        try
        {
            if (!GoogleTasksService.IsConnected())
            {
                Disconnected.Visibility = Visibility.Visible;
                return;
            }
            Disconnected.Visibility = Visibility.Collapsed;
            await LoadTasksForCurrentListAsync();
        }
        catch (Exception ex)
        {
            ShowError("Failed to load tasks", ex);
        }
        finally
        {
            _isBusy = false;
        }
    }

    private async Task LoadTasksForCurrentListAsync()
    {
        var listId = CurrentListId;
        if (listId == null) return;
        var tasks = await GoogleTasksService.GetTasksAsync(_config, listId);
        RenderTasks(listId, tasks);
    }

    // Google Tasks has no per-list color field, so the tint is purely local (see
    // TaskListColorsService) - blended at low opacity into the panel's base dark color
    // rather than used at full swatch strength, which would be unreadable as a background.
    private void ApplyTint()
    {
        var listId = CurrentListId;
        var colors = TaskListColorsService.Load();
        string? hex = listId != null && colors.TryGetValue(listId, out var h) ? h : null;
        ListColorBtn.SetColor(hex);
        RootBorder.Background = TintedPanelBg(hex);
    }

    private static Brush TintedPanelBg(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return new SolidColorBrush(Color.FromRgb(0x13, 0x12, 0x17));
        var tint = (Color)ColorConverter.ConvertFromString(hex);
        const double a = 0.16;
        byte Mix(byte baseC, byte tintC) => (byte)(baseC * (1 - a) + tintC * a);
        return new SolidColorBrush(Color.FromRgb(Mix(0x13, tint.R), Mix(0x12, tint.G), Mix(0x17, tint.B)));
    }

    private void ShowError(string prefix, Exception ex)
    {
        EmptyHint.Text = $"{prefix}: {ex.GetType().Name}: {ex.Message}";
        EmptyHint.Visibility = Visibility.Visible;
    }

    private void RenderTasks(string listId, List<GoogleTaskItem> allTasks)
    {
        _lastRenderedListId = listId;
        _lastRenderedTasks = allTasks;

        // A matching child pulls its (otherwise non-matching) parent along too, so nesting
        // never breaks - but only that child, not its unrelated siblings. Same rule for the
        // project filter, applied on top of the search filter.
        var localTags = ItemTagsService.Load();
        var tasks = allTasks;
        if (!string.IsNullOrEmpty(_searchText))
        {
            var matchingIds = allTasks.Where(t => t.Title.Contains(_searchText, StringComparison.OrdinalIgnoreCase)
                                               || t.Description.Contains(_searchText, StringComparison.OrdinalIgnoreCase)
                                               || (localTags.TryGetValue($"task:{t.Id}", out var tags) &&
                                                   tags.Any(tag => tag.Contains(_searchText, StringComparison.OrdinalIgnoreCase))))
                .Select(t => t.Id).ToHashSet();
            var parentsOfMatches = allTasks.Where(t => t.ParentId != null && matchingIds.Contains(t.Id))
                .Select(t => t.ParentId!).ToHashSet();
            tasks = allTasks.Where(t => matchingIds.Contains(t.Id) || parentsOfMatches.Contains(t.Id)).ToList();
        }

        var projectFilterId = ProjectFilter.SelectedIndex >= 0 && ProjectFilter.SelectedIndex < _projectFilterIds.Count
            ? _projectFilterIds[ProjectFilter.SelectedIndex] : null;
        if (projectFilterId != null)
        {
            var projectTags = ItemProjectTagsService.Load();
            bool Tagged(GoogleTaskItem t) => projectTags.TryGetValue($"task:{t.Id}", out var pid) && pid == projectFilterId;
            var matchingIds = tasks.Where(Tagged).Select(t => t.Id).ToHashSet();
            var parentsOfMatches = tasks.Where(t => t.ParentId != null && matchingIds.Contains(t.Id))
                .Select(t => t.ParentId!).ToHashSet();
            tasks = tasks.Where(t => matchingIds.Contains(t.Id) || parentsOfMatches.Contains(t.Id)).ToList();
        }

        TasksList.Items.Clear();
        var activeCount = allTasks.Count(task => !task.Completed);
        TaskCountText.Text = allTasks.Count == 0 ? "" : $"{activeCount} active";
        EmptyHint.Text = allTasks.Count == 0 ? "This list has no tasks yet." : "No tasks found.";
        EmptyHint.Visibility = tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var topLevel = tasks.Where(t => t.ParentId == null).ToList();
        var childrenByParent = tasks.Where(t => t.ParentId != null)
            .GroupBy(t => t.ParentId!)
            .ToDictionary(g => g.Key, g => g.ToList());

        // A parent moves below the divider only when it is itself done - its subtasks travel
        // with it either way, so the nesting never gets split across the divider.
        void Emit(GoogleTaskItem task)
        {
            childrenByParent.TryGetValue(task.Id, out var kids);
            // Done subtasks sink within their own parent's block, same rule one level down.
            kids = kids?.OrderBy(k => k.Completed).ToList();
            // Collapsed by default the first time a parent is ever seen; after that the user's
            // own chevron clicks (tracked in _collapsedParents) are what decide.
            if (kids is { Count: > 0 } && _seenParents.Add(task.Id))
                _collapsedParents.Add(task.Id);
            TasksList.Items.Add(BuildRow(listId, task, indent: 0, children: kids));
            // A search always shows matching children, ignoring collapsed state - otherwise
            // a match inside a collapsed parent would be invisible.
            if (kids != null && (!string.IsNullOrEmpty(_searchText) || !_collapsedParents.Contains(task.Id)))
                foreach (var kid in kids)
                    TasksList.Items.Add(BuildRow(listId, kid, indent: 22, children: null));
        }

        foreach (var task in topLevel.Where(t => !t.Completed)) Emit(task);

        var done = topLevel.Where(t => t.Completed).ToList();
        if (done.Count > 0)
        {
            TasksList.Items.Add(BuildDoneDivider(done.Count));
            foreach (var task in done) Emit(task);
        }
    }

    private UIElement BuildDoneDivider(int count)
    {
        var grid = new Grid { Margin = new Thickness(4, 10, 4, 4), IsHitTestVisible = false };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var label = new TextBlock
        {
            Text = $"Completed ({count})",
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("MutedBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        var line = new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            VerticalAlignment = VerticalAlignment.Center,
        };

        Grid.SetColumn(label, 0);
        Grid.SetColumn(line, 1);
        grid.Children.Add(label);
        grid.Children.Add(line);
        return grid;
    }

    private UIElement BuildRow(string listId, GoogleTaskItem task, double indent, List<GoogleTaskItem>? children)
    {
        var row = new Grid { Margin = new Thickness(4 + indent, 2, 4, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        UIElement chevron;
        if (children is { Count: > 0 })
        {
            var collapsed = _collapsedParents.Contains(task.Id);
            var btn = new Button
            {
                Content = collapsed ? "\uE76C" : "\uE70D",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 9,
                Width = 20,
                Height = 26,
                Style = (Style)FindResource("IconBtnStyle"),
                Foreground = (Brush)FindResource("MutedBrush"),
            };
            btn.Click += (_, _) =>
            {
                if (!_collapsedParents.Add(task.Id)) _collapsedParents.Remove(task.Id);
                if (_lastRenderedListId != null && _lastRenderedTasks != null)
                    RenderTasks(_lastRenderedListId, _lastRenderedTasks);
            };
            chevron = btn;
        }
        else
        {
            chevron = new Border { Width = 20 };
        }

        var check = new Button
        {
            // Explicit \u escapes, not the literal private-use glyphs: pasting Segoe MDL2
            // codepoints as raw characters is how these silently became empty strings once.
            Content = task.Completed ? "\uE73A" : "\uE739",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 13,
            Width = 26,
            Height = 26,
            Style = (Style)FindResource("IconBtnStyle"),
            Foreground = task.Completed ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("MutedBrush"),
        };
        check.Click += async (_, _) =>
        {
            await GoogleTasksService.ToggleTaskAsync(_config, listId, task.Id, !task.Completed);
            await RefreshTasksAsync();
        };

        var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
        content.Children.Add(new TextBlock
        {
            Text = task.Title,
            Foreground = task.Completed ? (Brush)FindResource("MutedBrush") : Brushes.White,
            TextDecorations = task.Completed ? TextDecorations.Strikethrough : null,
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
        });
        if (!string.IsNullOrWhiteSpace(task.Description))
        {
            content.Children.Add(new TextBlock
            {
                Text = task.Description.Trim(),
                Foreground = (Brush)FindResource("MutedBrush"),
                FontSize = 10.5,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 33,
                Margin = new Thickness(0, 2, 0, 0),
            });
        }
        if (task.Due != null && !task.Completed)
        {
            var days = (task.Due.Value.Date - DateTime.Today).Days;
            var (text, overdue) = days switch
            {
                0 => ("Today", false),
                1 => ("Due in 1 day", false),
                > 1 => ($"Due in {days} days", false),
                -1 => ("Overdue by 1 day", true),
                _ => ($"Overdue by {-days} days", true),
            };
            content.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 10.5,
                Foreground = overdue ? new SolidColorBrush(Color.FromRgb(0xE5, 0x60, 0x5A)) : (Brush)FindResource("MutedBrush"),
                Margin = new Thickness(0, 1, 0, 0),
            });
        }

        var labels = ItemTagsService.Get("task", task.Id);
        if (labels.Count > 0)
        {
            var chips = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            foreach (var label in labels)
            {
                chips.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0x22, 0x4A, 0x7D, 0xFF)),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(6, 1, 6, 1),
                    Margin = new Thickness(0, 0, 4, 2),
                    Child = new TextBlock { Text = label, FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(0x9F, 0xBB, 0xFF)) },
                });
            }
            content.Children.Add(chips);
        }

        var dueBtn = new Button
        {
            Content = "\uE787",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 11,
            Width = 22,
            Height = 22,
            ToolTip = "Set due date",
            Style = (Style)FindResource("IconBtnStyle"),
        };
        dueBtn.Click += async (_, _) =>
        {
            var (changed, date) = DueDateDialog.Show(this, task.Due);
            if (!changed) return;
            await GoogleTasksService.SetDueDateAsync(_config, listId, task.Id, date);
            await RefreshTasksAsync();
        };

        var del = new Button
        {
            Content = "\uE74D",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 11,
            Width = 22,
            Height = 22,
            Style = (Style)FindResource("IconBtnStyle"),
        };
        del.Click += async (_, _) =>
        {
            if (children is { Count: > 0 })
            {
                var confirm = MessageBox.Show(
                    $"\"{task.Title}\" has {children.Count} subtask{(children.Count == 1 ? "" : "s")}. Delete the parent and all subtasks?",
                    "Confirm delete", MessageBoxButton.YesNo);
                if (confirm != MessageBoxResult.Yes) return;

                foreach (var child in children)
                    await GoogleTasksService.DeleteTaskAsync(_config, listId, child.Id);
            }
            await GoogleTasksService.DeleteTaskAsync(_config, listId, task.Id);
            await RefreshTasksAsync();
        };

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        // Google Tasks only supports one level of nesting, so a subtask itself gets no
        // "add subtask" button - only top-level rows do.
        if (indent == 0)
        {
            var addSub = new Button
            {
                Content = "\uE710",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 11,
                Width = 22,
                Height = 22,
                ToolTip = "Add subtask",
                Style = (Style)FindResource("IconBtnStyle"),
            };
            addSub.Click += async (_, _) =>
            {
                var subTitle = PromptDialog.Show(this, "New subtask", "");
                if (string.IsNullOrWhiteSpace(subTitle)) return;
                await GoogleTasksService.AddTaskAsync(_config, listId, subTitle.Trim(), task.Id);
                _collapsedParents.Remove(task.Id);
                await RefreshTasksAsync();
            };
            actions.Children.Add(addSub);
        }

        var tagBtn = new Button
        {
            Content = "\uE8A5",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 11,
            Width = 22,
            Height = 22,
            ToolTip = "Assign project",
            Style = (Style)FindResource("IconBtnStyle"),
            Foreground = ItemProjectTagsService.Get("task", task.Id) != null ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("MutedBrush"),
        };
        tagBtn.Click += (_, _) =>
        {
            var picked = ProjectPickerDialog.Show(this, ItemProjectTagsService.Get("task", task.Id));
            if (picked == null) return;
            ItemProjectTagsService.Set("task", task.Id, picked == "" ? null : picked);
            if (_lastRenderedListId != null && _lastRenderedTasks != null)
                RenderTasks(_lastRenderedListId, _lastRenderedTasks);
        };

        var labelBtn = new Button
        {
            Content = "\uE8EC",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 11,
            Width = 22,
            Height = 22,
            ToolTip = "Labels",
            Style = (Style)FindResource("IconBtnStyle"),
            Foreground = labels.Count > 0 ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("MutedBrush"),
        };
        labelBtn.Click += (_, _) =>
        {
            var picked = LabelPickerDialog.Show(this, labels);
            if (picked == null) return;
            ItemTagsService.Set("task", task.Id, picked);
            if (_lastRenderedListId != null && _lastRenderedTasks != null)
                RenderTasks(_lastRenderedListId, _lastRenderedTasks);
        };

        actions.Children.Add(tagBtn);
        actions.Children.Add(labelBtn);
        actions.Children.Add(dueBtn);
        actions.Children.Add(del);
        actions.Opacity = 0;
        row.MouseEnter += (_, _) => actions.Opacity = 1;
        row.MouseLeave += (_, _) => actions.Opacity = 0;

        Grid.SetColumn(chevron, 0);
        Grid.SetColumn(check, 1);
        Grid.SetColumn(content, 2);
        Grid.SetColumn(actions, 3);
        row.Children.Add(chevron);
        row.Children.Add(check);
        row.Children.Add(content);
        row.Children.Add(actions);
        row.Cursor = Cursors.Hand;
        row.MouseLeftButtonUp += async (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && FindAncestor<ButtonBase>(d) != null) return;

            var status = task.Completed ? "Completed" : "In progress";
            var due = task.Due == null ? "No due date" : $"Due {task.Due.Value:dd/MM/yyyy}";
            var result = ItemDetailsDialog.Show(this, task.Title, $"{status} · {due}", task.Description,
                "Open Google Tasks", "https://tasks.google.com/", canEditDescription: true);
            if (!result.DescriptionChanged) return;

            try
            {
                await GoogleTasksService.SetDescriptionAsync(_config, listId, task.Id, result.Description);
                await RefreshTasksAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not save description: {ex.Message}", "Error");
            }
        };
        return row;
    }

    private static T? FindAncestor<T>(DependencyObject d) where T : DependencyObject
    {
        while (d is not null)
        {
            if (d is T match) return match;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    private async void AddTask_Click(object sender, RoutedEventArgs e) => await AddTaskAsync();

    private async void NewTaskInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await AddTaskAsync();
    }

    private async Task AddTaskAsync()
    {
        var title = NewTaskInput.Text.Trim();
        var listId = CurrentListId;
        if (string.IsNullOrEmpty(title) || listId == null) return;
        NewTaskInput.Text = "";
        await GoogleTasksService.AddTaskAsync(_config, listId, title);
        await RefreshTasksAsync();
    }
}
