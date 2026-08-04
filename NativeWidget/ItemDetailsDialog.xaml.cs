using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Navigation;
using System.Windows.Media;
using NativeWidget.Services;

namespace NativeWidget;

public partial class ItemDetailsDialog : Window
{
    private readonly string _originalDescription;
    private readonly string? _externalUrl;
    private bool _editing;

    public bool DescriptionChanged { get; private set; }
    public string Description => DescriptionEditor.Text.Trim();

    private ItemDetailsDialog(string title, string metadata, string description,
        string externalButtonText, string? externalUrl, bool canEditDescription)
    {
        InitializeComponent();
        TitleText.Text = title;
        MetadataText.Text = metadata;
        _originalDescription = description;
        _externalUrl = externalUrl;
        DescriptionEditor.Text = description;
        RenderDescription(description);
        EditButton.Visibility = canEditDescription ? Visibility.Visible : Visibility.Collapsed;
        ExternalButton.Content = externalButtonText;
        ExternalButton.Visibility = string.IsNullOrWhiteSpace(externalUrl) ? Visibility.Collapsed : Visibility.Visible;
    }

    public static ItemDetailsDialog Show(Window owner, string title, string metadata, string description,
        string externalButtonText, string? externalUrl, bool canEditDescription)
    {
        var dialog = new ItemDetailsDialog(title, metadata, description, externalButtonText, externalUrl, canEditDescription)
        {
            Owner = owner,
        };
        dialog.ShowDialog();
        return dialog;
    }

    private void RenderDescription(string text)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0), LineHeight = 19 };
        if (string.IsNullOrWhiteSpace(text))
        {
            paragraph.Inlines.Add(new Run("No description")
            {
                Foreground = (Brush)FindResource("MutedBrush"),
                FontStyle = FontStyles.Italic,
            });
        }
        else
        {
            var cursor = 0;
            foreach (var link in LinkDetection.Find(text))
            {
                if (link.Index > cursor)
                    paragraph.Inlines.Add(new Run(text[cursor..link.Index]));
                var hyperlink = new Hyperlink(new Run(link.Text))
                {
                    NavigateUri = link.Target,
                    Foreground = (Brush)FindResource("AccentBrush"),
                    TextDecorations = TextDecorations.Underline,
                };
                hyperlink.RequestNavigate += Link_RequestNavigate;
                paragraph.Inlines.Add(hyperlink);
                cursor = link.Index + link.Length;
            }
            if (cursor < text.Length)
                paragraph.Inlines.Add(new Run(text[cursor..]));
        }
        DescriptionView.Document = new FlowDocument(paragraph)
        {
            PagePadding = new Thickness(0),
            Background = Brushes.Transparent,
        };
    }

    private static void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void ExternalButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_externalUrl))
            Process.Start(new ProcessStartInfo(_externalUrl) { UseShellExecute = true });
    }

    private void EditButton_Click(object sender, RoutedEventArgs e) => SetEditing(true);

    private void CancelEditButton_Click(object sender, RoutedEventArgs e)
    {
        DescriptionEditor.Text = _originalDescription;
        SetEditing(false);
    }

    private void SetEditing(bool editing)
    {
        _editing = editing;
        DescriptionView.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
        DescriptionEditor.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        EditButton.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
        CancelEditButton.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        CloseButton.Content = editing ? "Save" : "Close";
        if (editing)
        {
            DescriptionEditor.Focus();
            DescriptionEditor.CaretIndex = DescriptionEditor.Text.Length;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_editing)
        {
            DescriptionChanged = !string.Equals(Description, _originalDescription.Trim(), StringComparison.Ordinal);
            DialogResult = true;
        }
        else
        {
            DialogResult = false;
        }
    }
}
