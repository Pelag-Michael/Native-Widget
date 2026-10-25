using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdTable = Markdig.Extensions.Tables.Table;

namespace NativeWidget.Services;

// Native text only: no WebView, focusable controls, image requests or HTML execution.
internal static class HoverAiMarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseEmphasisExtras(Markdig.Extensions.EmphasisExtras.EmphasisExtraOptions.Strikethrough)
        .UsePipeTables().DisableHtml().Build();

    internal static void Render(TextBlock target, string markdown)
    {
        target.Inlines.Clear();
        var document = Markdown.Parse(markdown, Pipeline);
        AppendBlocks(target.Inlines, document, target.FontSize);
    }

    private static void AppendBlocks(InlineCollection output, ContainerBlock blocks, double fontSize)
    {
        var first = true;
        foreach (var block in blocks)
        {
            if (!first) output.Add(new LineBreak());
            first = false;
            switch (block)
            {
                case HeadingBlock heading:
                    var title = new Span { FontWeight = FontWeights.Bold, FontSize = fontSize * (heading.Level <= 2 ? 1.25 : 1.1) };
                    AppendInlines(title.Inlines, heading.Inline); output.Add(title);
                    break;
                case CodeBlock code:
                    output.Add(new Run(code.Lines.ToString()) { FontFamily = new FontFamily("Consolas") });
                    break;
                case ListBlock list:
                    _ = int.TryParse(list.OrderedStart?.ToString(), out var number);
                    if (number < 1) number = 1;
                    var firstItem = true;
                    foreach (var item in list.OfType<ListItemBlock>())
                    {
                        if (!firstItem) output.Add(new LineBreak());
                        firstItem = false;
                        output.Add(new Run(list.IsOrdered ? $"{number++}. " : "\u2022 "));
                        AppendBlocks(output, item, fontSize);
                    }
                    break;
                case QuoteBlock quote:
                    var quotation = new Span { FontStyle = FontStyles.Italic };
                    quotation.Inlines.Add(new Run("\u2502 "));
                    AppendBlocks(quotation.Inlines, quote, fontSize); output.Add(quotation);
                    break;
                case MdTable table:
                    // Rows wrap naturally even in the user's extremely narrow text frame.
                    foreach (var row in table.OfType<Markdig.Extensions.Tables.TableRow>())
                    {
                        if (row != table.First()) output.Add(new LineBreak());
                        var cells = row.OfType<Markdig.Extensions.Tables.TableCell>().ToArray();
                        for (var i = 0; i < cells.Length; i++)
                        {
                            if (i > 0) output.Add(new Run("  |  "));
                            var cell = new Span { FontWeight = row.IsHeader ? FontWeights.Bold : FontWeights.Normal };
                            AppendBlocks(cell.Inlines, cells[i], fontSize); output.Add(cell);
                        }
                    }
                    break;
                case LeafBlock leaf when leaf.Inline != null:
                    AppendInlines(output, leaf.Inline);
                    break;
                case ThematicBreakBlock:
                    output.Add(new Run("\u2500\u2500\u2500"));
                    break;
                case ContainerBlock container:
                    AppendBlocks(output, container, fontSize);
                    break;
            }
        }
    }

    private static void AppendInlines(InlineCollection output, ContainerInline? container)
    {
        if (container == null) return;
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal: output.Add(new Run(literal.Content.ToString())); break;
                case CodeInline code: output.Add(new Run(code.Content) { FontFamily = new FontFamily("Consolas") }); break;
                case LineBreakInline line:
                    if (line.IsHard) output.Add(new LineBreak()); else output.Add(new Run(" "));
                    break;
                case EmphasisInline emphasis:
                    Span span = emphasis.DelimiterChar == '~'
                        ? new Span { TextDecorations = TextDecorations.Strikethrough }
                        : emphasis.DelimiterCount >= 2 ? new Bold() : new Italic();
                    AppendInlines(span.Inlines, emphasis); output.Add(span);
                    break;
                case LinkInline link:
                    // Keep links and image alt text readable without launching anything.
                    var label = new Span(); AppendInlines(label.Inlines, link); output.Add(label);
                    break;
                case AutolinkInline link: output.Add(new Run(link.Url)); break;
                case ContainerInline nested: AppendInlines(output, nested); break;
            }
        }
    }
}
