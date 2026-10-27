using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactBudgetTests
{
    [Fact]
    public void DeadlineIncludesElapsedWorkAndNeverReportsNegativeRemainingTime()
    {
        var clock = new ManualClock();
        var budget = new CoverageArtifactWorkBudget(clock, TimeSpan.FromSeconds(2));
        budget.Check(CancellationToken.None);
        Assert.Equal(TimeSpan.FromSeconds(2), budget.Remaining);
        clock.Advance(TimeSpan.FromMilliseconds(750));
        Assert.Equal(TimeSpan.FromMilliseconds(750), budget.Elapsed);
        Assert.Equal(TimeSpan.FromMilliseconds(1250), budget.Remaining);
        budget.Check(CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(1250));
        Assert.Equal(TimeSpan.Zero, budget.Remaining);
        Assert.Throws<TimeoutException>(() => budget.Check(CancellationToken.None));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.Zero, budget.Remaining);
    }

    [Fact]
    public void ExplicitWorkHasNoTimeDeadlineButPreservesCallerCancellation()
    {
        var clock = new ManualClock();
        var budget = new CoverageArtifactWorkBudget(clock, null);
        clock.Advance(TimeSpan.FromDays(1));
        Assert.Null(budget.Remaining);
        budget.Check(CancellationToken.None);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var error = Assert.Throws<OperationCanceledException>(() => budget.Check(cancelled.Token));
        Assert.Equal(cancelled.Token, error.CancellationToken);
    }

    [Theory]
    [InlineData("permission", "Permission denied")]
    [InlineData("metadata", "metadata or policy is invalid")]
    [InlineData("json", "metadata or policy is invalid")]
    [InlineData("cancelled", "caller cancelled")]
    [InlineData("unexpected", "Storage operation failed")]
    public async Task DiagnosticClassifiesSafeCausesAndKeepsPrivateMessagesOutOfRenderedOutput(string kind, string expected)
    {
        Exception exception = kind switch
        {
            "permission" => new UnauthorizedAccessException("secret=password"),
            "metadata" => new InvalidDataException("secret=password"),
            "json" => new System.Text.Json.JsonException("secret=password"),
            "cancelled" => new OperationCanceledException("secret=password"),
            _ => new InvalidOperationException("secret=password")
        };
        try { throw exception; }
        catch (Exception captured) { exception = captured; }
        var diagnostic = CoverageArtifactDiagnostic.Failure("ASCOV201", "inspect", exception);
        Assert.Contains(expected, diagnostic.Cause, StringComparison.Ordinal);
        Assert.NotNull(diagnostic.Trace);
        using var output = new StringWriter();
        await (diagnostic with { ObservedBytes = 2 }).WriteAsync(output);
        Assert.Contains("allowedBytes=unknown", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret=password", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret=password", diagnostic.Trace, StringComparison.Ordinal);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        internal void Advance(TimeSpan elapsed) => _ticks += elapsed.Ticks;
    }
}
