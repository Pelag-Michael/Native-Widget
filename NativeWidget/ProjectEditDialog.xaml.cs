using System.Windows;
using Microsoft.Win32;

namespace NativeWidget;

public partial class ProjectEditDialog : Window
{
    private ProjectEditDialog(string title, string name, string? folder, string? note)
    {
        InitializeComponent();
        DialogTitle.Text = title;
        NameInput.Text = name;
        FolderInput.Text = folder ?? "";
        NoteInput.Text = note ?? "";
        Loaded += (_, _) =>
        {
            NameInput.Focus();
            NameInput.SelectAll();
        };
    }

    /// Returns the entered (name, folder, note), or null if the user cancelled.
    public static (string Name, string? Folder, string? Note)? Show(
        Window owner, string title, string name = "", string? folder = null, string? note = null)
    {
        var dialog = new ProjectEditDialog(title, name, folder, note) { Owner = owner };
        if (dialog.ShowDialog() != true) return null;

        var resultName = dialog.NameInput.Text.Trim();
        var resultFolder = string.IsNullOrWhiteSpace(dialog.FolderInput.Text) ? null : dialog.FolderInput.Text.Trim();
        var resultNote = string.IsNullOrWhiteSpace(dialog.NoteInput.Text) ? null : dialog.NoteInput.Text.Trim();
        return (resultName, resultFolder, resultNote);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Chọn folder dự án" };
        if (dialog.ShowDialog(this) == true) FolderInput.Text = dialog.FolderName;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameInput.Text))
        {
            MessageBox.Show("Nhập tên dự án.", "Thiếu thông tin");
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
