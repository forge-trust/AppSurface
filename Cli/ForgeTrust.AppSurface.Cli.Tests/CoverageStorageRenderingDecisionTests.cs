using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

[Collection("CoverageGate process state")]
public sealed class CoverageStorageRenderingDecisionTests
{
    [Fact]
    public async Task Partial_maintenance_uses_maintenance_guidance_and_keeps_progress()
    {
        using var domain = TestDirectory.Create();
        using var configured = ConfigureDomain(domain.Path);

        using var result = await ExecuteFailureAsync(new ProbeCommand(
            "maintain", new { apply = true, candidateCount = 2, deletedRunIds = new[] { "opaque-run" } },
            status: "partial", effects: "partial"));

        var envelope = result.Document.RootElement;
        Assert.Equal("partial", envelope.GetProperty("status").GetString());
        Assert.Equal("ASCOV204", envelope.GetProperty("diagnostics")[0].GetProperty("code").GetString());
        Assert.Contains("History maintenance stopped after a partial pass", envelope.GetProperty("diagnostics")[0].GetProperty("cause").GetString(), StringComparison.Ordinal);
        Assert.Contains("retry maintenance once", envelope.GetProperty("recoveryAction").GetString(), StringComparison.Ordinal);
        Assert.Equal("partial", envelope.GetProperty("sideEffects").GetString());
        Assert.Contains("opaque-run", envelope.GetProperty("data").GetProperty("deletedRunIds").EnumerateArray().Select(item => item.GetString()));
        Assert.NotNull(envelope.GetProperty("reconcileAction").GetString());
    }

    [Fact]
    public async Task Partial_unrecognized_operation_uses_generic_guidance_and_retains_data()
    {
        using var domain = TestDirectory.Create();
        using var configured = ConfigureDomain(domain.Path);

        using var result = await ExecuteFailureAsync(new ProbeCommand(
            "future-operation", new { remaining = 3 }, status: "partial"));

        var envelope = result.Document.RootElement;
        Assert.Equal("ASCOV201", envelope.GetProperty("diagnostics")[0].GetProperty("code").GetString());
        Assert.Equal("The operation returned an incomplete result.", envelope.GetProperty("diagnostics")[0].GetProperty("cause").GetString());
        Assert.Equal("Inspect coverage storage status --json, resolve the reported condition, then retry once.",
            envelope.GetProperty("recoveryAction").GetString());
        Assert.Equal(3, envelope.GetProperty("data").GetProperty("remaining").GetInt32());
        Assert.Equal("none", envelope.GetProperty("sideEffects").GetString());
    }

