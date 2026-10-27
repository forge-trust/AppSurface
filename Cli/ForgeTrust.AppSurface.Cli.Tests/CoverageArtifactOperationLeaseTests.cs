using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactOperationLeaseTests
{
    [Fact]
    public void SharedLeases_ShouldCoexist_AndExclusiveShouldConflictInBothDirections()
    {
        using var tree = LeaseTree.Create();
        using var first = CoverageArtifactOperationLease.Acquire(tree.Output, exclusive: false);
        using var second = CoverageArtifactOperationLease.Acquire(tree.Output, exclusive: false);
        Assert.Equal(first.PhysicalIdentity, second.PhysicalIdentity);
        Assert.Throws<CoverageExecutionException>(() => CoverageArtifactOperationLease.Acquire(tree.Output, exclusive: true));

        second.Dispose();
        first.Dispose();
        using var exclusive = CoverageArtifactOperationLease.Acquire(tree.Output, exclusive: true);
        Assert.Throws<CoverageExecutionException>(() => CoverageArtifactOperationLease.Acquire(tree.Output, exclusive: false));
        exclusive.VerifyBinding();
    }

    [Fact]
    public void DistinctOutputs_ShouldAllowConcurrentExclusiveLeases()
    {
        using var tree = LeaseTree.Create();
        using var left = CoverageArtifactOperationLease.Acquire(tree.Left, exclusive: true);
        using var right = CoverageArtifactOperationLease.Acquire(tree.Right, exclusive: true);
        Assert.NotEqual(left.PhysicalIdentity, right.PhysicalIdentity);
    }

    [Fact]
    public void FixedOperatingSystemAliases_ShouldShareOneLock()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var name = "coverage-fixed-alias-" + Guid.NewGuid().ToString("N");
        var firstPath = Path.Join("/tmp", name);
        var secondPath = Path.Join("/private/tmp", name);
        Directory.CreateDirectory(firstPath);
        try
        {
            using var held = CoverageArtifactOperationLease.Acquire(firstPath, exclusive: true);
            Assert.Throws<CoverageExecutionException>(() => CoverageArtifactOperationLease.Acquire(secondPath, exclusive: false));
        }
        finally { Directory.Delete(firstPath, recursive: true); }
    }

    [Fact]
    public void ArbitrarySymlinkAlias_ShouldBeRejectedByNoFollowContract()
    {
        if (OperatingSystem.IsWindows()) return;
        using var tree = LeaseTree.Create();
        var alias = Path.Join(tree.Root, "alias");
        Directory.CreateSymbolicLink(alias, tree.Output);
        Assert.Throws<IOException>(() => CoverageArtifactOperationLease.Acquire(alias, exclusive: false));
    }

    [Fact]
    public void AncestorSwap_ShouldFailBindingAndLeaveExternalSentinelUntouched()
    {
        if (OperatingSystem.IsWindows()) return;
        using var tree = LeaseTree.Create();
        var parent = Path.Join(tree.Root, "parent");
        Directory.CreateDirectory(parent);
        var output = Path.Join(parent, "output");
        Directory.CreateDirectory(output);
        var external = Path.Join(tree.Root, "external");
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Join(external, "sentinel"), "safe");
        using var lease = CoverageArtifactOperationLease.Acquire(output, exclusive: true);
        Directory.Move(parent, parent + "-moved");
        Directory.CreateSymbolicLink(parent, external);
        try
        {
            Assert.Throws<IOException>(lease.VerifyBinding);
            Assert.Equal("safe", File.ReadAllText(Path.Join(external, "sentinel")));
            Assert.False(Directory.Exists(Path.Join(external, "output")));
        }
        finally { Directory.Delete(parent); }
    }

    [Fact]
    public void DisposedLease_ShouldRejectBindingVerification()
    {
        using var tree = LeaseTree.Create();
        var lease = CoverageArtifactOperationLease.Acquire(tree.Output, exclusive: true);
        lease.Dispose();
        lease.Dispose();
        Assert.Throws<ObjectDisposedException>(lease.VerifyBinding);
    }

    private sealed class LeaseTree : IDisposable
    {
        private readonly CoverageRunOutputLease rootLease;
        private LeaseTree(string root, CoverageRunOutputLease lease) { Root = root; rootLease = lease; }
        internal string Root { get; }
        internal string Output => Path.Join(Root, "output");
        internal string Left => Path.Join(Root, "left");
        internal string Right => Path.Join(Root, "right");
        internal static LeaseTree Create()
        {
            var root = Path.Join(Path.GetTempPath(), "coverage-operation-lease-" + Guid.NewGuid().ToString("N"));
            var lease = CoverageRunOutputLease.AcquireArtifactDirectory(root, true, true)!;
            foreach (var name in new[] { "output", "left", "right" }) lease.CreateArtifactDirectory(name).Dispose();
            return new LeaseTree(root, lease);
        }
        public void Dispose()
        {
            rootLease.Dispose();
            Directory.Delete(Root, recursive: true);
        }
    }
}
