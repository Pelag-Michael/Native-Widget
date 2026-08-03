using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using NativeWidget.Models;
using NativeWidget.Services;

namespace NativeWidget;

public partial class NotesWindow : Window
{
    private static readonly SemaphoreSlim NotionSyncGate = new(1, 1);
    private const long MaxNotionAttachmentBytes = 20L * 1024 * 1024;
    private readonly Dictionary<object, string> _cardIds = new();
    private readonly HashSet<Hyperlink> _wiredHyperlinks = new();
    private string? _currentId;
    private readonly string? _openDirectlyId;
    private string _searchText = "";
    private readonly List<string?> _projectFilterIds = new();
    private readonly AppConfig _config;
    private readonly DispatcherTimer _notionSyncTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private string _editorBaselineMarkdown = "";

    public NotesWindow(AppConfig config) : this(config, null) { }

    /// Opens straight into a specific note's editor instead of the list - used to pop a
    /// note out into its own window so several notes can be open side by side at once.
    public NotesWindow(AppConfig config, string? openNoteId)
    {
        InitializeComponent();
        WindowInterop.HideFromAltTab(this);
        _config = config;
        _openDirectlyId = openNoteId;
        NoteText.AddHandler(DataObject.PastingEvent, new DataObjectPastingEventHandler(NoteText_Pasting));
        Loaded += (_, _) =>
        {
            if (_openDirectlyId != null)
            {
                OpenEditor(_openDirectlyId);
                NewNoteBtn.Visibility = Visibility.Collapsed;
                PopOutBtn.Visibility = Visibility.Collapsed;
            }
            else
            {
                PopulateProjectFilter();
                RenderList();
            }
        };

        // Only the main list window polls - a pop-out (_openDirectlyId != null) would just
        // run a redundant concurrent sync pass alongside it.
        if (_openDirectlyId == null)
        {
            _notionSyncTimer.Tick += async (_, _) => await RunNotionSyncAsync();
            _notionSyncTimer.Start();
        }
    }

