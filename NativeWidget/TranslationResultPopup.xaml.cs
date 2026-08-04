using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using NativeWidget.Services;

namespace NativeWidget;

public partial class TranslationResultPopup : Window
{
    private TranslationResult _result;
    private readonly string _captureMethod;
    private readonly string _sourceApp;

    public event Func<Task>? RetryRequested;
    public event Func<Task>? SwapRequested;
    public event Action? Saved;
    public TranslationResult CurrentResult => _result;

    public TranslationResultPopup(TranslationResult result, string captureMethod, string sourceApp)
    {
        InitializeComponent();
        WindowInterop.HideFromAltTab(this);
        _result = result;
        _captureMethod = captureMethod;
        _sourceApp = sourceApp;
        Render();
        SourceInitialized += (_, _) => PositionNearCursor();
    }

    public void UpdateResult(TranslationResult result)
    {
        _result = result;
        SaveButton.IsEnabled = true;
        SaveButton.Content = "Lưu";
        SetStatus("");
        Render();
    }

    public void SetStatus(string message, bool isError = false)
    {
        PopupStatus.Text = message;
        PopupStatus.Foreground = isError
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE5, 0x60, 0x5A))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8F, 0xE0, 0xA8));
    }

    private void Render()
    {
        LanguagePairText.Text = $"{TranslationLanguages.NameOf(_result.SourceLanguage)} → {TranslationLanguages.NameOf(_result.TargetLanguage)}";
        SourceView.Document = LinkDocumentBuilder.Build(_result.SourceText, this);
        TranslationView.Document = LinkDocumentBuilder.Build(_result.TranslatedText, this);
    }

    private void PositionNearCursor()
    {
        GetCursorPos(out var cursor);
        var source = PresentationSource.FromVisual(this) as HwndSource;
        var cursorDip = source?.CompositionTarget?.TransformFromDevice.Transform(new Point(cursor.X, cursor.Y))
                        ?? new Point(cursor.X, cursor.Y);
        Left = Math.Max(SystemParameters.VirtualScreenLeft + 8,
            Math.Min(cursorDip.X + 18, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width - 8));
        Top = Math.Max(SystemParameters.VirtualScreenTop + 8,
            Math.Min(cursorDip.Y + 18, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 430));
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(_result.TranslatedText);
        PopupStatus.Text = "Đã sao chép bản dịch";
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        PopupStatus.Text = "Đang dịch lại...";
        if (RetryRequested != null) await RetryRequested();
    }

    private async void Swap_Click(object sender, RoutedEventArgs e)
    {
        PopupStatus.Text = "Đang đảo chiều...";
        if (SwapRequested != null) await SwapRequested();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        VocabularyService.Add(_result, _captureMethod, _sourceApp);
        SaveButton.Content = "Đã lưu";
        SaveButton.IsEnabled = false;
        PopupStatus.Text = "Đã thêm vào sổ từ vựng";
        Saved?.Invoke();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);
}
