using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CliFx;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Cli;
using ForgeTrust.AppSurface.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Locks the v3 command envelope to its closed request and terminal-result contract.</summary>
[Collection("CoverageGate process state")]
public sealed class CoverageFanInV3CommandTests
{
    private const string Profile = "coverlet-collector-cobertura-v1";
    private static readonly JsonSerializerOptions WireOptions = CreateWireOptions();

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    /// <summary>protects: version-specific policy projection. fails_when: schema and policy versions diverge. no_claim: profile qualification.</summary>
    public void Request_reader_requires_a_homogeneous_explicit_policy_version(int version)
    {
        using var root = TestDirectory.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(Envelope("capture", CaptureRequest(root.Path, version), version));

        var parsed = CoverageFanInRequestReader.Parse(bytes);
        CoverageFanInRequestReader.ValidateOperationRequest(parsed);
        CoverageFanInRequestReader.ValidateCanonicalPolicy(parsed);

        Assert.Equal(version, parsed.ContractVersion);
        var policy = Assert.IsType<CoverageCaptureRequest>(parsed.Request).Policy;
        Assert.Equal(version, policy.ContractVersion);
        Assert.Equal(version == 3 ? Profile : null, policy.ReportProfileId);
        using var document = JsonDocument.Parse(bytes);
        var policyElement = document.RootElement.GetProperty("request").GetProperty("policy");
        Assert.Equal(version == 3, policyElement.TryGetProperty("contractVersion", out _));
        Assert.Equal(version == 3, policyElement.TryGetProperty("reportProfileId", out _));
    }

    [Fact]
    /// <summary>protects: the frozen v2 request shape. fails_when: v2 carries v3 policy members. no_claim: v3 admission.</summary>
    public void V2_reader_rejects_new_policy_properties_instead_of_accepting_an_ambiguous_legacy_request()
    {
        using var root = TestDirectory.Create();
        var legacy = JsonNode.Parse(Envelope("capture", CaptureRequest(root.Path, 2), 2))!.AsObject();
        var policy = legacy["request"]!["policy"]!.AsObject();
        policy["contractVersion"] = 2;
        policy["reportProfileId"] = null;

        Assert.Throws<CoverageFanInCliInputException>(() => CoverageFanInRequestReader.Parse(
            System.Text.Encoding.UTF8.GetBytes(legacy.ToJsonString(WireOptions))));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("lossy-attempt")]
    [InlineData("overflow-attempt")]
    [InlineData("wrong-profile")]
    /// <summary>protects: strict v3 claim parsing. fails_when: claims are unknown, duplicated, absent, lossy, or unsupported. no_claim: engine verification.</summary>
    public void V3_request_reader_rejects_noncanonical_claims(string mutation)
    {
        using var root = TestDirectory.Create();
        var baseline = Envelope("capture", CaptureRequest(root.Path, 3), 3);
        var request = mutation switch
        {
            "unknown" => baseline.Replace("\"producerId\":\"primary\"", "\"producerId\":\"primary\",\"claim\":true", StringComparison.Ordinal),
            "duplicate" => baseline.Replace("\"schemaVersion\":3", "\"schemaVersion\":3,\"schemaVersion\":3", StringComparison.Ordinal),
            "missing" => baseline.Replace("\"contractVersion\":3,", "", StringComparison.Ordinal),
            "lossy-attempt" => baseline.Replace("\"attempt\":1", "\"attempt\":1.5", StringComparison.Ordinal),
            "overflow-attempt" => baseline.Replace("\"attempt\":1", "\"attempt\":2147483648", StringComparison.Ordinal),
            _ => baseline.Replace(Profile, "unknown-profile", StringComparison.Ordinal),
        };

        Assert.Throws<CoverageFanInCliInputException>(() => CoverageFanInRequestReader.Parse(
            System.Text.Encoding.UTF8.GetBytes(request)));
    }

