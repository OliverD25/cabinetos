using System.Reflection;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Platform;
using CabinetOS.Core.Presentation;
using CabinetOS.Core.Protocol;
using CabinetOS.Core.Updates;
using CabinetOS.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CabinetOS;

// Help: About CabinetOS (help.about; docs/ui.md, "About"): the versions, the build, and the
// documents that come with a release. A dialog: Esc closes it, and no command runs while it is open.
public sealed partial class MainWindow
{
    private void RegisterAboutCommand() =>
        // A core that lists help.about for the window runs this; one that runs it itself answers
        // command_result, which OnCommandCompleted brings here too.
        _router.RegisterUiHandler("help.about", _ => ShowAboutAsync());

    private async Task ShowAboutAsync()
    {
        // One small file next to the program, read off the UI thread; with tool.json the only
        // files the window reads itself (brief §1).
        var release = await Task.Run(() => ReleaseFolder.Read(AppContext.BaseDirectory));
        PongReply? core = null;
        try
        {
            core = await _session.RequestAsync(new PingRequest()) as PongReply;
        }
        catch (IOException unreachable)
        {
            Diag.Info(Target, "about: the core did not answer ping", new LogField("error", unreachable.Message));
        }

        // The updater's state as the core sent it last, with the dot while a version waits (Phase 17).
        var update = UpdateText.AboutRow(_update.Status, _update.Progress, DateTime.Now);
        List<(string Label, string Value)> rows = [.. AboutText.Rows(Program.Version, core, release), ("Update", update.Text)];
        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 6, MinWidth = 360 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < rows.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = rows[i].Label, Foreground = ThemeResources.Brush("CbTextTertiaryBrush") };
            var value = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            if (i == rows.Count - 1 && update.Dot)
            {
                value.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "\u25CF ", Foreground = ThemeResources.Brush("CbAccentBrush") });
            }
            value.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = rows[i].Value });
            Grid.SetRow(label, i);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            grid.Children.Add(label);
            grid.Children.Add(value);
        }

        var error = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeResources.Brush("CbErrorTextBrush"),
            Visibility = Visibility.Collapsed,
        };
        var content = new StackPanel { Spacing = 12, MaxWidth = 440 };
        content.Children.Add(grid);
        if (release is { LicensePath: { } license, NoticesPath: { } notices })
        {
            // The links' own padding: moved left so their text lines up with the rows.
            var documents = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(-12, 0, 0, 0) };
            documents.Children.Add(DocumentLink("License", license, error));
            documents.Children.Add(DocumentLink("Third-party notices", notices, error));
            content.Children.Add(documents);
        }
        else
        {
            content.Children.Add(new TextBlock
            {
                Text = AboutText.MissingDocuments(release),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = ThemeResources.Brush("CbStatusTextBrush"),
            });
        }
        content.Children.Add(error);
        if (typeof(Program).Assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright is { Length: > 0 } copyright)
        {
            content.Children.Add(new TextBlock { Text = copyright, FontSize = 12, Foreground = ThemeResources.Brush("CbStatusTextBrush") });
        }
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = "About CabinetOS",
            Content = content,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
        };
        Diag.Info(Target, "about shown", new LogField("version", Program.Version), new LogField("core", core?.CoreVersion ?? ""),
            new LogField("protocol", core?.ProtocolVersion ?? 0), new LogField("release", release.HasReleaseFile), new LogField("update", update.Text));
        await ShowDialogAsync(dialog);
        FocusActivePane();
    }

    // A document next to the program, opened with its default application by the core (open_path);
    // the dialog's own link, so it does not go through the router, which runs nothing while the dialog is open.
    private HyperlinkButton DocumentLink(string title, string path, TextBlock error)
    {
        var link = new HyperlinkButton { Content = title };
        ToolTipService.SetToolTip(link, path);
        link.Click += async (_, _) =>
        {
            error.Visibility = Visibility.Collapsed;
            if (_session.CoreProcessId is { } corePid)
            {
                WindowsPlatform.AllowForeground(corePid);
            }
            CoreReply? reply;
            try
            {
                reply = await _session.RequestAsync(new OpenPathRequest(path));
            }
            catch (IOException failure)
            {
                reply = new ErrorReply(ErrorCodes.Io, failure.Message);
            }
            if (reply is ErrorReply refused)
            {
                error.Text = $"Cannot open {Path.GetFileName(path)}: {refused.Message}";
                error.Visibility = Visibility.Visible;
            }
        };
        return link;
    }
}
