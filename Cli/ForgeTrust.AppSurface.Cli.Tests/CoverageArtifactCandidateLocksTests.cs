using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactCandidateLocksTests
{
    [Fact]
    public void CandidateOffsetIsStableNonzeroAndDependsOnPhysicalIdentity()
    {
        var offset = CoverageArtifactOperationLease.CandidateLockOffset("physical-one");
        Assert.Equal(offset, CoverageArtifactOperationLease.CandidateLockOffset("physical-one"));
        Assert.InRange(offset, 1UL, (ulong)long.MaxValue);
        Assert.NotEqual(offset, CoverageArtifactOperationLease.CandidateLockOffset("physical-two"));
        Assert.Throws<ArgumentException>(() => CoverageArtifactOperationLease.CandidateLockOffset(" "));
    }

    [Fact]
    public void CandidateLeasePreventsContendersWhileAllowingDeletionAndIndependentCandidates()
    {
        using var temp = TestDirectory.Create();
        using var store = CoverageArtifactStore.Open(Path.Join(temp.Path, "domain"));
        var first = temp.CreateDirectory("first");
        var second = temp.CreateDirectory("second");
        using var firstLease = store.AcquireCandidateLease(first, true);
        using var secondLease = store.AcquireCandidateLease(second, true);
        firstLease.VerifyBinding();
        secondLease.VerifyBinding();
        Assert.Equal(0, store.GetStatus().StagingReservedBytes);
        Assert.Throws<CoverageExecutionException>(() => store.AcquireCandidateLease(first, false));
        Assert.Throws<CoverageExecutionException>(() => store.AcquireCandidateLease(first, true));
        var lockPath = TestPathUtils.PathUnder(store.RootPath, CoverageArtifactOperationLease.CandidateLockFileName);
        if (OperatingSystem.IsWindows()) Assert.Equal(0, new FileInfo(lockPath).Length);
        using var parent = CoverageRunOutputLease.AcquireArtifactDirectory(temp.Path, false, false)!;
        parent.DeleteArtifactEntry("first", 1, firstLease.PhysicalIdentity);
        // Windows completes pending directory deletion when its retained candidate handle closes.
        firstLease.Dispose();
        Assert.False(Directory.Exists(first));
        secondLease.VerifyBinding();
        Assert.True(Directory.Exists(second));
    }
}
