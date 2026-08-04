using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using NativeWidget.Models;
using NativeWidget.Services;

namespace NativeWidget;

public partial class TranslationWindow : Window
{
    private readonly AppConfig _config;
    private readonly GlobalSelectionService _selectionService;
    private TranslationResultPopup? _resultPopup;
    private bool _initializing = true;
    private bool _translating;
    private bool _updatingFilters;
    private bool _vocabularyExpanded;
    private double _expandedHeight = 590;
    private bool _panelExpanded;
    private int _interactionDepth;
    private readonly DispatcherTimer _collapseTimer = new() { Interval = TimeSpan.FromMilliseconds(550) };
    private (string Text, string Method, string SourceApp)? _queuedTranslation;

    public TranslationWindow(AppConfig config)
    {
        InitializeComponent();
        WindowInterop.HideFromAltTab(this);
        _config = config;
        _selectionService = new GlobalSelectionService(Dispatcher);
        _selectionService.TextCaptured += captured => TranslateAsync(captured.Text, "selection", captured.SourceApp);

        SourceLanguageSelect.ItemsSource = TranslationLanguages.All;
        SourceLanguageSelect.DisplayMemberPath = nameof(TranslationLanguage.Name);
        TargetLanguageSelect.ItemsSource = TranslationLanguages.All.Where(language => language.Code != "auto").ToList();
        TargetLanguageSelect.DisplayMemberPath = nameof(TranslationLanguage.Name);
        SourceLanguageSelect.SelectedItem = TranslationLanguages.All.FirstOrDefault(language => language.Code == _config.TranslationSourceLanguage)
                                            ?? TranslationLanguages.All[0];
        TargetLanguageSelect.SelectedItem = TranslationLanguages.All.FirstOrDefault(language => language.Code == _config.TranslationTargetLanguage && language.Code != "auto")
                                            ?? TranslationLanguages.All.First(language => language.Code == "vi");
        TrackingCheck.IsChecked = _config.TranslationSelectionTrackingEnabled;
        _initializing = false;

        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            if (ShouldKeepPanelOpen())
            {
                if (!IsMouseOver) _collapseTimer.Start();
                return;
            }
            SetPanelExpanded(false);
        };

