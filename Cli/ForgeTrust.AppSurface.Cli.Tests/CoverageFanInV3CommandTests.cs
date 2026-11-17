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

    [Theory]
    [InlineData("omitted")]
    [InlineData("null")]
    public void V3_optional_policy_and_collection_fields_keep_their_documented_defaults(string optionalBlocks)
    {
        using var root = TestDirectory.Create();
        var envelope = JsonNode.Parse(Envelope("collect", RequestFor("collect", root.Path, 3), 3))!.AsObject();
        var request = envelope["request"]!.AsObject();
        var policy = request["policy"]!.AsObject();
        var gate = policy["gate"]!.AsObject();
        if (optionalBlocks == "omitted")
        {
            policy.Remove("instrumentation");
            policy.Remove("limits");
        }
        else
        {
            policy["instrumentation"] = null;
            policy["limits"] = null;
        }
        foreach (var field in new[] { "minLine", "minBranch", "minPatchLine", "minPatchBranch", "tolerance", "patchLineMode" })
            gate.Remove(field);
        request.Remove("parallelism");
        request.Remove("noRestore");
        request.Remove("noBuild");

        var parsed = CoverageFanInRequestReader.Parse(System.Text.Encoding.UTF8.GetBytes(envelope.ToJsonString(WireOptions)));
        CoverageFanInRequestReader.ValidateOperationRequest(parsed);
        CoverageFanInRequestReader.ValidateCanonicalPolicy(parsed);

        var collect = Assert.IsType<CoverageCollectRequest>(parsed.Request);
        Assert.Equal(1, collect.Parallelism);
        Assert.False(collect.NoRestore);
        Assert.False(collect.NoBuild);
        Assert.Null(collect.Policy.Instrumentation);
        Assert.Null(collect.Policy.Limits);
        Assert.Equal(95m, collect.Policy.Gate.MinLine);
        Assert.Equal(85m, collect.Policy.Gate.MinBranch);
        Assert.Equal(95m, collect.Policy.Gate.MinPatchLine);
        Assert.Equal(85m, collect.Policy.Gate.MinPatchBranch);
        Assert.Equal(0.5m, collect.Policy.Gate.Tolerance);
        Assert.Equal("codecov", collect.Policy.Gate.PatchLineMode);
    }

    [Theory]
    [InlineData("missing-schema-version")]
    [InlineData("null-schema-version")]
    [InlineData("string-schema-version")]
    [InlineData("fractional-schema-version")]
    [InlineData("null-request")]
    [InlineData("unknown-envelope-field")]
    [InlineData("missing-profile")]
    [InlineData("null-profile")]
    [InlineData("wrong-profile-type")]
    [InlineData("overflow-policy-version")]
    [InlineData("wrong-attempt-type")]
    [InlineData("overflow-attempt")]
    [InlineData("missing-required-field")]
    [InlineData("unknown-request-field")]
    [InlineData("wrong-supplement-array-type")]
    [InlineData("null-no-build")]
    [InlineData("wrong-no-build-type")]
    [InlineData("overflow-deadline")]
    [InlineData("wrong-parallelism-type")]
    [InlineData("negative-deadline")]
    [InlineData("deadline-over-limit")]
    [InlineData("zero-parallelism")]
    /// <summary>protects: v3 closed shapes and lossless bounded selectors. fails_when: malformed types, absent fields, or invalid collection bounds are admitted. no_claim: facade execution.</summary>
    public void V3_reader_refuses_malformed_required_typed_and_bounded_fields(string mutation)
    {
        using var root = TestDirectory.Create();
        var operation = mutation switch
        {
            "wrong-supplement-array-type" => "compose",
            "null-no-build" or "wrong-no-build-type" or "overflow-deadline" or "wrong-parallelism-type"
                or "negative-deadline" or "deadline-over-limit" or "zero-parallelism" => "collect",
            _ => "accept"
        };
        var envelope = JsonNode.Parse(Envelope(operation, RequestFor(operation, root.Path, 3), 3))!.AsObject();
        var request = envelope["request"]!.AsObject();
        var policy = request["policy"]!.AsObject();
        switch (mutation)
        {
            case "missing-schema-version": envelope.Remove("schemaVersion"); break;
            case "null-schema-version": envelope["schemaVersion"] = null; break;
            case "string-schema-version": envelope["schemaVersion"] = "3"; break;
            case "fractional-schema-version": envelope["schemaVersion"] = JsonNode.Parse("3.5"); break;
            case "null-request": envelope["request"] = null; break;
            case "unknown-envelope-field": envelope["unrecognized"] = true; break;
            case "missing-profile": policy.Remove("reportProfileId"); break;
            case "null-profile": policy["reportProfileId"] = null; break;
            case "wrong-profile-type": policy["reportProfileId"] = 3; break;
            case "overflow-policy-version": policy["contractVersion"] = 2147483648L; break;
            case "wrong-attempt-type": policy["run"]!["attempt"] = "1"; break;
            case "overflow-attempt": policy["run"]!["attempt"] = 2147483648L; break;
            case "missing-required-field": request.Remove("composedOutputDirectory"); break;
            case "unknown-request-field": request["unknown"] = true; break;
            case "wrong-supplement-array-type": request["supplementaryCaptureDirectories"] = 1; break;
            case "null-no-build": request["noBuild"] = null; break;
            case "wrong-no-build-type": request["noBuild"] = "false"; break;
            case "overflow-deadline": request["deadlineSeconds"] = 2147483648L; break;
            case "wrong-parallelism-type": request["parallelism"] = "1"; break;
            case "negative-deadline": request["deadlineSeconds"] = -1; break;
            case "deadline-over-limit": request["deadlineSeconds"] = 86401; break;
            case "zero-parallelism": request["parallelism"] = 0; break;
        }
        var bytes = System.Text.Encoding.UTF8.GetBytes(envelope.ToJsonString(WireOptions));

        if (mutation is "negative-deadline" or "deadline-over-limit" or "zero-parallelism")
        {
            var parsed = CoverageFanInRequestReader.Parse(bytes);
            Assert.Throws<CoverageFanInCliInputException>(() => CoverageFanInRequestReader.ValidateOperationRequest(parsed));
        }
        else
        {
            Assert.Throws<CoverageFanInCliInputException>(() => CoverageFanInRequestReader.Parse(bytes));
        }
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

    [Theory]
    [InlineData("null-primary")]
    [InlineData("null-composed")]
    [InlineData("null-primary-line")]
    [InlineData("null-primary-branch")]
    [InlineData("null-composed-line")]
    [InlineData("null-composed-branch")]
    [InlineData("measured-reason")]
    [InlineData("measured-negative-changed")]
    [InlineData("measured-null-count")]
    [InlineData("measured-zero-denominator")]
    [InlineData("measured-negative-covered")]
    [InlineData("measured-over-covered")]
    [InlineData("measured-null-percent")]
    [InlineData("measured-high-percent")]
    [InlineData("na-reason")]
    [InlineData("na-null-changed")]
    [InlineData("na-measurable")]
    [InlineData("na-covered")]
    [InlineData("na-percent")]
    [InlineData("unverified-reason")]
    [InlineData("unverified-count")]
    [InlineData("unknown-outcome")]
    [InlineData("line-measurable-exceeds-changed")]
    [InlineData("inconsistent-changed-lines")]
    [InlineData("measured-null-changed")]
    [InlineData("measured-null-covered")]
    [InlineData("measured-negative-denominator")]
    [InlineData("measured-low-percent")]
    [InlineData("na-negative-changed")]
    [InlineData("na-null-covered")]
    [InlineData("unverified-measurable")]
    [InlineData("unverified-covered")]
    [InlineData("unverified-percent")]
    /// <summary>protects: closed v3 metric invariants. fails_when: any required metric shape, count, percentage, or shared changed-line constraint is corrupt. no_claim: recomputation from source reports.</summary>
    public async Task V3_result_suppresses_every_invalid_metric_shape(string defect)
    {
        using var root = TestDirectory.Create();
        var request = root.WriteFile("request.json", Envelope("accept", RequestFor("accept", root.Path, 3), 3));
        var valid = Applicability(
            Metric(CoveragePatchApplicabilityOutcome.Measured, "reported-points", 2, 2, 1, 50m),
            Metric(CoveragePatchApplicabilityOutcome.Measured, "reported-points", 2, 4, 2, 50m),
            Metric(CoveragePatchApplicabilityOutcome.Measured, "reported-points", 2, 2, 2, 100m),
            Metric(CoveragePatchApplicabilityOutcome.Measured, "reported-points", 2, 4, 4, 100m));
        var applicability = CorruptApplicability(valid, defect);
        var result = Result("accept", CoverageOperationOutcome.Succeeded, "GateFailed", applicability);
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
        Assert.Equal("accept", terminal.RootElement.GetProperty("operation").GetString());
        Assert.Equal("operation-1", terminal.RootElement.GetProperty("operationId").GetString());
    }

    [Fact]
    /// <summary>protects: safe diagnostic projection. fails_when: unsupported context and trace values escape or valid bounded context is lost. no_claim: exception stack completeness.</summary>
    public async Task V3_diagnostic_projection_keeps_only_allowlisted_context_and_safe_frames()
    {
        using var root = TestDirectory.Create();
        var request = root.WriteFile("request.json", Envelope("verify", RequestFor("verify", root.Path, 3), 3));
        var diagnostic = new CoverageDiagnostic("ASCOVF009", "execution", "operation", "Review the retained evidence.")
        {
            Cause = "unsupported-git-version",
            Context = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["contractVersion"] = "3",
                ["reportProfileId"] = Profile,
                ["headCommit"] = new string('a', 40),
                ["baseCommit"] = new string('b', 64),
                ["diffPolicyId"] = "strict-v3_policy",
                ["gitVersion"] = "2.45.1.windows.1",
                ["gitExitCode"] = "-1",
                ["exceptionType"] = "IOException",
                ["rawInput"] = "private payload",
                ["gitVersionBad"] = "2.45/secret",
                ["headCommitBad"] = new string('z', 40),
                ["exceptionTypeBad"] = "System.IO.IOException",
                ["contractVersionBad"] = "2",
                ["overlong"] = new string('x', 129),
                ["control"] = "safe\nunsafe"
            },
            Trace =
            [
                "ForgeTrust.AppSurface.Cli.CoverageFanInCommandBase.ExecuteAsync",
                "ForgeTrust.AppSurface.Evidence.Coverage.CoverageFanInEngine.Verify",
                "System.IO.File.ReadAllText",
                "/private/secret/source.cs",
                "ForgeTrust.AppSurface.Cli.Bad Frame"
            ]
        };
        var result = Result("verify", CoverageOperationOutcome.Rejected, "NotEvaluated", null, diagnostics: [diagnostic]);
        using var console = new FakeInMemoryConsole();

        var error = await Assert.ThrowsAsync<CommandException>(async () => await new ResultCommand("verify", result)
        {
            Request = request,
            Result = Path.Join(root.Path, "terminal.json"),
            Json = true
        }.ExecuteAsync(console, CancellationToken.None));

        Assert.Equal(1, error.ExitCode);
        using var terminal = JsonDocument.Parse(console.ReadOutputString());
        var projected = terminal.RootElement.GetProperty("result").GetProperty("diagnostics")[0];
        Assert.Equal("unsupported-git-version", projected.GetProperty("cause").GetString());
        var context = projected.GetProperty("context");
        Assert.Equal("3", context.GetProperty("contractVersion").GetString());
        Assert.Equal(Profile, context.GetProperty("reportProfileId").GetString());
        Assert.Equal(new string('a', 40), context.GetProperty("headCommit").GetString());
        Assert.Equal(new string('b', 64), context.GetProperty("baseCommit").GetString());
        Assert.Equal("strict-v3_policy", context.GetProperty("diffPolicyId").GetString());
        Assert.Equal("2.45.1.windows.1", context.GetProperty("gitVersion").GetString());
        Assert.Equal("-1", context.GetProperty("gitExitCode").GetString());
        Assert.Equal("IOException", context.GetProperty("exceptionType").GetString());
        Assert.Equal(8, context.EnumerateObject().Count());
        var trace = projected.GetProperty("trace").EnumerateArray().Select(frame => frame.GetString()).ToArray();
        Assert.Equal(2, trace.Length);
        Assert.Contains("ForgeTrust.AppSurface.Cli.CoverageFanInCommandBase.ExecuteAsync", trace);
        Assert.Contains("ForgeTrust.AppSurface.Evidence.Coverage.CoverageFanInEngine.Verify", trace);
        Assert.DoesNotContain("private payload", console.ReadOutputString() + console.ReadErrorString(), StringComparison.Ordinal);
        Assert.DoesNotContain("/private/secret", console.ReadOutputString() + console.ReadErrorString(), StringComparison.Ordinal);
    }

    [Fact]
    /// <summary>protects: failed atomic result delivery state. fails_when: a write failure loses operation status or reports a committed result. no_claim: filesystem fault recovery.</summary>
    public async Task V3_delivery_write_failure_reports_uncommitted_delivery_and_preserves_safe_result()
    {
        using var root = TestDirectory.Create();
        var request = root.WriteFile("request.json", Envelope("verify", RequestFor("verify", root.Path, 3), 3));
        var result = Result("verify", CoverageOperationOutcome.Succeeded, "NotEvaluated", null);
        using var console = new FakeInMemoryConsole();

        var error = await Assert.ThrowsAsync<CommandException>(async () => await new ResultCommand("verify", result,
            new ThrowOnWriteDeliveryFactory())
        {
            Request = request,
            Result = Path.Join(root.Path, "terminal.json"),
            Json = true
        }.ExecuteAsync(console, CancellationToken.None));

        Assert.Equal(1, error.ExitCode);
        using var terminal = JsonDocument.Parse(console.ReadOutputString());
        Assert.Equal("deliveryFailed", terminal.RootElement.GetProperty("status").GetString());
        Assert.Equal("succeeded", terminal.RootElement.GetProperty("result").GetProperty("outcome").GetString());
        Assert.Equal("delivery-failed", terminal.RootElement.GetProperty("diagnostics")[0].GetProperty("cause").GetString());
        Assert.True(terminal.RootElement.GetProperty("delivery").GetProperty("requested").GetBoolean());
        Assert.False(terminal.RootElement.GetProperty("delivery").GetProperty("resultFileCommitted").GetBoolean());
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

    [Theory]
    [InlineData("contractVersion", "2")]
    [InlineData("reportProfileId", "unknown")]
    [InlineData("headCommit", "short")]
    [InlineData("headCommit", "zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    [InlineData("gitExitCode", "2147483648")]
    [InlineData("exceptionType", "1Exception")]
    [InlineData("exceptionType", "System.Exception")]
    [InlineData("gitVersion", "2.45/secret")]
    [InlineData("gitVersion", "")]
    [InlineData("gitVersion", "2.45\nsecret")]
    [InlineData("diffPolicyId", "invalid/policy")]
    [InlineData("exceptionType", "has-dash")]
    [InlineData("exceptionType", "")]
    [InlineData("baseCommit", "short")]
    [InlineData("baseCommit", "zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void V3_diagnostics_drop_invalid_values_under_recognized_context_keys(string key, string value)
    {
        var diagnostic = CoverageFanInCliCodec.Diagnostic(3, "ASCOVF009", "input", "policy", "Fix input.",
            "private-cause", new Dictionary<string, string> { [key] = value });

        Assert.Null(diagnostic.Cause);
        Assert.Null(diagnostic.Context);
        var rendered = CoverageFanInCliCodec.FormatDiagnostic(diagnostic, 3);
        Assert.Contains("invalid-policy", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("context=", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void V3_diagnostics_bound_sanitize_and_preserve_safe_fallback_frames()
    {
        var diagnostic = CoverageFanInCliCodec.Diagnostic(3, "", "stage/secret", "field\nname",
            new string('x', 520) + "private tail", "unexpected-failure",
            new Dictionary<string, string>
            {
                ["gitVersion"] = new string('a', 129),
                ["exceptionType"] = "_Exception9"
            },
            ["handled-at:ForgeTrust.AppSurface.Cli.CoverageFanInCommandBase.ExecuteAsync",
                "", new string('a', 257)]);

        Assert.Equal("unknown", diagnostic.Code);
        Assert.Equal("stage_secret", diagnostic.Stage);
        Assert.Equal("field_name", diagnostic.Field);
        Assert.Equal(512, diagnostic.Recovery.Length);
        Assert.Equal("_Exception9", Assert.Single(diagnostic.Context!).Value);
        Assert.Single(diagnostic.Trace!);
        Assert.StartsWith("handled-at:", diagnostic.Trace![0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("instrumentation", "[]")]
    [InlineData("limits", "[]")]
    [InlineData("limit-type", "\"10\"")]
    [InlineData("limit-fraction", "1.5")]
    [InlineData("limit-overflow", "9223372036854775808")]
    [InlineData("limit-underflow", "-9223372036854775809")]
    [InlineData("limit-decimal-overflow", "1e100")]
    [InlineData("threshold-type", "\"95\"")]
    [InlineData("threshold-overflow", "1e100")]
    [InlineData("mode-type", "1")]
    [InlineData("mode-unknown", "\"unknown\"")]
    [InlineData("policy-version", "2")]
    [InlineData("attempt-underflow", "-2147483649")]
    [InlineData("attempt-decimal-overflow", "1e100")]
    [InlineData("blank-source", "\" \"")]
    [InlineData("supplements-type", "{}")]
    [InlineData("filters-type", "[]")]
    public void V3_reader_refuses_malformed_nested_scalars_and_shapes(string mutation, string value)
    {
        using var root = TestDirectory.Create();
        var envelope = JsonNode.Parse(Envelope("capture", CaptureRequest(root.Path, 3), 3))!.AsObject();
        var policy = envelope["request"]!["policy"]!.AsObject();
        var node = JsonNode.Parse(value);
        switch (mutation)
        {
            case "instrumentation": policy["instrumentation"] = node; break;
            case "limits": policy["limits"] = node; break;
            case "threshold-type" or "threshold-overflow": policy["gate"]!["minLine"] = node; break;
            case "mode-type" or "mode-unknown": policy["gate"]!["mode"] = node; break;
            case "policy-version": policy["contractVersion"] = node; break;
            case "attempt-underflow" or "attempt-decimal-overflow": policy["run"]!["attempt"] = node; break;
            case "blank-source": policy["source"]!["sdk"] = node; break;
            case "supplements-type": policy["supplements"] = node; break;
            case "filters-type": envelope["request"]!["rawCoveragePaths"] = node; break;
            default: policy["limits"] = new JsonObject { ["totalBytes"] = node }; break;
        }

        Assert.Throws<CoverageFanInCliInputException>(() => CoverageFanInRequestReader.Parse(
            System.Text.Encoding.UTF8.GetBytes(envelope.ToJsonString(WireOptions))));
    }

    [Fact]
    public void V3_reader_refuses_duplicate_and_blank_raw_report_keys()
    {
        using var root = TestDirectory.Create();
        var baseline = JsonNode.Parse(Envelope("capture", CaptureRequest(root.Path, 3), 3))!;
        baseline["request"]!["rawCoveragePaths"] = new JsonObject { ["primary"] = "Category=Fast" };
        var json = baseline.ToJsonString(WireOptions);
        foreach (var malformed in new[]
        {
            json.Replace("\"primary\":\"Category=Fast\"", "\"primary\":\"Category=Fast\",\"primary\":\"Category=Other\"", StringComparison.Ordinal),
            json.Replace("\"primary\":\"Category=Fast\"", "\" \":\"Category=Fast\"", StringComparison.Ordinal)
        })
            Assert.Throws<CoverageFanInCliInputException>(() => CoverageFanInRequestReader.Parse(
                System.Text.Encoding.UTF8.GetBytes(malformed)));
    }

    [Theory]
    [InlineData("compose", "outputDirectory")]
    [InlineData("verify", "acceptedOutputDirectory")]
    [InlineData("verify", "composedOutputDirectory")]
    [InlineData("accept", "outputDirectory")]
    [InlineData("accept", "expectedPatchSha256")]
    [InlineData("collect", "collectionDirectory")]
    public void V3_operation_validation_rejects_missing_optional_selectors(string operation, string field)
    {
        using var root = TestDirectory.Create();
        var envelope = JsonNode.Parse(Envelope(operation, RequestFor(operation, root.Path, 3), 3))!;
        // Keep the wire shape valid; the typed validation seam must still reject empty selectors.
        var parsed = CoverageFanInRequestReader.Parse(System.Text.Encoding.UTF8.GetBytes(envelope.ToJsonString(WireOptions)));
        object invalid = parsed.Request switch
        {
            CoverageComposeRequest request => request with { OutputDirectory = "" },
            CoverageVerifyRequest request when field == "acceptedOutputDirectory" => request with { AcceptedOutputDirectory = "" },
            CoverageVerifyRequest request => request with { ComposedOutputDirectory = "" },
            CoverageAcceptRequest request when field == "expectedPatchSha256" => request with { ExpectedPatchSha256 = null },
            CoverageAcceptRequest request => request with { OutputDirectory = "" },
            CoverageCollectRequest request => request with { CollectionDirectory = "" },
            _ => throw new InvalidOperationException()
        };

        Assert.Throws<CoverageFanInCliInputException>(() => CoverageFanInRequestReader.ValidateOperationRequest(
            new(operation, invalid) { ContractVersion = 3 }));
    }

    [Fact]
    public async Task V3_human_failure_renders_unverified_metrics_and_delivery_warning()
    {
        using var root = TestDirectory.Create();
        var request = root.WriteFile("request.json", Envelope("accept", RequestFor("accept", root.Path, 3), 3));
        var unverified = Metric(CoveragePatchApplicabilityOutcome.Unverified, "invalid-evidence", null, null, null, null);
        var result = Result("accept", CoverageOperationOutcome.Rejected, "NotEvaluated",
            Applicability(unverified, unverified, unverified, unverified));
        using var console = new FakeInMemoryConsole();

        var error = await Assert.ThrowsAsync<CommandException>(async () => await new ResultCommand("accept", result,
            new ThrowOnWriteDeliveryFactory())
        { Request = request, Result = Path.Join(root.Path, "terminal.json") }
            .ExecuteAsync(console, CancellationToken.None));

        Assert.Equal(1, error.ExitCode);
        Assert.Contains("primary line: unverified (invalid-evidence)", console.ReadOutputString(), StringComparison.Ordinal);
        Assert.Contains("composed branch: unverified (invalid-evidence)", console.ReadOutputString(), StringComparison.Ordinal);
        Assert.DoesNotContain("trust:", console.ReadOutputString(), StringComparison.Ordinal);
        Assert.Contains("result delivery failed", console.ReadErrorString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void V3_patch_accepted_rejects_unverified_evidence_in_each_metric_position(int position)
    {
        var metrics = Enumerable.Repeat(Metric(CoveragePatchApplicabilityOutcome.Measured, "reported-points", 1, 1, 1, 100m), 4).ToArray();
        metrics[position] = Metric(CoveragePatchApplicabilityOutcome.Unverified, "invalid-evidence", null, null, null, null);
        var result = Result("accept", CoverageOperationOutcome.Succeeded, "PatchAccepted",
            Applicability(metrics[0], metrics[1], metrics[2], metrics[3]), accepted: true);

        Assert.False(CoverageFanInCliResult.IsValidForV3(result));
    }

    [Fact]
    public void V3_reader_retains_explicit_collection_and_instrumentation_values()
    {
        using var root = TestDirectory.Create();
        var envelope = JsonNode.Parse(Envelope("collect", RequestFor("collect", root.Path, 3), 3))!;
        var request = envelope["request"]!;
        request["parallelism"] = 2;
        request["noBuild"] = true;
        request["noRestore"] = true;
        request["policy"]!["instrumentation"] = new JsonObject
        {
            ["driver"] = "collector",
            ["format"] = "cobertura",
            ["include"] = "[Demo]*",
            ["exclude"] = "[*.Tests]*",
            ["normalization"] = "appsurface-coverage-merge"
        };
        request["policy"]!["limits"] = new JsonObject { ["xmlBytes"] = 1024, ["operationSeconds"] = 60 };
        var parsed = CoverageFanInRequestReader.Parse(System.Text.Encoding.UTF8.GetBytes(envelope.ToJsonString(WireOptions)));
        CoverageFanInRequestReader.ValidateOperationRequest(parsed);
        CoverageFanInRequestReader.ValidateCanonicalPolicy(parsed);
        var collect = Assert.IsType<CoverageCollectRequest>(parsed.Request);

        Assert.Equal(2, collect.Parallelism);
        Assert.True(collect.NoBuild);
        Assert.True(collect.NoRestore);
        Assert.Equal("[Demo]*", collect.Policy.Instrumentation!.Include);
        Assert.Equal(1024, collect.Policy.Limits!.XmlBytes);
        Assert.Equal(60, collect.Policy.Limits.OperationSeconds);
    }

    [Theory]
    [InlineData("compose")]
    [InlineData("capture")]
    public void V3_capture_and_compose_cannot_supply_patch_applicability(string operation)
    {
        var metric = Metric(CoveragePatchApplicabilityOutcome.NotApplicable, "no-reported-patch-points", 0, 0, 0, null);
        Assert.False(CoverageFanInCliResult.IsValidForV3(Result(operation, CoverageOperationOutcome.Succeeded,
            "NotEvaluated", Applicability(metric, metric, metric, metric))));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"schemaVersion\":1,\"operation\":\"capture\",\"request\":{}}")]
    public void Invalid_envelope_shapes_remain_input_failures(string json)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        Assert.Equal(2, CoverageFanInRequestReader.TryReadEnvelopeVersion(bytes));
        Assert.Throws<CoverageFanInCliInputException>(() => CoverageFanInRequestReader.Parse(bytes));
    }

    [Theory]
    [InlineData("not-a-command")]
    [InlineData("")]
    public void V3_reader_rejects_unregistered_operations(string operation)
    {
        using var root = TestDirectory.Create();
        Assert.Throws<CoverageFanInCliInputException>(() => CoverageFanInRequestReader.Parse(
            System.Text.Encoding.UTF8.GetBytes(Envelope(operation, CaptureRequest(root.Path, 3), 3))));
    }

    [Fact]
    public void V3_diagnostics_truncate_safe_trace_and_recovery_without_leaking_tail()
    {
        var frames = Enumerable.Range(0, 10)
            .Select(index => $"ForgeTrust.AppSurface.Cli.Safe.Type.Method{index}").ToArray();
        var diagnostic = CoverageFanInCliCodec.Diagnostic(3, "ASCOVF009", "input", "policy",
            "first\nsecond\t" + new string('r', 600) + "private-tail-sentinel", "invalid-policy", trace: frames);

        Assert.Equal(8, diagnostic.Trace!.Count);
        Assert.Equal(frames.Take(8), diagnostic.Trace);
        Assert.StartsWith("first second ", diagnostic.Recovery, StringComparison.Ordinal);
        Assert.DoesNotContain("private-tail-sentinel", diagnostic.Recovery, StringComparison.Ordinal);
        Assert.Equal(512, diagnostic.Recovery.Length);
        Assert.Contains("trace=", CoverageFanInCliCodec.FormatDiagnostic(diagnostic, 3), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task V3_human_output_reports_each_independently_absent_dimension(int absentPosition)
    {
        using var root = TestDirectory.Create();
        var request = root.WriteFile("request.json", Envelope("accept", RequestFor("accept", root.Path, 3), 3));
        var metrics = Enumerable.Repeat(Metric(CoveragePatchApplicabilityOutcome.Measured, "reported-points", 1, 1, 1, 100m), 4).ToArray();
        metrics[absentPosition] = Metric(CoveragePatchApplicabilityOutcome.NotApplicable, "no-reported-patch-points", 1, 0, 0, null);
        var result = Result("accept", CoverageOperationOutcome.Succeeded, "PatchAccepted",
            Applicability(metrics[0], metrics[1], metrics[2], metrics[3]), accepted: true);
        using var console = new FakeInMemoryConsole();

        await new ResultCommand("accept", result) { Request = request, Result = Path.Join(root.Path, "terminal.json") }
            .ExecuteAsync(console, CancellationToken.None);

        var labels = new[] { "primary line", "primary branch", "composed line", "composed branch" };
        var output = console.ReadOutputString();
        for (var index = 0; index < labels.Length; index++)
            Assert.Contains(labels[index] + (index == absentPosition ? ": N/A" : ": measured 1/1"), output, StringComparison.Ordinal);
        Assert.Contains("this does not establish source completeness", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V3_human_fully_measured_acceptance_omits_the_na_notice()
    {
        using var root = TestDirectory.Create();
        var request = root.WriteFile("request.json", Envelope("accept", RequestFor("accept", root.Path, 3), 3));
        var metric = Metric(CoveragePatchApplicabilityOutcome.Measured, "reported-points", 1, 1, 1, 100m);
        using var console = new FakeInMemoryConsole();

        await new ResultCommand("accept", Result("accept", CoverageOperationOutcome.Succeeded, "PatchAccepted",
            Applicability(metric, metric, metric, metric), accepted: true))
        { Request = request, Result = Path.Join(root.Path, "terminal.json") }.ExecuteAsync(console, CancellationToken.None);

        var output = console.ReadOutputString();
        Assert.Contains("accepted generation: independently verified", output, StringComparison.Ordinal);
        Assert.Contains("composed branch: measured 1/1", output, StringComparison.Ordinal);
        Assert.DoesNotContain("trust:", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("capture")]
    [InlineData("collect")]
    [InlineData("compose")]
    [InlineData("accept")]
    [InlineData("verify")]
    public void V3_context_tracks_the_typed_operation_policy_and_optional_exception_type(string operation)
    {
        using var root = TestDirectory.Create();
        var parsed = CoverageFanInRequestReader.Parse(System.Text.Encoding.UTF8.GetBytes(
            Envelope(operation, RequestFor(operation, root.Path, 3), 3)));
        var context = CoverageFanInCliCodec.ContextFor(parsed, typeof(IOException));

        Assert.NotNull(context);
        Assert.Equal("3", context["contractVersion"]);
        Assert.Equal(Profile, context["reportProfileId"]);
        Assert.Equal("IOException", context["exceptionType"]);
        Assert.Equal(new string('a', 40), context["headCommit"]);
        Assert.Equal(operation == "accept", context.ContainsKey("baseCommit"));
        if (operation == "accept") Assert.Equal(new string('c', 40), context["baseCommit"]);
        Assert.Null(CoverageFanInCliCodec.ContextFor(parsed with { ContractVersion = 2 }));
    }

    [Fact]
    public void V3_diagnostics_filter_unsafe_trace_and_allow_valid_identifiers_without_context()
    {
        var diagnostic = CoverageFanInCliCodec.Diagnostic(3, "ASCOVF009", "input", "policy", "Fix.", "invalid-policy",
            trace: ["handled-at:ForgeTrust.AppSurface.Cli.Safe.Type.Run", "ForgeTrust.AppSurface.Cli." + new string('X', 260)]);
        Assert.Null(diagnostic.Context);
        Assert.Equal("handled-at:ForgeTrust.AppSurface.Cli.Safe.Type.Run", Assert.Single(diagnostic.Trace!));
        Assert.Null(CoverageFanInCliCodec.SafeTrace(null));
        Assert.Null(CoverageFanInCliCodec.ContextFor(2));
        Assert.Equal("3", Assert.Single(CoverageFanInCliCodec.ContextFor(3)!).Value);
    }

    [Fact]
    public void V3_canonical_reader_rejects_invalid_utf8_without_exposing_decoder_details()
    {
        byte[] malformed = [(byte)'{', (byte)'"', (byte)'x', (byte)'"', (byte)':', (byte)'"', 0xff, (byte)'"', (byte)'}'];
        var error = Assert.Throws<CoverageFanInCliInputException>(() => CoverageFanInRequestReader.Parse(malformed));
        Assert.Equal("invalid-policy", error.Cause);
        Assert.Equal(2, CoverageFanInRequestReader.TryReadEnvelopeVersion(malformed));
    }

    [Theory]
    [InlineData("raw-null")]
    [InlineData("raw-values")]
    [InlineData("supplement-projects-null")]
    [InlineData("supplement-projects")]
    public void V3_reader_preserves_optional_raw_report_and_supplement_inventories(string scenario)
    {
        using var root = TestDirectory.Create();
        var envelope = JsonNode.Parse(Envelope("capture", CaptureRequest(root.Path, 3), 3))!;
        var request = envelope["request"]!;
        if (scenario.StartsWith("raw", StringComparison.Ordinal))
            request["rawCoveragePaths"] = scenario == "raw-null" ? null : new JsonObject { ["tests/Demo.Tests.csproj"] = "raw.xml" };
        else
            request["policy"]!["supplements"] = new JsonArray(new JsonObject
            {
                ["id"] = "linux",
                ["platform"] = "linux",
                ["filter"] = "Category=Native",
                ["testProjects"] = scenario.EndsWith("null", StringComparison.Ordinal) ? null : new JsonArray("tests/Native.Tests.csproj")
            });
        var parsed = CoverageFanInRequestReader.Parse(System.Text.Encoding.UTF8.GetBytes(envelope.ToJsonString(WireOptions)));
        var capture = Assert.IsType<CoverageCaptureRequest>(parsed.Request);

        if (scenario == "raw-values")
        {
            var mapping = Assert.Single(capture.RawCoveragePaths!);
            Assert.Equal("tests/Demo.Tests.csproj", mapping.Key);
            Assert.Equal("raw.xml", mapping.Value);
        }
        else Assert.Null(capture.RawCoveragePaths);
        if (scenario == "supplement-projects") Assert.Equal("tests/Native.Tests.csproj", Assert.Single(capture.Policy.Supplements[0].TestProjects!));
        if (scenario == "supplement-projects-null") Assert.Null(capture.Policy.Supplements[0].TestProjects);
    }

    [Fact]
    public void Versioned_terminal_codec_rejects_unknown_versions_and_unknown_metric_outcomes()
    {
        var metric = Metric((CoveragePatchApplicabilityOutcome)99, "invalid", null, null, null, null);
        var result = CoverageFanInCliResult.From(Result("verify", CoverageOperationOutcome.Rejected,
            "NotEvaluated", Applicability(metric, metric, metric, metric)), 3);
        var envelope = new CoverageFanInCliEnvelope(4, "verify", "failed", null, null, null, false,
            null, new(false, "notRequested", false, null), []);

        Assert.Throws<InvalidOperationException>(() => CoverageFanInCliCodec.Serialize(envelope, WireOptions));
        Assert.Throws<InvalidOperationException>(() => CoverageFanInCliCodec.Serialize(
            envelope with { SchemaVersion = 3, Result = result }, WireOptions));
    }

    [Theory]
    [InlineData("wrong-kind")]
    [InlineData("blank-key")]
    [InlineData("wrong-value")]
    public void V3_reader_refuses_invalid_raw_report_dictionary_shapes(string scenario)
    {
        using var root = TestDirectory.Create();
        var envelope = JsonNode.Parse(Envelope("capture", CaptureRequest(root.Path, 3), 3))!;
        envelope["request"]!["rawCoveragePaths"] = scenario switch
        {
            "wrong-kind" => new JsonArray(),
            "blank-key" => new JsonObject { [" "] = "report.xml" },
            _ => new JsonObject { ["tests/Demo.Tests.csproj"] = 1 }
        };

        Assert.Throws<CoverageFanInCliInputException>(() => CoverageFanInRequestReader.Parse(
            System.Text.Encoding.UTF8.GetBytes(envelope.ToJsonString(WireOptions))));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"schemaVersion\":3,\"operation\":\"capture\",\"request\":{}")]
    public async Task Invalid_v3_requests_render_a_safe_human_error_without_result_side_effects(string json)
    {
        using var root = TestDirectory.Create();
        using var console = new FakeInMemoryConsole();
        var request = root.WriteFile("request.json", json);
        var resultFile = Path.Join(root.Path, "result.json");

        var exception = await Assert.ThrowsAsync<CommandException>(async () => await new CoverageFanInCaptureCommand
        { Request = request, Result = resultFile }.ExecuteAsync(console, CancellationToken.None));

        Assert.Equal(2, exception.ExitCode);
        Assert.False(File.Exists(resultFile));
        Assert.Contains("invalid input", console.ReadOutputString(), StringComparison.Ordinal);
        Assert.Contains("ASCOVF001", console.ReadErrorString(), StringComparison.Ordinal);
    }

    [Fact]
    public void V3_exception_trace_uses_a_safe_fallback_for_an_unthrown_exception_and_bounds_real_frames()
    {
        var fallback = CoverageFanInCliCodec.TraceFor(new IOException("private-details"));
        Assert.Equal("handled-at:ForgeTrust.AppSurface.Cli.CoverageFanInCommandBase.ExecuteAsync", Assert.Single(fallback));
        var exception = Assert.Throws<IOException>(() => ThrowDeeply(12));

        var frames = CoverageFanInCliCodec.TraceFor(exception);

        Assert.Equal(8, frames.Count);
        Assert.All(frames, frame => Assert.StartsWith("ForgeTrust.AppSurface.Cli.", frame, StringComparison.Ordinal));
        Assert.DoesNotContain("private-details", string.Join('\n', frames), StringComparison.Ordinal);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ThrowDeeply(int remaining)
    {
        if (remaining == 0) throw new IOException("private-details");
        try { ThrowDeeply(remaining - 1); }
        finally { GC.KeepAlive(remaining); }
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

    private static CoveragePatchApplicability CorruptApplicability(CoveragePatchApplicability source, string defect)
    {
        var primaryLine = source.Primary.Line;
        var primaryBranch = source.Primary.Branch;
        var composedLine = source.Composed.Line;
        var composedBranch = source.Composed.Branch;
        switch (defect)
        {
            case "null-primary": return new(null!, source.Composed);
            case "null-composed": return new(source.Primary, null!);
            case "null-primary-line": primaryLine = null!; break;
            case "null-primary-branch": primaryBranch = null!; break;
            case "null-composed-line": composedLine = null!; break;
            case "null-composed-branch": composedBranch = null!; break;
            case "measured-reason": primaryLine = primaryLine with { Reason = "excluded" }; break;
            case "measured-negative-changed": primaryLine = primaryLine with { ChangedLines = -1 }; break;
            case "measured-null-count": primaryLine = primaryLine with { Measurable = null }; break;
            case "measured-zero-denominator": primaryLine = primaryLine with { Measurable = 0 }; break;
            case "measured-negative-covered": primaryLine = primaryLine with { Covered = -1 }; break;
            case "measured-over-covered": primaryLine = primaryLine with { Covered = primaryLine.Measurable + 1 }; break;
            case "measured-null-percent": primaryLine = primaryLine with { Percent = null }; break;
            case "measured-high-percent": primaryLine = primaryLine with { Percent = 100.0001m }; break;
            case "na-reason": primaryLine = Metric(CoveragePatchApplicabilityOutcome.NotApplicable, "forged", 2, 0, 0, null); break;
            case "na-null-changed": primaryLine = Metric(CoveragePatchApplicabilityOutcome.NotApplicable, "no-reported-patch-points", null, 0, 0, null); break;
            case "na-measurable": primaryLine = Metric(CoveragePatchApplicabilityOutcome.NotApplicable, "no-reported-patch-points", 2, null, 0, null); break;
            case "na-covered": primaryLine = Metric(CoveragePatchApplicabilityOutcome.NotApplicable, "no-reported-patch-points", 2, 0, 1, null); break;
            case "na-percent": primaryLine = Metric(CoveragePatchApplicabilityOutcome.NotApplicable, "no-reported-patch-points", 2, 0, 0, 0m); break;
            case "unverified-reason": primaryLine = Metric(CoveragePatchApplicabilityOutcome.Unverified, "unsupported", null, null, null, null); break;
            case "unverified-count": primaryLine = Metric(CoveragePatchApplicabilityOutcome.Unverified, "invalid-evidence", 2, null, null, null); break;
            case "unknown-outcome": primaryLine = Metric((CoveragePatchApplicabilityOutcome)999, "reported-points", 2, 2, 1, 50m); break;
            case "line-measurable-exceeds-changed": primaryLine = primaryLine with { ChangedLines = 1 }; break;
            case "inconsistent-changed-lines": primaryBranch = primaryBranch with { ChangedLines = 3 }; break;
            case "measured-null-changed": primaryLine = primaryLine with { ChangedLines = null }; break;
            case "measured-null-covered": primaryLine = primaryLine with { Covered = null }; break;
            case "measured-negative-denominator": primaryLine = primaryLine with { Measurable = -1 }; break;
            case "measured-low-percent": primaryLine = primaryLine with { Percent = -0.0001m }; break;
            case "na-negative-changed": primaryLine = Metric(CoveragePatchApplicabilityOutcome.NotApplicable, "no-reported-patch-points", -1, 0, 0, null); break;
            case "na-null-covered": primaryLine = Metric(CoveragePatchApplicabilityOutcome.NotApplicable, "no-reported-patch-points", 2, 0, null, null); break;
            case "unverified-measurable": primaryLine = Metric(CoveragePatchApplicabilityOutcome.Unverified, "invalid-evidence", null, 1, null, null); break;
            case "unverified-covered": primaryLine = Metric(CoveragePatchApplicabilityOutcome.Unverified, "invalid-evidence", null, null, 0, null); break;
            case "unverified-percent": primaryLine = Metric(CoveragePatchApplicabilityOutcome.Unverified, "invalid-evidence", null, null, null, 0m); break;
            default: throw new ArgumentOutOfRangeException(nameof(defect));
        }

        return new(new(primaryLine, primaryBranch), new(composedLine, composedBranch));
    }

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

    private sealed class ResultCommand(string operation, CoverageFanInResult result,
        ICoverageFanInResultDeliveryFactory? deliveryFactory = null)
        : CoverageFanInCommandBase(operation, requiresResult: true, deliveryFactory)
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

    private sealed class ThrowOnWriteDeliveryFactory : ICoverageFanInResultDeliveryFactory
    {
        public ICoverageFanInResultDelivery Prepare(string path) => new ThrowOnWriteDelivery();
    }

    private sealed class ThrowOnWriteDelivery : ICoverageFanInResultDelivery
    {
        public bool ResultFileCreated => false;
        public bool? ResultFileCommitted => false;
        public void Write(ReadOnlySpan<byte> document) => throw new IOException("private-destination-details");
        public void Dispose() { }
    }
}
