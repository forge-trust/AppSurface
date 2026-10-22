using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Verifies the storage command envelope, noninteractive behavior, and safe failure rendering.</summary>
[Collection("CoverageGate process state")]
public sealed class CoverageStorageCommandTests
{
    [Fact]
    public async Task Status_json_uses_an_isolated_domain_and_never_emits_its_path()
    {
        using var root = TestDirectory.Create();
        using var domain = TestDirectory.Create();
        using var configured = ConfigureDomain(domain.Path);
        using var console = new FakeInMemoryConsole();

        // Exercise the public CliFx entry point and its cancellation-token adapter.
        await new CoverageStorageStatusCommand { Json = true }.ExecuteAsync(console);

        var output = console.ReadOutputString();
        using var document = System.Text.Json.JsonDocument.Parse(output);
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("status", document.RootElement.GetProperty("operation").GetString());
        Assert.Equal("complete", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("none", document.RootElement.GetProperty("sideEffects").GetString());
        Assert.DoesNotContain(domain.Path, output, StringComparison.Ordinal);
        Assert.DoesNotContain(root.Path, output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Join(domain.Path, "private-store")));
        Assert.Equal(0, document.RootElement.GetProperty("data").GetProperty("overflowObservationCount").GetInt32());
        Assert.Equal(0, document.RootElement.GetProperty("data").GetProperty("metadataBytes").GetInt64());
        var data = document.RootElement.GetProperty("data");
        AssertPolicy(data.GetProperty("policy"), "policy.json", 2L * 1024 * 1024 * 1024, 7, 1L * 1024 * 1024 * 1024, 2L * 1024 * 1024 * 1024, 24);
        AssertManagementNotProbed(data.GetProperty("managementCapability"));
        AssertFixedResourceLimits(data.GetProperty("limits").GetProperty("fixedResources"));
    }

    [Fact]
    public async Task Maintain_defaults_to_preview_and_reports_run_ids_without_deleting_archives()
    {
        using var domain = TestDirectory.Create();
        using var configured = ConfigureDomain(domain.Path);
        using var console = new FakeInMemoryConsole();

        await new CoverageStorageMaintainCommand { Json = true }.ExecuteAsync(console, CancellationToken.None);

        using var document = System.Text.Json.JsonDocument.Parse(console.ReadOutputString());
        var data = document.RootElement.GetProperty("data");
        Assert.False(data.GetProperty("apply").GetBoolean());
        Assert.Empty(data.GetProperty("deletedRunIds").EnumerateArray());
        Assert.Equal("none", document.RootElement.GetProperty("sideEffects").GetString());
        Assert.False(Directory.Exists(Path.Join(domain.Path, "private-store")));
    }

