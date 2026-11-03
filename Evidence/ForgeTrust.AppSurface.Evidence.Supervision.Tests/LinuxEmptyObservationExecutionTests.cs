using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Final timing data controls only; no native owner, custody, account or execution is fabricated.</summary>
public sealed class LinuxEmptyObservationExecutionTests
{
    [Fact]
    public void DeliberateDeadlineDisposalDoesNotMakeCapturedFinalClosesExpired()
    {
        var clock = new Clock();
        using var deadline = new SupervisionTeardownDeadline(clock);
        deadline.Begin(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10));
        clock.Advance(2);
        var startedAt = clock.GetTimestamp();
        var remaining = deadline.Remaining;
        var borrowed = deadline.Token;
        deadline.Dispose();
        Assert.True(borrowed.IsCancellationRequested);
        LinuxEmptyObservationExecution.RequireFinalClose(clock, startedAt, remaining);
        clock.Advance(7);
        LinuxEmptyObservationExecution.RequireFinalClose(clock, startedAt, remaining);
        clock.Advance(1);
        Assert.Equal("ASEVD410", Assert.Throws<EvidenceAdmissionException>(() =>
            LinuxEmptyObservationExecution.RequireFinalClose(clock, startedAt, remaining)).Code);
    }

    [Fact]
    public void ExpiredOrRegressedFinalCloseDataCannotPassAfterOwnerCloses()
    {
        var clock = new Clock();
        Assert.Throws<EvidenceAdmissionException>(() => LinuxEmptyObservationExecution.RequireFinalClose(clock, 0, TimeSpan.Zero));
        Assert.Throws<EvidenceAdmissionException>(() => LinuxEmptyObservationExecution.RequireFinalClose(clock, 0, TimeSpan.FromSeconds(661)));
        Assert.Throws<EvidenceAdmissionException>(() => LinuxEmptyObservationExecution.RequireFinalClose(null!, 0, TimeSpan.FromSeconds(1)));
        clock.Advance(2);
        Assert.Throws<EvidenceAdmissionException>(() => LinuxEmptyObservationExecution.RequireFinalClose(clock, 0, TimeSpan.FromSeconds(2)));
        Assert.Throws<EvidenceAdmissionException>(() => LinuxEmptyObservationExecution.RequireFinalClose(clock, clock.GetTimestamp() + 1, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void CallerCancellationRemainsItsOriginalCancellation()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var error = Assert.Throws<OperationCanceledException>(() =>
            LinuxEmptyObservationExecution.RequireFinalClose(new Clock(), 0, TimeSpan.FromSeconds(1), cancelled.Token));
        Assert.Equal(cancelled.Token, error.CancellationToken);
    }

    private sealed class Clock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        internal void Advance(int seconds) => _timestamp += TimeSpan.FromSeconds(seconds).Ticks;
    }
}
