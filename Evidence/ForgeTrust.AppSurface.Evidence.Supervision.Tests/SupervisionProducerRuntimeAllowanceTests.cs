using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Supervision;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Original-reservation arithmetic only; no native owner, process, deadline or admission is created.</summary>
public sealed class SupervisionProducerRuntimeAllowanceTests
{
    private static readonly TimeSpan Stopping = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Cleanup = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Collection = TimeSpan.FromSeconds(3);

    [Theory]
    [InlineData(99_999_999L)]
    [InlineData(100_000_000L)]
    public void InsufficientOrExactlyReservedRemainderCannotDispatchPositiveRuntime(long remainingTicks)
    {
        var error = Assert.Throws<EvidenceAdmissionException>(() =>
            SupervisionProducerRuntimeAllowance.Calculate(TimeSpan.FromTicks(remainingTicks),
                Stopping, Cleanup, Collection));

        Assert.Equal("ASEVD410", error.Code);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData(100_000_001L, 1L)]
    [InlineData(1_200_000_000L, 1_100_000_000L)]
    [InlineData(18_099_999_999L, 17_999_999_999L)]
    [InlineData(18_100_000_000L, 18_000_000_000L)]
    [InlineData(18_100_000_001L, 18_000_000_000L)]
    [InlineData(36_000_000_000L, 18_000_000_000L)]
    public void AvailableRuntimeKeepsEachReservationAndTheExistingCap(long remainingTicks, long expectedRuntimeTicks)
    {
        var originalRemaining = TimeSpan.FromTicks(remainingTicks);
        var runtime = SupervisionProducerRuntimeAllowance.Calculate(originalRemaining,
            Stopping, Cleanup, Collection);

        Assert.Equal(TimeSpan.FromTicks(expectedRuntimeTicks), runtime);
        Assert.True(runtime > TimeSpan.Zero);
        Assert.True(runtime + Stopping + Cleanup + Collection <= originalRemaining);
        Assert.True(runtime <= TimeSpan.FromSeconds(1800));
        Assert.Equal(1800, LinuxProducerUnit.MaximumRuntimeSeconds);
    }

    [Theory]
    [InlineData(3, 5, 3, 109)]
    [InlineData(2, 6, 3, 109)]
    [InlineData(2, 5, 4, 109)]
    public void EachLargerOriginalReservationReducesRuntimeInsteadOfRenewingTheRemainder(
        int stoppingSeconds, int cleanupSeconds, int collectionSeconds, int expectedRuntimeSeconds)
    {
        var runtime = SupervisionProducerRuntimeAllowance.Calculate(TimeSpan.FromSeconds(120),
            TimeSpan.FromSeconds(stoppingSeconds), TimeSpan.FromSeconds(cleanupSeconds),
            TimeSpan.FromSeconds(collectionSeconds));

        Assert.Equal(TimeSpan.FromSeconds(expectedRuntimeSeconds), runtime);
        Assert.Equal(TimeSpan.FromSeconds(120), runtime + TimeSpan.FromSeconds(stoppingSeconds
            + cleanupSeconds + collectionSeconds));
    }
}
