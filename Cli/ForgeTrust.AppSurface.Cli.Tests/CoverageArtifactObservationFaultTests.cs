using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Cli;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactObservationFaultTests
{
    private const string FirstScope = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string SecondScope = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly DateTimeOffset FixedUtc = DateTimeOffset.Parse("2026-10-21T12:00:00Z");

    [Theory]
    [InlineData("null-scopes")]
    [InlineData("null-scope")]
    [InlineData("invalid-scope")]
    [InlineData("null-paths")]
    [InlineData("invalid-path")]
    [InlineData("duplicate-scope")]
    [InlineData("busy")]
    [InlineData("state-entry-is-directory")]
    public async Task ObserveAsync_PreflightAndTerminalTreatCursorFaultsAsSecondary(string fault)
    {
        using var fixture = new ObservationFixture(repositoryWarningBytes: 1024, domainWarningBytes: 100_000_000);
        var coveragePath = TestPathUtils.PathUnder(fixture.RepositoryPath, "TestResults", "coverage.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(coveragePath)!);
        File.WriteAllText(coveragePath, new string('x', 512));

        using var store = fixture.OpenStore();
        CoverageArtifactInspectionCursorStore.Write(store, FirstScope, ["pending"], fixture.Clock);
        var inspectionPath = TestPathUtils.PathUnder(store.RootPath, "staging", "inspection");
        var statePath = TestPathUtils.PathUnder(inspectionPath, "state.json");
        string? corruptState = null;
        if (fault is not "busy" and not "state-entry-is-directory")
        {
            corruptState = CorruptCursorState(fault);
            File.WriteAllText(statePath, corruptState, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        else if (fault == "state-entry-is-directory")
        {
            File.Delete(statePath);
            Directory.CreateDirectory(statePath);
        }

        using var errors = new StringWriter();
        var warnings = new CoverageArtifactWarnings(fixture.RepositoryPath, store, fixture.Clock, errors);
        if (fault == "busy")
        {
            using var blocker = CoverageArtifactOperationLease.Acquire(inspectionPath, exclusive: true);
            await warnings.ObserveAsync(terminal: false, inventory: null, CancellationToken.None);
        }
        else
        {
            await warnings.ObserveAsync(terminal: false, inventory: null, CancellationToken.None);
        }

        Assert.Contains("ASCOV201 inspection-cursor:", errors.ToString(), StringComparison.Ordinal);
        if (corruptState is not null)
        {
            Assert.Equal(corruptState, File.ReadAllText(statePath));
        }
        else if (fault == "state-entry-is-directory")
        {
            Assert.True(Directory.Exists(statePath));
        }

        File.WriteAllText(coveragePath, new string('x', 4096));
        await warnings.ObserveAsync(terminal: true, inventory: null, CancellationToken.None);

        Assert.Contains("ASCOV210 storage-growth:", errors.ToString(), StringComparison.Ordinal);
        Assert.Contains("observedBytes=4096", errors.ToString(), StringComparison.Ordinal);
        if (corruptState is not null)
        {
            Assert.Equal(corruptState, File.ReadAllText(statePath));
        }
    }

    [Fact]
    public void Write_WhenEncodedStateExceeds64KiB_EvictsOldScopeAndKeepsNewScopePaths()
    {
        using var temp = TestDirectory.Create();
        using var store = CoverageArtifactStore.Open(TestPathUtils.PathUnder(temp.Path, "store"));
        var clock = new FixedTimeProvider(FixedUtc);
        var oldPaths = Enumerable.Range(0, 14).Select(index => $"old-{index:D2}-" + new string('o', 4000)).ToArray();
        var newPaths = Enumerable.Range(0, 14).Select(index => $"new-{index:D2}-" + new string('n', 4000)).ToArray();

        CoverageArtifactInspectionCursorStore.Write(store, FirstScope, oldPaths, clock);
        CoverageArtifactInspectionCursorStore.Write(store, SecondScope, newPaths, clock);

        Assert.Null(CoverageArtifactInspectionCursorStore.Read(store, FirstScope, clock));
        Assert.Equal(newPaths, CoverageArtifactInspectionCursorStore.Read(store, SecondScope, clock));
        Assert.InRange(new FileInfo(TestPathUtils.PathUnder(store.RootPath, "staging", "inspection", "state.json")).Length, 1, 64 * 1024);
    }

    [Fact]
    public async Task ObserveAsync_ReportsCapacityAndGrowthWhilePreservingUnknownWarningState()
    {
        using var fixture = new ObservationFixture(repositoryWarningBytes: 100_000_000, domainWarningBytes: 1);
        using var store = fixture.OpenStore();
        var registryPath = TestPathUtils.PathUnder(store.RootPath, "registry.json");
        File.WriteAllText(registryPath, JsonSerializer.Serialize(new
        {
            SchemaVersion = 1,
            Registrations = Array.Empty<object>(),
            Reservations = Array.Empty<object>(),
            OverflowObservations = Array.Empty<object>(),
            IsComplete = false,
            IncompleteReason = "registration capacity reached",
            ExpiryReceipts = Array.Empty<object>()
        }));

        var warningState = "{\"SchemaVersion\":2,\"Conditions\":{}}";
        var warningStatePath = TestPathUtils.PathUnder(store.RootPath, "staging", "warnings", "state.json");
        using (var warningDirectory = CoverageRunOutputLease.AcquireArtifactDirectory(Path.GetDirectoryName(warningStatePath)!, true, true)!)
        using (var state = warningDirectory.OpenArtifactFile("state.json", createNew: true))
            state.Write(Encoding.UTF8.GetBytes(warningState));

        using var errors = new StringWriter();
        var warnings = new CoverageArtifactWarnings(fixture.RepositoryPath, store, fixture.Clock, errors);
        await warnings.ObserveAsync(terminal: true, inventory: null, CancellationToken.None);

        var output = errors.ToString();
        Assert.Contains("ASCOV207 accounting:", output, StringComparison.Ordinal);
        Assert.Contains("ASCOV210 storage-growth: Registered domain contains at least", output, StringComparison.Ordinal);
        Assert.Contains("ASCOV201 warning-state:", output, StringComparison.Ordinal);
        Assert.Equal(warningState, File.ReadAllText(warningStatePath));
    }

    [Fact]
    public void Observe_ReportsUnknownForMissingFrameworkEvaluationAndMalformedOrOversizedOverrides()
    {
        using var temp = TestDirectory.Create();
        var project = new CoverageRunProject(
            "Sample.Tests.csproj",
            TestPathUtils.PathUnder(temp.Path, "Sample.Tests.csproj"),
            "sample-tests",
            IsExclusive: false);
        var capability = Capability("net10.0");
        var blankFramework = CoverageArtifactSettings.Observe(project, " ", capability, "Debug", []);
        var missingEvaluatedFramework = CoverageArtifactSettings.Observe(
            project,
            "net10.0",
            "{\"Properties\":{\"Configuration\":\"Debug\"}}",
            "Debug",
            []);
        var malformedOverride = CoverageArtifactSettings.Observe(
            project,
            "net10.0",
            capability,
            "Debug",
            ["--property:RunSettingsFilePath=selected.runsettings;"]);
        var oversizedOverride = CoverageArtifactSettings.Observe(
            project,
            "net10.0",
            capability,
            "Debug",
            ["--property:RunSettingsFilePath=" + new string('p', 4097)]);

        Assert.Equal("unknown", blankFramework.State);
        Assert.Equal("framework-evaluation-unavailable", blankFramework.Cause);
        Assert.Equal("unknown", missingEvaluatedFramework.State);
        Assert.Equal("framework-evaluation-unavailable", missingEvaluatedFramework.Cause);
        Assert.Equal("unknown", malformedOverride.State);
        Assert.Equal("settings-override-unusable", malformedOverride.Cause);
        Assert.Equal("unknown", oversizedOverride.State);
        Assert.Equal("settings-override-unusable", oversizedOverride.Cause);
    }

    private static string CorruptCursorState(string fault)
    {
        var timestamp = FixedUtc;
        object Scope(string? id, string[]? paths) => new { ScopeId = id, UpdatedAtUtc = timestamp, Paths = paths };
        object?[]? scopes = fault switch
        {
            "null-scopes" => null,
            "null-scope" => new object?[] { null },
            "invalid-scope" => [Scope("not-a-hash", ["pending"])],
            "null-paths" => [Scope(FirstScope, null)],
            "invalid-path" => [Scope(FirstScope, ["pending/../escape"])],
            "duplicate-scope" => [Scope(FirstScope, ["first"]), Scope(FirstScope, ["second"])],
            _ => throw new ArgumentOutOfRangeException(nameof(fault), fault, "Unknown cursor fault.")
        };
        return JsonSerializer.Serialize(new { SchemaVersion = 1, Scopes = scopes });
    }

    private static string Capability(string targetFramework) => JsonSerializer.Serialize(new
    {
        Properties = new { Configuration = "Debug", TargetFramework = targetFramework }
    });

    private sealed class ObservationFixture : IDisposable
    {
        private readonly TestDirectory _temp = TestDirectory.Create();
        private readonly long _repositoryWarningBytes;
        private readonly long _domainWarningBytes;

        internal ObservationFixture(long repositoryWarningBytes, long domainWarningBytes)
        {
            _repositoryWarningBytes = repositoryWarningBytes;
            _domainWarningBytes = domainWarningBytes;
            StorePath = TestPathUtils.PathUnder(_temp.Path, "private-store");
            RepositoryPath = TestPathUtils.PathUnder(_temp.Path, "repository");
            Directory.CreateDirectory(RepositoryPath);
            using (CoverageArtifactStore.Open(StorePath, Clock)) { }
            WritePolicy();
        }

        internal string StorePath { get; }
        internal string RepositoryPath { get; }
        internal FixedTimeProvider Clock { get; } = new(FixedUtc);

        internal CoverageArtifactStore OpenStore() => CoverageArtifactStore.Open(StorePath, Clock);

        public void Dispose() => _temp.Dispose();

        private void WritePolicy()
        {
            var path = TestPathUtils.PathUnder(StorePath, "policy.json");
            var json = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                repositoryWarningBytes = _repositoryWarningBytes,
                domainWarningBytes = _domainWarningBytes,
                warningCooldownHours = 24
            });
            TestFileSystem.WritePrivateFile(path, json);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
