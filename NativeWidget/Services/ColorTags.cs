using System.Windows.Media;

namespace NativeWidget.Services;

/// The small fixed palette offered by every color-tag picker (Timers, Notes, Calendar).
public static class ColorTags
{
    public static readonly (string Name, string Hex)[] Palette =
    {
        ("Mặc định", ""),
        ("Xanh dương", "#4A7DFF"),
        ("Tím", "#9C6BFF"),
        ("Hồng", "#FF6B9C"),
        ("Đỏ", "#E5605A"),
        ("Cam", "#F0A050"),
        ("Vàng", "#E8D25A"),
        ("Xanh lá", "#5AC98A"),
        ("Ngọc", "#4AC9C9"),
    };

    public static Brush Resolve(string? hex, Brush fallback) =>
        string.IsNullOrEmpty(hex) ? fallback : new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
}
