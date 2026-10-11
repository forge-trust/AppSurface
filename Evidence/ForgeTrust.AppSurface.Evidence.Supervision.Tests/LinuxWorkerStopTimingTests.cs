using System.Text.Json;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Numeric diagnostic bookkeeping controls; these grant no process, exit, deadline or native authority.</summary>
public sealed class LinuxWorkerStopTimingTests
{
    /// <summary>Observations require the actual begin sample and preserve every absent milestone as null.</summary>
    [Fact]
    public void MissingEventsStayNullAndBeginIsIrreversible()
    {
        var clock = new Clock();
        var timing = new LinuxWorkerStopTiming(clock);
        timing.Observe(LinuxWorkerStopMilestone.MonitorJoined);
        Assert.Null(timing.Snapshot);
        timing.Begin(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(5), 15_000_000);
        clock.Advance(10);
        timing.Begin(TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(30), 30_000_000);
        var snapshot = Assert.IsType<LinuxWorkerStopTimingSnapshot>(timing.Snapshot);
        Assert.Equal(20_000, snapshot.JobRemainingMilliseconds);
        Assert.Equal(5_000, snapshot.StoppingMilliseconds);
        Assert.Equal(15_000_000UL, snapshot.RuntimeMicroseconds);
        Assert.Null(snapshot.MonitorJoined);
        Assert.Null(snapshot.StopJoined);
        using var json = JsonDocument.Parse(snapshot.Encode());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("stop_joined_ms").ValueKind);
        Assert.Equal(15, json.RootElement.EnumerateObject().Count());
        Assert.InRange(snapshot.Encode().Length, 1, 1024);
    }

    /// <summary>Two actual requests retain distinct entry/reply events and first samples cannot be overwritten.</summary>
    [Fact]
    public void RequestsAndFinalJoinsUseOriginalMonotonicClock()
    {
        var clock = new Clock();
        var timing = new LinuxWorkerStopTiming(clock);
        timing.Begin(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(5), 15_000_000);
        timing.Observe(LinuxWorkerStopMilestone.ContainmentStarted);
        clock.Advance(2);
        var first = timing.StopRequestStarted();
        clock.Advance(3);
        timing.StopRequestReplied(first);
        clock.Advance(2);
        var second = timing.StopRequestStarted();
        clock.Advance(4);
        timing.StopRequestReplied(second);
        timing.Observe(LinuxWorkerStopMilestone.UnitAndGroupStopped);
        clock.Advance(2);
        timing.Observe(LinuxWorkerStopMilestone.MonitorJoined);
        clock.Advance(2);
        timing.Observe(LinuxWorkerStopMilestone.PumpsJoined);
        clock.Advance(2);
        timing.Observe(LinuxWorkerStopMilestone.FinalUnitAndGroupSampled);
        clock.Advance(2);
        timing.Observe(LinuxWorkerStopMilestone.SettlementPublished);
        timing.Observe(LinuxWorkerStopMilestone.StopJoined);
        clock.Advance(10);
        timing.Observe(LinuxWorkerStopMilestone.StopJoined);
        var snapshot = Assert.IsType<LinuxWorkerStopTimingSnapshot>(timing.Snapshot);
        Assert.Equal(1, first); Assert.Equal(2, second);
        Assert.Equal(0, snapshot.ContainmentStarted);
        Assert.Equal(2, snapshot.FirstStopStarted); Assert.Equal(5, snapshot.FirstStopReplied);
        Assert.Equal(7, snapshot.SecondStopStarted); Assert.Equal(11, snapshot.SecondStopReplied);
        Assert.Equal(11, snapshot.UnitAndGroupStopped);
        Assert.Equal(13, snapshot.MonitorJoined); Assert.Equal(15, snapshot.PumpsJoined);
        Assert.Equal(17, snapshot.FinalUnitAndGroupSampled);
        Assert.Equal(19, snapshot.SettlementPublished); Assert.Equal(19, snapshot.StopJoined);
        Assert.NotSame(snapshot, timing.Snapshot);
    }

    /// <summary>A failed request remains without reply and does not invent a later exit or successful join.</summary>
    [Fact]
    public void RequestFailureDoesNotInventReplyOrSettlement()
    {
        var clock = new Clock();
        var timing = new LinuxWorkerStopTiming(clock);
        timing.Begin(TimeSpan.FromMilliseconds(-1), TimeSpan.FromSeconds(5), null);
        timing.StopRequestStarted();
        timing.Observe(LinuxWorkerStopMilestone.MonitorJoined);
        timing.Observe(LinuxWorkerStopMilestone.StopJoined);
        var snapshot = Assert.IsType<LinuxWorkerStopTimingSnapshot>(timing.Snapshot);
        Assert.Equal(-1, snapshot.JobRemainingMilliseconds);
        Assert.Null(snapshot.FirstStopReplied);
        Assert.Null(snapshot.UnitAndGroupStopped);
        Assert.Null(snapshot.SettlementPublished);
        Assert.Null(snapshot.RuntimeMicroseconds);
        _ = snapshot.Encode();
    }

    /// <summary>Ambiguous extra attempts or unavailable clocks disable only the diagnostic.</summary>
    [Theory]
    [InlineData("begin-clock")]
    [InlineData("event-clock")]
    [InlineData("unknown-event")]
    [InlineData("third-attempt")]
    public void InvalidTelemetryRemainsUnavailable(string failure)
    {
        var clock = new Clock { Failed = failure == "begin-clock" };
        var timing = new LinuxWorkerStopTiming(clock);
        timing.Begin(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(5), 15_000_000);
        if (failure == "event-clock") { clock.Failed = true; timing.Observe(LinuxWorkerStopMilestone.PumpsJoined); }
        if (failure == "unknown-event") timing.Observe((LinuxWorkerStopMilestone)999);
        if (failure == "third-attempt")
        {
            timing.StopRequestStarted(); timing.StopRequestStarted(); timing.StopRequestStarted();
        }
        Assert.Null(timing.Snapshot);
    }

    /// <summary>Invalid detached numeric data rejects without rendering arbitrary input or supplying authority.</summary>
    [Theory]
    [InlineData("remaining")]
    [InlineData("stopping")]
    [InlineData("runtime-zero")]
    [InlineData("runtime-large")]
    [InlineData("negative-event")]
    [InlineData("large-event")]
    public void InvalidDetachedNumbersReject(string failure)
    {
        var snapshot = new LinuxWorkerStopTimingSnapshot(20_000, 5000, 15_000_000,
            null, null, null, null, null, null, null, null, null, null, null);
        snapshot = failure switch
        {
            "remaining" => snapshot with { JobRemainingMilliseconds = 3_600_001 },
            "stopping" => snapshot with { StoppingMilliseconds = 0 },
            "runtime-zero" => snapshot with { RuntimeMicroseconds = 0 },
            "runtime-large" => snapshot with { RuntimeMicroseconds = 3_600_000_001 },
            "negative-event" => snapshot with { PumpsJoined = -1 },
            "large-event" => snapshot with { StopJoined = 3_600_001 },
            _ => throw new InvalidOperationException(),
        };
        var error = Assert.Throws<InvalidOperationException>(() => snapshot.Encode());
        Assert.Equal("ASEVD410: Worker stop timing data rejected.", error.Message);
        Assert.Null(error.InnerException);
    }

    private sealed class Clock : TimeProvider
    {
        private long _timestamp;
        internal bool Failed { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Failed ? throw new IOException("private-clock-canary") : _timestamp;
        internal void Advance(int milliseconds) => _timestamp += TimeSpan.FromMilliseconds(milliseconds).Ticks;
    }
}
