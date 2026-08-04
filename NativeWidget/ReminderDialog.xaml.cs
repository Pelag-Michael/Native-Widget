using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;

namespace NativeWidget;

public partial class ReminderDialog : Window
{
    private static readonly Regex DigitsOnly = new("^[0-9]+$");
    private bool _clear;

    public TimeSpan? Duration { get; private set; }

    private ReminderDialog(bool hasExisting)
    {
        InitializeComponent();
        if (hasExisting)
        {
            ExistingHint.Text = "This note already has a reminder — setting a new one will replace it.";
            ExistingHint.Visibility = Visibility.Visible;
            ClearBtn.Visibility = Visibility.Visible;
        }
    }

    /// Returns (changed, duration). changed=false means cancelled. duration=null with
    /// changed=true means "clear the reminder".
    public static (bool Changed, TimeSpan? Duration) Show(Window owner, bool hasExisting)
    {
        var dialog = new ReminderDialog(hasExisting) { Owner = owner };
        var ok = dialog.ShowDialog() == true;
        if (!ok) return (false, null);
        if (dialog._clear) return (true, null);
        return (true, dialog.Duration);
    }

    private void Digits_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !DigitsOnly.IsMatch(e.Text);
    }

    private static int ParseBox(System.Windows.Controls.TextBox box) => int.TryParse(box.Text, out var v) ? Math.Max(v, 0) : 0;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var duration = new TimeSpan(ParseBox(DaysInput), ParseBox(HoursInput), ParseBox(MinsInput), 0);
        if (duration <= TimeSpan.Zero)
        {
            MessageBox.Show("Enter a duration greater than 0.", "Reminder");
            return;
        }
        Duration = duration;
        DialogResult = true;
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _clear = true;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
