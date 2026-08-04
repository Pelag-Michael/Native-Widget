namespace NativeWidget.Services;

public sealed record TranslationLanguage(string Code, string Name)
{
    public override string ToString() => Name;
}

public static class TranslationLanguages
{
    public static readonly IReadOnlyList<TranslationLanguage> All = new[]
    {
        new TranslationLanguage("auto", "Auto-detect"),
        new TranslationLanguage("vi", "Vietnamese"),
        new TranslationLanguage("en", "English"),
        new TranslationLanguage("ja", "日本語"),
        new TranslationLanguage("ko", "한국어"),
        new TranslationLanguage("zh-CN", "中文（简体）"),
        new TranslationLanguage("fr", "Français"),
        new TranslationLanguage("de", "Deutsch"),
        new TranslationLanguage("es", "Español"),
        new TranslationLanguage("th", "ภาษาไทย"),
        new TranslationLanguage("id", "Bahasa Indonesia"),
        new TranslationLanguage("ru", "Русский"),
    };

    public static string NameOf(string code) => All.FirstOrDefault(language =>
        string.Equals(language.Code, code, StringComparison.OrdinalIgnoreCase))?.Name ?? code;
}
