using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Simulated reopen observations and native private metadata checks; no simulated remount is native lifecycle evidence.</summary>
[Collection("CoverageGate process state")]
public sealed class CoverageArtifactStoreIdentityTests
{
    private const string Persisted = "FileObjectIdentity { DeviceOrVolume = 16777232, FileId = 930550216 }";
    private const string Observed = "FileObjectIdentity { DeviceOrVolume = 16777231, FileId = 930550216 }";

    /// <summary>Rejects the reported device change in both open modes while preserving every store byte.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExactReportedVolumeMismatch_RefusesBothOpenModesWithoutChangingAnyBytes(bool readOnly)
    {
        using var fixture = new Fixture();
        fixture.Write("store.json", JsonSerializer.Serialize(new { SchemaVersion = 1, PhysicalIdentity = Persisted }));
        var before = fixture.Capture();
        var exception = Assert.Throws<InvalidDataException>(() => readOnly
            ? CoverageArtifactStore.OpenReadOnly(fixture.Root, observeIdentity: _ => Observed)
            : CoverageArtifactStore.Open(fixture.Root, observeIdentity: _ => Observed));
        var diagnostic = CoverageArtifactDiagnostic.Failure("ASCOV201", "status", exception);

        Assert.Equal("VolumeIdentityMismatch", diagnostic.Reason);
        Assert.Contains("matching file ID does not prove", diagnostic.Cause, StringComparison.Ordinal);
        Assert.Contains("reconciliation is refused", diagnostic.Recovery, StringComparison.Ordinal);
        Assert.DoesNotContain(Persisted, JsonSerializer.Serialize(diagnostic), StringComparison.Ordinal);
        fixture.AssertUnchanged(before);
    }

    /// <summary>Keeps malformed or object-only changes diagnostic and never treats them as ownership authority.</summary>
    [Theory]
    [InlineData("FileObjectIdentity { DeviceOrVolume = 16777232, FileId = 1 }", "IdentityMismatch")]
    [InlineData("FileObjectIdentity { DeviceOrVolume = 1, FileId = 930550216 }", "VolumeIdentityMismatch")]
    [InlineData("FileObjectIdentity { DeviceOrVolume = 1, FileId = 2 }", "VolumeIdentityMismatch")]
    [InlineData("FileObjectIdentity { DeviceOrVolume = invalid, FileId = 2 }", "IdentityMismatch")]
    [InlineData("FileObjectIdentity { DeviceOrVolume = 1, FileId = invalid }", "IdentityMismatch")]
    [InlineData("FileObjectIdentity { DeviceOrVolume = 1 }", "IdentityMismatch")]
    [InlineData("unknown", "IdentityMismatch")]
    [InlineData("FileObjectIdentity { DeviceOrVolume = 1, FileId = 2 }suffix", "IdentityMismatch")]
    public void IdentityComparison_UsesCompleteTuplesOnlyAndNeverGrantsAuthority(string observed, string expected)
        => Assert.Equal(expected, CoverageArtifactStoreFailure.CompareIdentity(Persisted, observed).ToString());

    /// <summary>Guards the diagnostic parser against drift in the real native identity producer's record format.</summary>
    [Fact]
    public void IdentityComparison_RecognizesTheActualNativeIdentityFormat()
    {
        using var temp = TestDirectory.Create();
        using var directory = CoverageRunOutputLease.AcquireArtifactDirectory(temp.Path, false, false)!;
        var identity = directory.ArtifactDirectoryIdentity;
        var match = System.Text.RegularExpressions.Regex.Match(identity, @"DeviceOrVolume = (\d+), FileId = (\d+)");
        Assert.True(match.Success);
        var volume = ulong.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var fileId = ulong.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        var changedVolume = identity.Replace($"DeviceOrVolume = {volume}", $"DeviceOrVolume = {volume ^ 1}", StringComparison.Ordinal);
        var changedFile = identity.Replace($"FileId = {fileId}", $"FileId = {fileId ^ 1}", StringComparison.Ordinal);
        Assert.NotEqual(identity, changedVolume);
        Assert.NotEqual(identity, changedFile);
        Assert.Equal(CoverageArtifactStoreFailureKind.VolumeIdentityMismatch, CoverageArtifactStoreFailure.CompareIdentity(identity, changedVolume));
        Assert.Equal(CoverageArtifactStoreFailureKind.IdentityMismatch, CoverageArtifactStoreFailure.CompareIdentity(identity, changedFile));
        directory.VerifyArtifactBinding();
    }

