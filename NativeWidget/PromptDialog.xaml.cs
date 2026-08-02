using System.Windows;
using System.Windows.Input;

namespace NativeWidget;

public partial class PromptDialog : Window
{
    private PromptDialog(string title, string initialValue)
    {
        InitializeComponent();
        PromptTitle.Text = title;
        ValueInput.Text = initialValue;
        Loaded += (_, _) =>
        {
            ValueInput.Focus();
            ValueInput.SelectAll();
        };
    }

    /// Returns the entered text, or null if the user cancelled.
    public static string? Show(Window owner, string title, string initialValue = "")
    {
        var dialog = new PromptDialog(title, initialValue) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.ValueInput.Text.Trim() : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ValueInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) DialogResult = true;
        else if (e.Key == Key.Escape) DialogResult = false;
    }
}