    [Fact]
    public async Task Inspect_without_a_tty_returns_a_bounded_read_only_observation()
    {
        using var root = TestDirectory.Create();
        using var domain = TestDirectory.Create();
        using var configured = ConfigureDomain(domain.Path);
        root.WriteFile("TestResults/coverage.cobertura.xml", "<coverage />");
        using var console = new FakeInMemoryConsole();

        await new CoverageStorageInspectCommand { Root = root.Path, Json = true }.ExecuteAsync(console, CancellationToken.None);

        using var document = System.Text.Json.JsonDocument.Parse(console.ReadOutputString());
        Assert.Equal("inspect", document.RootElement.GetProperty("operation").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("data").GetProperty("files").GetInt32());
        Assert.Equal("none", document.RootElement.GetProperty("sideEffects").GetString());
        Assert.DoesNotContain(root.Path, console.ReadOutputString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Join(domain.Path, "private-store")));
    }

    [Fact]
    public async Task Status_human_output_includes_storage_bytes_and_limits()
    {
        using var domain = TestDirectory.Create();
        using var configured = ConfigureDomain(domain.Path);
        using var console = new FakeInMemoryConsole();

        await new CoverageStorageStatusCommand().ExecuteAsync(console, CancellationToken.None);

        var output = console.ReadOutputString();
        Assert.Contains("0 B current", output, StringComparison.Ordinal);
        Assert.Contains("0 B metadata", output, StringComparison.Ordinal);
        Assert.Contains("history limits:", output, StringComparison.Ordinal);
        Assert.Contains("overflow observations: 0", output, StringComparison.Ordinal);
        Assert.Contains("policy: policy.json", output, StringComparison.Ordinal);
        Assert.Contains("management: not verified (read-only status)", output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Join(domain.Path, "private-store")));
    }

    [Fact]
    public async Task Status_existing_domain_uses_custom_policy_and_does_not_mutate_tree_or_timestamps()
    {
        using var domain = TestDirectory.Create();
        var storePath = Path.Join(domain.Path, "private-store");
        WritePolicy(storePath, "{\"schemaVersion\":1,\"historyMaxBytes\":12345,\"historyMaxAgeDays\":19,\"repositoryWarningBytes\":23456,\"domainWarningBytes\":34567,\"warningCooldownHours\":36}");
        using (CoverageArtifactStore.Open(storePath)) { }
        var before = CaptureStoreObservation(storePath);
        using var configured = ConfigureDomain(domain.Path);
        using var console = new FakeInMemoryConsole();

        await new CoverageStorageStatusCommand { Json = true }.ExecuteAsync(console, CancellationToken.None);

        var output = console.ReadOutputString();
        using var document = System.Text.Json.JsonDocument.Parse(output);
        var data = document.RootElement.GetProperty("data");
        AssertPolicy(data.GetProperty("policy"), "policy.json", 12345, 19, 23456, 34567, 36);
        AssertManagementNotProbed(data.GetProperty("managementCapability"));
        Assert.True(data.GetProperty("metadataBytes").GetInt64() > 0);
        Assert.DoesNotContain(domain.Path, output, StringComparison.Ordinal);
        AssertStoreObservationEqual(before, CaptureStoreObservation(storePath));
    }

    [Fact]
    public async Task Maintain_apply_preserves_deleted_run_ids_when_a_later_candidate_is_busy()
    {
        using var domain = TestDirectory.Create();
        var storePath = Path.Join(domain.Path, "private-store");
        WritePolicy(storePath, "{\"schemaVersion\":1,\"historyMaxBytes\":0}");
        const string firstRunId = "11111111111111111111111111111111";
        const string secondRunId = "22222222222222222222222222222222";
        using (var store = CoverageArtifactStore.Open(storePath))
        {
            var first = CreateCurrentRun(domain.Path, firstRunId, "success");
            await store.RegisterAsync(first, CancellationToken.None);
            await store.SnapshotAsync(first, pin: false, automatic: false, CancellationToken.None);

            var second = CreateCurrentRun(domain.Path, secondRunId, "failed");
            await store.RegisterAsync(second, CancellationToken.None);
            await store.SnapshotAsync(second, pin: false, automatic: false, CancellationToken.None);

            var current = CreateCurrentRun(domain.Path, "33333333333333333333333333333333", "success");
            await store.RegisterAsync(current, CancellationToken.None);
        }
        using var heldLease = CoverageArtifactOperationLease.Acquire(Path.Join(storePath, "archives", secondRunId), exclusive: true, windowsLockRoot: storePath);
        using var configured = ConfigureDomain(domain.Path);
        using var console = new FakeInMemoryConsole();

        var error = await Assert.ThrowsAsync<CliFx.CommandException>(async () =>
            await new CoverageStorageMaintainCommand { Apply = true, Json = true }.ExecuteAsync(console, CancellationToken.None));

        Assert.Equal(1, error.ExitCode);
        var output = console.ReadOutputString();
        using var document = System.Text.Json.JsonDocument.Parse(output);
        var result = document.RootElement;
        Assert.Equal("failed", result.GetProperty("status").GetString());
        Assert.Equal("ASCOV202", result.GetProperty("diagnostics")[0].GetProperty("code").GetString());
        Assert.Equal("partial", result.GetProperty("sideEffects").GetString());
        Assert.Equal(new[] { firstRunId }, result.GetProperty("data").GetProperty("deletedRunIds").EnumerateArray().Select(item => item.GetString()));
        Assert.True(Directory.Exists(Path.Join(storePath, "archives", secondRunId)));
        Assert.False(Directory.Exists(Path.Join(storePath, "archives", firstRunId)));
        Assert.DoesNotContain(storePath, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pre_cancelled_maintenance_returns_cancelled_schema_and_exit_code_130()
    {
        using var domain = TestDirectory.Create();
        using var configured = ConfigureDomain(domain.Path);
        using var console = new FakeInMemoryConsole();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var error = await Assert.ThrowsAsync<CliFx.CommandException>(async () =>
            await new CoverageStorageMaintainCommand { Json = true }.ExecuteAsync(console, cancellation.Token));

        Assert.Equal(130, error.ExitCode);
        using var document = System.Text.Json.JsonDocument.Parse(console.ReadOutputString());
        Assert.Equal("cancelled", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("none", document.RootElement.GetProperty("sideEffects").GetString());
        Assert.DoesNotContain(domain.Path, console.ReadOutputString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restore_invalid_run_is_redacted_schema_208_and_exit_code_2()
    {
        using var domain = TestDirectory.Create();
        using var configured = ConfigureDomain(domain.Path);
        using var console = new FakeInMemoryConsole();

        var error = await Assert.ThrowsAsync<CliFx.CommandException>(async () =>
            await new CoverageStorageRestoreCommand { Run = "not-a-run", Output = Path.Join(domain.Path, "secret-output"), Json = true }
                .ExecuteAsync(console, CancellationToken.None));

        Assert.Equal(2, error.ExitCode);
        var output = console.ReadOutputString();
        using var document = System.Text.Json.JsonDocument.Parse(output);
        Assert.Equal("failed", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("ASCOV208", document.RootElement.GetProperty("diagnostics")[0].GetProperty("code").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("maxRetries").GetInt32());
        Assert.DoesNotContain(domain.Path, output, StringComparison.Ordinal);
        Assert.DoesNotContain("not-a-run", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pin", true)]
    [InlineData("pin", false)]
    [InlineData("unpin", true)]
    [InlineData("unpin", false)]
    [InlineData("restore", true)]
    [InlineData("restore", false)]
    public async Task Invalid_run_names_the_option_and_accepted_format_without_echoing_input(string operation, bool json)
    {
        using var domain = TestDirectory.Create();
        using var configured = ConfigureDomain(domain.Path);
        using var console = new FakeInMemoryConsole();
        CoverageStorageCommandBase command = operation switch
        {
            "pin" => new CoverageStoragePinCommand { Run = "secret-input-value", Json = json },
            "unpin" => new CoverageStorageUnpinCommand { Run = "secret-input-value", Json = json },
            _ => new CoverageStorageRestoreCommand { Run = "secret-input-value", Output = "unused", Json = json },
        };

        var error = await Assert.ThrowsAsync<CliFx.CommandException>(async () =>
            await command.ExecuteAsync(console, CancellationToken.None));

        Assert.Equal(2, error.ExitCode);
        var output = console.ReadOutputString() + console.ReadErrorString();
        Assert.Contains("--run", output, StringComparison.Ordinal);
        Assert.Contains("32 hexadecimal characters", output, StringComparison.Ordinal);
        Assert.Contains("without hyphens", output, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-input-value", output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Join(domain.Path, "private-store")));
        if (json)
        {
            using var document = System.Text.Json.JsonDocument.Parse(console.ReadOutputString());
            Assert.Equal("none", document.RootElement.GetProperty("sideEffects").GetString());
            Assert.Contains("--run", document.RootElement.GetProperty("recoveryAction").GetString(), StringComparison.Ordinal);
            Assert.Contains("--run", document.RootElement.GetProperty("diagnostics")[0].GetProperty("recovery").GetString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Restore_missing_output_names_its_fresh_destination_requirement()
    {
        using var domain = TestDirectory.Create();
        using var configured = ConfigureDomain(domain.Path);
        using var console = new FakeInMemoryConsole();

        var error = await Assert.ThrowsAsync<CliFx.CommandException>(async () =>
            await new CoverageStorageRestoreCommand { Run = Guid.NewGuid().ToString("N"), Json = true }
                .ExecuteAsync(console, CancellationToken.None));

        Assert.Equal(2, error.ExitCode);
        Assert.Contains("--output", console.ReadOutputString(), StringComparison.Ordinal);
        Assert.Contains("fresh destination directory", console.ReadOutputString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Join(domain.Path, "private-store")));
    }

    [Fact]
    public async Task Inspect_missing_root_reports_partial_and_typed_diagnostic()
    {
        using var domain = TestDirectory.Create();
        using var configured = ConfigureDomain(domain.Path);
        using var console = new FakeInMemoryConsole();

        var error = await Assert.ThrowsAsync<CliFx.CommandException>(async () =>
            await new CoverageStorageInspectCommand { Root = Path.Join(domain.Path, "missing"), Json = true }
                .ExecuteAsync(console, CancellationToken.None));

        Assert.Equal(1, error.ExitCode);
        using var document = System.Text.Json.JsonDocument.Parse(console.ReadOutputString());
        Assert.Equal("partial", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("ASCOV203", document.RootElement.GetProperty("diagnostics")[0].GetProperty("code").GetString());
        Assert.Equal("none", document.RootElement.GetProperty("sideEffects").GetString());
    }

    [Fact]
    public async Task Busy_maintenance_is_reported_without_leaking_exception_text()
    {
        using var domain = TestDirectory.Create();
        var storePath = Path.Join(domain.Path, "private-store");
        using (CoverageArtifactStore.Open(storePath)) { }
        using var heldLease = CoverageArtifactOperationLease.Acquire(storePath, exclusive: true);
        using var configured = ConfigureDomain(domain.Path);
        using var console = new FakeInMemoryConsole();

        var error = await Assert.ThrowsAsync<CliFx.CommandException>(async () =>
            await new CoverageStorageMaintainCommand { Apply = true, Json = true }.ExecuteAsync(console, CancellationToken.None));

        Assert.Equal(1, error.ExitCode);
        var output = console.ReadOutputString();
        using var document = System.Text.Json.JsonDocument.Parse(output);
        Assert.Equal("failed", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("ASCOV202", document.RootElement.GetProperty("diagnostics")[0].GetProperty("code").GetString());
        Assert.Equal("none", document.RootElement.GetProperty("sideEffects").GetString());
        Assert.Contains("retry once", output, StringComparison.Ordinal);
        Assert.DoesNotContain(storePath, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_reports_actual_archive_metadata_staging_and_protected_bytes()
    {
        using var domain = TestDirectory.Create();
        var storePath = Path.Join(domain.Path, "private-store");
        using (var store = CoverageArtifactStore.Open(storePath))
        {
            await store.RegisterAsync(CreateCurrentRun(domain.Path), CancellationToken.None);
        }
        using var configured = ConfigureDomain(domain.Path);
        using var console = new FakeInMemoryConsole();

        await new CoverageStorageNamedStatusCommand { Json = true }.ExecuteAsync(console, CancellationToken.None);

        using var document = System.Text.Json.JsonDocument.Parse(console.ReadOutputString());
        var data = document.RootElement.GetProperty("data");
        Assert.Equal(7, data.GetProperty("currentBytes").GetInt64());
        Assert.Equal(7, data.GetProperty("protectedBytes").GetInt64());
        Assert.Equal(0, data.GetProperty("pinnedBytes").GetInt64());
        Assert.True(data.GetProperty("metadataBytes").GetInt64() > 0);
        Assert.Equal(0, data.GetProperty("overflowObservationCount").GetInt32());
        Assert.Contains("current", data.GetProperty("protectedReasons").EnumerateArray().Select(item => item.GetString()));
    }

    private static CoverageArtifactCurrentRun CreateCurrentRun(string root, string runId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", string outcome = "success")
    {
        var output = Path.Join(root, "output");
        var repository = Path.Join(root, "repo");
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(repository);
        var bytes = System.Text.Encoding.UTF8.GetBytes("payload");
        File.WriteAllBytes(Path.Join(output, "coverage.xml"), bytes);
        var entry = new CoverageArtifactInventoryEntry("coverage.xml", bytes.Length, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant());
        var manifest = new CoverageArtifactRunManifest
        {
            RunId = runId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Outcome = outcome,
            Version = "test"
        };
        return new CoverageArtifactCurrentRun(manifest, [entry], output, repository);
    }

    private static void AssertPolicy(System.Text.Json.JsonElement policy, string path, long historyMaxBytes, int historyMaxAgeDays,
        long repositoryWarningBytes, long domainWarningBytes, int warningCooldownHours)
    {
        Assert.Equal(path, policy.GetProperty("path").GetString());
        Assert.Equal(1, policy.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(historyMaxBytes, policy.GetProperty("historyMaxBytes").GetInt64());
        Assert.Equal(historyMaxAgeDays, policy.GetProperty("historyMaxAgeDays").GetInt32());
        Assert.Equal(repositoryWarningBytes, policy.GetProperty("repositoryWarningBytes").GetInt64());
        Assert.Equal(domainWarningBytes, policy.GetProperty("domainWarningBytes").GetInt64());
        Assert.Equal(warningCooldownHours, policy.GetProperty("warningCooldownHours").GetInt32());
    }

    private static void AssertManagementNotProbed(System.Text.Json.JsonElement capability)
    {
        Assert.Equal("not-probed", capability.GetProperty("status").GetString());
        Assert.Equal("unknown", capability.GetProperty("capabilityLastVerified").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, capability.GetProperty("supported").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, capability.GetProperty("fileSystem").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, capability.GetProperty("nativeLocking").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, capability.GetProperty("noFollowTraversal").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, capability.GetProperty("atomicNoReplaceRename").ValueKind);
    }

    private static void AssertFixedResourceLimits(System.Text.Json.JsonElement limits)
    {
        Assert.Equal(CoverageArtifactLimits.MaximumPolicyBytes, limits.GetProperty("maximumPolicyBytes").GetInt32());
        Assert.Equal(CoverageArtifactLimits.AutomaticSnapshotMaxOriginalBytes, limits.GetProperty("automaticSnapshotMaxOriginalBytes").GetInt64());
        Assert.Equal(CoverageArtifactLimits.AutomaticSnapshotMaxStagingBytes, limits.GetProperty("automaticSnapshotMaxStagingBytes").GetInt64());
        Assert.Equal(CoverageArtifactLimits.DomainStagingMaxBytes, limits.GetProperty("domainStagingMaxBytes").GetInt64());
        Assert.Equal(CoverageArtifactLimits.ExplicitExpandedMaxBytes, limits.GetProperty("explicitExpandedMaxBytes").GetInt64());
        Assert.Equal(CoverageArtifactLimits.MaximumFiles, limits.GetProperty("maximumFiles").GetInt32());
        Assert.Equal(CoverageArtifactLimits.MaximumRegistrations, limits.GetProperty("maximumRegistrations").GetInt32());
        Assert.Equal(CoverageArtifactLimits.RunInventoryMaxBytes, limits.GetProperty("runInventoryMaxBytes").GetInt64());
        Assert.Equal(CoverageArtifactLimits.RegistryMaxBytes, limits.GetProperty("registryMaxBytes").GetInt64());
        Assert.Equal(CoverageArtifactLimits.OverflowObservationReserveBytes, limits.GetProperty("overflowObservationReserveBytes").GetInt64());
        Assert.Equal(CoverageArtifactLimits.MaximumOverflowObservations, limits.GetProperty("maximumOverflowObservations").GetInt32());
        Assert.Equal(CoverageArtifactLimits.OperationBufferMaxBytes, limits.GetProperty("operationBufferMaxBytes").GetInt32());
        Assert.Equal(CoverageArtifactLimits.InspectionBudgetMilliseconds, limits.GetProperty("inspectionBudgetMilliseconds").GetInt32());
        Assert.Equal(CoverageArtifactLimits.InspectionMaxEntries, limits.GetProperty("inspectionMaxEntries").GetInt32());
        Assert.Equal(CoverageArtifactLimits.RoutineMaintenanceBudgetMilliseconds, limits.GetProperty("routineMaintenanceBudgetMilliseconds").GetInt32());
        Assert.Equal(CoverageArtifactLimits.WarningStateMaxConditions, limits.GetProperty("warningStateMaxConditions").GetInt32());
        Assert.Equal(CoverageArtifactLimits.WarningStateMaxBytes, limits.GetProperty("warningStateMaxBytes").GetInt64());
        Assert.Equal(CoverageArtifactLimits.ExpiryReceiptsMaxEntries, limits.GetProperty("expiryReceiptsMaxEntries").GetInt32());
        Assert.Equal(CoverageArtifactLimits.ExpiryReceiptsMaxBytes, limits.GetProperty("expiryReceiptsMaxBytes").GetInt64());
        Assert.Equal(CoverageArtifactLimits.ExpiryReceiptMaxAgeDays, limits.GetProperty("expiryReceiptMaxAgeDays").GetInt32());
    }

    private static void WritePolicy(string storePath, string json)
    {
        using (CoverageArtifactStore.Open(storePath)) { }
        using var root = CoverageRunOutputLease.AcquireArtifactDirectory(storePath, createMissing: false, requirePrivate: true)!;
        using var policy = root.OpenArtifactFile("policy.json", createNew: true);
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        policy.Write(bytes);
        policy.Flush(true);
    }

    private static StoreObservation CaptureStoreObservation(string root)
    {
        var entries = Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path)).Order(StringComparer.Ordinal).ToArray();
        var directoryTimes = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Prepend(root)
            .ToDictionary(path => Path.GetRelativePath(root, path), path => Directory.GetLastWriteTimeUtc(path).Ticks, StringComparer.Ordinal);
        var fileObservations = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(root, path), path => (new FileInfo(path).Length, File.GetLastWriteTimeUtc(path).Ticks), StringComparer.Ordinal);
        return new StoreObservation(entries, directoryTimes, fileObservations);
    }

    private static void AssertStoreObservationEqual(StoreObservation expected, StoreObservation actual)
    {
        Assert.Equal(expected.Entries, actual.Entries);
        Assert.Equal(expected.DirectoryWriteTimes.OrderBy(item => item.Key, StringComparer.Ordinal), actual.DirectoryWriteTimes.OrderBy(item => item.Key, StringComparer.Ordinal));
        Assert.Equal(expected.FileObservations.OrderBy(item => item.Key, StringComparer.Ordinal), actual.FileObservations.OrderBy(item => item.Key, StringComparer.Ordinal));
    }

    private sealed record StoreObservation(string[] Entries, Dictionary<string, long> DirectoryWriteTimes,
        Dictionary<string, (long Length, long LastWriteTicks)> FileObservations);

    private static IDisposable ConfigureDomain(string path)
    {
        var previous = Environment.GetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME");
        Environment.SetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME", Path.Join(path, "private-store"));
        return new RestoreEnvironment(() => Environment.SetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME", previous));
    }

    private sealed class RestoreEnvironment(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
