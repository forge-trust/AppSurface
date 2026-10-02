using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.ComponentModel;
using System.Text;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Evidence.Cli;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;
using ForgeTrust.AppSurface.Testing;
using Xunit.Abstractions;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class EvidenceProtectedCliExecutionTests(ITestOutputHelper output)
{
    private const string BrokerEnvironmentVariable = "EVIDENCEHOST_TEST_BROKER_SOCKET";

    [Theory]
    [InlineData(-1, null)]
    [InlineData(0, null)]
    [InlineData(1, 1)]
    [InlineData(17, 17)]
    [InlineData(4095, 4095)]
    [InlineData(4096, null)]
    public void AllocationDiagnostic_bounds_errno_and_omits_exception_canaries(int errno, int? expected)
    {
        const string canary = "secret-subject-path-token";
        var error = new IOException(canary, new Win32Exception(errno, canary));
        error.Data[canary] = canary;
        var diagnostic = EvidenceProtectedCliExecution.CreateAllocationFailureDiagnostic(EvidenceAllocationPhase.Allocation,
            EvidenceLinuxArtifactAllocationOperation.CreateSlot, EvidenceWorkerStageOutcome.Failed,
            EvidenceWorkerTerminalCode.StageFailed, error);
        Assert.Equal(expected, diagnostic.NativeErrno);
        Assert.Equal(EvidenceAllocationErrorClass.Io, diagnostic.ErrorClass);
        using var console = new FakeInMemoryConsole();
        EvidenceWorkerCommand.WriteAllocationDiagnostic(console, diagnostic);
        var line = console.ReadErrorString();
        Assert.True(Encoding.UTF8.GetByteCount(line) <= 1024);
        Assert.DoesNotContain(canary, line, StringComparison.Ordinal);
        Assert.Equal(string.Empty, console.ReadOutputString());
        using var json = JsonDocument.Parse(line);
        Assert.Equal("evidence-allocation-failure-v1", json.RootElement.GetProperty("schema").GetString());
        Assert.Equal("CreateSlot", json.RootElement.GetProperty("operation").GetString());
        Assert.Equal("Io", json.RootElement.GetProperty("errorClass").GetString());
        Assert.Equal(7, json.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public void AllocationDiagnostic_classifies_only_closed_error_values_and_native_allocation_errno()
    {
        var cases = new (Exception? Error, EvidenceAllocationErrorClass Class)[]
        {
            (null, EvidenceAllocationErrorClass.None),
            (new Exception("canary"), EvidenceAllocationErrorClass.Unknown),
            (new OutOfMemoryException("canary"), EvidenceAllocationErrorClass.OutOfMemory),
            (new UnauthorizedAccessException("canary"), EvidenceAllocationErrorClass.AccessDenied),
            (new PlatformNotSupportedException("canary"), EvidenceAllocationErrorClass.Unsupported),
            (new ArgumentException("canary"), EvidenceAllocationErrorClass.Argument),
            (new OperationCanceledException("canary"), EvidenceAllocationErrorClass.Cancelled),
            (new TimeoutException("canary"), EvidenceAllocationErrorClass.Timeout),
            (new EvidenceAdmissionException("ASEVD409", "canary"), EvidenceAllocationErrorClass.Admission),
            (new InvalidOperationException("canary"), EvidenceAllocationErrorClass.InvalidOperation),
        };
        foreach (var (error, expected) in cases)
        {
            var diagnostic = EvidenceProtectedCliExecution.CreateAllocationFailureDiagnostic(EvidenceAllocationPhase.Activation,
                EvidenceLinuxArtifactAllocationOperation.Completed, EvidenceWorkerStageOutcome.Failed,
                EvidenceWorkerTerminalCode.StageFailed, error);
            Assert.Equal(expected, diagnostic.ErrorClass);
            Assert.Null(diagnostic.NativeErrno);
        }
        var native = new IOException("canary", new Win32Exception(13, "canary"));
        foreach (var phase in new[] { EvidenceAllocationPhase.BeforeAllocation, EvidenceAllocationPhase.BeforeActivation, EvidenceAllocationPhase.Activation })
            Assert.Null(EvidenceProtectedCliExecution.CreateAllocationFailureDiagnostic(phase,
                EvidenceLinuxArtifactAllocationOperation.CreateSlot, EvidenceWorkerStageOutcome.Failed,
                EvidenceWorkerTerminalCode.StageFailed, native).NativeErrno);
        var unknown = EvidenceProtectedCliExecution.CreateAllocationFailureDiagnostic((EvidenceAllocationPhase)999,
            (EvidenceLinuxArtifactAllocationOperation)999, (EvidenceWorkerStageOutcome)999, (EvidenceWorkerTerminalCode)999, native);
        Assert.Equal(EvidenceAllocationPhase.None, unknown.Phase);
        Assert.Equal(EvidenceLinuxArtifactAllocationOperation.None, unknown.Operation);
        Assert.Equal(EvidenceWorkerStageOutcome.Failed, unknown.StageOutcome);
        Assert.Equal(EvidenceWorkerTerminalCode.StageFailed, unknown.TerminalCode);
        Assert.Null(unknown.NativeErrno);
    }

    [Fact]
    public async Task AllocationDiagnostic_sink_runs_only_after_join_and_cannot_replace_original_failure()
    {
        var supervisor = new DiagnosticSupervisor();
        var execution = new EvidenceWorkerExecution(supervisor, TimeProvider.System, TimeSpan.FromMinutes(1),
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2));
        var calls = 0;
        Action<EvidenceAllocationFailureDiagnostic> sink = _ => calls++;
        EvidenceProtectedCliExecution.ReportAllocationFailure(execution, EvidenceWorkerStageOutcome.Failed,
            EvidenceAllocationPhase.Allocation, EvidenceLinuxArtifactAllocationOperation.CreateSlot, sink);
        Assert.Equal(0, calls);
        var original = new IOException("secret-canary", new Win32Exception(17));
        var stage = await execution.ExecuteAsync<int>(EvidenceRunStage.Admission, TimeSpan.FromSeconds(1), _ => throw original);
        Assert.Equal(EvidenceWorkerStageOutcome.Failed, stage.Outcome);
        Assert.True(supervisor.Joined);
        Assert.Same(original, execution.TerminalException);
        EvidenceProtectedCliExecution.ReportAllocationFailure(execution, EvidenceWorkerStageOutcome.Passed,
            EvidenceAllocationPhase.Completed, EvidenceLinuxArtifactAllocationOperation.Completed, sink);
        Assert.Equal(0, calls);
        EvidenceProtectedCliExecution.ReportAllocationFailure(execution, stage.Outcome, EvidenceAllocationPhase.Allocation,
            EvidenceLinuxArtifactAllocationOperation.CreateSlot, diagnostic =>
            {
                calls++;
                Assert.True(supervisor.Joined);
                Assert.Equal(17, diagnostic.NativeErrno);
                throw new IOException("sink-canary");
            });
        Assert.Equal(1, calls);
        Assert.Same(original, execution.TerminalException);
        Assert.Equal(EvidenceWorkerTerminalCode.StageFailed, execution.TerminalCode);
    }

    private sealed class DiagnosticSupervisor : IEvidenceExecutionSupervisor
    {
        public bool IsArmed => true;
        public string RunId => "diagnostic-control";
        internal bool Joined { get; private set; }
        public void CloseAdmission() { }
        public ValueTask RequestStopAsync(CancellationToken stoppingToken) => ValueTask.CompletedTask;
        public ValueTask WaitForOwnedExitAsync(CancellationToken stoppingToken)
        {
            Joined = true;
            return ValueTask.CompletedTask;
        }
    }

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
        var producerResult = Assert.Single(manifest.ProducerResults);
        var report = producerResult.Artifacts!.Single(item => item.LogicalName == "coverage-report");
        var reportPath = TestPathUtils.PathUnder(outputDirectory, producerResult.ProducerId, report.RelativePath);
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
