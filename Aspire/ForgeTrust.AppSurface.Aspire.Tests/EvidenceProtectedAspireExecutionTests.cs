using System.Net.Sockets;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;

namespace ForgeTrust.AppSurface.Aspire.Tests;

public sealed class EvidenceProtectedAspireExecutionTests
{
    private const string BrokerEnvironmentVariable = "EVIDENCEHOST_TEST_BROKER_SOCKET";

    [Fact]
    public async Task ConnectAsync_RejectsUnsupportedPlatformOrMissingFixtureChannel()
    {
        if (OperatingSystem.IsLinux())
        {
            var missing = Path.Combine(Path.GetTempPath(), $"evidencehost-missing-{Guid.NewGuid():N}.sock");
            await Assert.ThrowsAnyAsync<SocketException>(() =>
                EvidenceLinuxWorkerSupervisor.ConnectAsync(missing, CancellationToken.None));
            return;
        }

        var unsupported = await Assert.ThrowsAsync<EvidenceAdmissionException>(() =>
            EvidenceLinuxWorkerSupervisor.ConnectAsync("/tmp/evidencehost-unsupported.sock", CancellationToken.None));
        Assert.Equal("ASEVD402", unsupported.Code);
        Assert.Contains("unavailable on this platform", unsupported.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_EmptyObservationWritesVerifiedInformationalManifestAndConfiguresOnce()
    {
        var fixture = await RequireFixtureAsync("aspire-empty");
        if (fixture is null) return;
        var plan = ResolvePlan(fixture);
        var configureCalls = 0;
        await using var host = EvidenceHostBootstrap.Create(plan, _ => configureCalls++);

        var manifest = await host.RunAsync(new EvidenceExecutionRequest(EvidenceExecutionMode.Observation, fixture.Socket));

        Assert.Equal(1, configureCalls);
        Assert.Equal(EvidenceHostState.Completed, host.State);
        Assert.Equal(EvidenceExecutionMode.Observation, manifest.Mode);
        Assert.Equal(EvidenceExecutionVerdict.Passed, manifest.ExecutionVerdict);
        Assert.Equal(EvidenceClaimKind.ObservationOnly, manifest.ClaimKind);
        Assert.Equal(EvidenceClaimEligibility.Informational, manifest.Eligibility);
        Assert.Empty(manifest.ProducerResults);
        Assert.True(EvidenceManifestBuilder.Verify(plan, manifest));

        var output = fixture.OutputDirectory;
        var manifestPath = Path.Combine(output, "manifest.json");
        Assert.True(File.Exists(manifestPath));
        var onDisk = EvidenceCanonicalJson.Deserialize<EvidenceManifest>(await File.ReadAllBytesAsync(manifestPath));
        Assert.True(EvidenceManifestBuilder.Verify(plan, onDisk));
        Assert.Equal(manifest.ManifestDigest, onDisk.ManifestDigest);
        Assert.Equal(["ready", "stop", "wait", "exit"], ReadOperations(fixture));
        AssertPeerMatchesTestHost(fixture);
    }

    [Fact]
    public async Task RunAsync_TrustedWithoutAcceptedProofRejectsBeforeConfigureOrAllocation()
    {
        var fixture = await RequireFixtureAsync("aspire-trusted");
        if (fixture is null) return;
        var plan = ResolvePlan(fixture);
        var configureCalls = 0;
        await using var host = EvidenceHostBootstrap.Create(plan, _ => configureCalls++);

        var exception = await Assert.ThrowsAsync<EvidenceAdmissionException>(() => host.RunAsync(
            new EvidenceExecutionRequest(EvidenceExecutionMode.Trusted, fixture.Socket)));

        Assert.Equal("ASEVD407", exception.Code);
        Assert.Equal(0, configureCalls);
        Assert.False(Directory.Exists(fixture.OutputDirectory));
        Assert.DoesNotContain("run", ReadOperations(fixture));
        Assert.Contains("stop", ReadOperations(fixture));
        Assert.Contains("wait", ReadOperations(fixture));
    }

    [Fact]
    public async Task RunAsync_RequestModeConflictRejectsBeforeConfigureOrAllocation()
    {
        var fixture = await RequireFixtureAsync("aspire-mode-conflict");
        if (fixture is null) return;
        var plan = ResolvePlan(fixture);
        var configureCalls = 0;
        await using var host = EvidenceHostBootstrap.Create(plan, _ => configureCalls++);

        var exception = await Assert.ThrowsAsync<EvidenceAdmissionException>(() => host.RunAsync(
            new EvidenceExecutionRequest(EvidenceExecutionMode.Trusted, fixture.Socket)));

        Assert.Equal("ASEVD401", exception.Code);
        Assert.Equal(0, configureCalls);
        Assert.False(Directory.Exists(fixture.OutputDirectory));
        Assert.DoesNotContain("run", ReadOperations(fixture));
        Assert.Contains("stop", ReadOperations(fixture));
        Assert.Contains("wait", ReadOperations(fixture));

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.RunAsync(
            new EvidenceExecutionRequest(EvidenceExecutionMode.Observation, fixture.Socket)));
        Assert.Equal(0, configureCalls);
        var operations = ReadOperations(fixture);
        Assert.Equal(["ready", "stop", "wait"], operations);
        Assert.Equal(1, operations.Count(static operation => operation == "ready"));
    }

    [Fact]
    public async Task RunAsync_ProtectedPolicyHashDriftRejectsBeforeConfigureOrAllocation()
    {
        var fixture = await RequireFixtureAsync("aspire-policy-drift");
        if (fixture is null) return;
        var plan = ResolvePlan(fixture);
        var configureCalls = 0;
        await using var host = EvidenceHostBootstrap.Create(plan, _ => configureCalls++);

        var exception = await Assert.ThrowsAsync<EvidenceAdmissionException>(() => host.RunAsync(
            new EvidenceExecutionRequest(EvidenceExecutionMode.Observation, fixture.Socket)));

        Assert.Equal("ASEVD403", exception.Code);
        Assert.Equal(0, configureCalls);
        Assert.False(Directory.Exists(fixture.OutputDirectory));
        Assert.DoesNotContain("run", ReadOperations(fixture));
        Assert.Contains("stop", ReadOperations(fixture));
        Assert.Contains("wait", ReadOperations(fixture));
    }

    private static async Task<Fixture?> RequireFixtureAsync(string scenario)
    {
        if (!OperatingSystem.IsLinux())
        {
            var unsupported = await Assert.ThrowsAsync<EvidenceAdmissionException>(() =>
                EvidenceLinuxWorkerSupervisor.ConnectAsync("/tmp/evidencehost-unsupported.sock", CancellationToken.None));
            Assert.Equal("ASEVD402", unsupported.Code);
            Assert.Contains("unavailable on this platform", unsupported.Message, StringComparison.Ordinal);
            return null;
        }

        var directory = Environment.GetEnvironmentVariable(BrokerEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(directory))
        {
            var missing = Path.Combine(Path.GetTempPath(), $"evidencehost-missing-{Guid.NewGuid():N}.sock");
            await Assert.ThrowsAnyAsync<SocketException>(() =>
                EvidenceLinuxWorkerSupervisor.ConnectAsync(missing, CancellationToken.None));
            throw new InvalidOperationException(
                "The missing-channel rejection is not a production-path pass; run selected scenarios under the root fixture.");
        }

        var metadataPath = Path.Combine(directory, scenario + ".json");
        using var metadata = JsonDocument.Parse(await File.ReadAllBytesAsync(metadataPath));
        var root = metadata.RootElement;
        return new Fixture(
            root.GetProperty("socket").GetString()!,
            root.GetProperty("policyFile").GetString()!,
            root.GetProperty("paths").EnumerateArray().Select(static path => path.GetString()!).ToArray(),
            root.GetProperty("outputParent").GetString()!,
            root.GetProperty("outputSlot").GetString()!,
            root.GetProperty("operationsFile").GetString()!,
            root.GetProperty("peerFile").GetString()!,
            root.GetProperty("workerUid").GetUInt32(),
            root.GetProperty("workerGid").GetUInt32());
    }

    private static EvidencePlan ResolvePlan(Fixture fixture)
    {
        var policy = EvidenceCanonicalJson.Deserialize<EvidencePolicy>(File.ReadAllBytes(fixture.PolicyFile));
        return new EvidencePlanner().Resolve(policy, fixture.Paths.Select(static path => new NormalizedDiffPath(path)).ToArray());
    }

    private static string[] ReadOperations(Fixture fixture) => File.ReadAllLines(fixture.OperationsFile)
        .Where(static line => !string.IsNullOrWhiteSpace(line))
        .Select(ReadOperation)
        .ToArray();

    private static string ReadOperation(string line)
    {
        using var document = JsonDocument.Parse(line);
        return document.RootElement.GetProperty("op").GetString()!;
    }

    private static void AssertPeerMatchesTestHost(Fixture fixture)
    {
        using var peer = JsonDocument.Parse(File.ReadAllBytes(fixture.PeerFile));
        Assert.Equal(Environment.ProcessId, peer.RootElement.GetProperty("pid").GetInt32());
        Assert.Equal(fixture.WorkerUid, peer.RootElement.GetProperty("uid").GetUInt32());
        Assert.Equal(fixture.WorkerGid, peer.RootElement.GetProperty("gid").GetUInt32());
    }

    private sealed record Fixture(string Socket, string PolicyFile, string[] Paths,
        string OutputParent, string OutputSlot, string OperationsFile, string PeerFile,
        uint WorkerUid, uint WorkerGid)
    {
        public string OutputDirectory => Path.Combine(OutputParent, OutputSlot);
    }
}