        Loaded += (_, _) => { UpdateTracking(); RenderVocabulary(); };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                UpdateTracking();
                SetPanelExpanded(false, animate: false);
            }
            else
            {
                _collapseTimer.Stop();
                _selectionService.Stop();
                _resultPopup?.Close();
            }
        };
    }

    public WidgetHeaderControls Header => HeaderControls;
    public void Refresh() => RenderVocabulary();

    private string SourceCode => (SourceLanguageSelect.SelectedItem as TranslationLanguage)?.Code ?? "auto";
    private string TargetCode => (TargetLanguageSelect.SelectedItem as TranslationLanguage)?.Code ?? "vi";

    private void DragBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Window_MouseEnter(object sender, MouseEventArgs e) => SetPanelExpanded(true);

    private void Window_MouseLeave(object sender, MouseEventArgs e)
    {
        _collapseTimer.Stop();
        _collapseTimer.Start();
    }

    public void SetPanelExpanded(bool expanded, bool animate = true)
    {
        _collapseTimer.Stop();
        if (!expanded && _panelExpanded && _vocabularyExpanded && ActualHeight > 420)
            _expandedHeight = ActualHeight;
        _panelExpanded = expanded;
        var target = expanded ? DesiredPanelHeight : 64;
        if (!animate)
        {
            BeginAnimation(HeightProperty, null);
            Height = target;
            return;
        }

        var animation = new DoubleAnimation
        {
            From = ActualHeight > 0 ? ActualHeight : Height,
            To = target,
            Duration = TimeSpan.FromMilliseconds(expanded ? 210 : 180),
            EasingFunction = new CubicEase
            {
                EasingMode = expanded ? EasingMode.EaseOut : EasingMode.EaseIn,
            },
        };
        animation.Completed += (_, _) =>
        {
            BeginAnimation(HeightProperty, null);
            Height = target;
        };
        BeginAnimation(HeightProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private double DesiredPanelHeight => _vocabularyExpanded ? Math.Max(500, _expandedHeight) : 370;

    private bool ShouldKeepPanelOpen() => IsMouseOver || _translating || _interactionDepth > 0 ||
        _resultPopup?.IsVisible == true || ManualTextInput.IsKeyboardFocusWithin ||
        VocabularySearch.IsKeyboardFocusWithin || HeaderControls.HasOpenPopup ||
        SourceLanguageSelect.IsDropDownOpen || TargetLanguageSelect.IsDropDownOpen ||
        LanguagePairFilter.IsDropDownOpen || CaptureMethodFilter.IsDropDownOpen ||
        SourceAppFilter.IsDropDownOpen || VocabularyTagFilter.IsDropDownOpen;

    private void ScheduleCollapseIfIdle()
    {
        if (IsMouseOver) return;
        _collapseTimer.Stop();
        _collapseTimer.Start();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    private void TrackingCheck_Click(object sender, RoutedEventArgs e)
    {
        _config.TranslationSelectionTrackingEnabled = TrackingCheck.IsChecked == true;
        _config.Save();
        UpdateTracking();
    }

    private void UpdateTracking()
    {
        if (IsVisible && TrackingCheck.IsChecked == true)
        {
            _selectionService.Start();
            TrackingState.Text = _selectionService.IsRunning ? "Đang bật" : "Không khởi động được";
            TrackingState.Foreground = _selectionService.IsRunning
                ? new SolidColorBrush(Color.FromRgb(0x8F, 0xE0, 0xA8))
                : new SolidColorBrush(Color.FromRgb(0xE5, 0x60, 0x5A));
        }
        else
        {
            _selectionService.Stop();
            TrackingState.Text = "Đã tắt";
            TrackingState.Foreground = (Brush)FindResource("MutedBrush");
        }
    }

    private void LanguageSelect_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        _config.TranslationSourceLanguage = SourceCode;
        _config.TranslationTargetLanguage = TargetCode;
        _config.Save();
    }

    private void SwapLanguages_Click(object sender, RoutedEventArgs e)
    {
        var oldSource = SourceCode;
        var oldTarget = TargetCode;
        var newSource = oldTarget;
        var newTarget = oldSource == "auto" ? "en" : oldSource;
        SourceLanguageSelect.SelectedItem = TranslationLanguages.All.First(language => language.Code == newSource);
        TargetLanguageSelect.SelectedItem = TranslationLanguages.All.First(language => language.Code == newTarget);
    }

    private async void TranslateClipboard_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Clipboard.ContainsText())
            {
                TranslateStatus.Text = "Clipboard không có văn bản.";
                return;
            }
            await TranslateAsync(Clipboard.GetText(), "clipboard", "Clipboard");
        }
        catch (Exception ex) { TranslateStatus.Text = $"Không đọc được clipboard: {ex.Message}"; }
    }

    private async void TranslateManual_Click(object sender, RoutedEventArgs e)
    {
        var text = ManualTextInput.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            TranslateStatus.Foreground = (Brush)FindResource("MutedBrush");
            TranslateStatus.Text = "Hãy nhập văn bản cần dịch.";
            ManualTextInput.Focus();
            return;
        }
        await TranslateAsync(text, "manual", "Nhập trực tiếp");
    }

    private async void CaptureScreen_Click(object sender, RoutedEventArgs e)
    {
        var wasTracking = _selectionService.IsRunning;
        _selectionService.Stop();
        _interactionDepth++;
        try
        {
            var region = ScreenRegionOverlay.Select(this);
            if (region == null) return;
            TranslateStatus.Text = "Đang nhận diện chữ trong ảnh...";
            await Task.Delay(100);
            var text = await ScreenOcrService.CaptureAndReadAsync(region.Value);
            if (string.IsNullOrWhiteSpace(text))
            {
                TranslateStatus.Text = "Không tìm thấy chữ trong vùng đã chọn.";
                return;
            }
            await TranslateAsync(text, "ocr", "Ảnh màn hình");
        }
        catch (Exception ex)
        {
            TranslateStatus.Text = $"OCR thất bại: {ex.Message}";
        }
        finally
        {
            _interactionDepth--;
            if (wasTracking && IsVisible) _selectionService.Start();
            ScheduleCollapseIfIdle();
        }
    }

    private async Task TranslateAsync(string text, string captureMethod, string sourceApp)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (_translating)
        {
            _queuedTranslation = (text, captureMethod, sourceApp);
            TranslateStatus.Text = "Đã xếp bản dịch mới tiếp theo...";
            return;
        }
        _translating = true;
        TranslateStatus.Foreground = (Brush)FindResource("MutedBrush");
        TranslateStatus.Text = "Đang dịch...";
        try
        {
            var result = await TranslationService.TranslateAsync(text, SourceCode, TargetCode);
            TranslateStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x8F, 0xE0, 0xA8));
            TranslateStatus.Text = $"Đã dịch {result.SourceText.Length} ký tự · {TranslationLanguages.NameOf(result.SourceLanguage)} → {TranslationLanguages.NameOf(result.TargetLanguage)}";
            ShowResult(result, captureMethod, sourceApp);
        }
        catch (Exception ex)
        {
            ShowTranslationError(ex);
        }
        finally
        {
            _translating = false;
            if (_queuedTranslation is { } queued)
            {
                _queuedTranslation = null;
                await TranslateAsync(queued.Text, queued.Method, queued.SourceApp);
            }
            else ScheduleCollapseIfIdle();
        }
    }

    private void ShowResult(TranslationResult result, string captureMethod, string sourceApp)
    {
        _resultPopup?.Close();
        var popup = new TranslationResultPopup(result, captureMethod, sourceApp) { Owner = this };
        _resultPopup = popup;
        popup.Closed += (_, _) =>
        {
            if (_resultPopup == popup) _resultPopup = null;
            ScheduleCollapseIfIdle();
        };
        popup.Saved += RenderVocabulary;
        popup.RetryRequested += async () =>
        {
            try
            {
                var current = popup.CurrentResult;
                popup.UpdateResult(await TranslationService.TranslateAsync(current.SourceText, current.SourceLanguage, current.TargetLanguage));
            }
            catch (Exception ex)
            {
                ShowTranslationError(ex, "Dịch lại thất bại");
                popup.SetStatus("Dịch lại thất bại", isError: true);
            }
        };
        popup.SwapRequested += async () =>
        {
            try
            {
                var current = popup.CurrentResult;
                var swapped = await TranslationService.TranslateAsync(current.TranslatedText, current.TargetLanguage, current.SourceLanguage);
                popup.UpdateResult(swapped);
            }
            catch (Exception ex)
            {
                ShowTranslationError(ex, "Đảo chiều thất bại");
                popup.SetStatus("Đảo chiều thất bại", isError: true);
            }
        };
        popup.Show();
    }

    private void ShowTranslationError(Exception exception, string prefix = "Dịch thất bại")
    {
        var message = exception switch
        {
            TimeoutException => "Dịch vụ phản hồi quá chậm. Hãy thử lại.",
            HttpRequestException => "Không thể kết nối dịch vụ dịch. Hãy kiểm tra mạng.",
            _ => exception.Message,
        };
        TranslateStatus.Text = $"{prefix}: {message}";
        TranslateStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xE7, 0x7A, 0x72));
    }

    private void VocabularySearch_TextChanged(object sender, TextChangedEventArgs e) => RenderVocabulary();

    private void VocabularyHeader_Click(object sender, MouseButtonEventArgs e)
    {
        SetVocabularyExpanded(!_vocabularyExpanded);
    }

    public void SetVocabularyExpanded(bool expanded)
    {
        if (!expanded)
        {
            if (Height > 320) _expandedHeight = Height;
        }
        _vocabularyExpanded = expanded;
        VocabularySection.Visibility = _vocabularyExpanded ? Visibility.Visible : Visibility.Collapsed;
        VocabularyChevron.Text = _vocabularyExpanded ? "\uE70D" : "\uE76C";
        VocabularyOpenHint.Text = _vocabularyExpanded ? "Thu gọn" : "Mở";
        if (_vocabularyExpanded) RenderVocabulary();
        if (_panelExpanded) SetPanelExpanded(true);
    }

    private void FilterToggle_Click(object sender, RoutedEventArgs e)
    {
        SetMetadataFiltersVisible(MetadataFilterPanel.Visibility != Visibility.Visible);
        e.Handled = true;
    }

    public void SetMetadataFiltersVisible(bool visible) =>
        MetadataFilterPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    private void MetadataFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingFilters) RenderVocabulary();
    }

    private void CreateVocabularyTag_Click(object sender, RoutedEventArgs e)
    {
        _interactionDepth++;
        string? tag;
        try { tag = PromptDialog.Show(this, "Tạo tag từ vựng"); }
        finally
        {
            _interactionDepth--;
            ScheduleCollapseIfIdle();
        }
        if (string.IsNullOrWhiteSpace(tag)) return;
        tag = tag.Trim().TrimStart('#');
        if (tag.Length == 0) return;
        VocabularyTagsService.Add(tag);
        RenderVocabulary();
        SelectFilter(VocabularyTagFilter, tag);
        e.Handled = true;
    }

    private void RenderVocabulary()
    {
        if (!IsInitialized) return;
        var allItems = VocabularyService.Load();
        RefreshFilterOptions(allItems);
        var search = VocabularySearch.Text.Trim();
        var pair = SelectedFilter(LanguagePairFilter);
        var method = SelectedFilter(CaptureMethodFilter);
        var sourceApp = SelectedFilter(SourceAppFilter);
        var tag = SelectedFilter(VocabularyTagFilter);
        var items = allItems
            .Where(item => string.IsNullOrEmpty(search) ||
                           item.SourceText.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                           item.TranslatedText.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                           item.MeaningGroups.Any(group => group.Meanings.Any(meaning => meaning.Contains(search, StringComparison.OrdinalIgnoreCase))) ||
                           item.Examples.Any(example => example.Contains(search, StringComparison.OrdinalIgnoreCase)) ||
                           item.Tags.Any(itemTag => itemTag.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .Where(item => pair.Length == 0 || $"{item.SourceLanguage}>{item.TargetLanguage}" == pair)
            .Where(item => method.Length == 0 || item.CaptureMethod == method)
            .Where(item => sourceApp.Length == 0 || item.SourceApp == sourceApp)
            .Where(item => tag.Length == 0 || item.Tags.Any(itemTag => string.Equals(itemTag, tag, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(item => item.CreatedAtUnix).ToList();

        VocabularyList.Items.Clear();
        VocabularyTitle.Text = items.Count == allItems.Count
            ? $"SỔ TỪ VỰNG ({allItems.Count})"
            : $"SỔ TỪ VỰNG ({items.Count}/{allItems.Count})";
        EmptyVocabulary.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var item in items) VocabularyList.Items.Add(BuildVocabularyCard(item));
    }

    private void RefreshFilterOptions(IReadOnlyCollection<VocabularyEntry> items)
    {
        _updatingFilters = true;
        try
        {
            PopulateFilter(LanguagePairFilter, "Mọi cặp ngôn ngữ", items
                .Select(item => ($"{item.SourceLanguage}>{item.TargetLanguage}",
                    $"{TranslationLanguages.NameOf(item.SourceLanguage)} → {TranslationLanguages.NameOf(item.TargetLanguage)}"))
                .Distinct().OrderBy(option => option.Item2));
            PopulateFilter(CaptureMethodFilter, "Mọi nguồn lưu", items
                .Select(item => (item.CaptureMethod, CaptureMethodName(item.CaptureMethod)))
                .Distinct().OrderBy(option => option.Item2));
            PopulateFilter(SourceAppFilter, "Mọi ứng dụng", items
                .Where(item => !string.IsNullOrWhiteSpace(item.SourceApp))
                .Select(item => (item.SourceApp, item.SourceApp)).Distinct().OrderBy(option => option.Item2));
            PopulateFilter(VocabularyTagFilter, "Mọi tag", VocabularyTagsService.LoadAll().Select(tag => (tag, $"#{tag}")));
        }
        finally { _updatingFilters = false; }
    }

    private static void PopulateFilter(ComboBox comboBox, string allLabel, IEnumerable<(string Key, string Label)> options)
    {
        var selected = SelectedFilter(comboBox);
        comboBox.Items.Clear();
        comboBox.Items.Add(new ComboBoxItem { Content = allLabel, Tag = "" });
        foreach (var (key, label) in options)
            comboBox.Items.Add(new ComboBoxItem { Content = label, Tag = key });
        SelectFilter(comboBox, selected);
    }

    private static string SelectedFilter(ComboBox comboBox) =>
        (comboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    private static void SelectFilter(ComboBox comboBox, string key)
    {
        comboBox.SelectedItem = comboBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, key, StringComparison.OrdinalIgnoreCase))
            ?? comboBox.Items[0];
    }

    private static string CaptureMethodName(string method) => method switch
    {
        "selection" => "Vùng chọn",
        "clipboard" => "Clipboard",
        "ocr" => "Ảnh / OCR",
        "manual" => "Nhập trực tiếp",
        _ => method,
    };

    private UIElement BuildVocabularyCard(VocabularyEntry item)
    {
        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x30, 0x25, 0x24, 0x2B)),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 8, 6, 8),
            Margin = new Thickness(2, 3, 2, 3), Cursor = Cursors.Hand,
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        card.Child = grid;

        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = item.SourceText, Foreground = Brushes.White, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, MaxHeight = 38, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = item.TranslatedText, Foreground = (Brush)FindResource("MutedBrush"), FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxHeight = 34, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0) });
        var alternativeMeanings = item.MeaningGroups.SelectMany(group => group.Meanings)
            .Where(meaning => !string.Equals(meaning, item.TranslatedText, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToList();
        if (alternativeMeanings.Count > 0)
            text.Children.Add(new TextBlock
            {
                Text = "Nghĩa khác: " + string.Join(" · ", alternativeMeanings),
                Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x9A, 0xB8)), FontSize = 9.5,
                TextWrapping = TextWrapping.Wrap, MaxHeight = 30, TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 4, 0, 0),
            });
        if (item.Examples.Count > 0)
            text.Children.Add(new TextBlock
            {
                Text = "Ví dụ: " + item.Examples[0], Foreground = new SolidColorBrush(Color.FromRgb(0x78, 0x78, 0x86)),
                FontSize = 9.5, FontStyle = FontStyles.Italic, TextWrapping = TextWrapping.Wrap,
                MaxHeight = 28, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0),
            });
        if (item.Tags.Count > 0)
        {
            var tags = new WrapPanel { Margin = new Thickness(0, 5, 0, 0) };
            foreach (var tag in item.Tags.Take(3))
                tags.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0x24, 0x7F, 0xA8, 0xFF)),
                    CornerRadius = new CornerRadius(7), Padding = new Thickness(5, 2, 5, 2), Margin = new Thickness(0, 0, 4, 0),
                    Child = new TextBlock { Text = $"#{tag}", Foreground = new SolidColorBrush(Color.FromRgb(0x9F, 0xB9, 0xF5)), FontSize = 9 },
                });
            if (item.Tags.Count > 3)
                tags.Children.Add(new TextBlock { Text = $"+{item.Tags.Count - 3}", Foreground = (Brush)FindResource("MutedBrush"), FontSize = 9, VerticalAlignment = VerticalAlignment.Center });
            text.Children.Add(tags);
        }
        var created = DateTimeOffset.FromUnixTimeSeconds(item.CreatedAtUnix).LocalDateTime;
        text.Children.Add(new TextBlock
        {
            Text = $"{TranslationLanguages.NameOf(item.SourceLanguage)} → {TranslationLanguages.NameOf(item.TargetLanguage)} · {created:dd/MM HH:mm}",
            Foreground = new SolidColorBrush(Color.FromRgb(0x72, 0x72, 0x80)), FontSize = 9.5, Margin = new Thickness(0, 5, 0, 0),
        });

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Opacity = 0.48, VerticalAlignment = VerticalAlignment.Center };
        var copy = new Button { Content = "\uE8C8", FontFamily = new FontFamily("Segoe MDL2 Assets"), ToolTip = "Sao chép bản dịch", Style = (Style)FindResource("IconBtnStyle"), Width = 24, Height = 24 };
        copy.Click += (_, e) => { Clipboard.SetText(item.TranslatedText); e.Handled = true; };
        var tagButton = new Button { Content = "\uE8EC", FontFamily = new FontFamily("Segoe MDL2 Assets"), ToolTip = "Gắn tag từ vựng", Style = (Style)FindResource("IconBtnStyle"), Width = 24, Height = 24 };
        tagButton.Click += (_, e) =>
        {
            _interactionDepth++;
            List<string>? selected;
            try { selected = VocabularyTagPickerDialog.Show(this, item.Tags); }
            finally { _interactionDepth--; }
            if (selected != null) { VocabularyService.SetTags(item.Id, selected); RenderVocabulary(); }
            e.Handled = true;
            ScheduleCollapseIfIdle();
        };
        var delete = new Button { Content = "\uE74D", FontFamily = new FontFamily("Segoe MDL2 Assets"), ToolTip = "Xoá khỏi sổ", Style = (Style)FindResource("IconBtnStyle"), Width = 24, Height = 24 };
        delete.Click += (_, e) => { VocabularyService.Delete(item.Id); RenderVocabulary(); e.Handled = true; };
        actions.Children.Add(copy);
        actions.Children.Add(tagButton);
        actions.Children.Add(delete);
        card.MouseEnter += (_, _) => actions.Opacity = 1;
        card.MouseLeave += (_, _) => actions.Opacity = 0;
        card.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is Button) return;
            ShowResult(new TranslationResult(item.SourceText, item.TranslatedText, item.SourceLanguage,
                item.TargetLanguage, item.MeaningGroups, item.Examples), item.CaptureMethod, item.SourceApp);
        };

        Grid.SetColumn(text, 0); Grid.SetColumn(actions, 1);
        grid.Children.Add(text); grid.Children.Add(actions);
        return card;
    }
}
