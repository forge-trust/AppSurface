using ForgeTrust.AppSurface.Evidence.Supervision;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Original-pump parser data controls; no pipe, worker, signal or native authority is fabricated.</summary>
public sealed class SupervisionCancellationPhaseObservationTests
{
    [Fact]
    public async Task ExactFragmentedFirstFrameCompletesOnlyAfterLf()
    {
        var data = SupervisionCancellationPhaseObservation.ExpectedFrame;
        var observed = new SupervisionCancellationPhaseObservation();
        observed.Feed(data.AsSpan(0, data.Length - 1));
        Assert.False(observed.Observed.IsCompleted);
        observed.Feed(data.AsSpan(data.Length - 1));
        await observed.Observed;
        observed.Feed("unrelated-following-diagnostic\n"u8);
        Assert.True(observed.Observed.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task WrongFirstFrameCannotBecomeSignalReadiness()
    {
        var observed = new SupervisionCancellationPhaseObservation();
        observed.Feed("secret-canary\n"u8);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => observed.Observed);
        Assert.DoesNotContain("secret-canary", error.Message);
        observed.Feed(SupervisionCancellationPhaseObservation.ExpectedFrame);
        Assert.True(observed.Observed.IsFaulted);
    }

    [Fact]
    public async Task MissingLfAtEofRejectsWithoutInventingObservation()
    {
        var observed = new SupervisionCancellationPhaseObservation();
        var data = SupervisionCancellationPhaseObservation.ExpectedFrame;
        observed.Feed(data.AsSpan(0, data.Length - 1));
        observed.Complete();
        await Assert.ThrowsAsync<InvalidOperationException>(() => observed.Observed);
    }

    [Fact]
    public async Task OverBoundFirstLineRejectsClosed()
    {
        var observed = new SupervisionCancellationPhaseObservation();
        observed.Feed(new byte[1025]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => observed.Observed);
    }
}
