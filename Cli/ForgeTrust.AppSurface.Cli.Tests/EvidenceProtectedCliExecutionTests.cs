using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Cli;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;
using Xunit.Abstractions;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class EvidenceProtectedCliExecutionTests(ITestOutputHelper output)
{
    private const string BrokerEnvironmentVariable = "EVIDENCEHOST_TEST_BROKER_SOCKET";

    [Fact]
    public async Task ConnectAsync_RejectsUnsupportedPlatformOrMissingFixtureChannel()
    {
        if (OperatingSystem.IsLinux())
        {
            var missing = Path.Combine(Path.GetTempPath(), $"evidencehost-missing-{Guid.NewGuid():N}.sock");
            await Assert.ThrowsAnyAsync<SocketException>(() =>
                EvidenceProtectedCliExecution.RunAsync(missing, CancellationToken.None));
            return;
        }

        var unsupported = await Assert.ThrowsAsync<EvidenceAdmissionException>(() =>
            EvidenceProtectedCliExecution.RunAsync("/tmp/evidencehost-unsupported.sock", CancellationToken.None));
        Assert.Equal("ASEVD402", unsupported.Code);
        Assert.Contains("unavailable on this platform", unsupported.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ExecutesAuthenticatedObservationCoverageAndRetainsDigestBoundOutput()
    {
        var fixture = await RequireFixtureAsync("cli-coverage");
        if (fixture is null) return;
        var manifest = await EvidenceProtectedCliExecution.RunAsync(fixture.Socket, CancellationToken.None);
        WriteFixtureManifestDiagnostics(manifest);
        var plan = ResolvePlan(fixture);

        Assert.Equal(EvidenceExecutionMode.Observation, manifest.Mode);
        Assert.Equal(EvidenceExecutionVerdict.Passed, manifest.ExecutionVerdict);
        Assert.Equal(EvidenceClaimKind.ObservationOnly, manifest.ClaimKind);
        Assert.Equal(EvidenceClaimEligibility.Informational, manifest.Eligibility);
        Assert.Equal(EvidenceProducerOutcome.Passed, Assert.Single(manifest.ProducerResults).Outcome);
        Assert.True(EvidenceManifestBuilder.Verify(plan, manifest));

        var outputDirectory = fixture.OutputDirectory;
        Assert.True(Directory.Exists(outputDirectory));
        var manifestPath = Path.Combine(outputDirectory, "evidence-manifest.json");
        var planPath = Path.Combine(outputDirectory, "evidence-plan.json");
        Assert.True(File.Exists(manifestPath));
        Assert.True(File.Exists(planPath));
        var diskManifest = EvidenceCanonicalJson.Deserialize<EvidenceManifest>(await File.ReadAllBytesAsync(manifestPath));
        Assert.True(EvidenceManifestBuilder.Verify(plan, diskManifest));
        Assert.Equal(manifest.ManifestDigest, diskManifest.ManifestDigest);
        var report = Assert.Single(manifest.ProducerResults).Artifacts!.Single(item => item.LogicalName == "coverage-report");
        var reportPath = Path.Combine(outputDirectory, report.RelativePath);
        Assert.Equal(report.LengthBytes, new FileInfo(reportPath).Length);
        await using var reportStream = File.OpenRead(reportPath);
        Assert.Equal(report.Sha256, Convert.ToHexString(await SHA256.HashDataAsync(reportStream)).ToLowerInvariant());

        var operations = ReadOperations(fixture);
        Assert.Contains("run", operations);
        Assert.Contains("artifacts", operations);
        Assert.Contains("stop", operations);
        Assert.Contains("wait", operations);
        Assert.Contains("exit", operations);
        AssertPeerMatchesTestHost(fixture);
    }

    [Fact]
    public async Task RunAsync_RejectsCallerModeConflictAndStopsAuthenticatedWorkerBeforeAllocationOrSubjectRun()
    {
        var fixture = await RequireFixtureAsync("cli-mode-conflict");
        if (fixture is null) return;
        var exception = await Assert.ThrowsAsync<EvidenceAdmissionException>(() =>
            EvidenceProtectedCliExecution.RunAsync(
                new EvidenceExecutionRequest(EvidenceExecutionMode.Trusted, fixture.Socket), CancellationToken.None));

        Assert.Equal("ASEVD401", exception.Code);
        Assert.Contains("The caller mode conflicts with the protected launcher.", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(fixture.OutputDirectory));
        Assert.Equal(["ready", "stop", "wait"], ReadOperations(fixture));
        AssertPeerMatchesTestHost(fixture);
    }

    [Fact]
    public async Task RunAsync_RejectsTrustedModeWithoutConsumerProofBeforeAllocationOrSubjectRun()
    {
        var fixture = await RequireFixtureAsync("cli-trusted");
        if (fixture is null) return;
        var exception = await Assert.ThrowsAsync<EvidenceAdmissionException>(() =>
            EvidenceProtectedCliExecution.RunAsync(
                new EvidenceExecutionRequest(EvidenceExecutionMode.Trusted, fixture.Socket), CancellationToken.None));

        Assert.Equal("ASEVD407", exception.Code);
        Assert.False(Directory.Exists(fixture.OutputDirectory));
        Assert.DoesNotContain("run", ReadOperations(fixture));
        Assert.DoesNotContain("artifacts", ReadOperations(fixture));
        Assert.Contains("stop", ReadOperations(fixture));
        Assert.Contains("wait", ReadOperations(fixture));
    }

    [Fact]
    public async Task RunAsync_RejectsProtectedPolicyDigestDriftBeforeAllocation()
    {
        var fixture = await RequireFixtureAsync("cli-policy-drift");
        if (fixture is null) return;
        var exception = await Assert.ThrowsAsync<EvidenceAdmissionException>(() =>
            EvidenceProtectedCliExecution.RunAsync(fixture.Socket, CancellationToken.None));

        Assert.Equal("ASEVD403", exception.Code);
        Assert.False(Directory.Exists(fixture.OutputDirectory));
        Assert.DoesNotContain("run", ReadOperations(fixture));
        Assert.Contains("stop", ReadOperations(fixture));
        Assert.Contains("wait", ReadOperations(fixture));
    }

    [Fact]
    public async Task RunAsync_SubjectFailureProducesIncompleteManifestWithoutEligibility()
    {
        var fixture = await RequireFixtureAsync("cli-subject-failure");
        if (fixture is null) return;
        var manifest = await EvidenceProtectedCliExecution.RunAsync(fixture.Socket, CancellationToken.None);
        WriteFixtureManifestDiagnostics(manifest);

        Assert.Equal(EvidenceExecutionVerdict.Incomplete, manifest.ExecutionVerdict);
        Assert.Equal(EvidenceClaimKind.None, manifest.ClaimKind);
        Assert.Equal(EvidenceClaimEligibility.None, manifest.Eligibility);
        Assert.Equal(EvidenceProducerOutcome.Failed, Assert.Single(manifest.ProducerResults).Outcome);
        Assert.Equal("StageFailed", manifest.Metrics.TerminalFailureCode);
        Assert.True(File.Exists(Path.Combine(fixture.OutputDirectory, "evidence-manifest.json")));
        Assert.DoesNotContain("artifacts", ReadOperations(fixture));
        Assert.Contains("stop", ReadOperations(fixture));
        Assert.Contains("wait", ReadOperations(fixture));
        Assert.Contains("exit", ReadOperations(fixture));
    }

    [Fact]
    public async Task RunAsync_OutputQuotaOverflowCannotBeUpgradedByAZeroExitCode()
    {
        var fixture = await RequireFixtureAsync("cli-output-overflow");
        if (fixture is null) return;
        var manifest = await EvidenceProtectedCliExecution.RunAsync(fixture.Socket, CancellationToken.None);
        WriteFixtureManifestDiagnostics(manifest);
        var plan = ResolvePlan(fixture);

        Assert.Equal(EvidenceExecutionVerdict.Incomplete, manifest.ExecutionVerdict);
        Assert.Equal(EvidenceClaimKind.None, manifest.ClaimKind);
        Assert.Equal(EvidenceClaimEligibility.None, manifest.Eligibility);
        // A latched worker failure discards the callback value; no producer result can establish a late pass.
        Assert.Empty(manifest.ProducerResults);
        Assert.Empty(manifest.ClosedObligationIds);
        Assert.Equal(plan.Profile.Obligations.Select(static obligation => obligation.Id).OrderBy(static id => id, StringComparer.Ordinal),
            manifest.UnmediatedObligationIds);
        Assert.Equal("StageFailed", manifest.Metrics.TerminalFailureCode);
        Assert.True(manifest.Metrics.CleanupCompleted);
        Assert.True(EvidenceManifestBuilder.Verify(plan, manifest));
        Assert.True(File.Exists(Path.Combine(fixture.OutputDirectory, "evidence-manifest.json")));
        var operations = ReadOperations(fixture);
        Assert.Contains("run", operations);
        Assert.DoesNotContain("artifacts", operations);
        Assert.Contains("stop", operations);
        Assert.Contains("wait", operations);
        Assert.Contains("exit", operations);
    }

    [Fact]
    public async Task RunAsync_RejectsMalformedAuthenticatedDescriptorBeforeAllocation()
    {
        var fixture = await RequireFixtureAsync("cli-malformed");
        if (fixture is null) return;
        var exception = await Assert.ThrowsAsync<EvidenceAdmissionException>(() =>
            EvidenceProtectedCliExecution.RunAsync(fixture.Socket, CancellationToken.None));

        Assert.Equal("ASEVD402", exception.Code);
        Assert.False(Directory.Exists(fixture.OutputDirectory));
        Assert.Equal(["ready"], ReadOperations(fixture));
    }

    [Fact]
    public async Task RunAsync_HonorsCallerCancellationBeforeConnecting()
    {
        var fixture = await RequireFixtureAsync("cli-cancel");
        if (fixture is null) return;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            EvidenceProtectedCliExecution.RunAsync(fixture.Socket, cancellation.Token));

        Assert.Empty(ReadOperations(fixture));
        Assert.False(Directory.Exists(fixture.OutputDirectory));
    }

    private static async Task<Fixture?> RequireFixtureAsync(string scenario)
    {
        if (!OperatingSystem.IsLinux())
        {
            var unsupported = await Assert.ThrowsAsync<EvidenceAdmissionException>(() =>
                EvidenceProtectedCliExecution.RunAsync("/tmp/evidencehost-unsupported.sock", CancellationToken.None));
            Assert.Equal("ASEVD402", unsupported.Code);
            Assert.Contains("unavailable on this platform", unsupported.Message, StringComparison.Ordinal);
            return null;
        }

        var directory = Environment.GetEnvironmentVariable(BrokerEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(directory))
        {
            var missing = Path.Combine(Path.GetTempPath(), $"evidencehost-missing-{Guid.NewGuid():N}.sock");
            await Assert.ThrowsAnyAsync<SocketException>(() =>
                EvidenceProtectedCliExecution.RunAsync(missing, CancellationToken.None));
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

    private void WriteFixtureManifestDiagnostics(EvidenceManifest manifest)
    {
        // Only these synthetic root-broker scenarios emit this bounded context, which xUnit retains on assertion failure.
        output.WriteLine($"Fixture manifest: mode={manifest.Mode}; verdict={manifest.ExecutionVerdict}; claim={manifest.ClaimKind}; "
            + $"eligibility={manifest.Eligibility}; terminal={BoundedFixtureText(manifest.Metrics.TerminalFailureCode, 128)}; "
            + $"cleanup={manifest.Metrics.CleanupCompleted}; producers={manifest.ProducerResults.Count}; "
            + $"closed={manifest.ClosedObligationIds.Count}; unmediated={manifest.UnmediatedObligationIds.Count}.");
        foreach (var result in manifest.ProducerResults.Take(4))
        {
            output.WriteLine($"Fixture producer: id={BoundedFixtureText(result.ProducerId, 128)}; outcome={result.Outcome}; "
                + $"assertions={result.SatisfiedAssertionIds.Count}; artifacts={result.Artifacts?.Count ?? 0}; "
                + $"diagnostic={BoundedFixtureText(result.Diagnostic, 512)}.");
        }
    }

    private static string BoundedFixtureText(string? value, int maximumCharacters)
    {
        if (string.IsNullOrEmpty(value)) return "<none>";
        var bounded = value.Length <= maximumCharacters ? value : value[..maximumCharacters] + "...";
        return bounded.Replace('\r', ' ').Replace('\n', ' ');
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
