using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

[Collection("CoverageGate process state")]
public sealed class CoverageArtifactSessionRemainingTests
{
    [Fact]
    public async Task ExhaustedOptionalMaintenanceBudgetWarnsAndLeavesPrimarySessionUsable()
    {
        using var temp = TestDirectory.Create();
        using var configured = new StoreEnvironment(Path.Join(temp.Path, "store"));
        var repository = temp.CreateDirectory("repo");
        var output = temp.CreateDirectory("repo/output");
        using var errors = new StringWriter();
        using var session = await CoverageArtifactSession.BeginAsync(output, repository, false,
            CoverageTextWriters.Create(TextWriter.Null, errors), new AdvancingClock(), default);
        session.VerifyBinding();
        Assert.Contains("ASCOV204 maintenance", errors.ToString(), StringComparison.Ordinal);
        using var store = CoverageArtifactStore.Open();
        Assert.Equal(0, store.GetStatus().RegisteredRuns);
        Assert.False(File.Exists(Path.Join(output, CoverageArtifactSession.SidecarName)));
    }

    [Fact]
    public async Task OversizedProvenanceNeverPublishesSidecarOrOwnership()
    {
        using var temp = TestDirectory.Create();
        using var configured = new StoreEnvironment(Path.Join(temp.Path, "store"));
        var output = temp.CreateDirectory("repo/output");
        using var errors = new StringWriter();
        using var session = await CoverageArtifactSession.BeginAsync(output, Path.Join(temp.Path, "repo"), false,
            CoverageTextWriters.Create(TextWriter.Null, errors), TimeProvider.System, default);
        await session.CompleteAsync("failed", new("known", "dirty", new string('x', checked((int)CoverageArtifactLimits.RunInventoryMaxBytes))));
        Assert.Contains("ASCOV201 register", errors.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Join(output, CoverageArtifactSession.SidecarName)));
        using var store = CoverageArtifactStore.Open();
        Assert.Equal(0, store.GetStatus().RegisteredRuns);
    }

    [Fact]
    public async Task ReportsRecordedBeforeCompletionRemainUnpublishedUntilPrimaryOutcomeIsKnown()
    {
        using var temp = TestDirectory.Create();
        using var configured = new StoreEnvironment(Path.Join(temp.Path, "store"));
        var output = temp.CreateDirectory("repo/output");
        var report = Path.Join(output, "coverage-gate.json");
        await File.WriteAllTextAsync(report, "gate report");
        using var session = await CoverageArtifactSession.BeginAsync(output, Path.Join(temp.Path, "repo"), false,
            CoverageTextWriters.Create(TextWriter.Null, TextWriter.Null), TimeProvider.System, default);
        await session.RecordPublishedArtifactsAsync([report]);
        Assert.False(File.Exists(Path.Join(output, CoverageArtifactSession.SidecarName)));
        await session.CompleteAsync("interrupted", new(null, "dirty", "policy"));
        using var store = CoverageArtifactStore.Open();
        var run = Assert.IsType<CoverageArtifactCurrentRun>(store.GetCurrentRun(output));
        Assert.Equal("interrupted", run.ManifestBase.Outcome);
        Assert.Equal("dirty", run.ManifestBase.Provenance!.GitStateFingerprint);
        Assert.Contains(run.Inventory, entry => entry.Path == "coverage-gate.json");
    }

    [Fact]
    public async Task GateOnUnregisteredOutputDoesNotAdoptUnmanagedReports()
    {
        using var temp = TestDirectory.Create();
        using var configured = new StoreEnvironment(Path.Join(temp.Path, "store"));
        var output = temp.CreateDirectory("output");
        var report = Path.Join(output, "coverage-gate.json");
        await File.WriteAllTextAsync(report, "unmanaged");
        using var errors = new StringWriter();
        using var outputLease = CoverageArtifactOperationLease.Acquire(output, exclusive: true);
        await CoverageArtifactSession.UpdateRegisteredInventoryAsync(output, [report], errors, outputLease);
        Assert.Empty(errors.ToString());
        Assert.False(File.Exists(Path.Join(output, CoverageArtifactSession.SidecarName)));
        Assert.Equal("unmanaged", await File.ReadAllTextAsync(report));
    }

    [Fact]
    public void JsonNullSidecarIsRejectedAsEmptyMetadata()
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("null"));
        Assert.Throws<InvalidDataException>(() => CoverageArtifactSession.ReadCurrent(input));
    }

    [Fact]
    public async Task DisposingSessionTwiceDoesNotRetainTheRequiredOutputLease()
    {
        using var temp = TestDirectory.Create();
        using var configured = new StoreEnvironment(Path.Join(temp.Path, "store"));
        var output = temp.CreateDirectory("repo/output");
        var session = await CoverageArtifactSession.BeginAsync(output, Path.Join(temp.Path, "repo"), false,
            CoverageTextWriters.Create(TextWriter.Null, TextWriter.Null), TimeProvider.System, default);
        session.Dispose();
        session.Dispose();
        using var lease = CoverageArtifactOperationLease.Acquire(output, true);
        lease.VerifyBinding();
    }

    private sealed class AdvancingClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Add(ref _ticks, TimeSpan.TicksPerSecond * 6);
    }

    private sealed class StoreEnvironment : IDisposable
    {
        private readonly string? _before = Environment.GetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME");
        internal StoreEnvironment(string root)
        {
            Environment.SetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME", root);
        }

        public void Dispose() => Environment.SetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME", _before);
    }
}
