namespace CabinetOS.Tests.Support;

/// <summary>Paths inside the repository the tests run from.</summary>
internal static class Repo
{
    private static readonly Lazy<string> RootPath = new(() =>
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "sdk", "protocol", "request.schema.json"))
                && File.Exists(Path.Combine(dir.FullName, "core", "Cargo.toml")))
            {
                return dir.FullName;
            }
        }
        throw new InvalidOperationException($"no repository above {AppContext.BaseDirectory}");
    });

    /// <summary>The repository root.</summary>
    public static string Root => RootPath.Value;

    /// <summary>A file of <c>sdk/protocol</c>.</summary>
    public static string ProtocolSchema(string name) => Path.Combine(Root, "sdk", "protocol", name);

    /// <summary><c>sdk/config/cabinetos.schema.json</c>.</summary>
    public static string ConfigSchema => Path.Combine(Root, "sdk", "config", "cabinetos.schema.json");

    /// <summary><c>sdk/tools</c>: the Tool Extension schema and the tools written in this repository.</summary>
    public static string Tools => Path.Combine(Root, "sdk", "tools");

    /// <summary>A fresh folder under <c>%TEMP%\cabinetos-ui-test</c>, removed by the caller.</summary>
    public static string NewTempFolder(string purpose)
    {
        var path = Path.Combine(Path.GetTempPath(), "cabinetos-ui-test", $"{purpose}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Removes a folder made by <see cref="NewTempFolder"/>, ignoring a file still in use.</summary>
    public static void RemoveTempFolder(string path)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }
}