    /// <summary>Distinguishes invalid marker, registry and policy facts without mutating the private store.</summary>
    [Theory]
    [InlineData("marker-missing", "MarkerMissing")]
    [InlineData("marker-malformed", "MarkerInvalid")]
    [InlineData("marker-null", "MarkerInvalid")]
    [InlineData("marker-incomplete", "MarkerInvalid")]
    [InlineData("marker-schema", "MarkerSchemaUnsupported")]
    [InlineData("registry-missing", "RegistryInvalid")]
    [InlineData("registry-malformed", "RegistryInvalid")]
    [InlineData("registry-nested", "RegistryInvalid")]
    [InlineData("policy-malformed", "PolicyInvalid")]
    public void ReadOnlyOpen_ClassifiesSpecificMetadataFailureAndPreservesBytes(string state, string expected)
    {
        using var fixture = new Fixture();
        switch (state)
        {
            case "marker-missing": File.Delete(fixture.File("store.json")); break;
            case "marker-malformed": fixture.Write("store.json", "secret invalid JSON"); break;
            case "marker-null": fixture.Write("store.json", "null"); break;
            case "marker-incomplete": fixture.Write("store.json", "{\"SchemaVersion\":1}"); break;
            case "marker-schema": fixture.Write("store.json", "{\"SchemaVersion\":99,\"PhysicalIdentity\":\"secret\"}"); break;
            case "registry-missing": File.Delete(fixture.File("registry.json")); break;
            case "registry-malformed": fixture.Write("registry.json", "secret invalid JSON"); break;
            case "registry-nested":
                var registry = JsonNode.Parse(System.IO.File.ReadAllText(fixture.File("registry.json")))!;
                registry["Registrations"] = JsonNode.Parse("[{\"RunId\":\"invalid\",\"Inventory\":null}]");
                fixture.Write("registry.json", registry.ToJsonString());
                break;
            case "policy-malformed": fixture.Write("policy.json", "{\"schemaVersion\":999}"); break;
        }
        var before = fixture.Capture();
        var exception = Assert.Throws<InvalidDataException>(() => CoverageArtifactStore.OpenReadOnly(fixture.Root));
        var diagnostic = CoverageArtifactDiagnostic.Failure("ASCOV201", "status", exception);
        Assert.Equal(expected, diagnostic.Reason);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(diagnostic), StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Root, JsonSerializer.Serialize(diagnostic), StringComparison.Ordinal);
        fixture.AssertUnchanged(before);
    }

    /// <summary>Uses default retention policy without materializing a policy file during inspection.</summary>
    [Fact]
    public void MissingPolicy_UsesDefaultsAndDoesNotCreateFile()
    {
        using var fixture = new Fixture();
        var before = fixture.Capture();
        using var store = CoverageArtifactStore.OpenReadOnly(fixture.Root);
        Assert.Equal(2L * 1024 * 1024 * 1024, store.Policy.HistoryMaxBytes);
        fixture.AssertUnchanged(before);
    }

    /// <summary>Preserves old evidence, provenance, pinning and policy while initializing a separate empty domain.</summary>
    [Fact]
    public async Task RefusalAndSeparateNewDomain_PreserveEvidenceHashesProvenancePinsAndPolicy()
    {
        using var temp = TestDirectory.Create();
        var repository = temp.CreateDirectory("repository");
        var output = temp.CreateDirectory("repository/output");
        var evidence = temp.WriteFile("repository/output/coverage.xml", "historical failed evidence");
        var bytes = File.ReadAllBytes(evidence);
        var root = TestPathUtils.PathUnder(temp.Path, "old-domain");
        var provenance = new CoverageArtifactProvenance("revision", "working-tree", "coverage-policy");
        var run = new CoverageArtifactCurrentRun(new CoverageArtifactRunManifest
        {
            RunId = Guid.NewGuid().ToString("N"),
            Outcome = "failed",
            Version = "identity-regression",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Provenance = provenance
        }, [new CoverageArtifactInventoryEntry("coverage.xml", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant())], output, repository);
        using (var store = CoverageArtifactStore.Open(root))
        {
            await store.RegisterAsync(run, CancellationToken.None);
            await store.SnapshotAsync(run, pin: true, automatic: false, CancellationToken.None);
        }
        TestFileSystem.WritePrivateFile(TestPathUtils.PathUnder(root, "policy.json"), "{\"schemaVersion\":1,\"historyMaxBytes\":0}");
        var before = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
        Assert.Throws<InvalidDataException>(() => CoverageArtifactStore.OpenReadOnly(root, observeIdentity: _ => Observed));
        var fresh = TestPathUtils.PathUnder(temp.Path, "fresh-domain");
        CoverageArtifactStore.CreateNewDomain(fresh, true, CancellationToken.None);
        foreach (var (path, contents) in before) Assert.Equal(contents, File.ReadAllBytes(path));
        Assert.Equal(bytes, File.ReadAllBytes(evidence));
        using var original = CoverageArtifactStore.Open(root);
        Assert.Equal(0, original.Policy.HistoryMaxBytes);
        Assert.True(original.GetStatus().PinnedBytes > 0);
        Assert.Empty(await original.MaintainAsync(false, CancellationToken.None));
        var restored = TestPathUtils.PathUnder(temp.Path, "restored-history");
        await original.RestoreAsync(run.ManifestBase.RunId, restored, CancellationToken.None);
        Assert.Equal(bytes, File.ReadAllBytes(TestPathUtils.PathUnder(restored, "coverage.xml")));
        var current = original.GetCurrentRun(output)!;
        Assert.Equal("failed", current.ManifestBase.Outcome);
        Assert.Equal(provenance, current.ManifestBase.Provenance);
        using var empty = CoverageArtifactStore.OpenReadOnly(fresh);
        Assert.Equal(0, empty.GetStatus().RegisteredRuns);
    }

    /// <summary>Refuses untrusted permissions and preserves the original access mode.</summary>
    [Fact]
    public void BroadPrivatePermissions_AreDistinguishedWithoutRepair()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var path = fixture.File("store.json");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        var before = fixture.Capture();
        var failure = Assert.Throws<IOException>(() => CoverageArtifactStore.OpenReadOnly(fixture.Root));
        Assert.Equal("PrivateAccessRequired", CoverageArtifactDiagnostic.Failure("ASCOV201", "status", failure).Reason);
        Assert.True(File.GetUnixFileMode(path).HasFlag(UnixFileMode.GroupRead));
        fixture.AssertUnchanged(before);
    }

    /// <summary>Reports identity refusal through command envelopes without inventing zero counts or changing metadata.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StatusAndInspect_EmitSpecificIdentityDiagnosticWithUnavailableCountsAndNoMutation(bool inspect)
    {
        using var fixture = new Fixture();
        fixture.Write("store.json", JsonSerializer.Serialize(new { SchemaVersion = 1, PhysicalIdentity = Persisted }));
        var before = fixture.Capture();
        var previous = Environment.GetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME");
        Environment.SetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME", fixture.Root);
        try
        {
            using var console = new FakeInMemoryConsole();
            CoverageStorageCommandBase command = inspect
                ? new CoverageStorageInspectCommand { Json = true, Root = fixture.Root }
                : new CoverageStorageNamedStatusCommand { Json = true };
            var error = await Assert.ThrowsAsync<CliFx.CommandException>(async () => await command.ExecuteAsync(console, CancellationToken.None));
            Assert.Equal(1, error.ExitCode);
            var text = console.ReadOutputString();
            using var document = JsonDocument.Parse(text);
            var result = document.RootElement;
            Assert.Equal("failed", result.GetProperty("status").GetString());
            Assert.Equal("none", result.GetProperty("sideEffects").GetString());
            Assert.Equal(JsonValueKind.Null, result.GetProperty("data").ValueKind);
            Assert.False(result.GetProperty("observation").GetProperty("complete").GetBoolean());
            Assert.EndsWith("IdentityMismatch", result.GetProperty("diagnostics")[0].GetProperty("reason").GetString(), StringComparison.Ordinal);
            Assert.DoesNotContain(fixture.Root, text, StringComparison.Ordinal);
            fixture.AssertUnchanged(before);
        }
        finally { Environment.SetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME", previous); }
    }

    /// <summary>Accepts only the typed failure category and never parses exception messages as authority.</summary>
    [Fact]
    public void TrustedClassification_DoesNotUseAnArbitraryMessageOrDataValue()
    {
        var failure = new IOException("VolumeIdentityMismatch secret");
        failure.Data[typeof(CoverageArtifactStoreFailureKind)] = "VolumeIdentityMismatch";
        var diagnostic = CoverageArtifactDiagnostic.Failure("ASCOV201", "status", failure);
        Assert.Null(diagnostic.Reason);
        Assert.DoesNotContain("secret", diagnostic.Cause, StringComparison.Ordinal);
    }

    /// <summary>Keeps optional storage failure from changing the primary coverage outcome or historical metadata.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OptionalIdentityFailure_PreservesPrimarySessionOutcomeAndOldMetadata(bool success)
    {
        using var fixture = new Fixture();
        fixture.Write("store.json", JsonSerializer.Serialize(new { SchemaVersion = 1, PhysicalIdentity = Persisted }));
        var before = fixture.Capture();
        using var temp = TestDirectory.Create();
        var repository = temp.CreateDirectory("repo");
        var output = temp.CreateDirectory("repo/output");
        var evidence = temp.WriteFile("repo/output/evidence.xml", "primary evidence");
        var prior = Environment.GetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME");
        Environment.SetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME", fixture.Root);
        try
        {
            using var errors = new StringWriter();
            using var session = await CoverageArtifactSession.BeginAsync(output, repository, false,
                CoverageTextWriters.Create(TextWriter.Null, errors), TimeProvider.System, CancellationToken.None);
            session.Inventory.RecordFile(evidence);
            await session.CompleteAsync(success ? "success" : "failed", new CoverageArtifactProvenance(null, "unknown", "policy"));
            Assert.Contains("ASCOV201", errors.ToString(), StringComparison.Ordinal);
            Assert.Contains("identity", errors.ToString(), StringComparison.Ordinal);
            Assert.Equal("primary evidence", File.ReadAllText(evidence));
            fixture.AssertUnchanged(before);
        }
        finally { Environment.SetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME", prior); }
    }

    /// <summary>Renders bounded capability and permission reasons without leaking exception payloads.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnavailableCapabilitiesAndPermissionFailures_ExposeStableReasonsWithoutExceptionPayload(bool unsupported)
    {
        Exception exception = unsupported ? new PlatformNotSupportedException("secret") : new UnauthorizedAccessException("secret");
        var diagnostic = CoverageArtifactDiagnostic.Failure("ASCOV201", "status", exception);
        Assert.Equal(unsupported ? "CapabilityUnavailable" : "PrivateAccessRequired", diagnostic.Reason);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(diagnostic), StringComparison.Ordinal);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TestDirectory _temp = TestDirectory.Create();
        internal Fixture()
        {
            Root = TestPathUtils.PathUnder(_temp.Path, "store");
            using (CoverageArtifactStore.Open(Root)) { }
        }
        internal string Root { get; }
        internal string File(string name) => TestPathUtils.PathUnder(Root, name);
        internal void Write(string name, string text) => TestFileSystem.WritePrivateFile(File(name), text);
        internal Dictionary<string, string> Capture() => Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(Root, path), path => Convert.ToHexString(SHA256.HashData(System.IO.File.ReadAllBytes(path))));
        internal void AssertUnchanged(Dictionary<string, string> before) => Assert.Equal(before.OrderBy(item => item.Key), Capture().OrderBy(item => item.Key));
        public void Dispose() => _temp.Dispose();
    }
}
