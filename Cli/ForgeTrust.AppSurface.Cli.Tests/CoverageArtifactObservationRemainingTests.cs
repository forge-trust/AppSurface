using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactObservationRemainingTests
{
    [Fact]
    public async Task InvalidDomainObservedAfterPreflightProducesWarningWithoutClaimingCompleteTotals()
    {
        using var temp = TestDirectory.Create();
        using var store = CoverageArtifactStore.Open(Path.Join(temp.Path, "store"));
        var repository = temp.CreateDirectory("repo");
        using var errors = new StringWriter();
        var warnings = new CoverageArtifactWarnings(repository, store, TimeProvider.System, errors);
        await warnings.ObserveAsync(false, null, default);
        var registry = Path.Join(store.RootPath, "registry.json");
        const string damaged = "{\"SchemaVersion\":99}";
        File.WriteAllText(registry, damaged);
        await warnings.ObserveAsync(true, null, default);
        Assert.Contains("ASCOV201 warning-inspection", errors.ToString(), StringComparison.Ordinal);
        Assert.Equal(damaged, File.ReadAllText(registry));
        Assert.DoesNotContain("complete totals", errors.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a:b")]
    [InlineData("a\nb")]
    [InlineData("a//b")]
    [InlineData("a/./b")]
    [InlineData(null)]
    public void CursorRejectsNonportableOrMissingPathAndPreservesExistingState(string? path)
    {
        using var temp = TestDirectory.Create();
        using var store = CoverageArtifactStore.Open(Path.Join(temp.Path, "store"));
        var scope = new string('a', 64);
        CoverageArtifactInspectionCursorStore.Write(store, scope, ["TestResults"], TimeProvider.System);
        var state = Path.Join(store.RootPath, "staging", "inspection", "state.json");
        var before = File.ReadAllBytes(state);
        Assert.Throws<InvalidDataException>(() => CoverageArtifactInspectionCursorStore.Write(store, scope, [path!], TimeProvider.System));
        Assert.Equal(before, File.ReadAllBytes(state));
        Assert.Equal(["TestResults"], CoverageArtifactInspectionCursorStore.Read(store, scope, TimeProvider.System));
    }

    [Fact]
    public void CursorDropsFutureObservationWhenPublishingCurrentScope()
    {
        using var temp = TestDirectory.Create();
        using var store = CoverageArtifactStore.Open(Path.Join(temp.Path, "store"));
        var now = DateTimeOffset.Parse("2026-10-22T00:00:00Z");
        var future = new string('a', 64);
        var current = new string('b', 64);
        CoverageArtifactInspectionCursorStore.Write(store, future, ["TestResults/future"], new FixedClock(now.AddDays(1)));
        CoverageArtifactInspectionCursorStore.Write(store, current, ["TestResults/current"], new FixedClock(now));
        Assert.Null(CoverageArtifactInspectionCursorStore.Read(store, future, new FixedClock(now)));
        Assert.Equal(["TestResults/current"], CoverageArtifactInspectionCursorStore.Read(store, current, new FixedClock(now)));
    }

    [Fact]
    public void CursorDirectoryWithoutStateRemainsAnAbsentObservation()
    {
        using var temp = TestDirectory.Create();
        using var store = CoverageArtifactStore.Open(Path.Join(temp.Path, "store"));
        using var directory = CoverageRunOutputLease.AcquireArtifactDirectory(Path.Join(store.RootPath, "staging", "inspection"), true, true)!;
        Assert.Null(CoverageArtifactInspectionCursorStore.Read(store, new string('a', 64), TimeProvider.System));
        Assert.Empty(directory.EnumerateArtifactEntries(2));
    }

    [Fact]
    public void CursorRejectsExcessivePendingDirectoriesWithoutWritingState()
    {
        using var temp = TestDirectory.Create();
        using var store = CoverageArtifactStore.Open(Path.Join(temp.Path, "store"));
        Assert.Throws<InvalidDataException>(() => CoverageArtifactInspectionCursorStore.Write(store, new string('a', 64),
            Enumerable.Range(0, 129).Select(i => "pending-" + i).ToArray(), TimeProvider.System));
        Assert.False(Directory.Exists(Path.Join(store.RootPath, "staging", "inspection")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void CursorRejectsMissingScope(string? scope)
    {
        using var temp = TestDirectory.Create();
        using var store = CoverageArtifactStore.Open(Path.Join(temp.Path, "store"));
        Assert.Throws<InvalidDataException>(() => CoverageArtifactInspectionCursorStore.Read(store, scope!, TimeProvider.System));
    }

    [Fact]
    public void UnrecognizedDiagnosticUsesUnversionedGuideFallback()
    {
        var diagnostic = new CoverageArtifactDiagnostic("CUSTOM", "status", "safe", "safe");
        Assert.Equal("guides/coverage-storage.md#custom", diagnostic.Documentation);
    }

    [Fact]
    public void OutsideRepositoryPathIsNeverPresentedAsContainedRelativePath()
    {
        using var temp = TestDirectory.Create();
        var repository = temp.CreateDirectory("repo");
        Assert.Equal(CoverageArtifactDisplay.PathLabel(temp.Path), CoverageArtifactDisplay.PathLabel(temp.Path, repository));
        var sibling = temp.CreateDirectory("sibling");
        Assert.Equal(CoverageArtifactDisplay.PathLabel(sibling), CoverageArtifactDisplay.PathLabel(sibling, repository));
        Assert.Equal("inside.xml", CoverageArtifactDisplay.PathLabel(Path.Join(repository, "inside.xml"), repository));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
