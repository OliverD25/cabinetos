using System.Text.Json;
using CabinetOS.Core.Terminal;

namespace CabinetOS.Tests;

/// <summary>
/// The terminal's saved layout (<see cref="TerminalLayout"/>, the setting <c>terminal.tabs</c>, docs/config.md) as the window reads it
/// from the core's configuration and writes it back whole with one <c>set_value</c>.
/// </summary>
public class TerminalLayoutTests
{
    private static SavedSession Session(string profile, string? folder, int pane, TerminalMode mode = TerminalMode.Locked) => new(profile, folder, pane, mode);

    private static TerminalLayout Layout(IReadOnlyList<SavedSession> items, int? front = null, int? left = null, int? right = null) => new(items, front, left, right);

    private static TerminalLayout FromJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return TerminalLayout.FromConfig(document.RootElement);
    }

    // The configuration a core would send with this layout under terminal.tabs.
    private static TerminalLayout ReadBack(TerminalLayout layout) => FromJson("{\"terminal\":{\"tabs\":" + layout.ToJson().GetRawText() + "}}");


    [Fact]
    public void The_saved_layout_round_trips_through_its_json_with_what_the_core_writes()
    {
        var layout = Layout(
            [Session("pwsh", @"E:\work", 0, TerminalMode.Linked), Session("cmd", null, 1)],
            front: 1, left: 0, right: 1);

        var json = layout.ToJson().GetRawText();

        // An absent folder is left out; an index is written when it names a session.
        Assert.Equal(
            """{"items":[{"profile":"pwsh","folder":"E:\\work","pane":"left","mode":"linked"},{"profile":"cmd","pane":"right","mode":"locked"}],"front":1,"shown":{"left":0,"right":1}}""",
            json);
        var back = ReadBack(layout);
        Assert.Equal(layout.Items, back.Items);
        Assert.Equal((1, 0, 1), (back.Front, back.ShownLeft, back.ShownRight));
        Assert.Equal(TerminalMode.Linked, back.Items[0].Mode);
    }

    [Fact]
    public void A_folder_beyond_ascii_with_quotes_and_spaces_comes_back_whole()
    {
        var layout = Layout([Session("pwsh", @"E:\Звіт 'проєкт' 100%PATH% Ґанок", 0), Session("wsl", "E:\\cafe\u0301 \U0001F4C1", 1)]);

        Assert.Equal(layout.Items, ReadBack(layout).Items);
    }

    [Fact]
    public void Nothing_saved_is_the_empty_layout_and_an_index_past_the_sessions_is_never_written()
    {
        Assert.True(TerminalLayout.Empty.IsEmpty);
        Assert.Equal("""{"items":[],"shown":{}}""", TerminalLayout.Empty.ToJson().GetRawText());
        Assert.True(FromJson("{}").IsEmpty);
        Assert.True(FromJson("""{"terminal":{}}""").IsEmpty);
        Assert.True(FromJson("""{"terminal":{"tabs":"x"}}""").IsEmpty);

        // The core refuses a file whose front tab is not one of the sessions: an index that names none is left out.
        var odd = Layout([Session("cmd", null, 0)], front: 3, left: -1, right: 1);
        Assert.Equal("""{"items":[{"profile":"cmd","pane":"left","mode":"locked"}],"shown":{}}""", odd.ToJson().GetRawText());
    }

    [Fact]
    public void A_hand_edited_layout_reads_leniently_and_a_session_without_a_profile_is_dropped_with_its_index()
    {
        var layout = FromJson("""
            {"terminal":{"tabs":{"items":[
                {"profile":"pwsh","pane":"right","mode":"linked"},
                {"folder":"E:\\x"},
                {"profile":"","folder":"E:\\y"},
                {"profile":"cmd","folder":"","pane":"middle","mode":"following"},
                7
              ],"front":3,"shown":{"left":1,"right":0}}}}
            """);

        // The second, third and fifth entries cannot be started; the others keep their order, and the indexes move with them.
        Assert.Equal(
            [Session("pwsh", null, 1, TerminalMode.Linked), Session("cmd", null, 0)],
            layout.Items);
        Assert.Equal(1, layout.Front);
        Assert.Null(layout.ShownLeft);
        Assert.Equal(0, layout.ShownRight);
        Assert.Equal(0, layout.ShownIn(1) ?? -1);
        Assert.Null(layout.ShownIn(0));
    }
}