    [Theory]
    [InlineData("CAPACITY reached", "ASCOV207")]
    [InlineData("history LIMIT reached", "ASCOV207")]
    [InlineData("lower bound inventory", "ASCOV207")]
    [InlineData("lower-bound inventory", "ASCOV207")]
    [InlineData("repository access interrupted", "ASCOV201")]
    public async Task Partial_status_classifies_capacity_phrases_case_insensitively(string reason, string expectedCode)
    {
        using var domain = TestDirectory.Create();
        using var configured = ConfigureDomain(domain.Path);

        using var result = await ExecuteFailureAsync(new ProbeCommand(
            "status", new { incompleteReason = reason }, status: "partial"));

        Assert.Equal(expectedCode, result.Document.RootElement.GetProperty("diagnostics")[0].GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("positive-count", "ASCOV207")]
    [InlineData("zero-count", "ASCOV201")]
    [InlineData("string-count", "ASCOV201")]
    [InlineData("missing-count", "ASCOV201")]
    [InlineData("missing-details", "ASCOV201")]
    [InlineData("non-string-reason", "ASCOV201")]
    public async Task Partial_status_handles_missing_and_malformed_capacity_details(string detailsCase, string expectedCode)
    {
        using var domain = TestDirectory.Create();
        using var configured = ConfigureDomain(domain.Path);

        using var result = await ExecuteFailureAsync(new ProbeCommand(
            "status", StatusDetails(detailsCase), status: "partial"));

        Assert.Equal(expectedCode, result.Document.RootElement.GetProperty("diagnostics")[0].GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("inspect", "ASCOV203")]
    [InlineData("maintain", "ASCOV204")]
    [InlineData("restore", "ASCOV205")]
    [InlineData("pin", "ASCOV206")]
    [InlineData("unpin", "ASCOV206")]
    [InlineData("status", "ASCOV201")]
    [InlineData("future-operation", "ASCOV201")]
    public async Task Invalid_data_failure_uses_the_operation_diagnostic_without_leaking_exception_text(string operation, string expectedCode)
    {
        using var domain = TestDirectory.Create();
        using var configured = ConfigureDomain(domain.Path);

        using var result = await ExecuteFailureAsync(new ProbeCommand(
            operation, failure: new InvalidDataException("private-store-path/secret")));

        var envelope = result.Document.RootElement;
        Assert.Equal(expectedCode, envelope.GetProperty("diagnostics")[0].GetProperty("code").GetString());
        Assert.Equal("none", envelope.GetProperty("sideEffects").GetString());
        Assert.DoesNotContain("private-store-path/secret", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(domain.Path, result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ASCOV202", "complete", "none")]
    [InlineData("ASCOV202", "partial", "partial")]
    [InlineData("ASCOV500", "partial", "partial")]
    public async Task Execution_failures_preserve_only_confirmed_side_effects(string code, string effects, string expectedEffects)
    {
        using var domain = TestDirectory.Create();
        using var configured = ConfigureDomain(domain.Path);

        using var result = await ExecuteFailureAsync(new ProbeCommand(
            "restore", effects: effects, failure: new CoverageExecutionException(code + " safe failure")));

        Assert.Equal(code == "ASCOV500" ? "ASCOV205" : code,
            result.Document.RootElement.GetProperty("diagnostics")[0].GetProperty("code").GetString());
        Assert.Equal(expectedEffects, result.Document.RootElement.GetProperty("sideEffects").GetString());
    }

    [Fact]
    public async Task Input_argument_failure_returns_exit_two_before_store_initialization()
    {
        using var domain = TestDirectory.Create();
        var storePath = TestPathUtils.PathUnder(domain.Path, "private-store");
        using var configured = ConfigureDomain(domain.Path);

        using var result = await ExecuteFailureAsync(new ProbeCommand(
            "restore", validationFailure: new ArgumentException("invalid secret input")));

        Assert.Equal(2, result.ExitCode!.Value);
        Assert.Equal("ASCOV208", result.Document.RootElement.GetProperty("diagnostics")[0].GetProperty("code").GetString());
        Assert.Equal("none", result.Document.RootElement.GetProperty("sideEffects").GetString());
        Assert.False(Directory.Exists(storePath));
        Assert.DoesNotContain("invalid secret input", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("none", "none", false)]
    [InlineData("partial", "partial", true)]
    [InlineData("complete", "unknown", true)]
    public async Task Operation_argument_failure_sanitizes_effects_and_adds_reconciliation_when_needed(
        string effects, string expectedEffects, bool expectsReconcile)
    {
        using var domain = TestDirectory.Create();
        using var configured = ConfigureDomain(domain.Path);

        using var result = await ExecuteFailureAsync(new ProbeCommand(
            "pin", effects: effects, failure: new ArgumentException("secret run input")));

        var envelope = result.Document.RootElement;
        Assert.Equal(2, result.ExitCode!.Value);
        Assert.Equal("ASCOV208", envelope.GetProperty("diagnostics")[0].GetProperty("code").GetString());
        Assert.Equal(expectedEffects, envelope.GetProperty("sideEffects").GetString());
        Assert.Equal(expectsReconcile, envelope.GetProperty("reconcileAction").ValueKind != JsonValueKind.Null);
        Assert.DoesNotContain("secret run input", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Io_failure_preserves_published_progress_and_partial_effects_in_the_envelope()
    {
        using var domain = TestDirectory.Create();
        using var configured = ConfigureDomain(domain.Path);

        using var result = await ExecuteFailureAsync(new ProbeCommand(
            "maintain", new { deletedRunIds = new[] { "completed-run" }, candidateCount = 4 },
            effects: "partial", failure: new IOException("/private/path"), publishPartialDetails: true));

        var envelope = result.Document.RootElement;
        Assert.Equal("ASCOV204", envelope.GetProperty("diagnostics")[0].GetProperty("code").GetString());
        Assert.Equal("partial", envelope.GetProperty("sideEffects").GetString());
        Assert.Equal("completed-run", envelope.GetProperty("data").GetProperty("deletedRunIds")[0].GetString());
        Assert.NotNull(envelope.GetProperty("reconcileAction").GetString());
        Assert.DoesNotContain("/private/path", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unsupported_operation_failure_uses_unsupported_status_and_diagnostic()
    {
        using var domain = TestDirectory.Create();
        using var configured = ConfigureDomain(domain.Path);

        using var result = await ExecuteFailureAsync(new ProbeCommand(
            "inspect", failure: new NotSupportedException("private capability")));

        Assert.Equal(1, result.ExitCode!.Value);
        Assert.Equal("unsupported", result.Document.RootElement.GetProperty("status").GetString());
        Assert.Equal("ASCOV203", result.Document.RootElement.GetProperty("diagnostics")[0].GetProperty("code").GetString());
        Assert.Equal("none", result.Document.RootElement.GetProperty("sideEffects").GetString());
    }

    [Fact]
    public async Task Human_status_renders_defaults_for_missing_and_unrecognized_fields()
    {
        using var domain = TestDirectory.Create();
        using var configured = ConfigureDomain(domain.Path);
        using var console = new FakeInMemoryConsole();
        var command = new ProbeCommand("status", new
        {
            registeredRuns = "not-an-integer",
            currentBytes = 1024,
            policy = new { path = (string?)null, repositoryWarningBytes = "not-a-number" },
            managementCapability = new { status = "future-capability" },
            complete = false
        }, json: false);

        await command.ExecuteAsync(console, CancellationToken.None);

        var output = console.ReadOutputString();
        Assert.Contains("runs: 0 registered", output, StringComparison.Ordinal);
        Assert.Contains("bytes: 1.0 KiB current", output, StringComparison.Ordinal);
        Assert.Contains("policy: unknown; repository warning 0 B", output, StringComparison.Ordinal);
        Assert.Contains("management: not verified", output, StringComparison.Ordinal);
        Assert.Contains("inventory is incomplete; totals are lower bounds", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Human_maintenance_reports_unknown_when_candidate_count_is_malformed_or_absent()
    {
        using var domain = TestDirectory.Create();
        using var configured = ConfigureDomain(domain.Path);
        using var console = new FakeInMemoryConsole();

        await new ProbeCommand("maintain", new { candidateCount = "many", apply = "yes" }, json: false)
            .ExecuteAsync(console, CancellationToken.None);

        Assert.Contains("candidates: unknown; apply: False", console.ReadOutputString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1023L, "1,023 B")]
    [InlineData(1024L, "1.0 KiB")]
    [InlineData(1048576L, "1.0 MiB")]
    [InlineData(1073741824L, "1.00 GiB")]
    public async Task Human_status_formats_each_byte_unit_from_the_emitted_payload(long bytes, string expected)
    {
        using var domain = TestDirectory.Create();
        using var configured = ConfigureDomain(domain.Path);
        using var console = new FakeInMemoryConsole();

        await new ProbeCommand("status", new { currentBytes = bytes, complete = true }, json: false)
            .ExecuteAsync(console, CancellationToken.None);

        Assert.Contains(expected + " current", console.ReadOutputString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_data_reports_current_pinned_and_reserved_bytes_as_protected_reasons()
    {
        using var temp = TestDirectory.Create();
        var storePath = TestPathUtils.PathUnder(temp.Path, "private-store");
        var repository = temp.CreateDirectory("repository");
        var archived = CreateRun(temp, repository, "archived-output", "11111111111141118111111111111111");
        var current = CreateRun(temp, repository, "current-output", "22222222222242228222222222222222");
        using (var store = CoverageArtifactStore.Open(storePath))
        {
            await store.RegisterAsync(archived, CancellationToken.None);
            await store.SnapshotAsync(archived, pin: true, automatic: false, CancellationToken.None);
            await store.RegisterAsync(current, CancellationToken.None);
        }

        var registryPath = TestPathUtils.PathUnder(storePath, "registry.json");
        var registry = JsonNode.Parse(File.ReadAllText(registryPath))!.AsObject();
        registry["Reservations"]!.AsArray().Add(new JsonObject
        {
            ["Id"] = "33333333333343338333333333333333",
            ["RunId"] = current.ManifestBase.RunId,
            ["Bytes"] = CoverageArtifactLimits.DomainStagingMaxBytes,
            ["StartedAtUtc"] = "2026-10-22T12:00:00.0000000+00:00"
        });
        File.WriteAllText(registryPath, registry.ToJsonString());
        using var configured = ConfigureStore(storePath);

        using var result = await ExecuteSuccessAsync(new StatusDataCommand { Json = true });

        var data = result.Document.RootElement.GetProperty("data");
        var protectedReasons = data.GetProperty("protectedReasons").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Contains("current", protectedReasons);
        Assert.Contains("pinned", protectedReasons);
        Assert.Contains("unreconciled-reservation", protectedReasons);
        Assert.True(data.GetProperty("currentBytes").GetInt64() > 0);
        Assert.True(data.GetProperty("pinnedBytes").GetInt64() > 0);
        Assert.Equal(CoverageArtifactLimits.DomainStagingMaxBytes, data.GetProperty("stagingReservedBytes").GetInt64());
        Assert.Equal(data.GetProperty("currentBytes").GetInt64() + data.GetProperty("pinnedBytes").GetInt64(),
            data.GetProperty("protectedBytes").GetInt64());
    }

    [Fact]
    public async Task HumanResultWithoutDataStillRendersFailureAndItsDiagnostic()
    {
        using var temp = TestDirectory.Create();
        using var configured = ConfigureDomain(temp.Path);
        using var console = new FakeInMemoryConsole();
        await Assert.ThrowsAsync<CliFx.CommandException>(async () => await new ProbeCommand("inspect",
            failure: new IOException("private"), json: false).ExecuteAsync(console, default));
        Assert.Contains("coverage storage inspect: failed", console.ReadOutputString(), StringComparison.Ordinal);
        Assert.Contains("ASCOV203", console.ReadErrorString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnrepresentableHumanNumbersUseFallbackAndDoNotThrow()
    {
        using var temp = TestDirectory.Create();
        using var configured = ConfigureDomain(temp.Path);
        using var console = new FakeInMemoryConsole();
        await new ProbeCommand("status", new
        {
            registeredRuns = (long)int.MaxValue + 1,
            currentBytes = 1e30,
            complete = false,
            policy = new { path = 0 }
        }, json: false).ExecuteAsync(console, default);
        Assert.Contains("0 registered", console.ReadOutputString(), StringComparison.Ordinal);
        Assert.Contains("0 B current", console.ReadOutputString(), StringComparison.Ordinal);
        Assert.Contains("policy: unknown", console.ReadOutputString(), StringComparison.Ordinal);
        Assert.Contains("lower bounds", console.ReadOutputString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonDirectoryDomainReturnsUnknownIdentityWithSanitizedFailureEnvelope()
    {
        using var temp = TestDirectory.Create();
        var invalidDomain = temp.WriteFile("domain", "retained private contents");
        using var configured = ConfigureStore(invalidDomain);
        using var result = await ExecuteFailureAsync(new ProbeCommand("status"));
        Assert.Equal("domain-unknown", result.Document.RootElement.GetProperty("domainId").GetString());
        Assert.Equal("failed", result.Document.RootElement.GetProperty("status").GetString());
        Assert.Equal("none", result.Document.RootElement.GetProperty("sideEffects").GetString());
        Assert.Equal("retained private contents", File.ReadAllText(invalidDomain));
        Assert.DoesNotContain("retained private contents", result.Output, StringComparison.Ordinal);
    }

    private static object? StatusDetails(string detailsCase) => detailsCase switch
    {
        "positive-count" => new { overflowObservationCount = 1 },
        "zero-count" => new { overflowObservationCount = 0 },
        "string-count" => new { overflowObservationCount = "1" },
        "missing-count" => new { registeredRuns = 1 },
        "missing-details" => null,
        "non-string-reason" => new { incompleteReason = 7 },
        _ => throw new ArgumentOutOfRangeException(nameof(detailsCase))
    };

    private static CoverageArtifactCurrentRun CreateRun(TestDirectory temp, string repository, string outputName, string runId)
    {
        var output = temp.CreateDirectory(outputName);
        var file = TestPathUtils.PathUnder(output, "coverage.xml");
        var bytes = Encoding.UTF8.GetBytes(outputName);
        File.WriteAllBytes(file, bytes);
        return new(new CoverageArtifactRunManifest
        {
            RunId = runId,
            CreatedAtUtc = DateTimeOffset.Parse("2026-10-22T12:00:00Z"),
            Outcome = "success",
            Version = "coverage-storage-rendering-tests",
            Provenance = new CoverageArtifactProvenance("revision", "clean", "configuration")
        }, [new CoverageArtifactInventoryEntry("coverage.xml", bytes.LongLength,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant())], output, repository);
    }

    private static async Task<ExecutionResult> ExecuteFailureAsync(CoverageStorageCommandBase command)
    {
        using var console = new FakeInMemoryConsole();
        var exception = await Assert.ThrowsAsync<CliFx.CommandException>(async () =>
            await command.ExecuteAsync(console, CancellationToken.None));
        var output = console.ReadOutputString();
        return new ExecutionResult(JsonDocument.Parse(output), output, exception.ExitCode);
    }

    private static async Task<ExecutionResult> ExecuteSuccessAsync(CoverageStorageCommandBase command)
    {
        using var console = new FakeInMemoryConsole();
        await command.ExecuteAsync(console, CancellationToken.None);
        var output = console.ReadOutputString();
        return new ExecutionResult(JsonDocument.Parse(output), output, null);
    }

    private static IDisposable ConfigureDomain(string domainPath) => ConfigureStore(TestPathUtils.PathUnder(domainPath, "private-store"));

    private static IDisposable ConfigureStore(string storePath)
    {
        var previous = Environment.GetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME");
        Environment.SetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME", storePath);
        return new RestoreEnvironment(() => Environment.SetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME", previous));
    }

    private sealed class StatusDataCommand : CoverageStorageCommandBase
    {
        public StatusDataCommand() : base("status") { }

        protected override ValueTask<object?> ExecuteOperationAsync(
            CoverageArtifactStore store, CancellationToken token, Action<string> setEffects, Action<string> setStatus)
            => ValueTask.FromResult<object?>(CreateStatusData(store, setStatus));
    }

    private sealed class ProbeCommand : CoverageStorageCommandBase
    {
        private readonly object? _data;
        private readonly string? _status;
        private readonly string? _effects;
        private readonly Exception? _failure;
        private readonly bool _publishPartialDetails;
        private readonly Exception? _validationFailure;

        public ProbeCommand(
            string operation,
            object? data = null,
            string? status = null,
            string? effects = null,
            Exception? failure = null,
            bool publishPartialDetails = false,
            Exception? validationFailure = null,
            bool json = true)
            : base(operation)
        {
            _data = data;
            _status = status;
            _effects = effects;
            _failure = failure;
            _publishPartialDetails = publishPartialDetails;
            _validationFailure = validationFailure;
            Json = json;
        }

        protected override void ValidateInput()
        {
            if (_validationFailure is not null) throw _validationFailure;
        }

        protected override ValueTask<object?> ExecuteOperationAsync(
            CoverageArtifactStore store, CancellationToken token, Action<string> setEffects, Action<string> setStatus)
        {
            if (_effects is not null) setEffects(_effects);
            if (_status is not null) setStatus(_status);
            if (_publishPartialDetails && _data is not null) PublishPartialDetails(_data);
            if (_failure is not null) throw _failure;
            return ValueTask.FromResult(_data);
        }
    }

    private sealed record ExecutionResult(JsonDocument Document, string Output, int? ExitCode) : IDisposable
    {
        public void Dispose() => Document.Dispose();
    }

    private sealed class RestoreEnvironment(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
