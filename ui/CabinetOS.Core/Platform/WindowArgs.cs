namespace CabinetOS.Core.Platform;

/// <summary>
/// The window's command line: <c>--path &lt;folder&gt;</c> (the left pane's first
/// folder), <c>--tools-dir &lt;folder&gt;</c> (tools under development) and
/// <c>--self-test-crash</c>. <c>window.new</c> starts another window with
/// <see cref="ForNewWindow"/> (docs/ui.md, "Two windows").
/// </summary>
public sealed record WindowArgs(string? Path, string? ToolsDir, bool SelfTestCrash)
{
    private const string PathFlag = "--path";
    private const string ToolsFlag = "--tools-dir";
    private const string CrashFlag = "--self-test-crash";

    /// <summary>Reads a command line; a flag without its value is left out.</summary>
    public static WindowArgs Parse(IReadOnlyList<string> args) =>
        new(ValueOf(args, PathFlag), ValueOf(args, ToolsFlag), args.Contains(CrashFlag));

    /// <summary>
    /// The command line of a new window at <paramref name="path"/> (none when
    /// empty), with this window's tools folder. The crash self-test stays here.
    /// </summary>
    public IReadOnlyList<string> ForNewWindow(string? path)
    {
        var args = new List<string>();
        if (!string.IsNullOrEmpty(path))
        {
            args.AddRange([PathFlag, path]);
        }
        if (!string.IsNullOrEmpty(ToolsDir))
        {
            args.AddRange([ToolsFlag, ToolsDir]);
        }
        return args;
    }

    private static string? ValueOf(IReadOnlyList<string> args, string flag)
    {
        for (var i = 0; i + 1 < args.Count; i++)
        {
            if (args[i] == flag)
            {
                return args[i + 1];
            }
        }
        return null;
    }
}