    private async Task<bool> RunNotionSyncAsync(bool reportErrors = false)
    {
        Diag("tick, NotionSyncEnabled=" + _config.NotionSyncEnabled);
        if (!_config.NotionSyncEnabled) return false;
        if (reportErrors)
            await NotionSyncGate.WaitAsync();
        else if (!await NotionSyncGate.WaitAsync(0))
            return false;
        try
        {
            Diag("SyncOnceAsync starting");
            var editorId = _currentId;
            var editorWasDirty = EditorHasUnsavedChanges();
            var previousSyncedHash = editorId == null ? "" :
                NotesService.LoadIndex().FirstOrDefault(note => note.Id == editorId)?.LastSyncedHash ?? "";

            await NotionSyncService.SyncOnceAsync(_config, editorWasDirty ? editorId : null);

            // Keep a clean open editor following Notion. If the user began typing while the
            // network request was in flight, retain their document and restore the old sync
            // baseline so the next pass treats it as a real two-sided conflict.
            if (editorId != null && _currentId == editorId && !editorWasDirty)
            {
                var diskDocument = NotesService.LoadNote(editorId);
                var diskMarkdown = FlowDocumentMarkdownConverter.ToMarkdown(diskDocument);
                if (!string.Equals(diskMarkdown, _editorBaselineMarkdown, StringComparison.Ordinal))
                {
                    if (EditorHasUnsavedChanges())
                    {
                        NotesService.MarkSynced(editorId, previousSyncedHash);
                        Diag("Remote changed while local typing began; preserving both as conflict.");
                    }
                    else
                    {
                        NoteText.Document = diskDocument;
                        Linkify();
                        WireHyperlinks();
                        _editorBaselineMarkdown = CurrentEditorMarkdown();
                    }
                }
            }
            Diag("SyncOnceAsync OK");
            // A no-op if an editor is open (see Refresh's own guard) - never yanks the user
            // out of what they're typing to show a background sync's result.
            Refresh();
            return true;
        }
        catch (Exception ex)
        {
            Diag("SYNC EXCEPTION: " + ex);
            // Best-effort background sync - a transient network/API error shouldn't surface
            // as a popup on a 15s timer; it just retries next tick.
            if (reportErrors)
                MessageBox.Show(this, "Không thể đồng bộ Notion:\n" + ex.Message,
                    "Lỗi đồng bộ", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally
        {
            NotionSyncGate.Release();
        }
    }

    private static void Diag(string msg)
    {
        try
        {
            File.AppendAllText(AppConfig.TokenPath("settings-diag.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}");
        }
        catch { }
    }

    private void DragBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    public WidgetHeaderControls Header => HeaderControls;

    // Only re-renders the list view - a no-op while an editor is open, so a background
    // quick-add (see MainWindow's launcher right-click) can't yank the user out of it.
    public void Refresh()
    {
        if (_currentId == null && _openDirectlyId == null) RenderList();
    }



    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_currentId != null)
        {
            Linkify();
            NotesService.SaveNote(_currentId, NoteText.Document);
            _editorBaselineMarkdown = CurrentEditorMarkdown();
        }

        // The main Notes window (owned by the launcher's singleton reference) just hides so
        // it can be reopened instantly. A pop-out window isn't tracked by anything, so it
        // has to actually close for real or it would leak as an invisible, unreachable window.
        if (_openDirectlyId != null) return;

        e.Cancel = true;
        Hide();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchText = SearchBox.Text.Trim();
        RenderList();
    }

    private void PopulateProjectFilter()
    {
        var selected = ProjectFilter.SelectedIndex >= 0 && ProjectFilter.SelectedIndex < _projectFilterIds.Count
            ? _projectFilterIds[ProjectFilter.SelectedIndex] : null;

        ProjectFilter.Items.Clear();
        _projectFilterIds.Clear();
        ProjectFilter.Items.Add("Tất cả dự án");
        _projectFilterIds.Add(null);
        foreach (var p in ProjectsService.Load().Items)
        {
            ProjectFilter.Items.Add(p.Name);
            _projectFilterIds.Add(p.Id);
        }

        var restoreIndex = _projectFilterIds.IndexOf(selected);
        ProjectFilter.SelectedIndex = restoreIndex >= 0 ? restoreIndex : 0;
    }

    private void ProjectFilter_Changed(object sender, SelectionChangedEventArgs e) => RenderList();

    private void PopulateTagFilter(List<NoteMeta> allNotes)
    {
        var selected = TagFilter.SelectedIndex > 0 ? TagFilter.SelectedItem as string : null;

        TagFilter.SelectionChanged -= TagFilter_Changed;
        TagFilter.Items.Clear();
        TagFilter.Items.Add("Tất cả nhãn");
        foreach (var tag in allNotes.SelectMany(n => n.Tags).Distinct().OrderBy(t => t))
            TagFilter.Items.Add(tag);

        var restoreIndex = selected != null ? TagFilter.Items.IndexOf(selected) : -1;
        TagFilter.SelectedIndex = restoreIndex >= 0 ? restoreIndex : 0;
        TagFilter.SelectionChanged += TagFilter_Changed;
    }

    private void TagFilter_Changed(object sender, SelectionChangedEventArgs e) => RenderList();

    // ---- List of notes ----
    private void RenderList()
    {
        var allNotes = NotesService.LoadIndex();
        PopulateTagFilter(allNotes);

        var notes = string.IsNullOrEmpty(_searchText)
            ? allNotes
            : allNotes.Where(n => n.Title.Contains(_searchText, StringComparison.OrdinalIgnoreCase)
                                || n.Preview.Contains(_searchText, StringComparison.OrdinalIgnoreCase)).ToList();

        var projectFilterId = ProjectFilter.SelectedIndex >= 0 && ProjectFilter.SelectedIndex < _projectFilterIds.Count
            ? _projectFilterIds[ProjectFilter.SelectedIndex] : null;
        if (projectFilterId != null)
        {
            var projectTags = ItemProjectTagsService.Load();
            notes = notes.Where(n => projectTags.TryGetValue($"note:{n.Id}", out var pid) && pid == projectFilterId).ToList();
        }

        if (TagFilter.SelectedIndex > 0 && TagFilter.SelectedItem is string selectedTag)
            notes = notes.Where(n => n.Tags.Contains(selectedTag)).ToList();

        NotesList.Items.Clear();
        _cardIds.Clear();
        EmptyHint.Text = allNotes.Count == 0 ? "Chưa có ghi chú nào. Bấm + để tạo." : "Không tìm thấy ghi chú nào.";
        EmptyHint.Visibility = notes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var note in notes)
        {
            var card = new Grid { Cursor = Cursors.Hand, Margin = new Thickness(2, 4, 2, 4) };
            card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var bar = new Border
            {
                Width = 3,
                CornerRadius = new CornerRadius(2),
                Background = ColorTags.Resolve(note.Color, new SolidColorBrush(Color.FromRgb(0x45, 0x45, 0x4E))),
                Margin = new Thickness(2, 2, 10, 2),
            };

            var text = new StackPanel();
            var title = new TextBlock
            {
                Text = Regex.Replace(note.Title, @"\s+", " ").Trim(),
                Foreground = Brushes.White,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            text.Children.Add(title);
            if (!string.IsNullOrWhiteSpace(note.Preview))
            {
                text.Children.Add(new TextBlock
                {
                    Text = Regex.Replace(note.Preview, @"\s+", " ").Trim(),
                    Foreground = new SolidColorBrush(Color.FromRgb(0x9B, 0x9B, 0xA6)),
                    FontSize = 11,
                    TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 2, 0, 0),
                });
            }
            if (note.Tags.Count > 0)
            {
                var tagsPanel = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
                foreach (var t in note.Tags)
                {
                    tagsPanel.Children.Add(new Border
                    {
                        Background = new SolidColorBrush(Color.FromArgb(0x22, 0x4A, 0x7D, 0xFF)),
                        CornerRadius = new CornerRadius(8),
                        Padding = new Thickness(6, 1, 6, 1),
                        Margin = new Thickness(0, 0, 4, 4),
                        Child = new TextBlock { Text = t, FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(0x9F, 0xBB, 0xFF)) },
                    });
                }
                text.Children.Add(tagsPanel);
            }
            if (note.ReminderTimerId != null)
            {
                text.Children.Add(new TextBlock
                {
                    Text = "\uE916 Đang nhắc",
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 10,
                    Foreground = (Brush)FindResource("AccentBrush"),
                    Margin = new Thickness(0, 4, 0, 0),
                });
            }

            var colorBtn = new ColorTagButton { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6, 0, 0, 0) };
            colorBtn.SetColor(note.Color);
            colorBtn.ColorSelected += hex => { NotesService.SetColor(note.Id, hex); RenderList(); };

