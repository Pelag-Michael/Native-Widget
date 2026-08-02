using System.IO;
using System.Windows;
using System.Windows.Input;
using NativeWidget.Models;
using NativeWidget.Services;

namespace NativeWidget;

public partial class SettingsWindow : Window
{
    private readonly AppConfig _config;
    private readonly Func<Task> _onCalendarChanged;

    public SettingsWindow(AppConfig config, Func<Task> onCalendarChanged)
    {
        InitializeComponent();
        WindowInterop.HideFromAltTab(this);
        _config = config;
        _onCalendarChanged = onCalendarChanged;
        Loaded += (_, _) =>
        {
            GoogleClientIdInput.Text = _config.GoogleClientId;
            GoogleClientSecretInput.Text = _config.GoogleClientSecret;
            AutoStartCheck.IsChecked = AutoStartService.IsEnabled();
            NotionTokenInput.Text = _config.NotionToken;
            NotionPageIdInput.Text = _config.NotionParentPageId;
            NotionEnabledCheck.IsChecked = _config.NotionSyncEnabled;
        };
    }

    private void AutoStartCheck_Click(object sender, RoutedEventArgs e)
    {
        AutoStartService.SetEnabled(AutoStartCheck.IsChecked == true);
    }

    private void DragBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    public WidgetHeaderControls Header => HeaderControls;



    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    private void LoadBtn_Click(object sender, RoutedEventArgs e)
    {
        Diag("LoadBtn_Click fired");
        try
        {
            _config.GoogleClientId = GoogleClientIdInput.Text.Trim();
            _config.GoogleClientSecret = GoogleClientSecretInput.Text.Trim();

            var newPageId = NotionPageIdInput.Text.Trim();
            // A changed parent page orphans whatever database was cached under the old one -
            // clear it so EnsureDatabaseAsync creates a fresh one under the new page.
            if (newPageId != _config.NotionParentPageId) _config.NotionDatabaseId = "";
            _config.NotionToken = NotionTokenInput.Text.Trim();
            _config.NotionParentPageId = newPageId;
            _config.NotionSyncEnabled = NotionEnabledCheck.IsChecked == true;

            Diag($"about to Save() - NotionToken.Length={_config.NotionToken.Length} NotionSyncEnabled={_config.NotionSyncEnabled}");
            _config.Save();
            Diag("Save() returned OK");
            SettingsStatus.Text = "Đã lưu. Mở widget Calendar bấm Kết nối.";
        }
        catch (Exception ex)
        {
            Diag("EXCEPTION: " + ex);
            SettingsStatus.Text = "Lỗi khi lưu: " + ex.Message;
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

    private async void LogoutBtn_Click(object sender, RoutedEventArgs e)
    {
        GoogleCalendarService.Disconnect();
        await _onCalendarChanged();
        SettingsStatus.Text = "Đã log out Google Calendar.";
    }
}
