using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

[Collection("CoverageGate process state")]
public sealed class CoverageArtifactSessionBoundaryTests
{
    [Theory]
    [InlineData("outputIdentity")]
    [InlineData("repositoryIdentity")]
    public async Task BeginAsync_MisboundRecoverySidecarWarnsWithoutAdoptingOrReplacingOutput(string identityProperty)
    {
        using var fixture = TestDirectory.Create();
        using var storeHome = TestDirectory.Create();
        using var configuredHome = ConfigureStore(storeHome.Path);
        var repository = fixture.CreateDirectory("repo");
        var output = fixture.CreateDirectory("repo/coverage");
        var artifact = Path.Join(output, "coverage.xml");
        var contents = "producer-owned coverage";
        await File.WriteAllTextAsync(artifact, contents);
        await RegisterRunAsync(output, repository, artifact, "success");
        await RemoveRegistrationsAsync();

        var sidecarPath = Path.Join(output, CoverageArtifactSession.SidecarName);
        var sidecar = JsonNode.Parse(await File.ReadAllTextAsync(sidecarPath))!;
        sidecar["manifestBase"]![identityProperty] = "different-physical-identity";
        var mismatchedSidecar = sidecar.ToJsonString();
        await File.WriteAllTextAsync(sidecarPath, mismatchedSidecar);
        using var errors = new StringWriter();

        using var session = await CoverageArtifactSession.BeginAsync(output, repository, clean: true,
            CoverageTextWriters.Create(TextWriter.Null, errors), TimeProvider.System, CancellationToken.None);

        Assert.Contains("ASCOV200", errors.ToString(), StringComparison.Ordinal);
        using var store = CoverageArtifactStore.Open();
        Assert.Null(store.GetCurrentRun(output));
        Assert.Equal(0, store.GetStatus().ArchivedRuns);
        Assert.Equal(contents, await File.ReadAllTextAsync(artifact));
        Assert.Equal(mismatchedSidecar, await File.ReadAllTextAsync(sidecarPath));
    }

    [Fact]
    public async Task BeginAsync_RegisteredSidecarInventoryMismatchDoesNotSnapshotOrReplaceOutput()
    {
        using var fixture = TestDirectory.Create();
        using var storeHome = TestDirectory.Create();
        using var configuredHome = ConfigureStore(storeHome.Path);
        var repository = fixture.CreateDirectory("repo");
        var output = fixture.CreateDirectory("repo/coverage");
        var artifact = Path.Join(output, "coverage.xml");
        var contents = "registered coverage";
        await File.WriteAllTextAsync(artifact, contents);
        var prior = await RegisterRunAsync(output, repository, artifact, "failed");

        var sidecarPath = Path.Join(output, CoverageArtifactSession.SidecarName);
        var sidecar = JsonNode.Parse(await File.ReadAllTextAsync(sidecarPath))!;
        var firstEntry = sidecar["inventory"]!.AsArray()[0]!;
        firstEntry["sha256"] = new string('0', 64);
        var mismatchedSidecar = sidecar.ToJsonString();
        await File.WriteAllTextAsync(sidecarPath, mismatchedSidecar);
        using var errors = new StringWriter();

        using var session = await CoverageArtifactSession.BeginAsync(output, repository, clean: true,
            CoverageTextWriters.Create(TextWriter.Null, errors), TimeProvider.System, CancellationToken.None);

        Assert.Contains("ASCOV200", errors.ToString(), StringComparison.Ordinal);
        using var store = CoverageArtifactStore.Open();
        var current = store.GetCurrentRun(output)!;
        Assert.Equal(prior.ManifestBase.RunId, current.ManifestBase.RunId);
        Assert.Equal(prior.ManifestBase.Outcome, current.ManifestBase.Outcome);
        Assert.Equal(prior.Inventory.ToArray(), current.Inventory.ToArray());
        Assert.Equal(0, store.GetStatus().ArchivedRuns);
        Assert.Equal(contents, await File.ReadAllTextAsync(artifact));
        Assert.Equal(mismatchedSidecar, await File.ReadAllTextAsync(sidecarPath));
    }

