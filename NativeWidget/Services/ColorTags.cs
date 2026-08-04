using System.Windows.Media;

namespace NativeWidget.Services;

/// The small fixed palette offered by every color-tag picker (Timers, Notes, Calendar).
public static class ColorTags
{
    public static readonly (string Name, string Hex)[] Palette =
    {
        ("Default", ""),
        ("Blue", "#4A7DFF"),
        ("Purple", "#9C6BFF"),
        ("Pink", "#FF6B9C"),
        ("Red", "#E5605A"),
        ("Orange", "#F0A050"),
        ("Yellow", "#E8D25A"),
        ("Green", "#5AC98A"),
        ("Teal", "#4AC9C9"),
    };

    public static Brush Resolve(string? hex, Brush fallback) =>
        string.IsNullOrEmpty(hex) ? fallback : new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
}
