using Xunit;

namespace CoverageHang.Tests;

public sealed class HangTests
{
    // Together these outlast the progress probe's watchdog; each completion must be observed.
    [Theory]
    [Trait("Category", "HealthySequence")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    public async Task ShortHealthyTest(int row)
    {
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.InRange(row, 1, 12);
    }

    [Fact]
    [Trait("Category", "Hang")]
    public void NeverCompletes()
    {
        Thread.Sleep(Timeout.Infinite);
    }

    [Fact]
    [Trait("Category", "HealthyLong")]
    public void HealthyLongRunningTest()
    {
        var duration = TimeSpan.FromSeconds(
            int.TryParse(Environment.GetEnvironmentVariable("COVERAGE_HANG_HEALTHY_SECONDS"), out var seconds)
                ? Math.Clamp(seconds, 1, 3600)
                : 130);
        Thread.Sleep(duration);
        Assert.True(duration > TimeSpan.Zero);
    }
}
