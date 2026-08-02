using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NativeWidget.Services;

/// Converts the bounded set of WYSIWYG constructs supported by NotesWindow to and from
/// Markdown. Markdown is an internal storage/sync format only; it is never shown in the UI.
public static partial class FlowDocumentMarkdownConverter
{
    private const string Heading1 = "heading1";
    private const string Heading2 = "heading2";
    private const string Quote = "quote";
    private const string Code = "code";
    private const string Todo = "todo";

    [GeneratedRegex(@"^\d+\.\s(.*)$")]
    private static partial Regex NumberedItemRegex();

    [GeneratedRegex(@"^<span(?:\s+data-font=""([^""]*)"")?(?:\s+data-size=""([^""]*)"")?>(.*)</span>$",
        RegexOptions.Singleline)]
    private static partial Regex FontSpanRegex();

    public static string ToMarkdown(FlowDocument document)
    {
        var output = new List<string>();
        foreach (var block in document.Blocks)
            WriteBlock(block, output);
        return string.Join('\n', output).TrimEnd('\n');
    }

    public static FlowDocument FromMarkdown(string markdown)
    {
        var document = new FlowDocument();
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

        for (var i = 0; i < lines.Length;)
        {
            var line = lines[i];
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                var code = new StringBuilder();
                i++;
                while (i < lines.Length && !lines[i].StartsWith("```", StringComparison.Ordinal))
                {
                    if (code.Length > 0) code.Append('\n');
                    code.Append(lines[i++]);
                }
                if (i < lines.Length) i++;
                document.Blocks.Add(CreateStyledParagraph(Code, code.ToString(), parseInline: false));
                continue;
            }

            if (line.StartsWith("- [", StringComparison.Ordinal) && line.Length >= 6 &&
                (line[3] is ' ' or 'x' or 'X') && line[4] == ']' && line[5] == ' ')
            {
                document.Blocks.Add(CreateTodoParagraph(line[6..], line[3] is 'x' or 'X'));
                i++;
                continue;
            }

            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                var list = new List { MarkerStyle = TextMarkerStyle.Disc };
                while (i < lines.Length && lines[i].StartsWith("- ", StringComparison.Ordinal) &&
                       !(lines[i].Length >= 6 && lines[i][2] == '[' && lines[i][4] == ']'))
                {
                    list.ListItems.Add(new ListItem(CreateParagraph(lines[i][2..])));
                    i++;
                }
                document.Blocks.Add(list);
                continue;
            }

            if (NumberedItemRegex().Match(line) is { Success: true } numbered)
            {
                var list = new List { MarkerStyle = TextMarkerStyle.Decimal };
                while (i < lines.Length && NumberedItemRegex().Match(lines[i]) is { Success: true } item)
                {
                    list.ListItems.Add(new ListItem(CreateParagraph(item.Groups[1].Value)));
                    i++;
                }
                document.Blocks.Add(list);
                continue;
            }

