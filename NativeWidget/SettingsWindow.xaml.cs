using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
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
            GoogleClientSecretInput.Password = _config.GoogleClientSecret;
            AutoStartCheck.IsChecked = AutoStartService.IsEnabled();
            RestoreSessionCheck.IsChecked = _config.RestoreWindowSessionEnabled;
            NotionTokenInput.Password = _config.NotionToken;
            NotionPageIdInput.Text = _config.NotionParentPageId;
            NotionEnabledCheck.IsChecked = _config.NotionSyncEnabled;
            UpdateConnectionBadges();
        };
    }

    private void AutoStartCheck_Click(object sender, RoutedEventArgs e)
    {
        AutoStartService.SetEnabled(AutoStartCheck.IsChecked == true);
    }

    private void RestoreSessionCheck_Click(object sender, RoutedEventArgs e)
    {
        _config.RestoreWindowSessionEnabled = RestoreSessionCheck.IsChecked == true;
        _config.Save();
        if (_config.RestoreWindowSessionEnabled) WindowSessionService.SaveCurrentSession();
        SettingsStatus.Text = _config.RestoreWindowSessionEnabled
            ? "Work session restore enabled."
            : "Work session restore disabled.";
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
            _config.GoogleClientSecret = GoogleClientSecretInput.Password.Trim();
            _config.RestoreWindowSessionEnabled = RestoreSessionCheck.IsChecked == true;

            var newPageId = NotionPageIdInput.Text.Trim();
            // A changed parent page orphans whatever database was cached under the old one -
            // clear it so EnsureDatabaseAsync creates a fresh one under the new page.
            if (newPageId != _config.NotionParentPageId) _config.NotionDatabaseId = "";
            _config.NotionToken = NotionTokenInput.Password.Trim();
            _config.NotionParentPageId = newPageId;
            _config.NotionSyncEnabled = NotionEnabledCheck.IsChecked == true;

            Diag($"about to Save() - NotionToken.Length={_config.NotionToken.Length} NotionSyncEnabled={_config.NotionSyncEnabled}");
            _config.Save();
            Diag("Save() returned OK");
            UpdateConnectionBadges();
            SettingsStatus.Text = "Saved. Open the Calendar widget and connect if Google is not working yet.";
        }
        catch (Exception ex)
        {
            Diag("EXCEPTION: " + ex);
            SettingsStatus.Text = "Error saving: " + ex.Message;
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
        UpdateConnectionBadges();
        SettingsStatus.Text = "Logged out of Google Calendar.";
    }

    private void UpdateConnectionBadges()
    {
        var googleConnected = GoogleCalendarService.IsConnected();
        GoogleStatusText.Text = googleConnected ? "Connected" : "Not connected";
        GoogleStatusText.Foreground = googleConnected
            ? new SolidColorBrush(Color.FromRgb(0x8F, 0xE0, 0xA8)) : (Brush)FindResource("MutedBrush");
        GoogleStatusBadge.Background = googleConnected
            ? new SolidColorBrush(Color.FromArgb(0x22, 0x8F, 0xE0, 0xA8)) : new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));

        var notionEnabled = _config.NotionSyncEnabled && !string.IsNullOrWhiteSpace(_config.NotionToken);
        NotionStatusText.Text = notionEnabled ? "On" : "Off";
        NotionStatusText.Foreground = notionEnabled
            ? new SolidColorBrush(Color.FromRgb(0x8F, 0xE0, 0xA8)) : (Brush)FindResource("MutedBrush");
        NotionStatusBadge.Background = notionEnabled
            ? new SolidColorBrush(Color.FromArgb(0x22, 0x8F, 0xE0, 0xA8)) : new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
    }
}
