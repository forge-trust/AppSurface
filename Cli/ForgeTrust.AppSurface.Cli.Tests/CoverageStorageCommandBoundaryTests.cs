using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Exercises storage command results and failures at the CliFx command boundary.</summary>
[Collection("CoverageGate process state")]
public sealed class CoverageStorageCommandTestsBoundary
{
    private const string ArchivedRunId = "11111111111141118111111111111111";
    private const string CurrentRunId = "22222222222242228222222222222222";

    [Fact]
    public async Task Inspect_pre_cancelled_returns_cancelled_json_and_exit_code_130()
    {
        using var domain = TestDirectory.Create();
        using var configured = ConfigureDomain(domain.Path);
        using var console = new FakeInMemoryConsole();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var error = await Assert.ThrowsAsync<CliFx.CommandException>(async () =>
            await new CoverageStorageInspectCommand { Root = domain.Path, Json = true }.ExecuteAsync(console, cancellation.Token));

        Assert.Equal(130, error.ExitCode);
        var output = console.ReadOutputString();
        using var document = JsonDocument.Parse(output);
        var result = document.RootElement;
        Assert.Equal("inspect", result.GetProperty("operation").GetString());
        Assert.Equal("cancelled", result.GetProperty("status").GetString());
        Assert.Equal("ASCOV203", result.GetProperty("diagnostics")[0].GetProperty("code").GetString());
        Assert.Equal("none", result.GetProperty("sideEffects").GetString());
        Assert.Equal(1, result.GetProperty("maxRetries").GetInt32());
        Assert.DoesNotContain(domain.Path, output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(StorePath(domain.Path)));
    }

