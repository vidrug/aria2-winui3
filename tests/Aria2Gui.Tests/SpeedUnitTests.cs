using Aria2Gui.Helpers;
using Xunit;

namespace Aria2Gui.Tests;

public class SpeedUnitTests
{
    [Theory]
    [InlineData(1, "B", 1)]
    [InlineData(1, "KB", 1024)]
    [InlineData(1, "Kb", 128)]        // kilobit = 1024/8 bytes
    [InlineData(1, "MB", 1048576)]
    [InlineData(1, "Mb", 131072)]     // megabit = 1048576/8 bytes
    [InlineData(2.5, "MB", 2621440)]
    [InlineData(0, "MB", 0)]
    [InlineData(-5, "MB", 0)]
    [InlineData(double.NaN, "MB", 0)]
    public void ToBytes_converts_each_unit(double value, string unit, long expected) =>
        Assert.Equal(expected, SpeedUnit.ToBytes(value, unit));

    [Fact]
    public void FromBytes_rounds_for_display()
    {
        // 5 MB decimal in binary MB is 4.768... — shown rounded to 2 decimals (B25).
        var (value, unit) = SpeedUnit.FromBytes(5_000_000, "MB");
        Assert.Equal("MB", unit);
        Assert.Equal(4.77, value);
    }

    [Fact]
    public void FromBytes_zero_keeps_preferred_unit()
    {
        var (value, unit) = SpeedUnit.FromBytes(0, "Kb");
        Assert.Equal(0, value);
        Assert.Equal("Kb", unit);
    }

    [Theory]
    [InlineData(0, "MB")]            // no limit -> default unit
    [InlineData(1, "B")]
    [InlineData(2048, "KB")]
    [InlineData(1048576, "MB")]
    [InlineData(500, "Kb")]          // >= 1 kilobit but < 1 KB
    public void BestUnit_picks_largest_unit_with_value_at_least_one(long bytes, string expected) =>
        Assert.Equal(expected, SpeedUnit.BestUnit(bytes));

    [Theory]
    [InlineData("262144000", 262144000)]
    [InlineData("10240K", 10485760)]
    [InlineData("5M", 5242880)]
    [InlineData("0", 0)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    [InlineData("garbage", 0)]
    public void ParseStoredBytes_accepts_plain_and_legacy_grammar(string? stored, long expected) =>
        Assert.Equal(expected, SpeedUnit.ParseStoredBytes(stored));

    [Theory]
    [InlineData("MB", "MB")]
    [InlineData("Kb", "Kb")]
    [InlineData("XX", "MB")]
    [InlineData(null, "MB")]
    [InlineData("", "MB")]
    public void Sanitize_coerces_unknown_units_to_default(string? unit, string expected) =>
        Assert.Equal(expected, SpeedUnit.Sanitize(unit));

    [Fact]
    public void ToBytes_FromBytes_round_trip_is_stable()
    {
        foreach (string unit in SpeedUnit.Symbols)
        {
            long bytes = SpeedUnit.ToBytes(25, unit);
            var (back, _) = SpeedUnit.FromBytes(bytes, unit);
            Assert.Equal(25, back);
        }
    }
}
