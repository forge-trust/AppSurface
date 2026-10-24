using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactWarningCapacityTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WarningStateEvictsExpiredOrOldestFactsBeforeAdmittingAnotherCondition(bool expired)
    {
        using var temp = TestDirectory.Create();
        using var store = CoverageArtifactStore.Open(Path.Join(temp.Path, "store"));
        var now = DateTimeOffset.Parse("2026-10-22T00:00:00Z");
        var clock = new FixedClock(now);
        var conditions = new Dictionary<string, object>();
        for (var index = 0; index < 1000; index++)
            conditions.Add(index.ToString("x64"), new { Bytes = 0, ObservedAtUtc = expired ? now.AddDays(-8) : now.AddMinutes(index - 1000) });
        var warningsPath = Path.Join(store.RootPath, "staging", "warnings");
        using (var directory = CoverageRunOutputLease.AcquireArtifactDirectory(warningsPath, true, true)!)
        using (var state = directory.OpenArtifactFile("state.json", true))
            state.Write(JsonSerializer.SerializeToUtf8Bytes(new { SchemaVersion = 1, Conditions = conditions }));
        using var errors = new StringWriter();
        var warnings = new CoverageArtifactWarnings(temp.Path, store, clock, errors);
        await warnings.EmitAsync(new("ASCOV210", "storage-growth", "capacity fixture", "inspect"), "new-condition", 1024);
        var statePath = TestPathUtils.PathUnder(warningsPath, "state.json");
        var saved = JsonNode.Parse(File.ReadAllText(statePath))!["Conditions"]!.AsObject();
        Assert.Equal(expired ? 1 : 1000, saved.Count);
        Assert.False(saved.ContainsKey(0.ToString("x64")));
        Assert.Contains("ASCOV210", errors.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("ASCOV201", errors.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ObservationWritersReconcileOnlyTheirKnownSlotsAndStatusAccountsForBoth()
    {
        using var temp = TestDirectory.Create();
        using var store = CoverageArtifactStore.Open(Path.Join(temp.Path, "store"));
        const string scope = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var clock = new FixedClock(DateTimeOffset.Parse("2026-10-22T00:00:00Z"));
        var baseline = store.GetStatus().MetadataBytes;
        foreach (var name in new[] { "warnings", "inspection" })
        {
            using var directory = CoverageRunOutputLease.AcquireArtifactDirectory(TestPathUtils.PathUnder(store.RootPath, "staging", name), true, true)!;
            using var abandoned = directory.OpenArtifactFile(CoverageArtifactMetadataPublication.StagingName, true);
            abandoned.Write(new byte[17]);
        }
        Assert.Equal(baseline + 34, store.GetStatus().MetadataBytes);
        using var errors = new StringWriter();
        await new CoverageArtifactWarnings(temp.Path, store, clock, errors).EmitAsync(
            new("ASCOV210", "storage-growth", "recovery fixture", "inspect"), "condition", 1);
        CoverageArtifactInspectionCursorStore.Write(store, scope, ["TestResults"], clock);
        Assert.Equal(new[] { "TestResults" }, CoverageArtifactInspectionCursorStore.Read(store, scope, clock));
        foreach (var name in new[] { "warnings", "inspection" })
            Assert.False(File.Exists(TestPathUtils.PathUnder(store.RootPath, "staging", name, CoverageArtifactMetadataPublication.StagingName)));
        Assert.DoesNotContain("ASCOV201", errors.ToString(), StringComparison.Ordinal);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
