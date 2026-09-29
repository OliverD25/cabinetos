using CabinetOS.Core.Listing;

namespace CabinetOS.Tests;

/// <summary>The drive list under a pane (Alt+F1, Alt+F2): where a drive's row takes that pane.</summary>
public class DriveMemoryTests
{
    [Fact]
    public void A_drive_goes_to_the_folder_this_pane_last_showed_there_else_to_its_root()
    {
        var memory = new DriveMemory();
        Assert.Equal(@"D:\", memory.FolderOn('D'));

        memory.Remember(@"D:\work\docs");
        memory.Remember(@"C:\Windows");
        memory.Remember(@"d:\music");

        Assert.Equal(@"d:\music", memory.FolderOn('D'));
        Assert.Equal(@"d:\music", memory.FolderOn('d'));
        Assert.Equal(@"C:\Windows", memory.FolderOn('C'));
        Assert.Equal(@"E:\", memory.FolderOn('E'));
    }

    [Theory]
    [InlineData(@"C:\Users\me", 'C')]
    [InlineData(@"e:\", 'E')]
    [InlineData(@"\\?\D:\very\long", 'D')]
    [InlineData(@"\\nas\media\films", null)]
    [InlineData(@"\\?\UNC\nas\media", null)]
    [InlineData("", null)]
    public void A_folder_is_on_the_drive_its_letter_names(string path, char? letter) =>
        Assert.Equal(letter, DriveMemory.LetterOf(path));

    [Fact]
    public void A_share_is_not_remembered_as_a_drive()
    {
        var memory = new DriveMemory();
        memory.Remember(@"\\nas\media\films");
        Assert.Equal(@"N:\", memory.FolderOn('N'));
    }
}
