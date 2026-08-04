using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NativeWidget.Services;

namespace NativeWidget;

public partial class ProjectsWindow : Window
{
    private readonly Dictionary<object, string> _rowIds = new();

    public ProjectsWindow()
    {
        InitializeComponent();
        WindowInterop.HideFromAltTab(this);
        CurrentColorBtn.ColorSelected += hex =>
        {
            var data = ProjectsService.Load();
            if (data.CurrentId != null) ProjectsService.SetColor(data.CurrentId, hex);
            Render();
        };
        Loaded += (_, _) => Render();
    }

    public WidgetHeaderControls Header => HeaderControls;

    private void DragBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    public void Render()
    {
        var data = ProjectsService.Load();
        var current = data.Items.FirstOrDefault(p => p.Id == data.CurrentId);
        var others = data.Items.Where(p => p.Id != data.CurrentId).ToList();

        // ---- Current project card ----
        if (current == null)
        {
            CurrentName.Text = "No project selected";
            CurrentName.Foreground = (Brush)FindResource("MutedBrush");
            CurrentFolder.Visibility = Visibility.Collapsed;
            CurrentNote.Visibility = Visibility.Collapsed;
            CurrentColorBtn.Visibility = Visibility.Collapsed;
            CurrentEditBtn.Visibility = Visibility.Collapsed;
            CurrentBar.Background = (Brush)FindResource("MutedBrush");
        }
        else
        {
            CurrentName.Text = current.Name;
            CurrentName.Foreground = Brushes.White;
            CurrentColorBtn.Visibility = Visibility.Visible;
            CurrentEditBtn.Visibility = Visibility.Visible;
            CurrentColorBtn.SetColor(current.Color);

            var accent = ColorTags.Resolve(current.Color, (Brush)FindResource("AccentBrush"));
            CurrentBar.Background = accent;

            if (!string.IsNullOrWhiteSpace(current.FolderPath))
            {
                CurrentFolder.Text = "📁 " + current.FolderPath;
                CurrentFolder.Visibility = Visibility.Visible;
            }
            else
            {
                CurrentFolder.Visibility = Visibility.Collapsed;
            }

            if (!string.IsNullOrWhiteSpace(current.Note))
            {
                CurrentNote.Text = current.Note;
                CurrentNote.Visibility = Visibility.Visible;
            }
            else
            {
                CurrentNote.Visibility = Visibility.Collapsed;
            }
        }

        // ---- Other projects ----
        ProjectsList.Items.Clear();
        _rowIds.Clear();
        EmptyHint.Visibility = others.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var project in others)
        {
            var row = new Grid { Margin = new Thickness(4, 4, 4, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var bar = new Border
            {
                Width = 3,
                CornerRadius = new CornerRadius(2),
                Background = ColorTags.Resolve(project.Color, new SolidColorBrush(Color.FromRgb(0x45, 0x45, 0x4E))),
                Margin = new Thickness(2, 2, 10, 2),
            };

            var text = new StackPanel { Cursor = Cursors.Hand };
            var name = new TextBlock { Text = project.Name, Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            text.Children.Add(name);
            if (!string.IsNullOrWhiteSpace(project.FolderPath))
            {
                text.Children.Add(new TextBlock
                {
                    Text = "📁 " + project.FolderPath,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x9B, 0x9B, 0xA6)),
                    FontSize = 10.5,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0),
                });
            }
            text.MouseLeftButtonUp += (_, _) => { ProjectsService.SetCurrent(project.Id); Render(); };

            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
            var editBtn = new Button { Content = "\uE70F", Style = (Style)FindResource("IconBtnStyle"), Width = 22, Height = 22, FontSize = 11, ToolTip = "Edit" };
            editBtn.Click += (_, _) => EditProject(project.Id);
            var delBtn = new Button { Content = "\uE74D", Style = (Style)FindResource("IconBtnStyle"), Width = 22, Height = 22, FontSize = 11, ToolTip = "Delete" };
            delBtn.Click += (_, _) =>
            {
                if (MessageBox.Show($"Delete project \"{project.Name}\"?", "Confirm", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                {
                    ProjectsService.Delete(project.Id);
                    Render();
                }
            };
            actions.Children.Add(editBtn);
            actions.Children.Add(delBtn);

            Grid.SetColumn(bar, 0);
            Grid.SetColumn(text, 1);
            Grid.SetColumn(actions, 2);
            row.Children.Add(bar);
            row.Children.Add(text);
            row.Children.Add(actions);

            ProjectsList.Items.Add(row);
        }
    }

    /// A project result in the global finder is an intentional focus change: making it the
    /// current project gives all later note/task assignments the same clear context.
    public void OpenProjectFromSearch(string id)
    {
        if (!ProjectsService.Load().Items.Any(project => project.Id == id)) return;
        ProjectsService.SetCurrent(id);
        Render();
    }

    private void CurrentFolder_Click(object sender, MouseButtonEventArgs e)
    {
        var data = ProjectsService.Load();
        var current = data.Items.FirstOrDefault(p => p.Id == data.CurrentId);
        OpenFolder(current?.FolderPath);
    }

    private static void OpenFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Directory.Exists(path))
        {
            MessageBox.Show("This folder could not be found.", "Error");
            return;
        }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void AddBtn_Click(object sender, RoutedEventArgs e)
    {
        var result = ProjectEditDialog.Show(this, "New project");
        if (result == null) return;
        var id = ProjectsService.Add(result.Value.Name, result.Value.Folder, result.Value.Note);

        var data = ProjectsService.Load();
        if (data.Items.Count == 1) ProjectsService.SetCurrent(id);
        Render();
    }

    private void EditCurrent_Click(object sender, RoutedEventArgs e)
    {
        var data = ProjectsService.Load();
        if (data.CurrentId != null) EditProject(data.CurrentId);
    }

    private void EditProject(string id)
    {
        var data = ProjectsService.Load();
        var project = data.Items.FirstOrDefault(p => p.Id == id);
        if (project == null) return;

        var result = ProjectEditDialog.Show(this, "Edit project", project.Name, project.FolderPath, project.Note);
        if (result == null) return;
        ProjectsService.Update(id, result.Value.Name, result.Value.Folder, result.Value.Note);
        Render();
    }
}
