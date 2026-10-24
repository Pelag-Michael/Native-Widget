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
    public event Action<bool>? InvisibleHoverChanged;
    public event Action? HoverAiSettingsRequested;
    private void HoverAiSettings_Click(object sender, RoutedEventArgs e) => HoverAiSettingsRequested?.Invoke();

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
            InvisibleHoverToggle.IsChecked = _config.InvisibleHoverEnabled;
            NotionTokenInput.Password = _config.NotionToken;
            NotionPageIdInput.Text = _config.NotionParentPageId;
            NotionDatabaseIdInput.Text = _config.NotionDatabaseId;
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

            var newPageId = NormalizeNotionId(NotionPageIdInput.Text);
            var newDatabaseId = NormalizeNotionId(NotionDatabaseIdInput.Text);
            var changedParent = NormalizeNotionId(_config.NotionParentPageId) != newPageId;
            // Formatting the same ID with/without hyphens must not reset table identity.
            if (changedParent && newDatabaseId == NormalizeNotionId(_config.NotionDatabaseId))
                newDatabaseId = "";
            if (changedParent || newDatabaseId != NormalizeNotionId(_config.NotionDatabaseId))
                _config.NotionDataSourceId = "";
            _config.NotionDatabaseId = newDatabaseId;
            _config.NotionToken = NotionTokenInput.Password.Trim();
            _config.NotionParentPageId = newPageId;
            _config.NotionSyncEnabled = NotionEnabledCheck.IsChecked == true;

            Diag($"about to Save() - NotionToken.Length={_config.NotionToken.Length} NotionSyncEnabled={_config.NotionSyncEnabled}");
            _config.Save();
            NotionDatabaseIdInput.Text = _config.NotionDatabaseId;
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

    private void InvisibleHover_Click(object sender, RoutedEventArgs e)
    {
        _config.InvisibleHoverEnabled = InvisibleHoverToggle.IsChecked == true;
        _config.Save();
        InvisibleHoverChanged?.Invoke(_config.InvisibleHoverEnabled);
    }

    internal static string NormalizeNotionId(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return "";
        if (Guid.TryParse(text, out var id)) return id.ToString("D");
        throw new FormatException("Use the 32-character Notion page/database ID, with or without hyphens.");
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