    [Theory]
    [InlineData("capture")]
    [InlineData("collect")]
    [InlineData("compose")]
    [InlineData("accept")]
    [InlineData("verify")]
    /// <summary>protects: homogeneous v3 parsing across registered request shapes. fails_when: an operation omits v3 policy requirements. no_claim: facade effects.</summary>
    public void Registered_operation_shapes_parse_as_v3(string operation)
    {
        using var root = TestDirectory.Create();
        var request = RequestFor(operation, root.Path, 3);
        var parsed = CoverageFanInRequestReader.Parse(
            System.Text.Encoding.UTF8.GetBytes(Envelope(operation, request, 3)));

        CoverageFanInRequestReader.ValidateOperationRequest(parsed);
        CoverageFanInRequestReader.ValidateCanonicalPolicy(parsed);
        Assert.Equal(3, parsed.ContractVersion);
        Assert.Equal(operation, parsed.Operation);
    }

    [Fact]
    /// <summary>protects: v3 wire outcomes, required nulls, and safe diagnostics. fails_when: codec drops fields or exposes raw values. no_claim: source completeness.</summary>
    public async Task V3_terminal_projects_all_metrics_nullable_fields_and_safe_diagnostic_context()
    {
        using var root = TestDirectory.Create();
        var request = root.WriteFile("request.json", Envelope("accept", RequestFor("accept", root.Path, 3), 3));
        var result = Result("accept", CoverageOperationOutcome.Rejected, "NotEvaluated", Applicability(
            Metric(CoveragePatchApplicabilityOutcome.Measured, "reported-points", 2, 2, 1, 50m),
            Metric(CoveragePatchApplicabilityOutcome.NotApplicable, "no-reported-patch-points", 2, 0, 0, null),
            Metric(CoveragePatchApplicabilityOutcome.Measured, "reported-points", 2, 2, 2, 100m),
            Metric(CoveragePatchApplicabilityOutcome.Unverified, "invalid-evidence", null, null, null, null)),
            diagnostics: [new CoverageDiagnostic("ASCOVF009", "applicability", "report", "Recheck retained reports.")
            {
                Cause = "report-invalid",
                Context = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["contractVersion"] = "3",
                    ["reportProfileId"] = Profile,
                    ["headCommit"] = new string('a', 40),
                    ["exceptionType"] = "InvalidDataException",
                    ["rawInput"] = "must-not-escape"
                },
                Trace = ["ForgeTrust.AppSurface.Evidence.Coverage.CoverageFanInEngine.Accept", "/private/secret/file.cs"]
            }], accounting: new CoverageFanInAccounting(11, 100, 12, 13, 14, 15, 16, 2));
        using var console = new FakeInMemoryConsole();

        var error = await Assert.ThrowsAsync<CommandException>(async () => await new ResultCommand("accept", result)
        {
            Request = request,
            Result = Path.Join(root.Path, "terminal.json"),
            Json = true
        }.ExecuteAsync(console, CancellationToken.None));

        Assert.Equal(1, error.ExitCode);
        using var terminal = JsonDocument.Parse(console.ReadOutputString());
        var envelope = terminal.RootElement;
        Assert.Equal(3, envelope.GetProperty("schemaVersion").GetInt32());
        var terminalResult = envelope.GetProperty("result");
        var accounting = terminalResult.GetProperty("accounting");
        Assert.Equal(11, accounting.GetProperty("usedBytes").GetInt64());
        Assert.Equal(100, accounting.GetProperty("limitBytes").GetInt64());
        Assert.Equal(12, accounting.GetProperty("xmlBytes").GetInt64());
        Assert.Equal(13, accounting.GetProperty("jsonBytes").GetInt64());
        Assert.Equal(14, accounting.GetProperty("lineRecords").GetInt64());
        Assert.Equal(15, accounting.GetProperty("branchRecords").GetInt64());
        Assert.Equal(16, accounting.GetProperty("stagedBytes").GetInt64());
        Assert.Equal(2, accounting.GetProperty("stagedFiles").GetInt64());
        Assert.Equal(["usedBytes", "limitBytes", "xmlBytes", "jsonBytes", "lineRecords", "branchRecords", "stagedBytes", "stagedFiles"],
            accounting.EnumerateObject().Select(property => property.Name).ToArray());
        var metrics = terminalResult.GetProperty("patchApplicability");
        Assert.Equal("measured", metrics.GetProperty("primary").GetProperty("line").GetProperty("outcome").GetString());
        var notApplicable = metrics.GetProperty("primary").GetProperty("branch");
        Assert.Equal("notApplicable", notApplicable.GetProperty("outcome").GetString());
        Assert.True(notApplicable.TryGetProperty("percent", out var nullPercent));
        Assert.Equal(JsonValueKind.Null, nullPercent.ValueKind);
        var unverified = metrics.GetProperty("composed").GetProperty("branch");
        Assert.Equal("unverified", unverified.GetProperty("outcome").GetString());
        foreach (var field in new[] { "changedLines", "measurable", "covered", "percent" })
        {
            Assert.True(unverified.TryGetProperty(field, out var value));
            Assert.Equal(JsonValueKind.Null, value.ValueKind);
        }