            if (line.StartsWith("# ", StringComparison.Ordinal))
                document.Blocks.Add(CreateStyledParagraph(Heading1, line[2..]));
            else if (line.StartsWith("## ", StringComparison.Ordinal))
                document.Blocks.Add(CreateStyledParagraph(Heading2, line[3..]));
            else if (line.StartsWith("> ", StringComparison.Ordinal))
                document.Blocks.Add(CreateStyledParagraph(Quote, line[2..]));
            else
                document.Blocks.Add(CreateParagraph(line));
            i++;
        }

        if (document.Blocks.Count == 0) document.Blocks.Add(new Paragraph());
        return document;
    }

    private static void WriteBlock(Block block, List<string> output)
    {
        if (block is Paragraph paragraph)
        {
            var content = InlineMarkdown(paragraph.Inlines);
            switch (paragraph.Tag as string)
            {
                case Heading1: output.Add("# " + content); break;
                case Heading2: output.Add("## " + content); break;
                case Quote: output.Add("> " + content); break;
                case Code:
                    output.Add("```");
                    output.Add(new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text.TrimEnd('\r', '\n'));
                    output.Add("```");
                    break;
                case Todo:
                    var checkbox = paragraph.Inlines.OfType<InlineUIContainer>()
                        .Select(i => i.Child).OfType<CheckBox>().FirstOrDefault();
                    output.Add($"- [{(checkbox?.IsChecked == true ? "x" : " ")}] {content}");
                    break;
                default: output.Add(content); break;
            }
            return;
        }

        if (block is List list)
        {
            var number = 1;
            foreach (var item in list.ListItems)
            {
                foreach (var child in item.Blocks)
                {
                    if (child is not Paragraph listParagraph) continue;
                    var prefix = list.MarkerStyle == TextMarkerStyle.Decimal ? $"{number++}. " : "- ";
                    output.Add(prefix + InlineMarkdown(listParagraph.Inlines));
                }
            }
            return;
        }

        if (block is Section section)
            foreach (var child in section.Blocks) WriteBlock(child, output);
        else if (block is BlockUIContainer { Child: Image image } && ImageSource(image) is { } source)
            output.Add($"![]({source})");
    }

    private static string InlineMarkdown(InlineCollection inlines)
    {
        var builder = new StringBuilder();
        foreach (var inline in inlines)
        {
            if (inline is InlineUIContainer { Child: CheckBox }) continue;
            builder.Append(InlineMarkdown(inline));
        }
        return builder.ToString();
    }

    private static string InlineMarkdown(Inline inline)
    {
        string content;
        switch (inline)
        {
            case Run run:
                content = Escape(run.Text);
                break;
            case LineBreak:
                return "  \n";
            case Hyperlink link:
                content = $"[{InlineMarkdown(link.Inlines)}]({link.NavigateUri})";
                break;
            case Span span:
                content = InlineMarkdown(span.Inlines);
                break;
            case InlineUIContainer { Child: Image image }:
                return ImageSource(image) is { } source ? $"![]({source})" : "";
            default:
                return "";
        }

        var localWeight = inline.ReadLocalValue(TextElement.FontWeightProperty);
        var localStyle = inline.ReadLocalValue(TextElement.FontStyleProperty);
        var localDecorations = inline.ReadLocalValue(Inline.TextDecorationsProperty);
        if (inline is Bold || localWeight is FontWeight weight && weight == FontWeights.Bold)
            content = $"**{content}**";
        if (inline is Italic || localStyle is FontStyle style && style == FontStyles.Italic)
            content = $"*{content}*";
        if (localDecorations is TextDecorationCollection decorations &&
            decorations.Any(d => d.Location == TextDecorationLocation.Strikethrough))
            content = $"~~{content}~~";

        var font = inline.ReadLocalValue(TextElement.FontFamilyProperty) as FontFamily;
        var sizeValue = inline.ReadLocalValue(TextElement.FontSizeProperty);
        var size = sizeValue is double value ? value : (double?)null;
        if (font != null || size != null)
        {
            var attributes = new StringBuilder();
            if (font != null) attributes.Append($" data-font=\"{WebUtility.HtmlEncode(font.Source)}\"");
            if (size != null) attributes.Append($" data-size=\"{size.Value.ToString(CultureInfo.InvariantCulture)}\"");
            content = $"<span{attributes}>{content}</span>";
        }
        return content;
    }

    private static string? ImageSource(Image image) => image.Source switch
    {
        BitmapImage { UriSource: not null } bitmap => bitmap.UriSource.AbsoluteUri,
        _ => image.Source?.ToString(),
    };

    private static Paragraph CreateParagraph(string markdown)
    {
        var paragraph = new Paragraph();
        foreach (var inline in ParseInlines(markdown)) paragraph.Inlines.Add(inline);
        return paragraph;
    }

    private static Paragraph CreateStyledParagraph(string style, string content, bool parseInline = true)
    {
        var paragraph = parseInline ? CreateParagraph(content) : new Paragraph(new Run(content));
        paragraph.Tag = style;
        paragraph.FontSize = style == Heading1 ? 24 : style == Heading2 ? 18 : 12;
        paragraph.FontWeight = style is Heading1 or Heading2 ? FontWeights.Bold : FontWeights.Normal;
        paragraph.FontFamily = style == Code ? new FontFamily("Consolas") : new FontFamily("Segoe UI");
        paragraph.Margin = style == Quote ? new Thickness(14, 4, 0, 4) : new Thickness(0);
        paragraph.Background = style == Code
            ? new SolidColorBrush(Color.FromArgb(0x44, 0x00, 0x00, 0x00))
            : Brushes.Transparent;
        return paragraph;
    }

    private static Paragraph CreateTodoParagraph(string markdown, bool isChecked)
    {
        var paragraph = CreateParagraph(markdown);
        var checkbox = new CheckBox
        {
            IsChecked = isChecked,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };
        var container = new InlineUIContainer(checkbox);
        if (paragraph.Inlines.FirstInline == null) paragraph.Inlines.Add(container);
        else paragraph.Inlines.InsertBefore(paragraph.Inlines.FirstInline, container);
        paragraph.Tag = Todo;
        return paragraph;
    }

    private static IEnumerable<Inline> ParseInlines(string markdown)
    {
        var result = new List<Inline>();
        for (var i = 0; i < markdown.Length;)
        {
            if (markdown[i] == '\\' && i + 1 < markdown.Length)
            {
                AddText(result, markdown[i + 1].ToString());
                i += 2;
                continue;
            }

            if (markdown.AsSpan(i).StartsWith("<span ", StringComparison.Ordinal))
            {
                var close = markdown.IndexOf("</span>", i, StringComparison.Ordinal);
                if (close > i)
                {
                    var whole = markdown[i..(close + 7)];
                    var match = FontSpanRegex().Match(whole);
                    if (match.Success)
                    {
                        var span = new Span();
                        foreach (var child in ParseInlines(match.Groups[3].Value)) span.Inlines.Add(child);
                        if (match.Groups[1].Success)
                            span.FontFamily = new FontFamily(WebUtility.HtmlDecode(match.Groups[1].Value));
                        if (match.Groups[2].Success &&
                            double.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var size))
                            span.FontSize = size;
                        result.Add(span);
                        i = close + 7;
                        continue;
                    }
                }
            }

            if (markdown.AsSpan(i).StartsWith("![](", StringComparison.Ordinal))
            {
                var close = markdown.IndexOf(')', i + 4);
                if (close > i)
                {
                    var value = markdown[(i + 4)..close];
                    if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
                    {
                        try
                        {
                            result.Add(new InlineUIContainer(new Image
                            {
                                Source = new BitmapImage(uri),
                                MaxWidth = 260,
                                Stretch = Stretch.Uniform,
                                Margin = new Thickness(0, 4, 0, 4),
                            }));
                        }
                        catch { AddText(result, value); }
                    }
                    i = close + 1;
                    continue;
                }
            }

            if (markdown[i] == '[')
            {
                var labelEnd = markdown.IndexOf("](", i, StringComparison.Ordinal);
                var uriEnd = labelEnd >= 0 ? markdown.IndexOf(')', labelEnd + 2) : -1;
                if (labelEnd > i && uriEnd > labelEnd &&
                    Uri.TryCreate(markdown[(labelEnd + 2)..uriEnd], UriKind.Absolute, out var uri))
                {
                    var link = new Hyperlink { NavigateUri = uri };
                    foreach (var child in ParseInlines(markdown[(i + 1)..labelEnd])) link.Inlines.Add(child);
                    result.Add(link);
                    i = uriEnd + 1;
                    continue;
                }
            }

            if (TryDelimited(markdown, i, "**", out var boldInner, out var boldEnd))
            {
                var bold = new Bold();
                foreach (var child in ParseInlines(boldInner)) bold.Inlines.Add(child);
                result.Add(bold);
                i = boldEnd;
                continue;
            }
            if (TryDelimited(markdown, i, "~~", out var strikeInner, out var strikeEnd))
            {
                var span = new Span { TextDecorations = TextDecorations.Strikethrough };
                foreach (var child in ParseInlines(strikeInner)) span.Inlines.Add(child);
                result.Add(span);
                i = strikeEnd;
                continue;
            }
            if (TryDelimited(markdown, i, "*", out var italicInner, out var italicEnd))
            {
                var italic = new Italic();
                foreach (var child in ParseInlines(italicInner)) italic.Inlines.Add(child);
                result.Add(italic);
                i = italicEnd;
                continue;
            }

            AddText(result, markdown[i].ToString());
            i++;
        }
        return result;
    }

    private static bool TryDelimited(string text, int start, string delimiter, out string inner, out int next)
    {
        inner = "";
        next = start;
        if (!text.AsSpan(start).StartsWith(delimiter, StringComparison.Ordinal)) return false;
        var close = text.IndexOf(delimiter, start + delimiter.Length, StringComparison.Ordinal);
        if (close < start + delimiter.Length) return false;
        inner = text[(start + delimiter.Length)..close];
        next = close + delimiter.Length;
        return true;
    }

    private static void AddText(List<Inline> inlines, string text)
    {
        if (inlines.LastOrDefault() is Run run) run.Text += text;
        else inlines.Add(new Run(text));
    }

    private static string Escape(string text) => text
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("*", "\\*", StringComparison.Ordinal)
        .Replace("~", "\\~", StringComparison.Ordinal)
        .Replace("[", "\\[", StringComparison.Ordinal)
        .Replace("<", "\\<", StringComparison.Ordinal);
}
