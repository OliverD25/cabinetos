using CabinetOS.Core.Diagnostics;

namespace CabinetOS.Tests;

public class UlidTests
{
    [Fact]
    public void New_ids_are_valid_distinct_and_match_the_schema_pattern()
    {
        var ids = Enumerable.Range(0, 2000).Select(_ => Ulid.NewId()).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(ids, id =>
        {
            Assert.True(Ulid.IsValid(id));
            // The pattern of RequestId in sdk/protocol/request.schema.json.
            Assert.Matches("^[0-7][0-9A-HJKMNP-TV-Za-hjkmnp-tv-z]{25}$", id);
        });
    }

    [Fact]
    public void The_time_comes_first_so_ids_sort_by_creation()
    {
        var at = DateTimeOffset.FromUnixTimeMilliseconds(1_790_557_323_004);
        var first = Ulid.NewId(at);
        var later = Ulid.NewId(at.AddMilliseconds(1));
        Assert.True(string.CompareOrdinal(later, first) > 0);
        Assert.Equal(first[..10], Ulid.NewId(at)[..10]);
    }

    [Fact]
    public void Encodes_the_extremes()
    {
        Assert.Equal(new string('0', 26), Ulid.Encode(new byte[16]));
        Assert.Equal("7" + new string('Z', 25), Ulid.Encode(Enumerable.Repeat((byte)0xFF, 16).ToArray()));
    }

    [Theory]
    [InlineData("01J9ZQ4X7K3M5N8P2R6S0T1V4W", true)]
    [InlineData("01j9zq4x7k3m5n8p2r6s0t1v4w", true)]
    [InlineData("", false)]
    [InlineData("not-a-ulid", false)]
    [InlineData("01J9ZQ4X7K3M5N8P2R6S0T1V4", false)]
    [InlineData("01J9ZQ4X7K3M5N8P2R6S0T1V4WX", false)]
    [InlineData("01J9ZQ4X7K3M5N8P2R6S0T1V4U", false)]
    [InlineData("81J9ZQ4X7K3M5N8P2R6S0T1V4W", false)]
    public void Accepts_what_the_core_accepts(string text, bool valid) => Assert.Equal(valid, Ulid.IsValid(text));
}
