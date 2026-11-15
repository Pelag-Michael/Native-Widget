using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using NativeWidget;
using NativeWidget.Models;
using NativeWidget.Services;

internal static class EndTaskChecks
{
    internal static void Run()
    {
        var previousData = Environment.GetEnvironmentVariable("NATIVEWIDGET_DATA_DIR");
        var previousInstance = Environment.GetEnvironmentVariable("NATIVEWIDGET_INSTANCE_SUFFIX");
        var data = Path.Combine(Path.GetTempPath(), "nativewidget-exit-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("NATIVEWIDGET_DATA_DIR", data);
        Environment.SetEnvironmentVariable("NATIVEWIDGET_INSTANCE_SUFFIX", Guid.NewGuid().ToString("N"));
        try
        {
            var app = new App(); app.InitializeComponent();
            // This harness runs the dispatcher directly to avoid StartupUri creating
            // another launcher; mirror Application.Run's dispatcher exit behavior.
            app.Exit += (_, _) => app.Dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Background);
            var launcher = new MainWindow { CodexBackendEnabled = false };
            app.MainWindow = launcher;
            var closed = false; launcher.Closed += (_, _) => closed = true;
            var noteId = NotesService.CreateNote();
            var notes = new NotesWindow(new AppConfig());
            app.Dispatcher.BeginInvoke(new Action(() =>
            {
                launcher.Show(); notes.Show(); notes.OpenNoteFromSearch(noteId);
                ((RichTextBox)notes.FindName("NoteText")).Document = new FlowDocument(new Paragraph(new Run("Saved by End task")));
                ((Button)launcher.FindName("GlobalEndTaskBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }));
            System.Windows.Threading.Dispatcher.Run();
            var document = NotesService.LoadNote(noteId);
            if (!closed || !new TextRange(document.ContentStart, document.ContentEnd).Text.Contains("Saved by End task"))
                throw new InvalidOperationException("End task must shut down the launcher and save an open note.");
            Console.WriteLine("PASS End task: application exited and unsaved note content was preserved.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("NATIVEWIDGET_DATA_DIR", previousData);
            Environment.SetEnvironmentVariable("NATIVEWIDGET_INSTANCE_SUFFIX", previousInstance);
        }
    }
}
