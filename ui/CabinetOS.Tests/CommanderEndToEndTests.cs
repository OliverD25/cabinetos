using CabinetOS.Core.Ipc;
using CabinetOS.Core.Listing;
using CabinetOS.Core.Prompts;
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

    [Fact]
    public async Task Space_and_shift_alt_enter_count_a_folder_in_the_core_and_the_size_column_gets_the_total()
    {
        var coreExe = EndToEndTests.FindCoreOrSkip();
        var root = Repo.NewTempFolder("e2e-measure");
        try
        {
            var photos = Directory.CreateDirectory(Path.Combine(root, "files", "photos")).FullName;
            Directory.CreateDirectory(Path.Combine(photos, "2026"));
            File.WriteAllBytes(Path.Combine(photos, "a.jpg"), new byte[1000]);
            File.WriteAllBytes(Path.Combine(photos, "2026", "b.jpg"), new byte[2500]);
            var empty = Directory.CreateDirectory(Path.Combine(root, "files", "empty")).FullName;
            await using var core = await EndToEndTests.StartCoreAsync(coreExe, root);
            await core.Client.HelloAsync();

            var sizes = new FolderSizes();
            var started = await core.Client.RequestAsync<MeasureStartedReply>(new MeasurePathsRequest([photos, empty]));
            Assert.Equal([photos, empty], sizes.Start(started.MeasureId, [photos, empty]));

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (sizes.IsMeasuring)
            {
                var coreEvent = await core.Client.Events.ReadAsync(timeout.Token);
                switch (coreEvent)
                {
                    case MeasureProgressEvent progress:
                        Assert.True(sizes.Apply(progress));
                        break;
                    case MeasureFinishedEvent finished:
                        Assert.False(finished.Cancelled);
                        Assert.True(sizes.Apply(finished));
                        break;
                }
            }
            Assert.Equal(new FolderSize(3500, 2, 1, 0, Done: true), sizes.Get("photos"));
            Assert.Equal(new FolderSize(0, 0, 0, 0, Done: true), sizes.Get("empty"));

            // A cancel may cross the end of the measure: the answer is ok all the same.
            Assert.IsType<OkReply>(await core.Client.RequestAsync(new CancelMeasureRequest(started.MeasureId)));
            await core.ShutdownAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            Repo.RemoveTempFolder(root);
        }
    }

    [Fact]
    public async Task The_pattern_box_the_same_extension_and_quick_search_ask_the_core_which_rows_match()
    {
        var coreExe = EndToEndTests.FindCoreOrSkip();
        var root = Repo.NewTempFolder("e2e-match");
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(root, "listing")).FullName;
            Directory.CreateDirectory(Path.Combine(folder, "docs.md"));
            foreach (var name in new[] { "a.md", "B.MD", "notes.md", "LICENSE", "report.txt", "report-2.txt" })
            {
                File.WriteAllBytes(Path.Combine(folder, name), []);
            }
            await using var core = await EndToEndTests.StartCoreAsync(coreExe, root);
            await core.Client.HelloAsync();
            var opened = await core.Client.RequestAsync<ListingOpenedReply>(new ListDirectoryRequest(folder));
            using var view = ListingView.Open(opened.TakeSection()!, opened.SectionSize);

            async Task<string[]> Match(string patterns, bool filesOnly, uint? firstFrom = null)
            {
                var matches = await core.Client.RequestAsync<EntryMatchesReply>(
                    new MatchEntriesRequest(opened.ListingId, patterns) { FilesOnly = filesOnly, FirstFrom = firstFrom });
                Assert.Equal(view.Generation, matches.Generation);
                return [.. EntryRanges.Rows(matches.Ranges, view.Count).Select(view.Name)];
            }

            // Num +: the default "Include folders" off leaves the folder docs.md out; case is ignored; | leaves out.
            Assert.Equal(["a.md", "B.MD"], await Match("*.md|notes*", filesOnly: true));
            Assert.Equal(["docs.md", "a.md", "B.MD", "notes.md"], await Match("*.md", filesOnly: false));
            // Alt+Num +: the same extension, and Total Commander's *. for a name without one.
            Assert.Equal(["report-2.txt", "report.txt"], (await Match(PatternHistory.SameExtension("report.txt"), filesOnly: true)).Order(StringComparer.Ordinal));
            Assert.Equal(["LICENSE"], await Match(PatternHistory.SameExtension("LICENSE"), filesOnly: true));
            // Quick search: the first name that starts so, from the cursor on, round to the start.
            var reports = await Match("rep*", filesOnly: false, firstFrom: 0);
            Assert.Single(reports);
            Assert.StartsWith("report", reports[0]);
            Assert.Equal(["a.md"], await Match("a*", filesOnly: false, firstFrom: (uint)(view.Count - 1)));
            Assert.Empty(await Match("zzz*", filesOnly: false, firstFrom: 0));
            await core.ShutdownAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            Repo.RemoveTempFolder(root);
        }
    }

    [Fact]
    public async Task Shift_f4_makes_an_empty_file_once_and_f4_never_falls_back_from_a_missing_editor()
    {
        var coreExe = EndToEndTests.FindCoreOrSkip();
        var root = Repo.NewTempFolder("e2e-files");
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(root, "files")).FullName;
            Directory.CreateDirectory(Path.Combine(folder, "notes"));
            Directory.CreateDirectory(Path.Combine(root, "config"));
            // An editor found nowhere: edit_path must say so and try nothing else, so no Notepad opens here.
            File.WriteAllText(Path.Combine(root, "config", "cabinetos.json"),
                """{"version":1,"files":{"editor":{"command":"cabinetos-no-such-editor.exe","args":["--wait"]}}}""");
            await using var core = await EndToEndTests.StartCoreAsync(coreExe, root);
            await core.Client.HelloAsync();

            var path = Path.Combine(folder, "New Text Document.txt");
            Assert.IsType<OkReply>(await core.Client.RequestAsync(new CreateFileRequest(path)));
            Assert.Equal(0, new FileInfo(path).Length);
            // A taken name, a folder's too, is already_exists: the window then opens what has the name.
            Assert.Equal(ErrorCodes.AlreadyExists, Assert.IsType<ErrorReply>(await core.Client.RequestAsync(new CreateFileRequest(path))).Code);
            Assert.Equal(ErrorCodes.AlreadyExists, Assert.IsType<ErrorReply>(await core.Client.RequestAsync(new CreateFileRequest(Path.Combine(folder, "notes")))).Code);

            Assert.Equal(ErrorCodes.SpawnFailed, Assert.IsType<ErrorReply>(await core.Client.RequestAsync(new EditPathRequest(path))).Code);
            // A folder or a missing file is never edited. (This core looks for the editor first,
            // so with this setting both say spawn_failed rather than invalid_path and not_found.)
            Assert.IsType<ErrorReply>(await core.Client.RequestAsync(new EditPathRequest(Path.Combine(folder, "notes"))));
            Assert.IsType<ErrorReply>(await core.Client.RequestAsync(new EditPathRequest(Path.Combine(folder, "gone.txt"))));

            // Windows' own sheet only for paths that are there (a sheet itself would open a window here).
            Assert.Equal(ErrorCodes.NotFound, Assert.IsType<ErrorReply>(await core.Client.RequestAsync(new ShowPropertiesRequest([path, Path.Combine(folder, "gone.txt")]))).Code);
            Assert.Equal(ErrorCodes.InvalidPath, Assert.IsType<ErrorReply>(await core.Client.RequestAsync(new ShowPropertiesRequest(["relative.txt"]))).Code);
            Assert.Equal(ErrorCodes.ProtocolError, Assert.IsType<ErrorReply>(await core.Client.RequestAsync(new ShowPropertiesRequest([]))).Code);
            await core.ShutdownAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            Repo.RemoveTempFolder(root);
        }
    }
}
