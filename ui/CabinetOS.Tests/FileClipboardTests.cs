using CabinetOS.Core.Files;
using CabinetOS.Core.Protocol;

namespace CabinetOS.Tests;

/// <summary>The in-app clipboard of paths behind Ctrl+X, Ctrl+C and Ctrl+V.</summary>
public class FileClipboardTests
{
    [Fact]
    public void Copied_paths_paste_as_a_copy_as_often_as_wanted()
    {
        var clipboard = new FileClipboard();
        var text = clipboard.Set([@"C:\a.txt", @"C:\b.txt"], ClipboardMode.Copy);

        Assert.Equal("C:\\a.txt\r\nC:\\b.txt", text);
        var plan = clipboard.Plan(@"D:\dst");
        Assert.NotNull(plan);
        Assert.Equal((JobKind.Copy, @"D:\dst"), (plan.Kind, plan.Destination));
        Assert.Equal([@"C:\a.txt", @"C:\b.txt"], plan.Sources);

        clipboard.OnPasted();
        Assert.False(clipboard.IsEmpty);
    }

    [Fact]
    public void Cut_paths_paste_as_a_move_once()
    {
        var clipboard = new FileClipboard();
        clipboard.Set([@"C:\a.txt"], ClipboardMode.Cut);

        Assert.Equal(JobKind.Move, clipboard.Plan(@"D:\dst")!.Kind);
        clipboard.OnPasted();
        Assert.True(clipboard.IsEmpty);
        Assert.Null(clipboard.Plan(@"D:\dst"));
    }

    [Fact]
    public void Other_text_on_the_windows_clipboard_empties_the_paths()
    {
        var clipboard = new FileClipboard();
        var text = clipboard.Set([@"C:\a.txt"], ClipboardMode.Copy);
        var changes = 0;
        clipboard.Changed += () => changes++;

        Assert.True(clipboard.KeepIfSystemTextIs(text));
        Assert.False(clipboard.IsEmpty);
        Assert.False(clipboard.KeepIfSystemTextIs("something else"));
        Assert.True(clipboard.IsEmpty);
        Assert.False(clipboard.KeepIfSystemTextIs(null));
        Assert.Equal(1, changes);
    }
}
