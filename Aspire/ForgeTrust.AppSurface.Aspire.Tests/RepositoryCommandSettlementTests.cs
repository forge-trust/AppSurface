using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Validation;

namespace ForgeTrust.AppSurface.Aspire.Tests;

public sealed class RepositoryCommandSettlementTests
{
    [Fact]
    public void Observe_UsesExplicitCleanupStateAndNeverClearsIt()
    {
        var settlement = new RepositoryCommandSettlement();
        var confirmedFailure = new EvidenceCommandException("capture_failed", "capture failed", cleanupUnconfirmed: false);
        var unconfirmedFailure = new EvidenceCommandException("capture_failed", "capture did not settle", cleanupUnconfirmed: true);

        settlement.Observe(confirmedFailure);
        Assert.False(settlement.HasUnconfirmedCleanup);
        Assert.False(RepositoryCommandSettlement.IsUnconfirmedCleanup(confirmedFailure));

        settlement.Observe(unconfirmedFailure);
        Assert.True(settlement.HasUnconfirmedCleanup);
        Assert.True(RepositoryCommandSettlement.IsUnconfirmedCleanup(unconfirmedFailure));

        settlement.Observe(confirmedFailure);
        Assert.True(settlement.HasUnconfirmedCleanup);
    }

    [Fact]
    public async Task Track_ObservesCleanupFailedBeforeRethrowingTheOriginalException()
    {
        var settlement = new RepositoryCommandSettlement();
        var failure = new EvidenceCommandException(EvidenceCommandException.CleanupFailedCode, "child exit was not confirmed");
        var trackedRun = settlement.Track((_, _) => Task.FromException<EvidenceCommandResult>(failure));
        var request = new EvidenceCommandRequest("test-command", [], Environment.CurrentDirectory, TimeSpan.FromSeconds(1));

        var observed = await Assert.ThrowsAsync<EvidenceCommandException>(() => trackedRun(request, CancellationToken.None));

        Assert.Same(failure, observed);
        Assert.True(failure.CleanupUnconfirmed);
        Assert.True(settlement.HasUnconfirmedCleanup);
    }

    [Fact]
    public async Task TrackSourceCheck_PropagatesCancellationAndKeepsTypedCleanupFailureMonotonic()
    {
        var settlement = new RepositoryCommandSettlement();
        var canceledToken = new CancellationToken(canceled: true);
        var canceledCheck = settlement.TrackSourceCheck(token =>
        {
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledCheck(canceledToken));
        Assert.False(settlement.HasUnconfirmedCleanup);

        var failure = new EvidenceCommandException(EvidenceCommandException.CleanupFailedCode, "source command cleanup was not confirmed");
        var typedFailureCheck = settlement.TrackSourceCheck(_ => Task.FromException(failure));
        var observed = await Assert.ThrowsAsync<EvidenceCommandException>(() => typedFailureCheck(CancellationToken.None));

        Assert.Same(failure, observed);
        Assert.True(settlement.HasUnconfirmedCleanup);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledCheck(canceledToken));
        Assert.True(settlement.HasUnconfirmedCleanup);
    }
}
