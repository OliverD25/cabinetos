namespace CabinetOS.Tests.Support;

/// <summary>
/// Tests that check whether a handle value is still open run alone: Windows
/// reuses a closed handle's value at once, and a test running in parallel
/// could take it.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HandleTests
{
    public const string Name = "handle checks";
}
