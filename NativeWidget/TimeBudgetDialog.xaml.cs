using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace NativeWidget;

public partial class TimeBudgetDialog : Window
{
    public string BudgetTitle { get; private set; } = "";
    public double TargetHours { get; private set; } = 8.0;
    public int PeriodDays { get; private set; } = 7;
    public bool Repeat { get; private set; } = true;

    public TimeBudgetDialog(string? title = null, double? targetHours = null, int? periodDays = null, bool? repeat = null)
    {
        InitializeComponent();
        if (!string.IsNullOrEmpty(title))
        {
            DialogTitle.Text = "Edit time target";
            NameInput.Text = title;
        }
        if (targetHours.HasValue && targetHours.Value > 0)
        {
            HoursInput.Text = targetHours.Value.ToString("0.#", CultureInfo.InvariantCulture);
        }
        if (periodDays.HasValue && periodDays.Value > 0)
        {
            DaysInput.Text = periodDays.Value.ToString();
        }
        if (repeat.HasValue)
        {
            RepeatCheck.IsChecked = repeat.Value;
        }

        Loaded += (_, _) =>
        {
            NameInput.Focus();
            if (!string.IsNullOrEmpty(NameInput.Text))
            {
                NameInput.SelectAll();
            }
        };
    }

    private void HoursPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string val)
        {
            HoursInput.Text = val;
        }
    }

    private void DaysPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string val)
        {
            DaysInput.Text = val;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var name = NameInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show("Please enter a goal or activity name.", "Time target", MessageBoxButton.OK, MessageBoxImage.Information);
            NameInput.Focus();
            return;
        }

        var hoursText = HoursInput.Text.Trim().Replace(',', '.');
        if (!double.TryParse(hoursText, NumberStyles.Float, CultureInfo.InvariantCulture, out var hours) || hours <= 0)
        {
            MessageBox.Show("Please enter a valid number of target hours (e.g. 8).", "Time target", MessageBoxButton.OK, MessageBoxImage.Information);
            HoursInput.Focus();
            return;
        }

        var daysText = DaysInput.Text.Trim();
        if (!int.TryParse(daysText, out var days) || days <= 0)
        {
            MessageBox.Show("Please enter a valid number of days for the timeframe (e.g. 7).", "Time target", MessageBoxButton.OK, MessageBoxImage.Information);
            DaysInput.Focus();
            return;
        }

        BudgetTitle = name;
        TargetHours = Math.Round(hours, 2);
        PeriodDays = days;
        Repeat = RepeatCheck.IsChecked == true;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    public static (bool Success, string Title, double TargetHours, int PeriodDays, bool Repeat) Show(
        Window owner,
        string? initialTitle = null,
        double? initialHours = null,
        int? initialDays = null,
        bool? initialRepeat = null)
    {
        var dlg = new TimeBudgetDialog(initialTitle, initialHours, initialDays, initialRepeat) { Owner = owner };
        return dlg.ShowDialog() == true
            ? (true, dlg.BudgetTitle, dlg.TargetHours, dlg.PeriodDays, dlg.Repeat)
            : (false, "", 0, 7, true);
    }
}
