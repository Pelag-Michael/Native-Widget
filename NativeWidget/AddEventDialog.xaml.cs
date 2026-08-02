using System.Windows;

namespace NativeWidget;

public partial class AddEventDialog : Window
{
    public string EventTitle { get; private set; } = "";
    public DateTime Start { get; private set; }
    public bool AllDay { get; private set; }

    /// null = does not repeat; otherwise a Google RRULE FREQ value ("DAILY"/"WEEKLY"/"MONTHLY").
    public string? RecurrenceFreq { get; private set; }

    private AddEventDialog()
    {
        InitializeComponent();
        EventDatePicker.SelectedDate = DateTime.Today;
    }

    /// Returns null if the user cancelled or left the title blank.
    public static AddEventDialog? Show(Window owner)
    {
        var dialog = new AddEventDialog { Owner = owner };
        return dialog.ShowDialog() == true ? dialog : null;
    }

    private void AllDayCheck_Changed(object sender, RoutedEventArgs e)
    {
        TimeInput.IsEnabled = AllDayCheck.IsChecked != true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var title = TitleInput.Text.Trim();
        var date = EventDatePicker.SelectedDate;
        if (string.IsNullOrEmpty(title) || date == null) return;

        AllDay = AllDayCheck.IsChecked == true;
        var time = TimeSpan.Zero;
        if (!AllDay && !TimeSpan.TryParseExact(TimeInput.Text.Trim(), "hh\\:mm", null, out time))
        {
            MessageBox.Show("Giờ không hợp lệ, dùng định dạng HH:mm.", "Lỗi");
            return;
        }

        EventTitle = title;
        Start = date.Value.Date + time;
        RecurrenceFreq = RepeatCombo.SelectedIndex switch
        {
            1 => "DAILY",
            2 => "WEEKLY",
            3 => "MONTHLY",
            _ => null,
        };
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
