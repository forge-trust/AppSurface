using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactInventoryTests
{
    [Fact]
    public void NewProducerFileIsInventoriedButAbsentFailedWriteIsNotInvented()
    {
        using var temp = TestDirectory.Create();
        var inventory = new CoverageArtifactInventory(temp.Path, TimeProvider.System);
        var produced = Path.Join(temp.Path, "coverage.xml");
        Assert.Null(inventory.ObserveBeforeWrite(produced));
        File.WriteAllText(produced, "current");
        inventory.RecordAfterWrite(produced, null);
        inventory.RecordAfterWrite(Path.Join(temp.Path, "absent.xml"), null);

        var entry = Assert.Single(inventory.Capture(CancellationToken.None));

        Assert.Equal("coverage.xml", entry.Path);
        Assert.Equal(7, entry.Length);
    }

    [Fact]
    public void RetainedProducerDestinationPreventsFalseOwnershipEvenAfterLengthChange()
    {
        using var temp = TestDirectory.Create();
        var produced = Path.Join(temp.Path, "coverage.xml");
        File.WriteAllText(produced, "old");
        var inventory = new CoverageArtifactInventory(temp.Path, TimeProvider.System);
        var prior = inventory.ObserveBeforeWrite(produced);
        Assert.NotNull(prior);
        File.WriteAllText(produced, "new bytes have different length");
        inventory.RecordAfterWrite(produced, prior);

        Assert.Throws<InvalidDataException>(() => inventory.Capture(CancellationToken.None));
        Assert.Equal("new bytes have different length", File.ReadAllText(produced));
    }

    [Fact]
    public void MissingRecordedFileAndMissingFreshTreeAreNotInvented()
    {
        using var temp = TestDirectory.Create();
        var inventory = new CoverageArtifactInventory(temp.Path, TimeProvider.System);
        inventory.RecordFile(Path.Join(temp.Path, "missing.xml"));
        inventory.RecordFile(Path.Join(temp.Path, "missing-parent", "missing.xml"));
        inventory.RecordFreshTree(Path.Join(temp.Path, "missing-tree"));

        Assert.Empty(inventory.Capture(CancellationToken.None));
    }

    [Fact]
    public void FreshTreeCollectsNestedRegularFilesOnce()
    {
        using var temp = TestDirectory.Create();
        var nested = temp.CreateDirectory("fresh/nested");
        var produced = Path.Join(nested, "coverage.xml");
        File.WriteAllText(produced, "bytes");
        var inventory = new CoverageArtifactInventory(temp.Path, TimeProvider.System);
        inventory.RecordFreshTree(Path.Join(temp.Path, "fresh"));
        inventory.RecordFile(produced);

        var entry = Assert.Single(inventory.Capture(CancellationToken.None));

        Assert.Equal("fresh/nested/coverage.xml", entry.Path);
        Assert.Equal(5, entry.Length);
    }

    [Fact]
    public void OutsideFreshTreeOrObservationMakesInventoryIncompleteWithoutReadingOutsideData()
    {
        using var temp = TestDirectory.Create();
        var output = temp.CreateDirectory("output");
        var outside = Path.Join(temp.Path, "outside");
        var inventory = new CoverageArtifactInventory(output, TimeProvider.System);
        inventory.RecordFreshTree(outside);
        Assert.Throws<InvalidDataException>(() => inventory.Capture(CancellationToken.None));
        var observed = new CoverageArtifactInventory(output, TimeProvider.System);
        Assert.Null(observed.ObserveBeforeWrite(outside));
        Assert.Throws<InvalidDataException>(() => observed.Capture(CancellationToken.None));
        Assert.False(File.Exists(outside));
    }

    [Fact]
    public void CallerCancellationStopsBeforeHashingAndLeavesSourceBytes()
    {
        using var temp = TestDirectory.Create();
        var path = Path.Join(temp.Path, "coverage.xml");
        File.WriteAllText(path, "preserve");
        var inventory = new CoverageArtifactInventory(temp.Path, TimeProvider.System);
        inventory.RecordFile(path);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => inventory.Capture(cancelled.Token));
        Assert.Equal("preserve", File.ReadAllText(path));
    }

    [Fact]
    public void ProvenanceFingerprintsActualCoveragePolicyWithoutPublishingInputText()
    {
        var first = CoverageArtifactInventory.Provenance("collector", "Debug", ["one.csproj"], "[App]*", "[*.Tests]*");
        var same = CoverageArtifactInventory.Provenance("collector", "Debug", ["one.csproj"], "[App]*", "[*.Tests]*");
        var changed = CoverageArtifactInventory.Provenance("collector", "Release", ["one.csproj"], "[App]*", "[*.Tests]*");
        Assert.Equal(first, same);
        Assert.NotEqual(first.ConfigurationFingerprint, changed.ConfigurationFingerprint);
        Assert.Null(first.GitRevisionFingerprint);
        Assert.Equal("unknown", first.GitStateFingerprint);
        Assert.Equal(64, first.ConfigurationFingerprint!.Length);
        Assert.DoesNotContain("csproj", first.ConfigurationFingerprint, StringComparison.Ordinal);
        Assert.NotEmpty(CoverageArtifactInventory.ProducingVersion);
    }
}
