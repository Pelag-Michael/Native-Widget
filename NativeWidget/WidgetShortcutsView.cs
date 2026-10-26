using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NativeWidget;

public sealed class WidgetShortcutsView : UserControl
{
    private bool _quickAiOnly;
    private double _shortcutFontSize = 10.5;
    public double ShortcutFontSize { get => _shortcutFontSize; set { if (Math.Abs(_shortcutFontSize - value) < .01) return; _shortcutFontSize = value; Build(); } }
    public bool QuickAiOnly { get => _quickAiOnly; set { _quickAiOnly = value; Build(); } }
    public WidgetShortcutsView() => Build();
    private void Build()
    {
        var list = new StackPanel(); Content = list;
        var entries = new (string Key, string Action)[]
        {
            ("Ctrl + Alt + /", "Giữ để xem phím tắt"),
            ("Ctrl + Alt + \u2190", "Tìm vị trí widget"),
            ("Ctrl + rê", "Di chuyển icon compact"),
            ("Ctrl + \u2190", "Copy; Quicker sẽ gửi luôn"),
            ("Ctrl + ,", "Chụp ảnh"),
            ("Ctrl + \u2191", "Session mới"),
            ("Ctrl + \u2193", "Paste chữ / ảnh"),
            ("Ctrl + \u2192", "Gửi AI"),
            ("Ctrl + Alt + \u2193", "Xóa input"),
            ("Ctrl + Enter", "Gửi trong ô nhập"),
            ("Ctrl + V", "Paste vào input"),
            ("Enter", "Xuống dòng"),
            ("Esc", "Ẩn AI, thoát sửa layout"),
            ("Shift + kéo góc", "Di chuyển panel"),
            ("Kéo góc", "Đổi kích thước"),
            ("Hover 1.5s", "Mở thanh / chạy nút"),
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
            var keys = new TextBlock { Text = key, FontSize = ShortcutFontSize, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,7,0) };
            var description = new TextBlock { Text = action, FontSize = ShortcutFontSize, TextWrapping = TextWrapping.Wrap, Opacity = .8 };
            Grid.SetColumn(description,1); row.Children.Add(keys); row.Children.Add(description);
            list.Children.Add(new Border { Child = row, Padding = new Thickness(QuickAiOnly ? 4 : 6), Margin = new Thickness(0,0,0,QuickAiOnly ? 2 : 4), CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Color.FromArgb(8,255,255,255)) });
        }
    }
}
