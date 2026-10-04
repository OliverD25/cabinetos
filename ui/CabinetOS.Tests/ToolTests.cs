using System.Text.Json;
using CabinetOS.Core.Keys;
using CabinetOS.Core.Protocol;
using CabinetOS.Core.Tools;
using CabinetOS.Tests.Support;
using Json.Schema;

namespace CabinetOS.Tests;

/// <summary>Tool Extensions: the manifest, the catalog, and the messages between the window and a tool's page.</summary>
public class ToolTests
{
    private const string Valid = """
        {
          "id": "markdown-preview",
          "name": "Markdown Preview",
          "version": "0.1.0",
          "author": "CabinetOS",
          "description": "Shows Markdown.",
          "entry": "index.html",
          "accepts": ["*.md", "README"],
          "placement": "pane"
        }
        """;

    [Fact]
    public void The_shipped_markdown_preview_is_a_valid_tool_for_the_window_and_the_schema()
    {
        var folder = Path.Combine(Repo.Tools, "markdown-preview");
        var json = File.ReadAllText(Path.Combine(folder, "tool.json"));

        var manifest = ToolManifest.Parse(json, "markdown-preview");
        Assert.Equal(("markdown-preview", "Markdown Preview", "index.html", "pane"), (manifest.Id, manifest.Name, manifest.Entry, manifest.Placement));
        Assert.Equal(["*.md", "*.markdown"], manifest.Accepts);
        Assert.True(File.Exists(Path.Combine(folder, manifest.Entry)));

        var result = Schemas.Tool.Evaluate(JsonDocument.Parse(json).RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(result.IsValid);
        Assert.False(Schemas.Tool.Evaluate(JsonDocument.Parse(Valid.Replace("\"pane\"", "\"window\"", StringComparison.Ordinal)).RootElement).IsValid);
        Assert.False(Schemas.Tool.Evaluate(JsonDocument.Parse(Valid.Replace("\"id\"", "\"colour\": 1, \"id\"", StringComparison.Ordinal)).RootElement).IsValid);
    }

    [Theory]
    [InlineData("image-viewer", 18)]
    [InlineData("media-viewer", 13)]
    public void The_shipped_quick_view_viewers_are_valid_tools_for_the_window_and_the_schema(string id, int kinds)
    {
        var folder = Path.Combine(Repo.Tools, id);
        var json = File.ReadAllText(Path.Combine(folder, "tool.json"));

        var manifest = ToolManifest.Parse(json, id);
        Assert.Empty(manifest.Accepts); // a viewer opens nothing in a pane, so Enter keeps opening these files in their program
        Assert.True(File.Exists(Path.Combine(folder, manifest.Entry)));
        Assert.True(Schemas.Tool.Evaluate(JsonDocument.Parse(json).RootElement).IsValid);

        using var document = JsonDocument.Parse(json);
        var quickView = document.RootElement.GetProperty("quickView");
        Assert.Equal(kinds, quickView.GetProperty("kinds").GetArrayLength());
        Assert.True(File.Exists(Path.Combine(folder, quickView.GetProperty("entry").GetString()!)));
    }

    [Theory]
    [InlineData("""{"id":"markdown-preview"}""", "missing field `name`")]
    [InlineData("[]", "expected an object")]
    [InlineData("not json", "not JSON")]
    public void A_manifest_that_is_incomplete_or_not_json_is_refused(string json, string problem) =>
        Assert.Contains(problem, Assert.Throws<ToolManifestException>(() => ToolManifest.Parse(json, "markdown-preview")).Message);

    [Theory]
    [InlineData("id", "\"Markdown\"", "`id` must be")]
    [InlineData("id", "\"other-tool\"", "must be the folder's name")]
    [InlineData("version", "\"1.0\"", "`version` must be")]
    [InlineData("entry", "\"../index.html\"", "`entry` must be")]
    [InlineData("entry", "\"C:\\\\tools\\\\index.html\"", "`entry` must be")]
    [InlineData("entry", "\"index.js\"", "`entry` must be")]
    [InlineData("accepts", "[\"docs/*.md\"]", "each of `accepts`")]
    [InlineData("accepts", "[\"*md*\"]", "each of `accepts`")]
    [InlineData("placement", "\"window\"", "`placement` must be")]
    public void A_bad_value_names_the_field(string field, string value, string problem)
    {
        var json = Valid.Replace($"\"{field}\": ", $"\"{field}\": {value}, \"ignored-{field}\": ", StringComparison.Ordinal);
        // The old value moved to an unknown key; drop it again so only the bad value is judged.
        using var document = JsonDocument.Parse(json);
        var fields = document.RootElement.EnumerateObject().Where(p => !p.Name.StartsWith("ignored-", StringComparison.Ordinal))
            .ToDictionary(p => p.Name, p => p.Value.Clone());
        var error = Assert.Throws<ToolManifestException>(() => ToolManifest.Parse(JsonSerializer.Serialize(fields), "markdown-preview", @"C:\tools\markdown-preview\tool.json"));
        Assert.Contains(problem, error.Message);
        Assert.StartsWith(@"C:\tools\markdown-preview\tool.json: ", error.Message);
    }

    [Fact]
    public void An_unknown_field_is_refused_like_in_plugin_json()
    {
        var error = Assert.Throws<ToolManifestException>(() => ToolManifest.Parse(Valid.Replace("\"id\"", "\"colour\": \"blue\", \"id\"", StringComparison.Ordinal), "markdown-preview"));
        Assert.Contains("unknown field `colour`", error.Message);
    }

    [Theory]
    [InlineData("notes.md", true)]
    [InlineData("NOTES.MD", true)]
    [InlineData("README", true)]
    [InlineData("readme", true)]
    [InlineData("notes.md.txt", false)]
    [InlineData(".md", false)]
    [InlineData("md", false)]
    public void A_tool_accepts_names_by_extension_or_whole_name_without_case(string name, bool accepted) =>
        Assert.Equal(accepted, ToolManifest.Parse(Valid, "markdown-preview").AcceptsFile(name));

    [Fact]
    public void The_catalog_reads_each_root_and_the_first_root_wins()
    {
        var root = Repo.NewTempFolder("tools");
        try
        {
            var development = Directory.CreateDirectory(Path.Combine(root, "dev")).FullName;
            var installed = Directory.CreateDirectory(Path.Combine(root, "installed")).FullName;
            WriteTool(development, "markdown-preview", "0.2.0");
            WriteTool(installed, "markdown-preview", "0.1.0");
            WriteTool(installed, "hex-view", "1.0.0", accepts: "*.bin");
            Directory.CreateDirectory(Path.Combine(installed, "not-a-tool"));
            var broken = Directory.CreateDirectory(Path.Combine(installed, "broken")).FullName;
            File.WriteAllText(Path.Combine(broken, "tool.json"), "{");
            var pageless = Directory.CreateDirectory(Path.Combine(installed, "pageless")).FullName;
            File.WriteAllText(Path.Combine(pageless, "tool.json"), Valid.Replace("markdown-preview", "pageless", StringComparison.Ordinal));

            var catalog = ToolCatalog.Load([development, installed, Path.Combine(root, "missing")]);

            Assert.Equal(["markdown-preview", "hex-view"], catalog.Tools.Select(t => t.Manifest.Id));
            Assert.Equal("0.2.0", catalog.Tools[0].Manifest.Version);
            Assert.Equal(3, catalog.Problems.Count);
            Assert.Contains(catalog.Problems, p => p.Contains("found first in another folder", StringComparison.Ordinal));
            Assert.Contains(catalog.Problems, p => p.Contains("not JSON", StringComparison.Ordinal));
            Assert.Contains(catalog.Problems, p => p.Contains("the entry page index.html is missing", StringComparison.Ordinal));
            Assert.Equal("markdown-preview", catalog.ForFile("README.md")!.Manifest.Id);
            Assert.Equal("hex-view", catalog.ForFile("dump.BIN")!.Manifest.Id);
            Assert.Null(catalog.ForFile("photo.jpg"));
        }
        finally
        {
            Repo.RemoveTempFolder(root);
        }

        static void WriteTool(string root, string id, string version, string accepts = "*.md")
        {
            var folder = Directory.CreateDirectory(Path.Combine(root, id)).FullName;
            File.WriteAllText(Path.Combine(folder, "tool.json"), Valid
                .Replace("markdown-preview", id, StringComparison.Ordinal)
                .Replace("0.1.0", version, StringComparison.Ordinal)
                .Replace("\"*.md\", \"README\"", $"\"{accepts}\"", StringComparison.Ordinal));
            File.WriteAllText(Path.Combine(folder, "index.html"), "<!doctype html>");
        }
    }

    [Fact]
    public void The_repository_s_tools_folder_is_a_catalog_of_its_own()
    {
        var catalog = ToolCatalog.Load([Repo.Tools]);
        Assert.Empty(catalog.Problems);
        Assert.Equal("markdown-preview", catalog.ForFile("README.md")!.Manifest.Id);
    }

    [Theory]
    [InlineData("""{"type":"ready"}""", "ready", null)]
    [InlineData("""{"type":"command","id":"go.toPath","args":{"path":"C:\\"}}""", "command", "go.toPath")]
    [InlineData("""{"type":"command","id":"view.toggleTerminal"}""", "command", "view.toggleTerminal")]
    [InlineData("""{"type":"command","id":"view.toggleTerminal","args":null}""", "command", "view.toggleTerminal")]
    [InlineData("""{"type":"key","keys":"ctrl+shift+p"}""", "key", null)]
    public void The_messages_a_tool_may_send(string json, string type, string? command)
    {
        var message = ToolMessages.Parse(json)!;
        Assert.Equal((type, command), (message.Type, message.CommandId));
    }

    [Theory]
    [InlineData("""{"type":"command","args":{}}""")]
    [InlineData("""{"type":"command","id":"go.toPath","args":"C:\\"}""")]
    [InlineData("""{"type":"command","id":""}""")]
    [InlineData("""{"type":"navigate","url":"https://example.com"}""")]
    [InlineData("""{"type":"key"}""")]
    [InlineData("[1,2]")]
    [InlineData("{")]
    [InlineData("")]
    public void Anything_else_from_a_tool_is_dropped(string json) =>
        Assert.Null(ToolMessages.Parse(json));

    [Fact]
    public void A_message_over_64_KiB_is_dropped() =>
        Assert.Null(ToolMessages.Parse("""{"type":"command","id":"go.toPath","args":{"path":""" + "\"" + new string('a', 70_000) + "\"}}"));

    [Fact]
    public void The_arguments_of_a_command_reach_the_router_as_an_object()
    {
        var message = ToolMessages.Parse("""{"type":"command","id":"editor.openMarkdownPreview","args":{"path":"C:\\docs\\ui.md"}}""")!;
        Assert.Equal(@"C:\docs\ui.md", message.Args!.Value.GetProperty("path").GetString());
    }

    [Fact]
    public void The_window_tells_a_tool_what_is_selected_and_which_file_to_show()
    {
        using var context = JsonDocument.Parse(ToolMessages.Context([@"C:\a.md", @"C:\b.md"], @"C:\"));
        Assert.Equal("context", context.RootElement.GetProperty("type").GetString());
        Assert.Equal(2, context.RootElement.GetProperty("selection").GetArrayLength());
        Assert.Equal(@"C:\", context.RootElement.GetProperty("activeFolder").GetString());
        Assert.False(context.RootElement.TryGetProperty("truncated", out _));

        using var big = JsonDocument.Parse(ToolMessages.Context(Enumerable.Range(0, 1500).Select(i => $@"C:\{i}.md").ToList(), null));
        Assert.Equal(ToolMessages.MaxSelection, big.RootElement.GetProperty("selection").GetArrayLength());
        Assert.True(big.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(JsonValueKind.Null, big.RootElement.GetProperty("activeFolder").ValueKind);

        var host = ToolFileUrls.Host("markdown-preview", 3, "cabinetos.example");
        Assert.Equal("f3.markdown-preview.cabinetos.example", host);
        using var open = JsonDocument.Parse(ToolMessages.Open(@"C:\my docs\read me.md", ToolFileUrls.Url(host, @"C:\my docs\read me.md")));
        Assert.Equal(@"C:\my docs\read me.md", open.RootElement.GetProperty("path").GetString());
        Assert.Equal("https://f3.markdown-preview.cabinetos.example/read%20me.md", open.RootElement.GetProperty("url").GetString());
    }

    [Fact]
    public async Task A_file_opened_again_in_a_loaded_page_gets_a_new_host_after_the_page_loads_again()
    {
        var page = new RecordingPage();
        var files = new ToolFileSession("markdown-preview", "cabinetos.example", page);

        Assert.True(await files.OpenAsync(@"C:\docs\README.md"));
        files.OnReady("""{"type":"context"}""");
        Assert.Equal(
            [
                @"map f1.markdown-preview.cabinetos.example C:\docs",
                "load",
                """post {"type":"context"}""",
                """post {"type":"open","path":"C:\\docs\\README.md","url":"https://f1.markdown-preview.cabinetos.example/README.md"}""",
            ],
            page.Calls);
        page.Calls.Clear();

        // Ctrl+K V on the file on screen (live check 2026-09-28): a loaded page cannot fetch from a host
        // mapped after it loaded, so the file's new host comes with a new load, and open waits for ready.
        Assert.True(await files.OpenAsync(@"C:\docs\README.md"));
        Assert.False(files.IsReady);
        Assert.Equal(["unmap f1.markdown-preview.cabinetos.example", @"map f2.markdown-preview.cabinetos.example C:\docs", "load"], page.Calls);
        files.OnReady(null);
        Assert.Equal("""post {"type":"open","path":"C:\\docs\\README.md","url":"https://f2.markdown-preview.cabinetos.example/README.md"}""", page.Calls[^1]);

        // After a crash the page loads again and gets the same file on the same host.
        page.Calls.Clear();
        files.OnStopped();
        Assert.True(await files.ReloadAsync());
        files.OnReady(null);
        Assert.Equal(["load", """post {"type":"open","path":"C:\\docs\\README.md","url":"https://f2.markdown-preview.cabinetos.example/README.md"}"""], page.Calls);
    }

    [Theory]
    [InlineData("go.toPath", true)]
    [InlineData("terminal.new", true)]
    [InlineData("editor.openMarkdownPreview", true)]
    [InlineData("palette.show", true)]
    [InlineData("file.delete", false)]
    [InlineData("file.deletePermanently", false)]
    [InlineData("file.rename", false)]
    [InlineData("edit.paste", false)]
    [InlineData("plugins.grant", false)]
    [InlineData("keys.rebind", false)]
    [InlineData("pane.openSelected", false)]
    [InlineData("hello.say", false)]
    public void A_tool_may_move_around_and_open_things_never_change_files_or_grant_rights(string command, bool allowed) =>
        Assert.Equal(allowed, ToolMessages.MayRun(command));

    [Fact]
    public void A_page_that_follows_a_plugin_may_run_that_plugin_s_commands_and_no_other()
    {
        var agent = new CommandSource("plugin", "agent", "Agent");
        var core = new CommandSource("core", null, null);
        var subscriptions = new ToolSubscriptions();

        // Not followed: as before, only the fixed list.
        Assert.False(ToolMessages.MayRun("agent.chat", agent, subscriptions.Wants));
        Assert.True(ToolMessages.MayRun("go.toPath", core, subscriptions.Wants));

        subscriptions.Subscribe("agent");
        // Followed: every command the plugin registered, by whatever name, needs no name in the window.
        Assert.True(ToolMessages.MayRun("agent.chat", agent, subscriptions.Wants));
        Assert.True(ToolMessages.MayRun("agent.rule.add", agent, subscriptions.Wants));
        Assert.True(ToolMessages.MayRun("agent.audit", agent, subscriptions.Wants));
        // A command that is not listed, or is listed by another source, or has another prefix, stays refused.
        Assert.False(ToolMessages.MayRun("agent.chat", null, subscriptions.Wants));
        Assert.False(ToolMessages.MayRun("agent.chat", core, subscriptions.Wants));
        Assert.False(ToolMessages.MayRun("agentx.chat", agent, subscriptions.Wants));
        Assert.False(ToolMessages.MayRun("agent.", agent, subscriptions.Wants));
        Assert.False(ToolMessages.MayRun("file.delete", agent, subscriptions.Wants));

        // A page that follows the name of a core group gets none of the core's commands by it: their source is the core.
        subscriptions.Subscribe("file");
        Assert.False(ToolMessages.MayRun("file.delete", core, subscriptions.Wants));

        // Stopping the follow takes the right away.
        subscriptions.Unsubscribe("agent");
        Assert.False(ToolMessages.MayRun("agent.chat", agent, subscriptions.Wants));
    }

    [Fact]
    public void The_key_script_uses_the_window_s_key_names_and_keys()
    {
        var script = ToolKeyScript.Build(["ctrl+shift+p", "ctrl+backquote"]);
        Assert.Contains("\"192\":\"backquote\"", script);
        Assert.Contains("\"80\":\"p\"", script);
        Assert.Contains("'key'", script);
        // A key held down is passed on once (docs/keybindings.md, "Keys held down").
        Assert.Contains("if (!event.repeat)", script);
        // The keys as a JSON array (the serializer writes "+" as +, which is "+" in JavaScript too).
        var start = script.IndexOf("new Set(", StringComparison.Ordinal) + "new Set(".Length;
        var keys = JsonSerializer.Deserialize<string[]>(script[start..script.IndexOf(");", start, StringComparison.Ordinal)]);
        Assert.Equal(["ctrl+shift+p", "ctrl+backquote"], keys!);
    }

    [Fact]
    public void The_window_s_own_chord_joins_the_keymap_only_where_it_takes_nothing()
    {
        static Keymap Map(params (string Keys, string Command)[] bindings) =>
            Keymap.From(new KeymapData(1000, bindings.Select(b => new KeymapBinding(b.Keys, b.Command, null)).ToList(), ["palette.show"]));
        static Binding Extra(string keys) =>
            new(KeySequence.TryParse(keys, out var sequence) ? sequence : throw new ArgumentException(keys), "editor.openMarkdownPreview", KeyContexts.FilesView);

        var merged = Map(("ctrl+k ctrl+s", "keys.open"), ("ctrl+shift+p", "palette.show")).With(Extra("ctrl+k v"));
        Assert.Equal("ctrl+k v", merged!.FirstFor("editor.openMarkdownPreview")!.Keys.ToString());

        Assert.Null(Map(("ctrl+k v", "user.command")).With(Extra("ctrl+k v")));
        Assert.Null(Map(("ctrl+k", "user.command")).With(Extra("ctrl+k v")));
        Assert.Null(Map(("ctrl+k ctrl+s", "keys.open")).With(Extra("ctrl+k")));
    }

    [Fact]
    public async Task A_folder_the_page_cannot_serve_ends_the_open_and_says_why()
    {
        // WebView2 refused a folder 333 characters long (edge cases, class B): the open must end, not leave a blank tool.
        // Any refusal ends it so, such as a folder gone since the listing.
        var page = new RecordingPage { Refuse = folder => folder.EndsWith("gone", StringComparison.Ordinal) };
        var files = new ToolFileSession("markdown-preview", "cabinetos.example", page);

        Assert.False(await files.OpenAsync(@"C:\edge\gone\notes.md"));
        Assert.Null(files.Host);
        Assert.Contains("cannot serve", files.Problem);
        Assert.DoesNotContain("load", page.Calls);

        // The next file, in a folder it can serve, opens as usual.
        Assert.True(await files.OpenAsync(@"C:\docs\README.md"));
        Assert.Null(files.Problem);
        Assert.Equal("f2.markdown-preview.cabinetos.example", files.Host);
    }

    [Fact]
    public async Task A_folder_refused_when_the_page_first_starts_ends_the_open_too()
    {
        // Before its first start, WebView2 only notes a folder; it serves (or refuses) it while the page starts.
        var page = new RecordingPage { Refuse = folder => folder.EndsWith("gone", StringComparison.Ordinal), Unstarted = true };
        var files = new ToolFileSession("markdown-preview", "cabinetos.example", page);

        Assert.False(await files.OpenAsync(@"C:\edge\gone\notes.md"));
        Assert.Null(files.Host);
        Assert.Contains("cannot serve", files.Problem);
        // The refused folder is let go, so the next start does not refuse it again.
        Assert.Equal("unmap f1.markdown-preview.cabinetos.example", page.Calls[^1]);

        Assert.True(await files.OpenAsync(@"C:\docs\README.md"));
        Assert.Null(files.Problem);
    }

    [Fact]
    public async Task A_file_whose_path_webview2_cannot_read_is_not_offered_to_the_page()
    {
        // On 2026-09-29 WebView2 showed a 255-character path from a 250-character folder it served,
        // failed to fetch a 272-character one from the same folder, and refused a 333-character folder.
        var page = new RecordingPage();
        var files = new ToolFileSession("markdown-preview", "cabinetos.example", page);
        var folder = @"C:\edge\band\" + new string('b', 250 - 13);
        Assert.Equal(250, folder.Length);

        Assert.True(await files.OpenAsync(folder + @"\n.md"));
        Assert.False(await files.OpenAsync(folder + @"\band notes, longer.md"));

        Assert.Contains("272 characters", files.Problem);
        Assert.Null(files.Host);
        // The page loses the first file's folder and is asked nothing for the second.
        Assert.Equal(["map", "load", "unmap"], page.Calls.Select(call => call.Split(' ')[0]));
    }

    [Fact]
    public async Task A_sidebar_page_starts_with_no_file_and_gets_the_context_and_never_an_open()
    {
        var page = new RecordingPage();
        var files = new ToolFileSession("quick-notes", "cabinetos.example", page);

        Assert.True(await files.StartViewAsync());
        Assert.Null(files.FilePath);
        Assert.Null(files.Host);
        Assert.False(files.IsReady);

        files.OnReady("""{"type":"context"}""");

        Assert.True(files.IsReady);
        // No folder is served and nothing is opened: the page loads, and says ready, and hears the context.
        Assert.Equal(["load", """post {"type":"context"}"""], page.Calls);

        // After a crash it loads again the same way.
        page.Calls.Clear();
        Assert.True(await files.ReloadAsync());
        files.OnReady(null);
        Assert.Equal(["load"], page.Calls);
    }

    // A page that writes down what the file session asks of it.
    private sealed class RecordingPage : IToolPage
    {
        private readonly Dictionary<string, string> _noted = [];

        public List<string> Calls { get; } = [];

        public Func<string, bool>? Refuse { get; init; }

        // Like a WebView2 that has not started yet: folders are checked when the page loads.
        public bool Unstarted { get; init; }

        public void MapFolder(string host, string folder)
        {
            if (!Unstarted && Refuse?.Invoke(folder) == true)
            {
                throw new DirectoryNotFoundException("The system cannot find the path specified.");
            }
            _noted[host] = folder;
            Calls.Add($"map {host} {folder}");
        }

        public void UnmapFolder(string host)
        {
            _noted.Remove(host);
            Calls.Add($"unmap {host}");
        }

        public Task<bool> LoadAsync()
        {
            Calls.Add("load");
            if (Unstarted && _noted.Values.Any(folder => Refuse?.Invoke(folder) == true))
            {
                return Task.FromException<bool>(new DirectoryNotFoundException("The system cannot find the path specified."));
            }
            return Task.FromResult(true);
        }

        public void Post(string message) => Calls.Add($"post {message}");
    }
}