            var projectBtn = new Button
            {
                Content = "\uE8A5",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 11,
                Width = 22,
                Height = 22,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(4, 0, 0, 0),
                ToolTip = "Gắn dự án",
                Style = (Style)FindResource("IconBtnStyle"),
                Foreground = ItemProjectTagsService.Get("note", note.Id) != null ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("MutedBrush"),
            };
            var noteId = note.Id;
            projectBtn.Click += (_, _) =>
            {
                var picked = ProjectPickerDialog.Show(this, ItemProjectTagsService.Get("note", noteId));
                if (picked == null) return;
                ItemProjectTagsService.Set("note", noteId, picked == "" ? null : picked);
                RenderList();
            };

            var labelBtn = new Button
            {
                Content = "\uE8EC",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 11,
                Width = 22,
                Height = 22,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(4, 0, 0, 0),
                ToolTip = "Nhãn",
                Style = (Style)FindResource("IconBtnStyle"),
                Foreground = note.Tags.Count > 0 ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("MutedBrush"),
            };
            labelBtn.Click += (_, _) =>
            {
                var current = string.Join(", ", note.Tags);
                var input = PromptDialog.Show(this, "Nhãn (cách nhau bởi dấu phẩy)", current);
                if (input == null) return;
                var tags = input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
                NotesService.SetTags(noteId, tags);
                RenderList();
            };

            var reminderBtn = new Button
            {
                Content = "\uE916",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 11,
                Width = 22,
                Height = 22,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(4, 0, 0, 0),
                ToolTip = "Nhắc nhở",
                Style = (Style)FindResource("IconBtnStyle"),
                Foreground = note.ReminderTimerId != null ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("MutedBrush"),
            };
            var reminderTimerId = note.ReminderTimerId;
            var noteTitle = note.Title;
            reminderBtn.Click += (_, _) =>
            {
                var (changed, duration) = ReminderDialog.Show(this, reminderTimerId != null);
                if (!changed) return;
                if (reminderTimerId != null) TimersService.Delete(reminderTimerId);
                if (duration == null)
                {
                    NotesService.SetReminderTimerId(noteId, null);
                }
                else
                {
                    var timer = TimersService.Add($"📝 {noteTitle}", duration.Value);
                    NotesService.SetReminderTimerId(noteId, timer.Id);
                }
                RenderList();
            };