    [Theory]
    [InlineData("null-manifest")]
    [InlineData("null-inventory")]
    [InlineData("null-output-path")]
    [InlineData("null-repository-path")]
    [InlineData("unsupported-schema")]
    [InlineData("invalid-run-id")]
    [InlineData("too-many-inventory-entries")]
    public void ReadCurrent_RejectsInvalidSidecarMetadata(string defect)
    {
        var manifest = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["runId"] = "00112233445566778899aabbccddeeff"
        };
        var sidecar = new JsonObject
        {
            ["manifestBase"] = manifest,
            ["inventory"] = new JsonArray(),
            ["outputPath"] = "/output",
            ["repositoryPath"] = "/repository"
        };

        switch (defect)
        {
            case "null-manifest":
                sidecar["manifestBase"] = null;
                break;
            case "null-inventory":
                sidecar["inventory"] = null;
                break;
            case "null-output-path":
                sidecar["outputPath"] = null;
                break;
            case "null-repository-path":
                sidecar["repositoryPath"] = null;
                break;
            case "unsupported-schema":
                sidecar["manifestBase"]!["schemaVersion"] = 2;
                break;
            case "invalid-run-id":
                sidecar["manifestBase"]!["runId"] = "not-a-guid";
                break;
            case "too-many-inventory-entries":
                var inventory = sidecar["inventory"]!.AsArray();
                for (var index = 0; index <= CoverageArtifactLimits.MaximumFiles; index++)
                {
                    inventory.Add(new JsonObject
                    {
                        ["path"] = $"coverage-{index}.xml",
                        ["length"] = 1,
                        ["sha256"] = new string('a', 64)
                    });
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(defect), defect, "Unknown sidecar defect.");
        }

        using var input = new MemoryStream(Encoding.UTF8.GetBytes(sidecar.ToJsonString()));