    [Fact]
    public async Task Maintain_apply_reports_deleted_run_and_complete_side_effects()
    {
        using var domain = TestDirectory.Create();
        var storePath = StorePath(domain.Path);
        SetHistoryLimit(storePath, 0);
        await SeedArchivedRunAsync(domain.Path, ArchivedRunId);
        await SeedCurrentRunAsync(domain.Path, CurrentRunId, Path.Join(domain.Path, "output-" + ArchivedRunId));
        using var configured = ConfigureDomain(domain.Path);
        using var console = new FakeInMemoryConsole();

        await new CoverageStorageMaintainCommand { Apply = true, Json = true }.ExecuteAsync(console, CancellationToken.None);

        var output = console.ReadOutputString();
        using var document = JsonDocument.Parse(output);
        var result = document.RootElement;
        Assert.Equal("maintain", result.GetProperty("operation").GetString());
        Assert.Equal("complete", result.GetProperty("status").GetString());
        Assert.Equal("complete", result.GetProperty("sideEffects").GetString());
        Assert.True(result.GetProperty("data").GetProperty("apply").GetBoolean());
        Assert.Equal(new[] { ArchivedRunId }, result.GetProperty("data").GetProperty("deletedRunIds")
            .EnumerateArray().Select(item => item.GetString()));
        Assert.False(Directory.Exists(Path.Join(storePath, "archives", ArchivedRunId)));
        Assert.DoesNotContain(storePath, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pin_and_unpin_commands_report_state_and_persist_the_round_trip()
    {
        using var domain = TestDirectory.Create();
        var seeded = await SeedArchivedRunAsync(domain.Path, ArchivedRunId);
        using var configured = ConfigureDomain(domain.Path);
        using var pinConsole = new FakeInMemoryConsole();

        await new CoverageStoragePinCommand { Run = ArchivedRunId, Json = true }
            .ExecuteAsync(pinConsole, CancellationToken.None);

        var pinOutput = pinConsole.ReadOutputString();
        using (var document = JsonDocument.Parse(pinOutput))
        {
            var result = document.RootElement;
            Assert.Equal("pin", result.GetProperty("operation").GetString());
            Assert.Equal("complete", result.GetProperty("status").GetString());
            Assert.Equal("complete", result.GetProperty("sideEffects").GetString());
            Assert.Equal(ArchivedRunId, result.GetProperty("data").GetProperty("runId").GetString());
            Assert.True(result.GetProperty("data").GetProperty("pinned").GetBoolean());
        }

        using (var store = CoverageArtifactStore.OpenReadOnly(seeded.StorePath))
            Assert.True(store.GetStatus().PinnedBytes > 0);

        using var unpinConsole = new FakeInMemoryConsole();
        await new CoverageStorageUnpinCommand { Run = ArchivedRunId }
            .ExecuteAsync(unpinConsole, CancellationToken.None);

        var unpinOutput = unpinConsole.ReadOutputString();
        Assert.Contains("coverage storage unpin: complete", unpinOutput, StringComparison.Ordinal);
        Assert.Contains("operation completed for the requested opaque run identifier", unpinOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(domain.Path, unpinOutput, StringComparison.Ordinal);
        using (var store = CoverageArtifactStore.OpenReadOnly(seeded.StorePath))
            Assert.Equal(0, store.GetStatus().PinnedBytes);
    }

    [Theory]
    [InlineData("pin")]
    [InlineData("unpin")]
    [InlineData("restore")]
    public async Task Invalid_command_inputs_return_redacted_ascov208(string operation)
    {
        using var domain = TestDirectory.Create();
        using var configured = ConfigureDomain(domain.Path);
        using var console = new FakeInMemoryConsole();
        var command = CreateInvalidCommand(operation, domain.Path);

        var error = await Assert.ThrowsAsync<CliFx.CommandException>(async () =>
            await command.ExecuteAsync(console));

        Assert.Equal(2, error.ExitCode);
        var output = console.ReadOutputString();
        using var document = JsonDocument.Parse(output);
        var result = document.RootElement;
        Assert.Equal("failed", result.GetProperty("status").GetString());
        Assert.Equal("ASCOV208", result.GetProperty("diagnostics")[0].GetProperty("code").GetString());
        Assert.Equal("none", result.GetProperty("sideEffects").GetString());
        Assert.Equal("Supply --run as 32 hexadecimal characters without hyphens, then retry once.", result.GetProperty("recoveryAction").GetString());
        Assert.DoesNotContain("secret-invalid-run", output, StringComparison.Ordinal);
        Assert.DoesNotContain(domain.Path, output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(StorePath(domain.Path)));
    }

    [Theory]
    [InlineData("pin", "ASCOV206")]
    [InlineData("unpin", "ASCOV206")]
    [InlineData("restore", "ASCOV205")]
    public async Task Well_formed_but_unknown_run_returns_operation_diagnostic(string operation, string code)
    {
        using var domain = TestDirectory.Create();
        using var configured = ConfigureDomain(domain.Path);
        using var console = new FakeInMemoryConsole();
        var missingRunId = "33333333333343338333333333333333";
        var command = CreateCommand(operation, missingRunId, Path.Join(domain.Path, "restore-parent", "restored"));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Join(domain.Path, "restore-parent", "restored"))!);

        var error = await Assert.ThrowsAsync<CliFx.CommandException>(async () =>
            await command.ExecuteAsync(console));

        Assert.Equal(1, error.ExitCode);
        var output = console.ReadOutputString();
        using var document = JsonDocument.Parse(output);
        var result = document.RootElement;
        Assert.Equal("failed", result.GetProperty("status").GetString());
        Assert.Equal(code, result.GetProperty("diagnostics")[0].GetProperty("code").GetString());
        Assert.Equal("unknown", result.GetProperty("sideEffects").GetString());
        Assert.DoesNotContain(domain.Path, output, StringComparison.Ordinal);
        Assert.DoesNotContain(missingRunId, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pin_rejects_malformed_archive_json_with_a_safe_diagnostic()
    {
        using var domain = TestDirectory.Create();
        var seeded = await SeedArchivedRunAsync(domain.Path, ArchivedRunId);
        File.WriteAllText(Path.Join(seeded.ArchivePath, "manifest.json"), "{ malformed archive secret }");
        using var configured = ConfigureDomain(domain.Path);
        using var console = new FakeInMemoryConsole();

        var error = await Assert.ThrowsAsync<CliFx.CommandException>(async () =>
            await new CoverageStoragePinCommand { Run = ArchivedRunId, Json = true }
                .ExecuteAsync(console, CancellationToken.None));

        Assert.Equal(1, error.ExitCode);
        var output = console.ReadOutputString();
        using var document = JsonDocument.Parse(output);
        var result = document.RootElement;
        Assert.Equal("failed", result.GetProperty("status").GetString());
        Assert.Equal("ASCOV206", result.GetProperty("diagnostics")[0].GetProperty("code").GetString());
        Assert.Contains("invalid or uses an unsupported schema", result.GetProperty("diagnostics")[0].GetProperty("cause").GetString(), StringComparison.Ordinal);
        Assert.Equal("unknown", result.GetProperty("sideEffects").GetString());
        Assert.DoesNotContain("malformed archive secret", output, StringComparison.Ordinal);
        Assert.DoesNotContain(domain.Path, output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pin")]
    [InlineData("unpin")]
    public async Task Pin_and_unpin_contention_returns_ascov202_without_leaking_paths(string operation)
    {
        using var domain = TestDirectory.Create();
        var seeded = await SeedArchivedRunAsync(domain.Path, ArchivedRunId);
        using var heldLease = CoverageArtifactOperationLease.Acquire(seeded.ArchivePath, exclusive: true, windowsLockRoot: seeded.StorePath);
        using var configured = ConfigureDomain(domain.Path);
        using var console = new FakeInMemoryConsole();

        var error = await Assert.ThrowsAsync<CliFx.CommandException>(async () =>
            await CreateCommand(operation, ArchivedRunId, string.Empty).ExecuteAsync(console));

        Assert.Equal(1, error.ExitCode);
        var output = console.ReadOutputString();
        using var document = JsonDocument.Parse(output);
        var result = document.RootElement;
        Assert.Equal("failed", result.GetProperty("status").GetString());
        Assert.Equal("ASCOV202", result.GetProperty("diagnostics")[0].GetProperty("code").GetString());
        Assert.Equal("none", result.GetProperty("sideEffects").GetString());
        Assert.Contains("retry once", result.GetProperty("diagnostics")[0].GetProperty("recovery").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain(domain.Path, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restore_contention_returns_ascov202_and_no_restore_output()
    {
        using var domain = TestDirectory.Create();
        var seeded = await SeedArchivedRunAsync(domain.Path, ArchivedRunId);
        var parent = Path.Join(domain.Path, "restore-parent");
        Directory.CreateDirectory(parent);
        var destination = Path.Join(parent, "restored");
        using var heldLease = CoverageArtifactOperationLease.Acquire(seeded.ArchivePath, exclusive: true, windowsLockRoot: seeded.StorePath);
        using var configured = ConfigureDomain(domain.Path);
        using var console = new FakeInMemoryConsole();

        var error = await Assert.ThrowsAsync<CliFx.CommandException>(async () =>
            await new CoverageStorageRestoreCommand { Run = ArchivedRunId, Output = destination, Json = true }
                .ExecuteAsync(console, CancellationToken.None));

        Assert.Equal(1, error.ExitCode);
        var output = console.ReadOutputString();
        using var document = JsonDocument.Parse(output);
        Assert.Equal("ASCOV202", document.RootElement.GetProperty("diagnostics")[0].GetProperty("code").GetString());
        Assert.Equal("none", document.RootElement.GetProperty("sideEffects").GetString());
        Assert.DoesNotContain(domain.Path, output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task Restore_existing_destination_returns_ascov205_without_clobbering_it()
    {
        using var domain = TestDirectory.Create();
        var seeded = await SeedArchivedRunAsync(domain.Path, ArchivedRunId);
        var parent = Path.Join(domain.Path, "restore-parent");
        Directory.CreateDirectory(parent);
        var destination = Path.Join(parent, "existing");
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Join(destination, "sentinel.txt"), "preserve existing output");
        using var configured = ConfigureDomain(domain.Path);
        using var console = new FakeInMemoryConsole();

        var error = await Assert.ThrowsAsync<CliFx.CommandException>(async () =>
            await new CoverageStorageRestoreCommand { Run = ArchivedRunId, Output = destination, Json = true }
                .ExecuteAsync(console, CancellationToken.None));

        Assert.Equal(1, error.ExitCode);
        var output = console.ReadOutputString();
        using var document = JsonDocument.Parse(output);
        var result = document.RootElement;
        Assert.Equal("ASCOV205", result.GetProperty("diagnostics")[0].GetProperty("code").GetString());
        Assert.Equal("unknown", result.GetProperty("sideEffects").GetString());
        Assert.DoesNotContain(domain.Path, output, StringComparison.Ordinal);
        Assert.Equal("preserve existing output", File.ReadAllText(Path.Join(destination, "sentinel.txt")));
        Assert.True(Directory.Exists(seeded.ArchivePath));
    }

    [Fact]
    public async Task Restore_json_includes_verified_provenance_in_the_history_receipt()
    {
        using var domain = TestDirectory.Create();
        var provenance = new CoverageArtifactProvenance("revision-fingerprint", "dirty-fingerprint", "configuration-fingerprint");
        var seeded = await SeedArchivedRunAsync(domain.Path, ArchivedRunId, provenance);
        var parent = Path.Join(domain.Path, "restore-parent");
        Directory.CreateDirectory(parent);
        var destination = Path.Join(parent, "restored");
        using var configured = ConfigureDomain(domain.Path);
        using var console = new FakeInMemoryConsole();

        await new CoverageStorageRestoreCommand { Run = ArchivedRunId, Output = destination, Json = true }
            .ExecuteAsync(console, CancellationToken.None);

        var output = console.ReadOutputString();
        using (var document = JsonDocument.Parse(output))
        {
            var result = document.RootElement;
            Assert.Equal("restore", result.GetProperty("operation").GetString());
            Assert.Equal("complete", result.GetProperty("status").GetString());
            Assert.Equal("complete", result.GetProperty("sideEffects").GetString());
            Assert.Equal(ArchivedRunId, result.GetProperty("data").GetProperty("runId").GetString());
            Assert.True(result.GetProperty("data").GetProperty("restored").GetBoolean());
            Assert.Empty(result.GetProperty("diagnostics").EnumerateArray());
        }
        Assert.DoesNotContain(domain.Path, output, StringComparison.Ordinal);
        Assert.Equal("verified coverage payload", File.ReadAllText(Path.Join(destination, "coverage.cobertura.xml")));

        var receiptPath = Path.Join(destination, "coverage-history.json");
        var receiptText = File.ReadAllText(receiptPath);
        using var receipt = JsonDocument.Parse(receiptText);
        var receiptRoot = receipt.RootElement;
        Assert.Equal(1, receiptRoot.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(ArchivedRunId, receiptRoot.GetProperty("runId").GetString());
        Assert.True(receiptRoot.GetProperty("historical").GetBoolean());
        var receiptProvenance = receiptRoot.GetProperty(nameof(CoverageArtifactRunManifest.Provenance));
        Assert.Equal(provenance.GitRevisionFingerprint,
            receiptProvenance.GetProperty(nameof(CoverageArtifactProvenance.GitRevisionFingerprint)).GetString());
        Assert.Equal(provenance.GitStateFingerprint,
            receiptProvenance.GetProperty(nameof(CoverageArtifactProvenance.GitStateFingerprint)).GetString());
        Assert.Equal(provenance.ConfigurationFingerprint,
            receiptProvenance.GetProperty(nameof(CoverageArtifactProvenance.ConfigurationFingerprint)).GetString());
        Assert.DoesNotContain(domain.Path, receiptText, StringComparison.Ordinal);
        Assert.True(Directory.Exists(seeded.ArchivePath));
    }

    [Theory]
    [MemberData(nameof(EnvelopeFailureCases))]
    public async Task Command_boundary_maps_unknown_json_and_unsupported_failures(
        Exception failure, string expectedStatus, int expectedExitCode, string expectedCode, string expectedEffects)
    {
        using var domain = TestDirectory.Create();
        using var configured = ConfigureDomain(domain.Path);
        using var console = new FakeInMemoryConsole();

        var error = await Assert.ThrowsAsync<CliFx.CommandException>(async () =>
            await new BoundaryProbeCommand(failure) { Json = true }.ExecuteAsync(console, CancellationToken.None));

        Assert.Equal(expectedExitCode, error.ExitCode);
        var output = console.ReadOutputString();
        using var document = JsonDocument.Parse(output);
        var result = document.RootElement;
        Assert.Equal(expectedStatus, result.GetProperty("status").GetString());
        Assert.Equal(expectedCode, result.GetProperty("diagnostics")[0].GetProperty("code").GetString());
        Assert.Equal(expectedEffects, result.GetProperty("sideEffects").GetString());
        Assert.Equal(expectedEffects, result.GetProperty("diagnostics")[0].GetProperty("sideEffects").GetString());
        Assert.Equal(1, result.GetProperty("data").GetProperty("completedCount").GetInt32());
        Assert.DoesNotContain("secret exception detail", output, StringComparison.Ordinal);
        Assert.DoesNotContain(domain.Path, output, StringComparison.Ordinal);
        if (expectedEffects == "partial")
            Assert.NotNull(result.GetProperty("reconcileAction").GetString());
    }

    public static TheoryData<Exception, string, int, string, string> EnvelopeFailureCases => new()
    {
        { new JsonException("secret exception detail"), "failed", 1, "ASCOV205", "partial" },
        { new NotSupportedException("secret exception detail"), "unsupported", 1, "ASCOV205", "partial" },
        { new IOException("secret exception detail"), "failed", 1, "ASCOV205", "partial" },
        { new ApplicationException("secret exception detail"), "failed", 1, "ASCOV205", "partial" },
        { new CoverageExecutionException("ASCOV208 secret exception detail", 2), "failed", 2, "ASCOV208", "partial" },
        { new OperationCanceledException("secret exception detail"), "cancelled", 130, "ASCOV205", "partial" }
    };

    private static CoverageStorageCommandBase CreateInvalidCommand(string operation, string domainPath) => operation switch
    {
        "pin" => new CoverageStoragePinCommand { Run = "secret-invalid-run", Json = true },
        "unpin" => new CoverageStorageUnpinCommand { Run = "secret-invalid-run", Json = true },
        "restore" => new CoverageStorageRestoreCommand { Run = "secret-invalid-run", Output = Path.Join(domainPath, "secret-output"), Json = true },
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    private static CoverageStorageCommandBase CreateCommand(string operation, string runId, string output) => operation switch
    {
        "pin" => new CoverageStoragePinCommand { Run = runId, Json = true },
        "unpin" => new CoverageStorageUnpinCommand { Run = runId, Json = true },
        "restore" => new CoverageStorageRestoreCommand { Run = runId, Output = output, Json = true },
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    private static async Task<SeededRun> SeedArchivedRunAsync(
        string domainPath, string runId, CoverageArtifactProvenance? provenance = null)
    {
        var storePath = StorePath(domainPath);
        var run = CreateCurrentRun(domainPath, runId, provenance);
        using var store = CoverageArtifactStore.Open(storePath);
        await store.RegisterAsync(run, CancellationToken.None);
        var manifest = await store.SnapshotAsync(run, pin: false, automatic: false, CancellationToken.None);
        Assert.True(manifest.Complete);
        return new SeededRun(storePath, Path.Join(storePath, "archives", runId), runId, run.OutputPath);
    }

    private static async Task SeedCurrentRunAsync(string domainPath, string runId, string outputPath)
    {
        using var store = CoverageArtifactStore.Open(StorePath(domainPath));
        await store.RegisterAsync(CreateCurrentRun(domainPath, runId, outputPath: outputPath), CancellationToken.None);
    }

    private static CoverageArtifactCurrentRun CreateCurrentRun(
        string domainPath, string runId, CoverageArtifactProvenance? provenance = null, string? outputPath = null)
    {
        var output = outputPath ?? Path.Join(domainPath, "output-" + runId);
        var repository = Path.Join(domainPath, "repository-" + runId);
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(repository);
        var bytes = Encoding.UTF8.GetBytes("verified coverage payload");
        File.WriteAllBytes(Path.Join(output, "coverage.cobertura.xml"), bytes);
        var entry = new CoverageArtifactInventoryEntry(
            "coverage.cobertura.xml", bytes.LongLength,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        var manifest = new CoverageArtifactRunManifest
        {
            RunId = runId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Outcome = "success",
            Version = "command-boundary-test",
            Provenance = provenance
        };
        return new CoverageArtifactCurrentRun(manifest, [entry], output, repository);
    }

    private static void SetHistoryLimit(string storePath, long maximumBytes)
    {
        using (CoverageArtifactStore.Open(storePath)) { }
        using var root = CoverageRunOutputLease.AcquireArtifactDirectory(storePath, createMissing: false, requirePrivate: true)!;
        using var policy = root.OpenArtifactFile("policy.json", createNew: true);
        var bytes = Encoding.UTF8.GetBytes($"{{\"schemaVersion\":1,\"historyMaxBytes\":{maximumBytes}}}");
        policy.Write(bytes);
        policy.Flush(true);
    }

    private static string StorePath(string domainPath) => Path.Join(domainPath, "private-store");

    private static IDisposable ConfigureDomain(string path)
    {
        var previous = Environment.GetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME");
        Environment.SetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME", StorePath(path));
        return new RestoreEnvironment(() => Environment.SetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME", previous));
    }

    private sealed record SeededRun(string StorePath, string ArchivePath, string RunId, string OutputPath);

    private sealed class RestoreEnvironment(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    private sealed class BoundaryProbeCommand(Exception failure) : CoverageStorageCommandBase("restore")
    {
        protected override ValueTask<object?> ExecuteOperationAsync(
            CoverageArtifactStore store, CancellationToken token, Action<string> setEffects, Action<string> setStatus)
        {
            setEffects("partial");
            PublishPartialDetails(new { completedCount = 1 });
            throw failure;
        }
    }
}
