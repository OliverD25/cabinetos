using CabinetOS.Core.Ipc;
using CabinetOS.Core.Platform;

namespace CabinetOS.Tests;

public class CoreLauncherTests
{
    private const string Ui = @"E:\repo\ui\CabinetOS\bin\Debug\net10.0-windows10.0.22621.0\win-x64";

    private static bool RepoLayout(string path) => path == @"E:\repo\core\Cargo.toml";

    [Fact]
    public void The_environment_comes_first_then_next_to_the_ui_then_the_repo_builds()
    {
        var candidates = CoreLauncher.Candidates(Ui, name => name == CoreLauncher.CoreExeEnv ? "\"D:\\cores\\cabinetos-core.exe\"" : null, RepoLayout);
        Assert.Equal(
        [
            @"D:\cores\cabinetos-core.exe",
            Ui + @"\cabinetos-core.exe",
            @"E:\repo\core\target\debug\cabinetos-core.exe",
            @"E:\repo\core\target\release\cabinetos-core.exe",
        ], candidates);
    }

    [Fact]
    public void Outside_a_repository_only_the_ui_folder_is_tried()
    {
        var candidates = CoreLauncher.Candidates(@"C:\Program Files\CabinetOS", _ => null, _ => false);
        Assert.Equal([@"C:\Program Files\CabinetOS\cabinetos-core.exe"], candidates);
    }

    [Fact]
    public void Find_takes_the_first_one_that_exists()
    {
        var exists = new HashSet<string> { @"E:\repo\core\Cargo.toml", @"E:\repo\core\target\release\cabinetos-core.exe" };
        Assert.Equal(@"E:\repo\core\target\release\cabinetos-core.exe", CoreLauncher.Find(Ui, _ => null, exists.Contains));
        Assert.Null(CoreLauncher.Find(Ui, _ => null, _ => false));
    }

    [Fact]
    public void A_pipe_token_is_16_lowercase_hex_digits_and_random()
    {
        var token = CoreLauncher.NewToken();
        Assert.Matches("^[0-9a-f]{16}$", token);
        Assert.NotEqual(token, CoreLauncher.NewToken());
        Assert.Equal("cabinetos-core-" + token, CoreClient.PipeNameForToken(token));
    }

    [Theory]
    [InlineData(10, 0, 22621, true)]
    [InlineData(10, 0, 26200, true)]
    [InlineData(10, 0, 22000, false)]
    [InlineData(10, 0, 19045, false)]
    [InlineData(6, 3, 9600, false)]
    public void Windows_11_22H2_is_the_floor(int major, int minor, int build, bool supported) =>
        Assert.Equal(supported, WindowsPlatform.IsSupported(new Version(major, minor, build)));
}
