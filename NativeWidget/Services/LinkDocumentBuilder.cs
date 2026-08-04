using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;

namespace NativeWidget.Services;

public static class LinkDocumentBuilder
{
    public static FlowDocument Build(string text, FrameworkElement resourceOwner, string emptyText = "No content")
    {
        var paragraph = new Paragraph { Margin = new Thickness(0), LineHeight = 19 };
        if (string.IsNullOrWhiteSpace(text))
        {
            paragraph.Inlines.Add(new Run(emptyText)
            {
                Foreground = (Brush)resourceOwner.FindResource("MutedBrush"),
                FontStyle = FontStyles.Italic,
            });
        }
        else
        {
            var cursor = 0;
            foreach (var link in LinkDetection.Find(text))
            {
                if (link.Index > cursor) paragraph.Inlines.Add(new Run(text[cursor..link.Index]));
                var hyperlink = new Hyperlink(new Run(link.Text))
                {
                    NavigateUri = link.Target,
                    Foreground = (Brush)resourceOwner.FindResource("AccentBrush"),
                    TextDecorations = TextDecorations.Underline,
                };
                hyperlink.RequestNavigate += OpenLink;
                paragraph.Inlines.Add(hyperlink);
                cursor = link.Index + link.Length;
            }
            if (cursor < text.Length) paragraph.Inlines.Add(new Run(text[cursor..]));
        }

        return new FlowDocument(paragraph)
        {
            PagePadding = new Thickness(0),
            Background = Brushes.Transparent,
        };
    }

    private static void OpenLink(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
