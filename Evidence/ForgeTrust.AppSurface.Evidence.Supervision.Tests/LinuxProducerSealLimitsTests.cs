using ForgeTrust.AppSurface.Evidence.Supervision;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Tests only the seal-time count/depth/byte decisions; no workspace or native ownership is constructed.</summary>
public sealed class LinuxProducerSealLimitsTests
{
    [Theory]
    [InlineData(LinuxProducerWorkspaceLayout.MaximumNodes - 1, false)]
    [InlineData(LinuxProducerWorkspaceLayout.MaximumNodes, true)]
    [InlineData(LinuxProducerWorkspaceLayout.MaximumNodes + 1, true)]
    public void ChildCapacityRejectsAtTheOriginalPreAddBoundary(int entriesCount, bool rejected) =>
        Assert.Equal(rejected, LinuxProducerSealLimits.NodeCapacityReachedBeforeAdd(entriesCount));

    [Theory]
    [InlineData(LinuxProducerWorkspaceLayout.MaximumNodes - 1, false)]
    [InlineData(LinuxProducerWorkspaceLayout.MaximumNodes, false)]
    [InlineData(LinuxProducerWorkspaceLayout.MaximumNodes + 1, true)]
    public void CompletedCollectionKeepsItsOriginalInclusiveNodeMaximum(int entriesCount, bool rejected) =>
        Assert.Equal(rejected, LinuxProducerSealLimits.CollectedNodeCountExceeded(entriesCount));

    [Theory]
    [InlineData(LinuxProducerSealLimits.MaximumDepth - 1, false)]
    [InlineData(LinuxProducerSealLimits.MaximumDepth, false)]
    [InlineData(LinuxProducerSealLimits.MaximumDepth + 1, true)]
    public void RecursiveDepthKeepsThirtyTwoAsTheInclusiveMaximum(int depth, bool rejected) =>
        Assert.Equal(rejected, LinuxProducerSealLimits.DepthExceeded(depth));

    [Fact]
    public void AggregateBytesAcceptTheExactLimitThenRejectTheNextByte()
    {
        var maximum = LinuxProducerWorkspaceLayout.MaximumTreeBytes;
        Assert.True(LinuxProducerSealLimits.TryAccumulateBytes(0, directory: false, length: (ulong)maximum, out var exact));
        Assert.Equal(maximum, exact);
        Assert.True(LinuxProducerSealLimits.TryAccumulateBytes(exact - 1, directory: false, length: 1, out var atLimit));
        Assert.Equal(maximum, atLimit);
        Assert.False(LinuxProducerSealLimits.TryAccumulateBytes(atLimit, directory: false, length: 1, out var rejected));
        Assert.Equal(atLimit, rejected);
    }

    [Fact]
    public void DirectoryLengthsAreIgnoredAndOversizedUnsignedFileLengthsRejectBeforeCast()
    {
        const long existing = 17;
        Assert.True(LinuxProducerSealLimits.TryAccumulateBytes(existing, directory: true, length: ulong.MaxValue, out var directoryTotal));
        Assert.Equal(existing, directoryTotal);
        Assert.False(LinuxProducerSealLimits.TryAccumulateBytes(0, directory: false, length: (ulong)long.MaxValue + 1, out var overLong));
        Assert.Equal(0, overLong);
        Assert.False(LinuxProducerSealLimits.TryAccumulateBytes(0, directory: false, length: ulong.MaxValue, out var overUnsigned));
        Assert.Equal(0, overUnsigned);
    }
}
