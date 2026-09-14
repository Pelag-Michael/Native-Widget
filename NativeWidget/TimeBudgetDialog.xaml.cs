using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace NativeWidget;

public partial class TimeBudgetDialog : Window
{
    public string BudgetTitle { get; private set; } = "";
    public double TargetHours { get; private set; } = 8.0;

    public TimeBudgetDialog(string? title = null, double? targetHours = null)
    {
        InitializeComponent();
        if (!string.IsNullOrEmpty(title))
        {
            DialogTitle.Text = "Edit time budget";
            NameInput.Text = title;
        }
        if (targetHours.HasValue && targetHours.Value > 0)
        {
            HoursInput.Text = targetHours.Value.ToString("0.#", CultureInfo.InvariantCulture);
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

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string val)
        {
            HoursInput.Text = val;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var name = NameInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show("Please enter a goal or activity name.", "Time budget", MessageBoxButton.OK, MessageBoxImage.Information);
            NameInput.Focus();
            return;
        }

        var hoursText = HoursInput.Text.Trim().Replace(',', '.');
        if (!double.TryParse(hoursText, NumberStyles.Float, CultureInfo.InvariantCulture, out var hours) || hours <= 0)
        {
            MessageBox.Show("Please enter a valid number of target hours (e.g. 8).", "Time budget", MessageBoxButton.OK, MessageBoxImage.Information);
            HoursInput.Focus();
            return;
        }

        BudgetTitle = name;
        TargetHours = Math.Round(hours, 2);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    public static (bool Success, string Title, double TargetHours) Show(Window owner, string? initialTitle = null, double? initialHours = null)
    {
        var dlg = new TimeBudgetDialog(initialTitle, initialHours) { Owner = owner };
        return dlg.ShowDialog() == true
            ? (true, dlg.BudgetTitle, dlg.TargetHours)
            : (false, "", 0);
    }
}