        Assert.Throws<InvalidDataException>(() => CoverageArtifactSession.ReadCurrent(input));
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("missing")]
    public async Task UpdateRegisteredInventoryAsync_RejectsChangedProducerBytesBeforePublishingReports(string producerFileState)
    {
        using var fixture = TestDirectory.Create();
        using var storeHome = TestDirectory.Create();
        using var configuredHome = ConfigureStore(storeHome.Path);
        var repository = fixture.CreateDirectory("repo");
        var output = fixture.CreateDirectory("repo/coverage");
        var artifact = Path.Join(output, "coverage.xml");
        var originalContents = "original producer coverage";
        await File.WriteAllTextAsync(artifact, originalContents);
        var prior = await RegisterRunAsync(output, repository, artifact, "failed");
        var sidecarPath = Path.Join(output, CoverageArtifactSession.SidecarName);
        var priorSidecar = await File.ReadAllBytesAsync(sidecarPath);

        if (producerFileState == "changed")
            await File.WriteAllTextAsync(artifact, "changed by an uncoordinated writer");
        else
            File.Delete(artifact);

        var gateReport = Path.Join(output, CoverageGateArtifactNames.Json);
        var reportContents = "new gate report";
        await File.WriteAllTextAsync(gateReport, reportContents);
        using var errors = new StringWriter();
        using var outputLease = CoverageArtifactOperationLease.Acquire(output, exclusive: true);

        await CoverageArtifactSession.UpdateRegisteredInventoryAsync(output, [gateReport], errors);

        Assert.Contains("ASCOV201", errors.ToString(), StringComparison.Ordinal);
        Assert.Contains("gate-inventory", errors.ToString(), StringComparison.Ordinal);
        using var store = CoverageArtifactStore.Open();
        var current = store.GetCurrentRun(output)!;
        Assert.Equal(prior.ManifestBase.RunId, current.ManifestBase.RunId);
        Assert.Equal(prior.ManifestBase.Outcome, current.ManifestBase.Outcome);
        Assert.Equal(prior.ManifestBase.Provenance, current.ManifestBase.Provenance);
        Assert.Equal(prior.Inventory.ToArray(), current.Inventory.ToArray());
        Assert.Equal(priorSidecar, await File.ReadAllBytesAsync(sidecarPath));
        Assert.Equal(reportContents, await File.ReadAllTextAsync(gateReport));
        if (producerFileState == "changed")
            Assert.Equal("changed by an uncoordinated writer", await File.ReadAllTextAsync(artifact));
        else
            Assert.False(File.Exists(artifact));
        Assert.DoesNotContain(current.Inventory, entry => entry.Path == CoverageGateArtifactNames.Json);
    }

    [Fact]
    public async Task UpdateRegisteredInventoryAsync_AllowsListedExistingReportToBeOverwritten()
    {
        using var fixture = TestDirectory.Create();
        using var storeHome = TestDirectory.Create();
        using var configuredHome = ConfigureStore(storeHome.Path);
        var repository = fixture.CreateDirectory("repo");
        var output = fixture.CreateDirectory("repo/coverage");
        var coverage = Path.Join(output, "coverage.xml");
        var gateReport = Path.Join(output, CoverageGateArtifactNames.Json);
        var coverageContents = "unchanged producer coverage";
        var oldReportContents = "previous report";
        var newReportContents = "replacement report";
        await File.WriteAllTextAsync(coverage, coverageContents);
        await File.WriteAllTextAsync(gateReport, oldReportContents);
        var provenance = Provenance();
        using (var session = await BeginAsync(output, repository))
        {
            session.Inventory.RecordFile(coverage);
            session.Inventory.RecordFile(gateReport);
            await session.CompleteAsync("failed", provenance);
        }
        CoverageArtifactCurrentRun prior;
        using (var store = CoverageArtifactStore.Open())
            prior = store.GetCurrentRun(output)!;

        await File.WriteAllTextAsync(gateReport, newReportContents);
        using var errors = new StringWriter();
        using var outputLease = CoverageArtifactOperationLease.Acquire(output, exclusive: true);

        await CoverageArtifactSession.UpdateRegisteredInventoryAsync(output, [gateReport], errors);

        Assert.Empty(errors.ToString());
        using var updatedStore = CoverageArtifactStore.Open();
        var updated = updatedStore.GetCurrentRun(output)!;
        Assert.Equal(prior.ManifestBase.RunId, updated.ManifestBase.RunId);
        Assert.Equal(prior.ManifestBase.Outcome, updated.ManifestBase.Outcome);
        Assert.Equal(prior.ManifestBase.Provenance, updated.ManifestBase.Provenance);
        Assert.Equal(coverageContents, await File.ReadAllTextAsync(coverage));
        Assert.Equal(newReportContents, await File.ReadAllTextAsync(gateReport));
        var coverageEntry = Assert.Single(updated.Inventory, entry => entry.Path == "coverage.xml");
        Assert.Equal(coverageContents.Length, coverageEntry.Length);
        Assert.Equal(Hash(coverageContents), coverageEntry.Sha256);
        var reportEntry = Assert.Single(updated.Inventory, entry => entry.Path == CoverageGateArtifactNames.Json);
        Assert.Equal(newReportContents.Length, reportEntry.Length);
        Assert.Equal(Hash(newReportContents), reportEntry.Sha256);
    }

    [Fact]
    public async Task CompleteAsync_OutputStatusWriterFailureKeepsFailedRunRegistered()
    {
        using var fixture = TestDirectory.Create();
        using var storeHome = TestDirectory.Create();
        using var configuredHome = ConfigureStore(storeHome.Path);
        var repository = fixture.CreateDirectory("repo");
        var output = fixture.CreateDirectory("repo/coverage");
        var artifact = Path.Join(output, "coverage.xml");
        await File.WriteAllTextAsync(artifact, "coverage from failed primary run");
        using var session = await CoverageArtifactSession.BeginAsync(output, repository, clean: false,
            CoverageTextWriters.Create(new FailingTextWriter(), TextWriter.Null),
            TimeProvider.System, CancellationToken.None);
        session.Inventory.RecordFile(artifact);

        await session.CompleteAsync("failed", Provenance());

        using var store = CoverageArtifactStore.Open();
        var current = store.GetCurrentRun(output)!;
        Assert.Equal("failed", current.ManifestBase.Outcome);
        Assert.Contains(current.Inventory, entry => entry.Path == "coverage.xml");
        Assert.Contains(current.Inventory, entry => entry.Path == CoverageArtifactSession.SidecarName);
    }

    [Fact]
    public async Task RecordPublishedArtifactsAsync_AfterCompletionRetainsRunOutcomeAndAddsReport()
    {
        using var fixture = TestDirectory.Create();
        using var storeHome = TestDirectory.Create();
        using var configuredHome = ConfigureStore(storeHome.Path);
        var repository = fixture.CreateDirectory("repo");
        var output = fixture.CreateDirectory("repo/coverage");
        var coverage = Path.Join(output, "coverage.xml");
        var gateReport = Path.Join(output, CoverageGateArtifactNames.Json);
        var provenance = Provenance();
        await File.WriteAllTextAsync(coverage, "coverage");

        using (var session = await BeginAsync(output, repository))
        {
            session.Inventory.RecordFile(coverage);
            await session.CompleteAsync("failed", provenance);
            await File.WriteAllTextAsync(gateReport, "gate report");
            await session.RecordPublishedArtifactsAsync([gateReport]);
        }

        using var store = CoverageArtifactStore.Open();
        var current = store.GetCurrentRun(output)!;
        Assert.Equal("failed", current.ManifestBase.Outcome);
        Assert.Equal(provenance, current.ManifestBase.Provenance);
        Assert.Contains(current.Inventory, entry => entry.Path == CoverageGateArtifactNames.Json);
    }

    private static async Task<CoverageArtifactCurrentRun> RegisterRunAsync(
        string output, string repository, string artifactPath, string outcome)
    {
        using var session = await BeginAsync(output, repository);
        session.Inventory.RecordFile(artifactPath);
        await session.CompleteAsync(outcome, Provenance());
        using var store = CoverageArtifactStore.Open();
        return store.GetCurrentRun(output)!;
    }

    private static Task<CoverageArtifactSession> BeginAsync(string output, string repository) =>
        CoverageArtifactSession.BeginAsync(output, repository, clean: false,
            CoverageTextWriters.Create(TextWriter.Null, TextWriter.Null), TimeProvider.System, CancellationToken.None);

    private static CoverageArtifactProvenance Provenance() =>
        new("revision-fingerprint", "clean-fingerprint", "configuration-fingerprint");

    private static async Task RemoveRegistrationsAsync()
    {
        using var store = CoverageArtifactStore.Open();
        var registryPath = Path.Join(store.RootPath, "registry.json");
        var registry = JsonNode.Parse(await File.ReadAllTextAsync(registryPath))!;
        registry["Registrations"] = new JsonArray();
        await File.WriteAllTextAsync(registryPath, registry.ToJsonString());
    }

    private static IDisposable ConfigureStore(string path)
    {
        const string variable = "APPSURFACE_ARTIFACT_HOME";
        var previous = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, Path.Join(path, "private-store"));
        return new RestoreEnvironment(variable, previous);
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class RestoreEnvironment(string name, string? value) : IDisposable
    {
        public void Dispose() => Environment.SetEnvironmentVariable(name, value);
    }

    private sealed class FailingTextWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override Task WriteLineAsync(string? value) =>
            Task.FromException(new IOException("The primary output stream is unavailable."));
    }
}
