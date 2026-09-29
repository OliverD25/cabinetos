using CabinetOS.Core.Ipc;
using CabinetOS.Core.Listing;
using CabinetOS.Core.Protocol;
using CabinetOS.Tests.Support;

namespace CabinetOS.Tests;

/// <summary>
/// Sub-phase 11a's requests against the real core (protocol 12): what the
/// window sends for Total Commander's keys is what the core answers. Runs
/// when the repository's core is built, like <see cref="EndToEndTests"/>.
/// </summary>
[Collection(HandleTests.Name)]
public class CommanderEndToEndTests
{
    [Fact]
    public async Task Ctrl_f4_lists_the_files_by_extension_with_the_folders_first()
    {
        var coreExe = EndToEndTests.FindCoreOrSkip();
        var root = Repo.NewTempFolder("e2e-sort");
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(root, "listing")).FullName;
            Directory.CreateDirectory(Path.Combine(folder, "z.dir"));
            foreach (var name in new[] { "b.txt", "a.md", "c", "d.TXT" })
            {
                File.WriteAllBytes(Path.Combine(folder, name), []);
            }
            await using var core = await EndToEndTests.StartCoreAsync(coreExe, root);
            await core.Client.HelloAsync();

            var sorted = PaneSort.Next(new SortSpec(PaneSort.Name, false), PaneSort.Extension);
            var opened = await core.Client.RequestAsync<ListingOpenedReply>(new ListDirectoryRequest(folder) { Sort = sorted });
            using var view = ListingView.Open(opened.TakeSection()!, opened.SectionSize);

            // Folders first; then no extension, then by extension ignoring case, then by name.
            Assert.Equal(["z.dir", "c", "a.md", "b.txt", "d.TXT"], Enumerable.Range(0, view.Count).Select(view.Name));

            var reversed = await core.Client.RequestAsync<ListingOpenedReply>(new ListDirectoryRequest(folder) { Sort = PaneSort.Next(sorted, PaneSort.Extension) });
            using var back = ListingView.Open(reversed.TakeSection()!, reversed.SectionSize);
            Assert.Equal("z.dir", back.Name(0));
            Assert.Equal("c", back.Name(back.Count - 1));
            await core.ShutdownAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            Repo.RemoveTempFolder(root);
        }
    }
}
