using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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

        Loaded += (_, _) => { UpdateTracking(); RenderVocabulary(); };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) UpdateTracking();
            else
            {
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

    private async void CaptureScreen_Click(object sender, RoutedEventArgs e)
    {
        var wasTracking = _selectionService.IsRunning;
        _selectionService.Stop();
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
            if (wasTracking && IsVisible) _selectionService.Start();
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
        }
    }

    private void ShowResult(TranslationResult result, string captureMethod, string sourceApp)
    {
        _resultPopup?.Close();
        var popup = new TranslationResultPopup(result, captureMethod, sourceApp) { Owner = this };
        _resultPopup = popup;
        popup.Closed += (_, _) => { if (_resultPopup == popup) _resultPopup = null; };
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

    private void RenderVocabulary()
    {
        if (!IsInitialized) return;
        var search = VocabularySearch.Text.Trim();
        var items = VocabularyService.Load()
            .Where(item => string.IsNullOrEmpty(search) ||
                           item.SourceText.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                           item.TranslatedText.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.CreatedAtUnix).ToList();

        VocabularyList.Items.Clear();
        VocabularyTitle.Text = $"SỔ TỪ VỰNG ({items.Count})";
        EmptyVocabulary.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var item in items) VocabularyList.Items.Add(BuildVocabularyCard(item));
    }

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
        var created = DateTimeOffset.FromUnixTimeSeconds(item.CreatedAtUnix).LocalDateTime;
        text.Children.Add(new TextBlock
        {
            Text = $"{TranslationLanguages.NameOf(item.SourceLanguage)} → {TranslationLanguages.NameOf(item.TargetLanguage)} · {created:dd/MM HH:mm}",
            Foreground = new SolidColorBrush(Color.FromRgb(0x72, 0x72, 0x80)), FontSize = 9.5, Margin = new Thickness(0, 5, 0, 0),
        });

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Opacity = 0, VerticalAlignment = VerticalAlignment.Center };
        var copy = new Button { Content = "\uE8C8", FontFamily = new FontFamily("Segoe MDL2 Assets"), ToolTip = "Sao chép bản dịch", Style = (Style)FindResource("IconBtnStyle"), Width = 24, Height = 24 };
        copy.Click += (_, e) => { Clipboard.SetText(item.TranslatedText); e.Handled = true; };
        var delete = new Button { Content = "\uE74D", FontFamily = new FontFamily("Segoe MDL2 Assets"), ToolTip = "Xoá khỏi sổ", Style = (Style)FindResource("IconBtnStyle"), Width = 24, Height = 24 };
        delete.Click += (_, e) => { VocabularyService.Delete(item.Id); RenderVocabulary(); e.Handled = true; };
        actions.Children.Add(copy);
        actions.Children.Add(delete);
        card.MouseEnter += (_, _) => actions.Opacity = 1;
        card.MouseLeave += (_, _) => actions.Opacity = 0;
        card.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is Button) return;
            ShowResult(new TranslationResult(item.SourceText, item.TranslatedText, item.SourceLanguage, item.TargetLanguage), item.CaptureMethod, item.SourceApp);
        };

        Grid.SetColumn(text, 0); Grid.SetColumn(actions, 1);
        grid.Children.Add(text); grid.Children.Add(actions);
        return card;
    }
}
