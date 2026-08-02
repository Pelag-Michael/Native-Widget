using System.Windows;

namespace NativeWidget;

public partial class DueDateDialog : Window
{
    private bool _clear;

    private DueDateDialog(DateTime? current)
    {
        InitializeComponent();
        DueDatePicker.SelectedDate = current ?? DateTime.Today;
    }

    /// Returns (changed, date). changed=false means the user cancelled - leave the task's
    /// due date untouched. changed=true with date=null means "clear the due date".
    public static (bool Changed, DateTime? Date) Show(Window owner, DateTime? current)
    {
        var dialog = new DueDateDialog(current) { Owner = owner };
        var ok = dialog.ShowDialog() == true;
        if (!ok) return (false, null);
        if (dialog._clear) return (true, null);
        return (true, dialog.DueDatePicker.SelectedDate);
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _clear = true;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