        var diagnostic = envelope.GetProperty("result").GetProperty("diagnostics")[0];
        Assert.Equal("report-invalid", diagnostic.GetProperty("cause").GetString());
        var context = diagnostic.GetProperty("context");
        Assert.Equal("3", context.GetProperty("contractVersion").GetString());
        Assert.False(context.TryGetProperty("rawInput", out _));
        var trace = Assert.Single(diagnostic.GetProperty("trace").EnumerateArray());
        Assert.StartsWith("ForgeTrust.AppSurface.Evidence.Coverage.", trace.GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("must-not-escape", console.ReadOutputString() + console.ReadErrorString(), StringComparison.Ordinal);
        Assert.DoesNotContain("/private/secret", console.ReadOutputString() + console.ReadErrorString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("capture-applicability")]
    [InlineData("line-denominator")]
    /// <summary>protects: v3 applicability invariants. fails_when: capture exposes metrics or line points exceed changed lines. no_claim: report provenance.</summary>
    public async Task V3_result_rejects_operation_and_metric_contradictions(string defect)
    {
        using var root = TestDirectory.Create();
        var operation = defect == "capture-applicability" ? "capture" : "accept";
        var request = root.WriteFile("request.json", Envelope(operation, RequestFor(operation, root.Path, 3), 3));
        var changedLines = defect == "line-denominator" ? 1 : 2;
        var measurableLines = defect == "line-denominator" ? 2 : 2;
        var result = Result(operation, CoverageOperationOutcome.Rejected, "NotEvaluated", Applicability(
            Metric(CoveragePatchApplicabilityOutcome.Measured, "reported-points", changedLines, measurableLines, 1, 50m),
            Metric(CoveragePatchApplicabilityOutcome.NotApplicable, "no-reported-patch-points", changedLines, 0, 0, null),
            Metric(CoveragePatchApplicabilityOutcome.Measured, "reported-points", changedLines, changedLines, changedLines, 100m),
            Metric(CoveragePatchApplicabilityOutcome.NotApplicable, "no-reported-patch-points", changedLines, 0, 0, null)));
        using var console = new FakeInMemoryConsole();

        var error = await Assert.ThrowsAsync<CommandException>(async () => await new ResultCommand(operation, result)
        {
            Request = request,
            Result = Path.Join(root.Path, "terminal.json"),
            Json = true
        }.ExecuteAsync(console, CancellationToken.None));

        Assert.Equal(1, error.ExitCode);
        using var terminal = JsonDocument.Parse(console.ReadOutputString());
        Assert.Equal(JsonValueKind.Null, terminal.RootElement.GetProperty("result").ValueKind);
        Assert.Equal("applicability-mismatch", terminal.RootElement.GetProperty("diagnostics")[0].GetProperty("cause").GetString());
    }

    [Fact]
    /// <summary>protects: human N/A wording and trust notice. fails_when: output implies source completeness. no_claim: measured coverage.</summary>
    public async Task V3_human_rendering_uses_literal_na_and_generic_report_trust_notice()
    {
        using var root = TestDirectory.Create();
        var request = root.WriteFile("request.json", Envelope("accept", RequestFor("accept", root.Path, 3), 3));
        var result = Result("accept", CoverageOperationOutcome.Succeeded, "PatchAccepted", Applicability(
            Metric(CoveragePatchApplicabilityOutcome.NotApplicable, "no-reported-patch-points", 0, 0, 0, null),
            Metric(CoveragePatchApplicabilityOutcome.Measured, "reported-points", 0, 1, 1, 100m),
            Metric(CoveragePatchApplicabilityOutcome.NotApplicable, "no-reported-patch-points", 0, 0, 0, null),
            Metric(CoveragePatchApplicabilityOutcome.Measured, "reported-points", 0, 2, 2, 100m)), accepted: true);
        using var console = new FakeInMemoryConsole();

        await new ResultCommand("accept", result)
        {
            Request = request,
            Result = Path.Join(root.Path, "terminal.json")
        }.ExecuteAsync(console, CancellationToken.None);

        var output = console.ReadOutputString();
        Assert.Contains("primary line: N/A", output, StringComparison.Ordinal);
        Assert.Contains("composed line: N/A", output, StringComparison.Ordinal);
        Assert.Contains("this does not establish source completeness", output, StringComparison.Ordinal);
        Assert.DoesNotContain("notApplicable", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unverified")]
    /// <summary>protects: PatchAccepted completeness. fails_when: any of four metrics is absent or unverified. no_claim: caller-supplied result correctness beyond its shape.</summary>
    public async Task V3_patch_accepted_cannot_be_delivered_without_four_verified_metrics(string defect)
    {
        using var root = TestDirectory.Create();
        var request = root.WriteFile("request.json", Envelope("accept", RequestFor("accept", root.Path, 3), 3));
        var partial = defect == "missing" ? null : Applicability(
            Metric(CoveragePatchApplicabilityOutcome.Measured, "reported-points", 1, 1, 1, 100m),
            Metric(CoveragePatchApplicabilityOutcome.Unverified, "invalid-evidence", null, null, null, null),
            Metric(CoveragePatchApplicabilityOutcome.Measured, "reported-points", 1, 1, 1, 100m),
            Metric(CoveragePatchApplicabilityOutcome.Measured, "reported-points", 1, 1, 1, 100m));
        var result = Result("accept", CoverageOperationOutcome.Succeeded, "PatchAccepted", partial, accepted: true);
        using var console = new FakeInMemoryConsole();

        var error = await Assert.ThrowsAsync<CommandException>(async () => await new ResultCommand("accept", result)
        {
            Request = request,
            Result = Path.Join(root.Path, "terminal.json"),
            Json = true
        }.ExecuteAsync(console, CancellationToken.None));

        Assert.Equal(1, error.ExitCode);
        using var terminal = JsonDocument.Parse(console.ReadOutputString());
        Assert.Equal("failed", terminal.RootElement.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, terminal.RootElement.GetProperty("result").ValueKind);
        Assert.Equal("applicability-mismatch", terminal.RootElement.GetProperty("diagnostics")[0].GetProperty("cause").GetString());
        Assert.True(File.Exists(Path.Join(root.Path, "terminal.json")));
    }

    [Fact]
    /// <summary>protects: v2 terminal wire compatibility. fails_when: v3 properties leak into v2 JSON. no_claim: v2 policy serialization outside the explicit fixture projection.</summary>
    public async Task V2_result_projection_keeps_legacy_shape_when_public_types_have_v3_members()
    {
        using var root = TestDirectory.Create();
        var request = root.WriteFile("request.json", Envelope("capture", CaptureRequest(root.Path, 2), 2));
        var result = Result("capture", CoverageOperationOutcome.Succeeded, "NotEvaluated", Applicability(
            Metric(CoveragePatchApplicabilityOutcome.Measured, "reported-points", 1, 1, 1, 100m),
            Metric(CoveragePatchApplicabilityOutcome.Measured, "reported-points", 1, 1, 1, 100m),
            Metric(CoveragePatchApplicabilityOutcome.Measured, "reported-points", 1, 1, 1, 100m),
            Metric(CoveragePatchApplicabilityOutcome.Measured, "reported-points", 1, 1, 1, 100m)),
            diagnostics: [new CoverageDiagnostic("ASCOVF008", "execution", "operation", "Use the v2 fixture.") { Cause = "unexpected-failure" }],
            accounting: new CoverageFanInAccounting(11, 100));
        using var console = new FakeInMemoryConsole();

        await new ResultCommand("capture", result)
        {
            Request = request,
            Result = Path.Join(root.Path, "terminal.json"),
            Json = true
        }.ExecuteAsync(console, CancellationToken.None);

        using var terminal = JsonDocument.Parse(console.ReadOutputString());
        var envelope = terminal.RootElement;
        Assert.Equal(2, envelope.GetProperty("schemaVersion").GetInt32());
        var resultElement = envelope.GetProperty("result");
        Assert.False(resultElement.TryGetProperty("accounting", out _));
        Assert.False(resultElement.TryGetProperty("patchApplicability", out _));
        Assert.False(resultElement.GetProperty("diagnostics")[0].TryGetProperty("cause", out _));
        Assert.False(resultElement.GetProperty("diagnostics")[0].TryGetProperty("context", out _));
        Assert.False(resultElement.GetProperty("diagnostics")[0].TryGetProperty("trace", out _));
        Assert.Empty(envelope.GetProperty("diagnostics").EnumerateArray());
    }

    [Theory]
    [InlineData("cancelled", 130, "cancelled")]
    [InlineData("timedOut", 124, "deadline-exceeded")]
    public async Task V3_terminal_failures_keep_legacy_exit_codes_and_typed_causes(string failure, int exitCode, string cause)
    {
        using var root = TestDirectory.Create();
        var request = root.WriteFile("request.json", Envelope("capture", CaptureRequest(root.Path, 3), 3));
        using var console = new FakeInMemoryConsole();
        using var cancellation = new CancellationTokenSource();
        if (failure == "cancelled") cancellation.Cancel();

        var command = new FailingCommand { Request = request, Result = Path.Join(root.Path, "terminal.json"), Json = true };
        var error = await Assert.ThrowsAsync<CommandException>(async () => await command.ExecuteAsync(console, cancellation.Token));

        Assert.Equal(exitCode, error.ExitCode);
        using var terminal = JsonDocument.Parse(console.ReadOutputString());
        var diagnostic = Assert.Single(terminal.RootElement.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal(cause, diagnostic.GetProperty("cause").GetString());
        Assert.Equal("3", diagnostic.GetProperty("context").GetProperty("contractVersion").GetString());
    }

    [Fact]
    /// <summary>protects: v3 input exit mapping. fails_when: invalid v3 input prepares delivery or loses exit 2. no_claim: operation execution.</summary>
    public async Task V3_invalid_input_keeps_exit_two_without_preparing_result_delivery()
    {
        using var root = TestDirectory.Create();
        var invalid = Envelope("capture", CaptureRequest(root.Path, 3), 3)
            .Replace("\"producerId\":\"primary\"", "\"producerId\":\"primary\",\"unknown\":true", StringComparison.Ordinal);
        var request = root.WriteFile("request.json", invalid);
        var resultPath = Path.Join(root.Path, "terminal.json");
        using var console = new FakeInMemoryConsole();

        var error = await Assert.ThrowsAsync<CommandException>(async () => await new CoverageFanInCaptureCommand
        {
            Request = request,
            Result = resultPath,
            Json = true
        }.ExecuteAsync(console, CancellationToken.None));

        Assert.Equal(2, error.ExitCode);
        Assert.False(File.Exists(resultPath));
        using var terminal = JsonDocument.Parse(console.ReadOutputString());
        Assert.Equal(3, terminal.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("invalidInput", terminal.RootElement.GetProperty("status").GetString());
    }

    private static CoverageFanInPolicy Policy(int version, CoverageGatePolicy? gate = null)
        => new(new(new string('a', 40), new string('b', 40), "10.0.402", "Release"),
            new("local", "consumer", "run-1", 1), ["tests/Demo.Tests.csproj"], [],
            gate ?? new(CoverageGateMode.AggregateOnly))
        {
            ContractVersion = version,
            ReportProfileId = version == 3 ? Profile : null
        };

    private static object CaptureRequest(string repositoryRoot, int version) => new
    {
        policy = CoveragePolicyEncoding.ToElement(Policy(version)),
        repositoryRoot,
        outputDirectory = Path.Join(repositoryRoot, "capture-output"),
        producerId = "primary",
        coveragePath = Path.Join(repositoryRoot, "coverage.cobertura.xml"),
        timingsPath = Path.Join(repositoryRoot, "timings.json")
    };

    private static object RequestFor(string operation, string repositoryRoot, int version)
    {
        var policy = Policy(version, operation == "accept"
            ? new(CoverageGateMode.AggregateAndPatch, DiffBase: new string('c', 40))
            : null);
        var policyElement = CoveragePolicyEncoding.ToElement(policy);
        var primary = Path.Join(repositoryRoot, "primary-capture");
        var composed = Path.Join(repositoryRoot, "composed");
        var supplementary = Array.Empty<string>();
        return operation switch
        {
            "capture" => CaptureRequest(repositoryRoot, version),
            "collect" => new
            {
                policy = policyElement,
                repositoryRoot,
                outputDirectory = Path.Join(repositoryRoot, "capture-output"),
                collectionDirectory = Path.Join(repositoryRoot, "collection"),
                producerId = "primary",
                deadlineSeconds = 10
            },
            "compose" => new
            {
                policy = policyElement,
                repositoryRoot,
                primaryCaptureDirectory = primary,
                supplementaryCaptureDirectories = supplementary,
                outputDirectory = composed
            },
            "accept" => new
            {
                policy = policyElement,
                repositoryRoot,
                primaryCaptureDirectory = primary,
                supplementaryCaptureDirectories = supplementary,
                composedOutputDirectory = composed,
                outputDirectory = Path.Join(repositoryRoot, "accepted"),
                expectedPatchSha256 = new string('d', 64)
            },
            "verify" => new
            {
                policy = policyElement,
                repositoryRoot,
                primaryCaptureDirectory = primary,
                supplementaryCaptureDirectories = supplementary,
                composedOutputDirectory = composed,
                acceptedOutputDirectory = Path.Join(repositoryRoot, "accepted")
            },
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
    }

    private static string Envelope(string operation, object request, int version)
        => JsonSerializer.Serialize(new { schemaVersion = version, operation, request }, WireOptions);

    private static CoveragePatchApplicability Applicability(CoveragePatchMetricResult primaryLine,
        CoveragePatchMetricResult primaryBranch, CoveragePatchMetricResult composedLine,
        CoveragePatchMetricResult composedBranch)
        => new(new(primaryLine, primaryBranch), new(composedLine, composedBranch));

    private static CoveragePatchMetricResult Metric(CoveragePatchApplicabilityOutcome outcome, string reason,
        int? changedLines, int? measurable, int? covered, decimal? percent)
        => new(outcome, reason, changedLines, measurable, covered, percent);

    private static CoverageFanInResult Result(string operation, CoverageOperationOutcome outcome, string patchOutcome,
        CoveragePatchApplicability? applicability, bool accepted = false, CoverageDiagnostic[]? diagnostics = null,
        CoverageFanInAccounting? accounting = null)
        => new(operation, outcome, "operation-1", null, new(false, false, true, false, null), [], diagnostics ?? [],
            accepted, verified: accepted, patchOutcome: patchOutcome, patchApplicability: applicability, accounting: accounting);

    private static JsonSerializerOptions CreateWireOptions()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }

    private sealed class ResultCommand(string operation, CoverageFanInResult result)
        : CoverageFanInCommandBase(operation, requiresResult: true)
    {
        internal override ValueTask<CoverageFanInResult> InvokeAsync(ParsedCoverageFanInRequest request,
            TextWriter diagnostics, CancellationToken cancellationToken) => ValueTask.FromResult(result);
    }

    private sealed class FailingCommand : CoverageFanInCommandBase
    {
        internal FailingCommand() : base("capture", requiresResult: true) { }

        internal override ValueTask<CoverageFanInResult> InvokeAsync(ParsedCoverageFanInRequest request,
            TextWriter diagnostics, CancellationToken cancellationToken)
            => ValueTask.FromException<CoverageFanInResult>(new TimeoutException("private-timeout-text"));
    }
}