            Grid.SetColumn(bar, 0);
            Grid.SetColumn(text, 1);
            Grid.SetColumn(colorBtn, 2);
            Grid.SetColumn(projectBtn, 3);
            Grid.SetColumn(labelBtn, 4);
            Grid.SetColumn(reminderBtn, 5);
            card.Children.Add(bar);
            card.Children.Add(text);
            card.Children.Add(colorBtn);
            card.Children.Add(projectBtn);
            card.Children.Add(labelBtn);
            card.Children.Add(reminderBtn);

            _cardIds[card] = note.Id;
            NotesList.Items.Add(card);
        }
    }

    private void NotesList_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (NotesList.SelectedItem != null && _cardIds.TryGetValue(NotesList.SelectedItem, out var id))
            OpenEditor(id);
    }

    private void NewNote_Click(object sender, RoutedEventArgs e)
    {
        var id = NotesService.CreateNote();
        OpenEditor(id);
    }

    private void OpenEditor(string id)
    {
        _currentId = id;
        NoteText.Document = NotesService.LoadNote(id);
        Linkify();
        WireHyperlinks();
        _editorBaselineMarkdown = CurrentEditorMarkdown();

        ListPanel.Visibility = Visibility.Collapsed;
        EditorPanel.Visibility = Visibility.Visible;
        NewNoteBtn.Visibility = Visibility.Collapsed;
        BackBtn.Visibility = Visibility.Visible;
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_currentId != null)
        {
            Linkify();
            NotesService.SaveNote(_currentId, NoteText.Document);
            _editorBaselineMarkdown = CurrentEditorMarkdown();
        }

        // A window opened straight into one note (a "pop-out") has no list to go back to -
        // its Back button just closes it.
        if (_openDirectlyId != null)
        {
            Close();
            return;
        }

        _currentId = null;
        RenderList();
        EditorPanel.Visibility = Visibility.Collapsed;
        ListPanel.Visibility = Visibility.Visible;
        NewNoteBtn.Visibility = Visibility.Visible;
        BackBtn.Visibility = Visibility.Collapsed;
    }

    private void PopOut_Click(object sender, RoutedEventArgs e)
    {
        if (_currentId == null) return;
        Linkify();
        NotesService.SaveNote(_currentId, NoteText.Document);
        _editorBaselineMarkdown = CurrentEditorMarkdown();

        var popped = new NotesWindow(_config, _currentId);
        popped.Left = Left + 30;
        popped.Top = Top + 30;
        popped.Show();
    }

    // ---- Formatting (applies to the currently highlighted text) ----
    private void FontFamilyCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (NoteText == null || NoteText.Selection.IsEmpty) return;
        var tag = (string)((ComboBoxItem)FontFamilyCombo.SelectedItem).Tag;
        NoteText.Selection.ApplyPropertyValue(TextElement.FontFamilyProperty, new FontFamily(tag));
    }

    private void FontSizeCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (NoteText == null || NoteText.Selection.IsEmpty) return;
        var size = double.Parse((string)((ComboBoxItem)FontSizeCombo.SelectedItem).Content);
        NoteText.Selection.ApplyPropertyValue(TextElement.FontSizeProperty, size);
    }

    private void Bold_Click(object sender, RoutedEventArgs e)
    {
        if (NoteText.Selection.IsEmpty) return;
        EditingCommands.ToggleBold.Execute(null, NoteText);
    }

    private void Italic_Click(object sender, RoutedEventArgs e)
    {
        if (NoteText.Selection.IsEmpty) return;
        EditingCommands.ToggleItalic.Execute(null, NoteText);
    }

    private void Strike_Click(object sender, RoutedEventArgs e)
    {
        if (NoteText.Selection.IsEmpty) return;
        var current = NoteText.Selection.GetPropertyValue(Inline.TextDecorationsProperty);
        var isStruck = current == TextDecorations.Strikethrough;
        NoteText.Selection.ApplyPropertyValue(Inline.TextDecorationsProperty,
            isStruck ? null : TextDecorations.Strikethrough);
    }

    private IEnumerable<Paragraph> SelectedParagraphs()
    {
        var start = NoteText.Selection.Start.IsAtLineStartPosition
            ? NoteText.Selection.Start
            : NoteText.Selection.Start.GetLineStartPosition(0) ?? NoteText.Selection.Start;
        var end = NoteText.Selection.End;
        var seen = new HashSet<Paragraph>();
        for (var position = start; position != null && position.CompareTo(end) <= 0;
             position = position.GetNextContextPosition(LogicalDirection.Forward))
        {
            if (position.Paragraph is Paragraph paragraph && seen.Add(paragraph))
                yield return paragraph;
        }

        if (seen.Count == 0 && NoteText.CaretPosition.Paragraph is Paragraph current)
            yield return current;
    }

    private void ApplyParagraphStyle(string style)
    {
        foreach (var paragraph in SelectedParagraphs().ToList())
        {
            var remove = string.Equals(paragraph.Tag as string, style, StringComparison.Ordinal);
            paragraph.Tag = remove ? null : style;
            paragraph.FontSize = remove ? 12 : style == "heading1" ? 24 : style == "heading2" ? 18 : 12;
            paragraph.FontWeight = !remove && style.StartsWith("heading", StringComparison.Ordinal)
                ? FontWeights.Bold : FontWeights.Normal;
            paragraph.FontFamily = !remove && style == "code"
                ? new FontFamily("Consolas") : new FontFamily("Segoe UI");
            paragraph.Margin = !remove && style == "quote"
                ? new Thickness(14, 4, 0, 4) : new Thickness(0);
            paragraph.Background = !remove && style == "code"
                ? new SolidColorBrush(Color.FromArgb(0x44, 0x00, 0x00, 0x00)) : Brushes.Transparent;
        }
        NoteText.Focus();
    }

    private void Heading1_Click(object sender, RoutedEventArgs e) => ApplyParagraphStyle("heading1");
    private void Heading2_Click(object sender, RoutedEventArgs e) => ApplyParagraphStyle("heading2");
    private void Quote_Click(object sender, RoutedEventArgs e) => ApplyParagraphStyle("quote");
    private void Code_Click(object sender, RoutedEventArgs e) => ApplyParagraphStyle("code");

    private void BulletList_Click(object sender, RoutedEventArgs e)
    {
        EditingCommands.ToggleBullets.Execute(null, NoteText);
        NoteText.Focus();
    }

    private void NumberList_Click(object sender, RoutedEventArgs e)
    {
        EditingCommands.ToggleNumbering.Execute(null, NoteText);
        NoteText.Focus();
    }

    private void Todo_Click(object sender, RoutedEventArgs e)
    {
        foreach (var paragraph in SelectedParagraphs().ToList())
        {
            var existing = paragraph.Inlines.OfType<InlineUIContainer>()
                .FirstOrDefault(i => i.Child is CheckBox && string.Equals(paragraph.Tag as string, "todo", StringComparison.Ordinal));
            if (existing != null)
            {
                paragraph.Inlines.Remove(existing);
                paragraph.Tag = null;
                continue;
            }

            paragraph.FontSize = 12;
            paragraph.FontWeight = FontWeights.Normal;
            paragraph.FontFamily = new FontFamily("Segoe UI");
            paragraph.Margin = new Thickness(0);
            paragraph.Background = Brushes.Transparent;

            var checkbox = new CheckBox
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
                IsChecked = false,
            };
            var container = new InlineUIContainer(checkbox);
            if (paragraph.Inlines.FirstInline == null) paragraph.Inlines.Add(container);
            else paragraph.Inlines.InsertBefore(paragraph.Inlines.FirstInline, container);
            paragraph.Tag = "todo";
        }
        NoteText.Focus();
    }

    private void NoteText_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S)
        {
            Save_Click(sender, e);
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.None && e.Key is Key.Space or Key.Enter)
            Dispatcher.BeginInvoke(LinkifyPreservingCaret, DispatcherPriority.Background);

        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt) && e.Key is Key.D1 or Key.NumPad1)
        {
            ApplyParagraphStyle("heading1");
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt) && e.Key is Key.D2 or Key.NumPad2)
        {
            ApplyParagraphStyle("heading2");
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.D8)
        {
            BulletList_Click(sender, e);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.D7)
        {
            NumberList_Click(sender, e);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.D9)
        {
            Todo_Click(sender, e);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.Q)
        {
            Quote_Click(sender, e);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.C)
        {
            Code_Click(sender, e);
            e.Handled = true;
        }
    }

    // ---- Toolbar actions ----
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_currentId == null) return;
        Linkify();
        NotesService.SaveNote(_currentId, NoteText.Document);
        _editorBaselineMarkdown = CurrentEditorMarkdown();

        if (!_config.NotionSyncEnabled)
        {
            SaveBtn.Content = "\uE74E";
            SaveBtn.ToolTip = "Đã lưu local; đồng bộ Notion đang tắt (Ctrl+S)";
            return;
        }

        SaveBtn.IsEnabled = false;
        SaveBtn.Content = "\uE895";
        SaveBtn.ToolTip = "Đang lưu và đồng bộ Notion…";
        try
        {
            var synced = await RunNotionSyncAsync(reportErrors: true);
            SaveBtn.Content = synced ? "\uE73E" : "\uEA39";
            SaveBtn.ToolTip = synced
                ? $"Đã lưu và đồng bộ lúc {DateTime.Now:HH:mm:ss} (Ctrl+S)"
                : "Đã lưu local nhưng đồng bộ Notion thất bại (Ctrl+S)";
        }
        finally
        {
            SaveBtn.IsEnabled = true;
        }
    }

    private string CurrentEditorMarkdown() =>
        FlowDocumentMarkdownConverter.ToMarkdown(NoteText.Document);

    private bool EditorHasUnsavedChanges() => _currentId != null &&
        !string.Equals(CurrentEditorMarkdown(), _editorBaselineMarkdown, StringComparison.Ordinal);

    private void LinkifyPreservingCaret()
    {
        var characterOffset = new TextRange(NoteText.Document.ContentStart, NoteText.CaretPosition).Text.Length;
        Linkify();
        NoteText.CaretPosition = TextPositionAtOffset(characterOffset);
    }

    private TextPointer TextPositionAtOffset(int characterOffset)
    {
        var position = NoteText.Document.ContentStart;
        while (position != null && position.CompareTo(NoteText.Document.ContentEnd) < 0)
        {
            if (position.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
            {
                var text = position.GetTextInRun(LogicalDirection.Forward);
                if (characterOffset <= text.Length)
                    return position.GetPositionAtOffset(characterOffset, LogicalDirection.Forward) ?? position;
                characterOffset -= text.Length;
            }
            position = position.GetNextContextPosition(LogicalDirection.Forward);
        }
        return NoteText.Document.ContentEnd;
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (_currentId == null) return;
        var name = PromptDialog.Show(this, "Đổi tên ghi chú", NotesService.GetTitle(_currentId));
        if (string.IsNullOrWhiteSpace(name)) return;
        NotesService.RenameNote(_currentId, name);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var text = new TextRange(NoteText.Document.ContentStart, NoteText.Document.ContentEnd).Text;
        if (!string.IsNullOrEmpty(text)) Clipboard.SetText(text);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_currentId == null) return;
        if (MessageBox.Show("Xoá ghi chú này?", "Xác nhận", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;

        NotesService.DeleteNote(_currentId);
        _currentId = null;
        RenderList();
        EditorPanel.Visibility = Visibility.Collapsed;
        ListPanel.Visibility = Visibility.Visible;
        NewNoteBtn.Visibility = Visibility.Visible;
        BackBtn.Visibility = Visibility.Collapsed;
    }

    // Turns http(s), www and bare domains into clickable hyperlinks on open/save. Only
    // top-level Runs are split, so unrelated formatting and existing attachment links stay intact.
    private void Linkify()
    {
        foreach (var block in NoteText.Document.Blocks.ToList())
        {
            if (block is not Paragraph para) continue;
            foreach (var run in para.Inlines.OfType<Run>().ToList())
            {
                var text = run.Text;
                var matches = LinkDetection.Find(text);
                if (matches.Count == 0) continue;
                var lastIndex = 0;
                foreach (var match in matches)
                {
                    if (match.Index > lastIndex)
                        para.Inlines.InsertBefore(run, StyledRun(run, text[lastIndex..match.Index]));
                    var link = new Hyperlink(StyledRun(run, match.Text)) { NavigateUri = match.Target };
                    WireHyperlink(link);
                    para.Inlines.InsertBefore(run, link);
                    lastIndex = match.Index + match.Length;
                }
                if (lastIndex < text.Length)
                    para.Inlines.InsertBefore(run, StyledRun(run, text[lastIndex..]));
                para.Inlines.Remove(run);
            }
        }
    }

    private static Run StyledRun(Run source, string text) => new(text)
    {
        FontFamily = source.FontFamily,
        FontSize = source.FontSize,
        FontStyle = source.FontStyle,
        FontWeight = source.FontWeight,
        Foreground = source.Foreground,
        TextDecorations = source.TextDecorations,
    };

    private void WireHyperlink(Hyperlink link)
    {
        if (!_wiredHyperlinks.Add(link)) return;
        link.Cursor = Cursors.Hand;
        link.ToolTip ??= link.NavigateUri?.ToString();
        link.RequestNavigate += (_, args) =>
        {
            OpenLink(args.Uri);
            args.Handled = true;
        };
    }

    private void NoteText_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var position = NoteText.GetPositionFromPoint(e.GetPosition(NoteText), snapToText: false);
        DependencyObject? current = position?.Parent;
        while (current != null && current != NoteText)
        {
            if (current is Hyperlink { NavigateUri: not null } link)
            {
                OpenLink(link.NavigateUri);
                e.Handled = true;
                return;
            }
            current = current is FrameworkContentElement content
                ? content.Parent : LogicalTreeHelper.GetParent(current);
        }
    }

    private static void OpenLink(Uri uri) =>
        Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });

    private void WireHyperlinks()
    {
        foreach (var paragraph in NoteText.Document.Blocks.OfType<Paragraph>())
            foreach (var link in paragraph.Inlines.OfType<Hyperlink>())
                WireHyperlink(link);
    }

    private void AttachFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Đính kèm file",
            Filter = "Tất cả file (*.*)|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) == true) AttachFiles(dialog.FileNames);
    }

    private void NoteText_PreviewDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void NoteText_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        AttachFiles(paths);
        e.Handled = true;
    }

    private void AttachFiles(IEnumerable<string> paths)
    {
        if (_currentId == null) return;
        var oversized = new List<string>();
        foreach (var sourcePath in paths.Where(File.Exists))
        {
            var info = new FileInfo(sourcePath);
            if (info.Length > MaxNotionAttachmentBytes)
            {
                oversized.Add(info.Name);
                continue;
            }

            var folder = Path.Combine(AppConfig.TokenPath("notes"), "attachments", _currentId);
            Directory.CreateDirectory(folder);
            var targetPath = Path.Combine(folder, $"{Guid.NewGuid():N}{Path.GetExtension(sourcePath)}");
            File.Copy(sourcePath, targetPath, overwrite: false);
            var label = "📎 " + Path.GetFileName(sourcePath).Replace(']', ')');
            var link = new Hyperlink(new Run(label), NoteText.CaretPosition)
            {
                NavigateUri = new Uri(targetPath),
                ToolTip = targetPath,
            };
            WireHyperlink(link);
            NoteText.CaretPosition = link.ElementEnd;
            new LineBreak(NoteText.CaretPosition);
        }
        NoteText.Focus();
        if (oversized.Count > 0)
            MessageBox.Show("Notion chỉ nhận file tối đa 20 MB:\n" + string.Join("\n", oversized),
                "Không thể đính kèm", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    // ---- Pasting images ----
    // Clipboard.GetImage() returns an InteropBitmap that XamlWriter can't serialize, so the
    // pasted image is saved to disk as a real PNG file and the note embeds an <Image> that
    // points at it by URI - that round-trips through Save/Load like any other inline.
    private void NoteText_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.SourceDataObject.GetData(DataFormats.FileDrop) is string[] paths)
        {
            AttachFiles(paths);
            e.CancelCommand();
            return;
        }
        if (!e.SourceDataObject.GetDataPresent(DataFormats.Bitmap)) return;
        if (e.SourceDataObject.GetData(DataFormats.Bitmap) is not BitmapSource bitmap) return;

        var folder = Path.Combine(AppConfig.TokenPath("notes"), "images");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{Guid.NewGuid():N}.png");

        using (var fs = File.Create(path))
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            encoder.Save(fs);
        }

        var image = new System.Windows.Controls.Image
        {
            Source = new BitmapImage(new Uri(path)),
            MaxWidth = 260,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 4, 0, 4),
        };
        new InlineUIContainer(image, NoteText.CaretPosition);

        e.CancelCommand();
    }
}
