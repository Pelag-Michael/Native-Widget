using System.Text.RegularExpressions;

namespace NativeWidget.Services;

public readonly record struct DetectedLink(int Index, int Length, string Text, Uri Target);

public static partial class LinkDetection
{
    [GeneratedRegex(
        @"(?<![\w@])(?<url>(?:https?://|www\.)[^\s<]+|(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}(?:/[^\s<]*)?)",
        RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    public static IReadOnlyList<DetectedLink> Find(string text)
    {
        var output = new List<DetectedLink>();
        foreach (Match match in UrlRegex().Matches(text))
        {
            var value = match.Groups["url"].Value
                .TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}');
            if (value.Length == 0) continue;
            var target = value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                         value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? value
                : "https://" + value;
            output.Add(new DetectedLink(match.Index, value.Length, value, new Uri(target)));
        }
        return output;
    }
}
