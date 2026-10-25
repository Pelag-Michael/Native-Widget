using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NativeWidget;

public sealed class WidgetShortcutsView : UserControl
{
    private bool _quickAiOnly;
    public bool QuickAiOnly { get => _quickAiOnly; set { _quickAiOnly = value; Build(); } }
    public WidgetShortcutsView() => Build();
    private void Build()
    {
        var list = new StackPanel(); Content = list;
        var entries = new (string Key, string Action)[]
        {
            ("Ctrl + Alt + \u2190", "Find the AI hover position (Invisible hover on)"),
            ("Ctrl + \u2190", "Copy selection; Quicker also sends to AI (Invisible hover on)"),
            ("Ctrl + ,", "Capture current area; Screenshot to AI sends automatically (Invisible hover on)"),
            ("Ctrl + \u2191", "Start a new AI session"),
            ("Ctrl + \u2193", "Paste clipboard into AI input"),
            ("Right Ctrl", "Send AI input (press and release by itself)"),
            ("Ctrl + Alt + \u2193", "Empty AI input; keep the response and configured prompts"),
            ("Ctrl + Enter", "Send AI input (bar or Add text focused)"),
            ("Ctrl + V", "Paste into AI input, without showing pasted content"),
            ("Enter", "New line in Add text"),
            ("Esc", "Hide AI and leave Edit layout (input focused)"),
            ("Shift + drag", "Move Add text / Response from a corner"),
            ("Drag corner", "Resize Add text / Response"),
            ("Ctrl + Alt + F", "Find original launcher (Invisible hover off)"),
            ("Ctrl + Alt + K", "Open workspace / folder search"),
            ("Ctrl + Alt + G", "Restore clicking on ghosted widgets"),
            ("Ctrl + S", "Save note and sync to Notion (Notes)"),
            ("Ctrl + Shift + C", "Copy cd command (folder result selected)"),
            ("F5", "Refresh folder index (Search)")
        };
        var visibleEntries = QuickAiOnly ? entries.TakeWhile(entry => entry.Key != "Ctrl + Alt + F") : entries.AsEnumerable();
        foreach (var (key, action) in visibleEntries)
        {
            var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(103) }); row.ColumnDefinitions.Add(new ColumnDefinition());
            var keys = new TextBlock { Text = key, FontSize = 10.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,7,0) };
            var description = new TextBlock { Text = action, FontSize = 10.5, TextWrapping = TextWrapping.Wrap, Opacity = .8 };
            Grid.SetColumn(description,1); row.Children.Add(keys); row.Children.Add(description);
            list.Children.Add(new Border { Child = row, Padding = new Thickness(6), Margin = new Thickness(0,0,0,4), CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Color.FromArgb(8,255,255,255)) });
        }
    }
}
