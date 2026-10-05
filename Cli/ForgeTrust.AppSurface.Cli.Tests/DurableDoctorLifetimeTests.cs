using ForgeTrust.AppSurface.Cli;
using Npgsql;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Verifies monotonic budget arithmetic and fixed terminal provenance without timing guesses.</summary>
public sealed class DurableDoctorLifetimeTests
{
    [Theory]
    [InlineData(1, 0.8)]
    [InlineData(10, 8)]
    [InlineData(120, 118)]
    public void Budget_reserves_cleanup_inside_the_single_total(double seconds, double workSeconds)
    {
        var time = new MonotonicTime();
        var budget = new DurableDoctorBudget(TimeSpan.FromSeconds(seconds), time);
        Assert.Equal(TimeSpan.FromSeconds(workSeconds), budget.WorkTimeout);
        Assert.Equal(TimeSpan.FromSeconds(seconds), budget.TotalRemaining);
        time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(TimeSpan.FromSeconds(workSeconds) - TimeSpan.FromMilliseconds(100), budget.WorkRemaining);
        Assert.Equal(TimeSpan.FromSeconds(seconds) - TimeSpan.FromMilliseconds(100), budget.TotalRemaining);
        time.Advance(TimeSpan.FromSeconds(seconds));
        Assert.True(budget.WorkRemaining < TimeSpan.Zero);
        Assert.True(budget.TotalRemaining < TimeSpan.Zero);
    }

    [Theory]
    [InlineData(0.9999999)]
    [InlineData(120.0000001)]
    public void Budget_rejects_unvalidated_timeout(double seconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableDoctorBudget(TimeSpan.FromSeconds(seconds), TimeProvider.System));

    [Theory]
    [InlineData(0, 0, 10, 8)]
    [InlineData(2, 3, 10, 2)]
    [InlineData(50, 50, 1, 1)]
    public void Dedicated_session_never_expands_shorter_driver_waits(int open, int command, double total, int expectedOpen)
    {
        var input = new NpgsqlConnectionStringBuilder
        {
            Host = "localhost",
            Database = "doctor",
            Timeout = open,
            CommandTimeout = command,
            Pooling = true,
            Multiplexing = true,
            Enlist = true,
            KeepAlive = 2
        };
        var budget = new DurableDoctorBudget(TimeSpan.FromSeconds(total), TimeProvider.System);
        var settings = DurableDoctorService.CreateSessionSettings(input.ConnectionString, budget.WorkTimeout);
        Assert.False(settings.Pooling);
        Assert.False(settings.Multiplexing);
        Assert.False(settings.Enlist);
        Assert.Equal(0, settings.KeepAlive);
        Assert.Equal(-1, settings.CancellationTimeout);
        Assert.Equal(expectedOpen, settings.Timeout);
        Assert.Equal(command == 0 ? 8 : Math.Min(command, (int)Math.Ceiling(budget.WorkTimeout.TotalSeconds)), settings.CommandTimeout);
    }

    [Fact]
    public void Caller_cancellation_wins_over_coincident_deadline_unexpected_and_cleanup()
    {
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        foreach (var exception in new Exception[]
        {
            new InvalidDataException("secret"), new OperationCanceledException(), new TimeoutException("secret"),
            new DurableDoctorFailureException(DurableDoctorFailureKind.Unavailable, "cleanup")
        })
        {
            var failure = DurableDoctorService.ClassifyFailure(exception, caller.Token, true);
            Assert.Equal(DurableDoctorFailureKind.Canceled, failure.Kind);
            Assert.Equal(["caller-canceled"], failure.Categories);
            Assert.DoesNotContain("secret", failure.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Owned_deadline_is_unavailable_and_unrelated_cancellation_is_contract_failure()
    {
        var owned = DurableDoctorService.ClassifyFailure(new OperationCanceledException(), CancellationToken.None, true);
        Assert.Equal(DurableDoctorFailureKind.Unavailable, owned.Kind);
        Assert.Equal(["deadline"], owned.Categories);
        var unrelated = DurableDoctorService.ClassifyFailure(new OperationCanceledException(), CancellationToken.None, false);
        Assert.Equal(DurableDoctorFailureKind.Failed, unrelated.Kind);
        Assert.Equal(["catalog-contract"], unrelated.Categories);
        using var unrelatedSource = new CancellationTokenSource();
        unrelatedSource.Cancel();
        var coincident = DurableDoctorService.ClassifyFailure(new OperationCanceledException(unrelatedSource.Token), CancellationToken.None, true);
        Assert.Equal(DurableDoctorFailureKind.Failed, coincident.Kind);
    }

    [Fact]
    public void Observation_allowlist_preserves_denial_transport_and_unexpected_sqlstate()
    {
        foreach (var state in new[] { "42501", "08006", "53300", "57P01", "57P02", "57P03" })
        {
            var failure = DurableDoctorService.ClassifyFailure(new PostgresException("secret", "ERROR", "ERROR", state), CancellationToken.None, false);
            Assert.Equal(DurableDoctorFailureKind.Unavailable, failure.Kind);
            Assert.Equal(["dependency"], failure.Categories);
        }
        foreach (var state in new[] { "42P01", "42883", "57014", "22023" })
        {
            var failure = DurableDoctorService.ClassifyFailure(new PostgresException("secret", "ERROR", "ERROR", state), CancellationToken.None, false);
            Assert.Equal(DurableDoctorFailureKind.Failed, failure.Kind);
            Assert.Equal(["catalog-contract"], failure.Categories);
        }
        var coincidentUnexpected = DurableDoctorService.ClassifyFailure(new PostgresException("secret", "ERROR", "ERROR", "42P01"), CancellationToken.None, true);
        Assert.Equal(DurableDoctorFailureKind.Failed, coincidentUnexpected.Kind);
        var timeout = DurableDoctorService.ClassifyFailure(new TimeoutException("secret"), CancellationToken.None, false);
        Assert.Equal(DurableDoctorFailureKind.Unavailable, timeout.Kind);
        var affinity = new DurableDoctorFailureException(DurableDoctorFailureKind.Unavailable, "session-affinity");
        Assert.Same(affinity, DurableDoctorService.ClassifyFailure(affinity, CancellationToken.None, false));
    }

    private sealed class MonotonicTime : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        internal void Advance(TimeSpan duration) => _timestamp += duration.Ticks;
    }
}
