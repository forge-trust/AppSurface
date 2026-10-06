using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Actual atomic contention/replay bookkeeping controls; these create no owner or account authority.</summary>
public sealed class SupervisionSingleAttemptTests
{
    [Fact]
    public async Task ContentionAdmitsExactlyOneProcedureAndReplayCannotEnterRollback()
    {
        var attempt = new SupervisionSingleAttempt();
        var dispatches = 0;
        var releases = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 32).Select(_ => Task.Run(async () =>
        {
            await releases.Task;
            try { attempt.Claim(); }
            catch (EvidenceAdmissionException error) { Assert.Equal("ASEVD410", error.Code); return false; }
            Interlocked.Increment(ref dispatches); // Represents work after the actual owner's claim boundary.
            return true;
        })).ToArray();
        releases.SetResult();
        var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, results.Count(static admitted => admitted));
        Assert.Equal(1, dispatches);
        var replay = Assert.Throws<EvidenceAdmissionException>(attempt.Claim);
        Assert.Equal("ASEVD410", replay.Code);
        Assert.Null(replay.InnerException);
        Assert.Equal(1, dispatches);
    }

    [Fact]
    public void FailureAfterClaimDoesNotReleaseItAndIndependentOwnerDataHasItsOwnLatch()
    {
        var attempt = new SupervisionSingleAttempt();
        Action failAfterClaim = () => { attempt.Claim(); throw new IOException("private-canary"); };
        Assert.Throws<IOException>(failAfterClaim);
        var replay = Assert.Throws<EvidenceAdmissionException>(attempt.Claim);
        Assert.Equal("ASEVD410", replay.Code);
        Assert.DoesNotContain("canary", replay.ToString());
        new SupervisionSingleAttempt().Claim(); // Independent bookkeeping only, never a second live account owner.
    }
}
