using CabinetOS.Core.Updates;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace CabinetOS.Views;

/// <summary>
/// The update dialog (docs/ui.md, "Updates"): the version, the release notes rendered natively
/// in a RichTextBlock (ADR 0014: no WebView2), a link to the whole changelog, and Restart now /
/// Later. What it shows comes from <see cref="UpdateText.Dialog"/>; the window shows it with its
/// own ShowDialogAsync, so no command runs while it is open.
/// </summary>
public static class UpdateDialog
{
    // The notes scroll inside the dialog past this height.
    private const double NotesHeight = 360;

    /// <summary>A dialog for <paramref name="view"/>; the primary button is Restart now when a version waits.</summary>
    public static ContentDialog Create(UpdateDialogView view, XamlRoot root, ElementTheme theme)
    {
        var content = new StackPanel { Spacing = 12, MaxWidth = 520, MinWidth = 400 };
        content.Children.Add(new TextBlock
        {
            Text = view.Subtitle,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeResources.Brush("CbStatusTextBrush"),
        });
        if (view.Notes.Count > 0)
        {
            content.Children.Add(new ScrollViewer
            {
                MaxHeight = NotesHeight,
                Padding = new Thickness(0, 0, 12, 0),
                Content = Notes(view.Notes),
            });
        }
        if (view.NotesMissing is { } missing)
        {
            content.Children.Add(new TextBlock { Text = missing, TextWrapping = TextWrapping.Wrap, Foreground = ThemeResources.Brush("CbDialogTextBrush") });
        }
        var links = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(-12, 0, 0, 0) };
        if (view.NotesMissing is not null && view.NotesUrl is { } notesUrl && Uri.TryCreate(notesUrl, UriKind.Absolute, out var notes))
        {
            links.Children.Add(new HyperlinkButton { Content = "Release notes in the browser", NavigateUri = notes });
        }
        if (Uri.TryCreate(view.ChangelogUrl, UriKind.Absolute, out var changelog))
        {
            var link = new HyperlinkButton { Content = "Full changelog", NavigateUri = changelog };
            ToolTipService.SetToolTip(link, view.ChangelogUrl);
            links.Children.Add(link);
        }
        content.Children.Add(links);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            RequestedTheme = theme,
            Title = view.Title,
            Content = content,
            CloseButtonText = view.CloseText,
            DefaultButton = view.RestartText is null ? ContentDialogButton.Close : ContentDialogButton.Primary,
        };
        if (view.RestartText is { } restart)
        {
            dialog.PrimaryButtonText = restart;
        }
        return dialog;
    }

    /// <summary>The notes as one RichTextBlock: a paragraph per block, links that open in the browser.</summary>
    public static RichTextBlock Notes(IReadOnlyList<NoteBlock> blocks)
    {
        var text = new RichTextBlock
        {
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeResources.Brush("CbDialogTextBrush"),
        };
        var mono = (FontFamily)ThemeResources.Get("CbMonoFont")!;
        foreach (var block in blocks)
        {
            var paragraph = new Paragraph();
            switch (block.Kind)
            {
                case NoteBlockKind.Heading:
                    paragraph.FontWeight = FontWeights.SemiBold;
                    paragraph.FontSize = block.Level <= 2 ? 16 : 14;
                    paragraph.Margin = new Thickness(0, 10, 0, 4);
                    paragraph.Foreground = ThemeResources.Brush("CbTextPrimaryBrush");
                    break;
                case NoteBlockKind.ListItem:
                    // A negative first-line indent could hang the marker, but WinUI's Paragraph may refuse one at run time,
                    // which no test here would show: the wrapped lines start under the marker instead.
                    paragraph.Margin = new Thickness(4 + (18 * block.Level), 0, 0, 4);
                    paragraph.Inlines.Add(new Run { Text = block.Marker + "\u2002" });
                    break;
                case NoteBlockKind.Code:
                    paragraph.FontFamily = mono;
                    paragraph.FontSize = 12;
                    paragraph.Margin = new Thickness(0, 4, 0, 8);
                    break;
                default:
                    paragraph.Margin = new Thickness(0, 0, 0, 8);
                    break;
            }
            foreach (var span in block.Spans)
            {
                paragraph.Inlines.Add(Inline(span, mono));
            }
            text.Blocks.Add(paragraph);
        }
        return text;
    }

    private static Inline Inline(NoteSpan span, FontFamily mono)
    {
        switch (span.Kind)
        {
            case NoteSpanKind.Link when Uri.TryCreate(span.Url, UriKind.Absolute, out var uri):
                var link = new Hyperlink { NavigateUri = uri };
                link.Inlines.Add(new Run { Text = span.Text });
                return link;
            case NoteSpanKind.Bold:
                return new Run { Text = span.Text, FontWeight = FontWeights.SemiBold };
            case NoteSpanKind.Italic:
                return new Run { Text = span.Text, FontStyle = Windows.UI.Text.FontStyle.Italic };
            case NoteSpanKind.Code:
                return new Run { Text = span.Text, FontFamily = mono, FontSize = 12.5 };
            default:
                return new Run { Text = span.Text };
        }
    }
}
