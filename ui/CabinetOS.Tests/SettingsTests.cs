using System.Text.Json;
using CabinetOS.Core.Protocol;
using CabinetOS.Core.Settings;
using CabinetOS.Tests.Support;

namespace CabinetOS.Tests;

/// <summary>The shell's state in cabinetos.json, and writing one setting through the core.</summary>
public class SettingsTests
{
    [Fact]
    public void The_last_folders_and_the_pinned_ones_are_read_from_the_config()
    {
        using var config = JsonDocument.Parse("""
            {"ui":{"lastPaths":["C:\\Users\\me","D:\\work",7,""],"pinned":["E:\\music"]}}
            """);
        var state = ShellState.FromConfig(config.RootElement);
        Assert.Equal([@"C:\Users\me", @"D:\work"], state.LastPaths);
        Assert.Equal([@"E:\music"], state.Pinned);

        using var none = JsonDocument.Parse("""{"ui":{"lastPaths":"C:\\"}}""");
        Assert.Empty(ShellState.FromConfig(none.RootElement).LastPaths);
        Assert.Empty(ShellState.FromConfig(JsonDocument.Parse("[]").RootElement).Pinned);
    }

    [Fact]
    public void Both_keys_exist_in_the_config_schema()
    {
        var schema = Schemas.Config;
        using var config = JsonDocument.Parse("""{"version":1,"ui":{"lastPaths":["C:\\a","D:\\b"],"pinned":["E:\\c"],"dualPane":false,"sidebar":true}}""");
        Assert.True(schema.Evaluate(config.RootElement).IsValid);
    }

    [Fact]
    public async Task A_setting_is_written_through_the_core_as_json()
    {
        var core = new FakeChannel(_ => new OkReply());
        var writer = new SettingsWriter(core);

        Assert.True(await writer.SetAsync(ShellState.DualPaneKey, false));
        Assert.True(await writer.SetAsync(ShellState.LastPathsKey, [@"C:\Users\me", @"D:\work"]));

        var dual = Assert.IsType<SetValueRequest>(core.Requests[0]);
        Assert.Equal(("ui.dualPane", JsonValueKind.False), (dual.Path, dual.Value.ValueKind));
        var paths = Assert.IsType<SetValueRequest>(core.Requests[1]);
        Assert.Equal("""["C:\\Users\\me","D:\\work"]""", paths.Value.GetRawText());
    }

    [Fact]
    public async Task A_core_without_set_value_is_asked_once()
    {
        var core = new FakeChannel(request => new ErrorReply(ErrorCodes.UnknownRequest, $"unknown request type `{request.Type}`"));
        var writer = new SettingsWriter(core);

        Assert.False(await writer.SetAsync(ShellState.SidebarKey, true));
        Assert.False(await writer.SetAsync(ShellState.SidebarKey, false));

        Assert.False(writer.IsAvailable);
        Assert.Single(core.Requests);
        writer.Reset();
        Assert.True(writer.IsAvailable);
    }

    [Fact]
    public async Task A_refused_value_is_not_the_end_of_writing()
    {
        var core = new FakeChannel(_ => new ErrorReply(ErrorCodes.ConfigError, "the file has an error at line 3"));
        var writer = new SettingsWriter(core);

        Assert.False(await writer.SetAsync(ShellState.PinnedKey, [@"C:\x"]));
        Assert.True(writer.IsAvailable);
    }
}
