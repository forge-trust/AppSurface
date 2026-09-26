using Xunit;

namespace CoverageHang.Tests;

public sealed class HangTests
{
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
