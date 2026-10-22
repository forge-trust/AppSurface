using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactWarningStateFinalPathsTests
{
    [Theory]
    [InlineData("short-key")]
    [InlineData("nonhex-key")]
    [InlineData("null-condition")]
    [InlineData("negative-bytes")]
    public async Task InvalidWarningFactsNeverReplaceStateOrSuppressTheOriginalGrowthWarning(string fault)
    {
        using var temp = TestDirectory.Create();
        using var store = CoverageArtifactStore.Open(Path.Join(temp.Path, "domain"));
        var warnings = Path.Join(store.RootPath, "staging", "warnings");
        var key = fault == "short-key" ? "abc" : fault == "nonhex-key" ? new string('g', 64) : new string('a', 64);
        var now = new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
        object? condition = fault == "null-condition" ? null : new { Bytes = fault == "negative-bytes" ? -1L : 0, ObservedAtUtc = now };
        var original = JsonSerializer.SerializeToUtf8Bytes(new
        { SchemaVersion = 1, Conditions = new Dictionary<string, object?> { [key] = condition } });
        using (var directory = CoverageRunOutputLease.AcquireArtifactDirectory(warnings, true, true)!)
        using (var stream = directory.OpenArtifactFile("state.json", true)) stream.Write(original);
        using var errors = new StringWriter();
        await new CoverageArtifactWarnings(temp.Path, store, new FixedClock(now), errors).EmitAsync(
            new("ASCOV210", "storage-growth", "useful growth warning", "inspect"), "observed-growth", 1024);
        Assert.Contains("ASCOV201 warning-state", errors.ToString(), StringComparison.Ordinal);
        Assert.Contains("ASCOV210 storage-growth: useful growth warning", errors.ToString(), StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllBytes(Path.Join(warnings, "state.json")));
        Assert.False(File.Exists(Path.Join(warnings, CoverageArtifactMetadataPublication.StagingName)));
    }

    [Fact]
    public async Task ClockFailureDuringObservationBecomesASecondaryWarning()
    {
        using var temp = TestDirectory.Create();
        using var errors = new StringWriter();
        var warnings = new CoverageArtifactWarnings(temp.Path, null, new FailingClock(), errors);
        await warnings.ObserveAsync(true, null, default);
        Assert.Contains("ASCOV201 warning-inspection", errors.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private clock details", errors.ToString(), StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FailingClock : TimeProvider
    {
        public override long GetTimestamp() => throw new OperationCanceledException("private clock details");
    }
}
