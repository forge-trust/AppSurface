using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CliFx;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Cli;
using ForgeTrust.AppSurface.Coverage;
using ForgeTrust.AppSurface.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Verifies strict CLI request admission, facade dispatch, output channels, and safe result delivery.</summary>
[Collection("CoverageGate process state")]
public sealed class CoverageFanInCommandTests
{
    private static readonly JsonSerializerOptions WireOptions = CreateWireOptions();
    private static readonly CoverageFanInPolicy ValidPolicy = new(
        new CoverageSourceIdentity(new string('0', 40), new string('0', 40), "10.0.100", "Release"),
        new CoverageRunIdentity("local", "consumer", "run-1", 1),
        ["tests/Demo.Tests.csproj"], [], new CoverageGatePolicy(CoverageGateMode.AggregateOnly));

    [Fact]
    public void Cli_integer_lexical_forms_share_the_lossless_policy_encoding()
    {
        using var root = TestDirectory.Create();
        var baseline = CaptureEnvelope(root.Path);
        var equivalent = baseline.Replace("\"attempt\":1", "\"attempt\":1.0", StringComparison.Ordinal);
        var original = (CoverageCaptureRequest)CoverageFanInRequestReader.Parse(System.Text.Encoding.UTF8.GetBytes(baseline)).Request;
        var decoded = (CoverageCaptureRequest)CoverageFanInRequestReader.Parse(System.Text.Encoding.UTF8.GetBytes(equivalent)).Request;
        Assert.Equal(CoveragePolicyEncoding.Digest(original.Policy), CoveragePolicyEncoding.Digest(decoded.Policy));
        var lossy = baseline.Replace("\"minLine\":95", "\"minLine\":95.00000000000000000000000000001", StringComparison.Ordinal);
        Assert.ThrowsAny<ArgumentException>(() => CoverageFanInRequestReader.Parse(System.Text.Encoding.UTF8.GetBytes(lossy)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_before_dispatch_has_exit_130_and_delivers_only_cancellation_facts(bool json)
    {
        using var root = TestDirectory.Create();
        var requestPath = root.WriteFile("request.json", CaptureEnvelope(root.Path));
        var resultPath = Path.Join(root.Path, "cancelled.json");
        using var console = new FakeInMemoryConsole();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var exception = await Assert.ThrowsAsync<CommandException>(async () =>
            await new CoverageFanInCaptureCommand { Request = requestPath, Result = resultPath, Json = json }
                .ExecuteAsync(console, cancellation.Token));
        Assert.Equal(130, exception.ExitCode);
        using var result = JsonDocument.Parse(File.ReadAllText(resultPath));
        Assert.Equal("cancelled", result.RootElement.GetProperty("status").GetString());
        Assert.True(result.RootElement.GetProperty("effectsKnown").GetBoolean());
        Assert.Equal("ASCOVF004", result.RootElement.GetProperty("diagnostics")[0].GetProperty("code").GetString());
        Assert.False(Directory.Exists(Path.Join(root.Path, "operation-output")));
        Assert.DoesNotContain(root.Path, console.ReadErrorString() + console.ReadOutputString(), StringComparison.Ordinal);
        if (json) Assert.Equal(File.ReadAllText(resultPath).Trim(), console.ReadOutputString().Trim());
        else Assert.Contains("cancelled", console.ReadOutputString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Result_delivery_refuses_a_destination_created_after_preparation_and_preserves_foreign_bytes()
    {
        using var root = TestDirectory.Create();
        var destination = Path.Join(root.Path, "result.json");
        using var delivery = SafeCoverageFanInResultDeliveryFactory.Instance.Prepare(destination);
        File.WriteAllText(destination, "competing-writer");
        Assert.Throws<IOException>(() => delivery.Write("{}"u8));
        Assert.Equal("competing-writer", File.ReadAllText(destination));
        Assert.False(delivery.ResultFileCommitted);
        Assert.True(delivery.ResultFileCreated);
        Assert.Equal([destination], Directory.GetFiles(root.Path));
    }

    [Fact]
    public async Task Human_input_and_operation_failures_are_bounded_and_keep_the_same_nonzero_outcomes()
    {
        using var root = TestDirectory.Create();
        using var missingConsole = new FakeInMemoryConsole();
        var missing = await Assert.ThrowsAsync<CommandException>(async () =>
            await new CoverageFanInCaptureCommand().ExecuteAsync(missingConsole, CancellationToken.None));
        Assert.Equal(2, missing.ExitCode);
        Assert.Contains("ASCOVF001", missingConsole.ReadErrorString() + missingConsole.ReadOutputString());
        var request = root.WriteFile("request.json", CaptureEnvelope(root.Path));
        using var console = new FakeInMemoryConsole();
        var failure = await Assert.ThrowsAsync<CommandException>(async () =>
            await new CoverageFanInCaptureCommand { Request = request, Result = Path.Join(root.Path, "result.json") }
                .ExecuteAsync(console, CancellationToken.None));
        Assert.Equal(1, failure.ExitCode);
        Assert.Contains("rejected", console.ReadOutputString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(root.Path, console.ReadOutputString() + console.ReadErrorString());
    }

    [Theory]
    [InlineData("cancelled", 130)]
    [InlineData("timedOut", 124)]
    [InlineData("failed", 1)]
    public async Task Unexpected_adapter_dispatch_faults_preserve_unknown_effects_and_redact_the_cause(string status, int exitCode)
    {
        using var root = TestDirectory.Create();
        var request = root.WriteFile("request.json", CaptureEnvelope(root.Path));
        using var console = new FakeInMemoryConsole();
        var exception = await Assert.ThrowsAsync<CommandException>(async () =>
            await new FaultingCommand(status) { Request = request, Result = Path.Join(root.Path, "result.json"), Json = true }
                .ExecuteAsync(console, CancellationToken.None));
        Assert.Equal(exitCode, exception.ExitCode);
        using var result = JsonDocument.Parse(console.ReadOutputString());
        Assert.Equal(status, result.RootElement.GetProperty("status").GetString());
        Assert.False(result.RootElement.GetProperty("effectsKnown").GetBoolean());
        Assert.Equal(JsonValueKind.Null, result.RootElement.GetProperty("effects").ValueKind);
        Assert.DoesNotContain("private-exception-sentinel", console.ReadOutputString() + console.ReadErrorString());
        Assert.True(File.Exists(Path.Join(root.Path, "result.json")));
        var diagnostic = result.RootElement.GetProperty("diagnostics")[0];
        Assert.Equal(status switch
        {
            "cancelled" => "ASCOVF004",
            "timedOut" => "ASCOVF005",
            _ => "ASCOVF003",
        }, diagnostic.GetProperty("code").GetString());
        Assert.Equal(status == "failed" ? "execution" : status, diagnostic.GetProperty("stage").GetString());
        Assert.Equal("operation", diagnostic.GetProperty("field").GetString());
    }

    [Fact]
    public async Task Strict_parser_rejects_unknown_duplicate_missing_wrong_type_and_case_mismatches_before_mutation()
    {
        using var root = TestDirectory.Create();
        var baseline = CaptureEnvelope(root.Path);
        var cases = new[]
        {
            baseline.Replace("\"producerId\":\"primary\"", "\"producerId\":\"primary\",\"unexpected\":true", StringComparison.Ordinal),
            baseline.Replace("\"producerId\":\"primary\"", "\"producerId\":\"primary\",\"producerId\":\"primary\"", StringComparison.Ordinal),
            baseline.Replace("\"producerId\":\"primary\",", "", StringComparison.Ordinal),
            baseline.Replace("\"attempt\":1", "\"attempt\":\"1\"", StringComparison.Ordinal),
            baseline.Replace("\"mode\":\"aggregateOnly\"", "\"mode\":\"AggregateOnly\"", StringComparison.Ordinal),
            baseline.Replace("\"schemaVersion\":2", "\"schemaVersion\":2,\"schemaVersion\":2", StringComparison.Ordinal),
        };

        for (var index = 0; index < cases.Length; index++)
        {
            var requestPath = root.WriteFile($"invalid-{index}.json", cases[index]);
            var operationOutput = Path.Join(root.Path, $"operation-output-{index}");
            var resultPath = Path.Join(root.Path, $"result-{index}.json");
            using var console = new FakeInMemoryConsole();

            var exception = await Assert.ThrowsAsync<CommandException>(async () =>
                await new CoverageFanInCaptureCommand { Request = requestPath, Result = resultPath, Json = true }
                    .ExecuteAsync(console, CancellationToken.None));

            Assert.Equal(2, exception.ExitCode);
            using var output = JsonDocument.Parse(console.ReadOutputString());
            Assert.Equal("invalidInput", output.RootElement.GetProperty("status").GetString());
            Assert.False(File.Exists(resultPath));
            Assert.False(Directory.Exists(operationOutput));
            Assert.DoesNotContain(root.Path, console.ReadOutputString() + console.ReadErrorString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Semantically_invalid_policy_budgets_and_nested_fields_fail_before_result_preparation()
    {
        using var root = TestDirectory.Create();
        var invalidPolicyMutations = new Action<JsonNode>[]
        {
            request => request["policy"]!["limits"] = new JsonObject { ["totalBytes"] = 0 },
            request => request["policy"]!["limits"] = new JsonObject { ["identitySeconds"] = 301 },
            request => request["policy"]!["gate"]!["minLine"] = 100.0000001m,
            request => request["policy"]!["instrumentation"] = new JsonObject { ["driver"] = "private-input-sentinel" },
        };

        for (var index = 0; index < invalidPolicyMutations.Length; index++)
        {
            var node = JsonNode.Parse(CaptureEnvelope(root.Path))!;
            invalidPolicyMutations[index](node["request"]!);
            var requestPath = root.WriteFile($"invalid-policy-{index}.json", node.ToJsonString(WireOptions));
            var resultPath = Path.Join(root.Path, $"result-{index}.json");
            var factory = new CountingDeliveryFactory();
            using var console = new FakeInMemoryConsole();

            var exception = await Assert.ThrowsAsync<CommandException>(async () =>
                await new CoverageFanInCaptureCommand(factory)
                {
                    Request = requestPath,
                    Result = resultPath,
                    Json = true,
                }.ExecuteAsync(console, CancellationToken.None));

            Assert.Equal(2, exception.ExitCode);
            Assert.Equal(0, factory.PrepareCalls);
            using var output = JsonDocument.Parse(console.ReadOutputString());
            Assert.Equal("invalidInput", output.RootElement.GetProperty("status").GetString());
            Assert.False(File.Exists(resultPath));
            Assert.False(Directory.Exists(Path.Join(root.Path, "operation-output")));
            Assert.DoesNotContain(root.Path, console.ReadOutputString() + console.ReadErrorString(), StringComparison.Ordinal);
            Assert.DoesNotContain("private-input-sentinel", console.ReadOutputString() + console.ReadErrorString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AppSurface_command_discovery_exposes_all_four_fan_in_help_contracts()
    {
        var originalExitCode = Environment.ExitCode;
        try
        {
            foreach (var operation in new[] { "capture", "compose", "accept", "verify" })
            {
                using var console = new FakeInMemoryConsole();
                Environment.ExitCode = 0;

                await AppSurfaceCliApp.RunAsync(["coverage", "fan-in", operation, "--help"], options =>
                    options.CustomRegistrations.Add(services => services.AddSingleton<IConsole>(console)));

                var help = console.ReadOutputString() + console.ReadErrorString();
                Assert.Equal(0, Environment.ExitCode);
                Assert.Contains($"coverage fan-in {operation}", help, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("--request", help, StringComparison.Ordinal);
                Assert.Contains("--result", help, StringComparison.Ordinal);
                Assert.Contains("--json", help, StringComparison.Ordinal);
                Assert.Contains("schemaVersion:2", help, StringComparison.Ordinal);
                if (operation == "capture")
                {
                    Assert.Contains("--collect", help, StringComparison.Ordinal);
                    Assert.Contains("operation=collect", help, StringComparison.Ordinal);
                    Assert.Contains("operation=capture", help, StringComparison.Ordinal);
                }
                else
                {
                    Assert.DoesNotContain("--collect", help, StringComparison.Ordinal);
                }
            }
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
        }
    }

    [Fact]
    public void StrictEnvelopeAndOperationSelectorsRefuseInvalidShapesBeforeAnyOperation()
    {
        using var root = TestDirectory.Create();
        var baseline = CaptureEnvelope(root.Path);
        foreach (var json in new[]
        {
            "[]", "null", "42",
            baseline.Replace("\"schemaVersion\":2", "\"schemaVersion\":1", StringComparison.Ordinal),
            baseline.Replace("\"schemaVersion\":2", "\"schemaVersion\":2.5", StringComparison.Ordinal),
            baseline.Replace("\"operation\":\"capture\"", "\"operation\":3", StringComparison.Ordinal),
            baseline.Replace("\"operation\":\"capture\"", "\"operation\":\"unknown\"", StringComparison.Ordinal),
            baseline.Replace("\"repositoryRoot\":", "\"unknown\":true,\"repositoryRoot\":", StringComparison.Ordinal),
            baseline.Replace("\"producerId\":\"primary\"", "\"producerId\":42", StringComparison.Ordinal)
        })
            Assert.ThrowsAny<Exception>(() => CoverageFanInRequestReader.Parse(System.Text.Encoding.UTF8.GetBytes(json)));

        var policy = ValidPolicy with { Supplements = [new("native", "Linux", "Category=fast")] };
        var validImport = new CoverageCaptureRequest(policy, root.Path, "fresh", "native", ImportDirectory: "download",
            OriginalSourceRoot: "/producer");
        CoverageFanInRequestReader.ValidateOperationRequest(new("capture", validImport));
        foreach (var invalid in new[]
        {
            validImport with { ProducerId = "undeclared" }, validImport with { OriginalSourceRoot = null },
            validImport with { CoveragePath = "coverage.xml" }, validImport with { TimingsPath = "timings.json" },
            validImport with { RawCoveragePath = "raw.xml" },
            validImport with { ImportDirectory = null, CoveragePath = "coverage.xml", RawCoveragePath = "raw.xml", OriginalSourceRoot = "/unexpected" },
            validImport with { ImportDirectory = null, CoveragePath = "coverage.xml", RawCoveragePath = null, OriginalSourceRoot = null },
            validImport with { ImportDirectory = null, CoveragePath = "coverage.xml", RawCoveragePath = "raw.xml", OriginalSourceRoot = null, TimingsPath = "unexpected" }
        })
            Assert.Throws<CoverageFanInCliInputException>(() => CoverageFanInRequestReader.ValidateOperationRequest(new("capture", invalid)));
        var collect = new CoverageCollectRequest(ValidPolicy, root.Path, "out", "collection", "primary", 60);
        foreach (var invalid in new[] { collect with { CollectionDirectory = " " }, collect with { DeadlineSeconds = 0 },
            collect with { DeadlineSeconds = 86401 }, collect with { Parallelism = 0 } })
            Assert.Throws<CoverageFanInCliInputException>(() => CoverageFanInRequestReader.ValidateOperationRequest(new("collect", invalid)));
        Assert.Throws<CoverageFanInCliInputException>(() => CoverageFanInRequestReader.ValidateOperationRequest(
            new("accept", new CoverageAcceptRequest(ValidPolicy, root.Path, "primary", [], "", "out"))));
        Assert.Throws<CoverageFanInCliInputException>(() => CoverageFanInRequestReader.ValidateOperationRequest(
            new("verify", new CoverageVerifyRequest(ValidPolicy, root.Path, "primary", [], "composed", ""))));
        Assert.Empty(Directory.EnumerateFileSystemEntries(root.Path));
    }

    [Theory]
    [InlineData(CoverageOperationOutcome.TimedOut, "timedOut", 124)]
    [InlineData(CoverageOperationOutcome.Cancelled, "cancelled", 130)]
    public async Task TypedOperationFailuresPreserveOutcomeExitCodeAndDeliverKnownEffects(
        CoverageOperationOutcome outcome, string status, int exitCode)
    {
        using var root = TestDirectory.Create();
        var request = root.WriteFile("request.json", CaptureEnvelope(root.Path));
        var result = new CoverageFanInResult("capture", outcome, "current", null,
            new CoverageOperationEffects(false, false, true, false, null), [], []);
        using var console = new FakeInMemoryConsole();
        var error = await Assert.ThrowsAsync<CommandException>(async () =>
            await new ResultCommand("capture", result, SafeCoverageFanInResultDeliveryFactory.Instance)
            { Request = request, Result = TestPathUtils.PathUnder(root.Path, "terminal.json"), Json = true }
                .ExecuteAsync(console, CancellationToken.None));
        Assert.Equal(exitCode, error.ExitCode);
        using var terminal = JsonDocument.Parse(console.ReadOutputString());
        Assert.Equal(status, terminal.RootElement.GetProperty("status").GetString());
        Assert.True(terminal.RootElement.GetProperty("effectsKnown").GetBoolean());
        Assert.False(terminal.RootElement.GetProperty("effects").GetProperty("generationCommitted").GetBoolean());
    }

    [Fact]
    public async Task CollectBooleanOptionsAcceptExplicitTrueAndFalseAndRejectOtherTypes()
    {
        using var root = TestDirectory.Create();
        foreach (var value in new[] { "true", "false" })
        {
            var node = JsonNode.Parse(Envelope("collect", CollectRequest(root.Path)))!;
            node["request"]!["noRestore"] = JsonNode.Parse(value);
            node["request"]!["noBuild"] = JsonNode.Parse(value);
            var parsed = CoverageFanInRequestReader.Parse(System.Text.Encoding.UTF8.GetBytes(node.ToJsonString()));
            Assert.Equal(value == "true", ((CoverageCollectRequest)parsed.Request).NoRestore);
            Assert.Equal(value == "true", ((CoverageCollectRequest)parsed.Request).NoBuild);
        }
        var malformed = JsonNode.Parse(Envelope("collect", CollectRequest(root.Path)))!;
        malformed["request"]!["noRestore"] = "true";
        var request = root.WriteFile("malformed.json", malformed.ToJsonString());
        using var console = new FakeInMemoryConsole();
        var exception = await Assert.ThrowsAsync<CommandException>(async () =>
            await new CoverageFanInCaptureCommand
            {
                Collect = true,
                Request = request,
                Result = TestPathUtils.PathUnder(root.Path, "terminal.json"),
                Json = true
            }.ExecuteAsync(console, CancellationToken.None));
        Assert.Equal(2, exception.ExitCode);
        Assert.False(File.Exists(TestPathUtils.PathUnder(root.Path, "terminal.json")));
    }

    [Fact]
    public async Task Acceptance_terminal_projection_delivers_every_known_artifact_role_without_host_paths()
    {
        using var root = TestDirectory.Create();
        var requestPath = root.WriteFile("request.json", Envelope("accept", RequestFor("accept", root.Path)));
        var roles = new Dictionary<string, string>
        {
            ["acceptance.json"] = "acceptance",
            ["collector.cobertura.xml"] = "collectorCoverage",
            ["composed-gate.json"] = "composedGate",
            ["composed-gate.md"] = "composedGateSummary",
            ["coverage.cobertura.xml"] = "coverage",
            ["diff.patch"] = "patchDiff",
            ["generation.json"] = "generation",
            ["manifest.json"] = "manifest",
            ["primary-gate.json"] = "primaryGate",
            ["primary-gate.md"] = "primaryGateSummary",
            ["primary-paths.cobertura.xml"] = "primaryCoverage",
            ["rejection.json"] = "rejection",
            ["timings.json"] = "timings",
            ["verification.json"] = "verification",
            ["unknown-private-role"] = "artifact"
        };
        var result = new CoverageFanInResult("accept", CoverageOperationOutcome.Succeeded, "current-operation", null,
            new CoverageOperationEffects(true, true, true, false, "current-generation"),
            roles.Keys.Select(role => new CoverageArtifactReference(role, "/private/secret/" + role, 1, new string('a', 64))).ToArray(),
            [], accepted: true, verified: true, patchOutcome: "PatchAccepted");
        using var console = new FakeInMemoryConsole();
        var destination = TestPathUtils.PathUnder(root.Path, "terminal.json");

        await new ResultCommand("accept", result, SafeCoverageFanInResultDeliveryFactory.Instance)
        { Request = requestPath, Result = destination, Json = true }.ExecuteAsync(console, CancellationToken.None);

        using var terminal = JsonDocument.Parse(console.ReadOutputString());
        Assert.Equal("succeeded", terminal.RootElement.GetProperty("status").GetString());
        var projected = terminal.RootElement.GetProperty("result");
        Assert.True(projected.GetProperty("accepted").GetBoolean());
        Assert.True(projected.GetProperty("verified").GetBoolean());
        Assert.Equal("PatchAccepted", projected.GetProperty("patchOutcome").GetString());
        Assert.Equal(roles.Values, projected.GetProperty("artifacts").EnumerateArray()
            .Select(artifact => artifact.GetProperty("role").GetString()));
        Assert.DoesNotContain("/private/secret", File.ReadAllText(destination), StringComparison.Ordinal);
        Assert.Equal(console.ReadOutputString().Trim(), File.ReadAllText(destination).Trim());
    }

    [Fact]
    public async Task Current_committed_capture_json_redacts_absolute_artifact_paths()
    {
        using var fixture = await CurrentCaptureFixture.Create();
        var requestPath = fixture.WriteRequest("capture-request.json");
        var resultPath = fixture.ResultPath("capture-result.json");
        using var console = new FakeInMemoryConsole();
        var originalExitCode = Environment.ExitCode;

        try
        {
            Environment.ExitCode = 0;
            await AppSurfaceCliApp.RunAsync(
                ["coverage", "fan-in", "capture", "--request", requestPath, "--result", resultPath, "--json"],
                options => options.CustomRegistrations.Add(services => services.AddSingleton<IConsole>(console)));

            var stdout = console.ReadOutputString();
            using var terminal = JsonDocument.Parse(stdout);
            var document = terminal.RootElement;
            Assert.Equal(0, Environment.ExitCode);
            Assert.Equal("succeeded", document.GetProperty("status").GetString());
            Assert.Equal("succeeded", document.GetProperty("operationOutcome").GetString());
            Assert.True(document.GetProperty("effects").GetProperty("generationCommitted").GetBoolean());
            var artifacts = document.GetProperty("result").GetProperty("artifacts").EnumerateArray().ToArray();
            Assert.NotEmpty(artifacts);
            Assert.All(artifacts, artifact =>
            {
                Assert.False(artifact.TryGetProperty("path", out _));
                Assert.Contains(artifact.GetProperty("role").GetString(), new[]
                {
                    "acceptance", "collectorCoverage", "composedGate", "coverage", "generation", "manifest",
                    "patchDiff", "primaryCoverage", "primaryGate", "rejection", "timings", "verification", "artifact",
                });
                Assert.True(artifact.GetProperty("length").GetInt64() > 0);
                Assert.Matches("^[0-9a-f]{64}$", artifact.GetProperty("sha256").GetString());
            });
            Assert.DoesNotContain(fixture.SensitiveAbsolutePath, stdout, StringComparison.Ordinal);
            Assert.DoesNotContain(fixture.SensitiveAbsolutePath, File.ReadAllText(resultPath), StringComparison.Ordinal);
            Assert.Equal(stdout.Trim(), File.ReadAllText(resultPath).Trim());
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
        }
    }

    [Fact]
    public async Task Current_committed_capture_human_output_reports_success_and_commit_state()
    {
        using var fixture = await CurrentCaptureFixture.Create();
        var requestPath = fixture.WriteRequest("human-capture-request.json");
        var resultPath = fixture.ResultPath("human-capture-result.json");
        using var console = new FakeInMemoryConsole();
        var originalExitCode = Environment.ExitCode;

        try
        {
            Environment.ExitCode = 0;
            await AppSurfaceCliApp.RunAsync(
                ["coverage", "fan-in", "capture", "--request", requestPath, "--result", resultPath],
                options => options.CustomRegistrations.Add(services => services.AddSingleton<IConsole>(console)));

            var output = console.ReadOutputString();
            Assert.Equal(0, Environment.ExitCode);
            Assert.Contains("coverage fan-in capture: succeeded", output, StringComparison.Ordinal);
            Assert.Contains("operation outcome: succeeded; committed generation: yes", output, StringComparison.Ordinal);
            Assert.DoesNotContain("{", output, StringComparison.Ordinal);
            Assert.DoesNotContain(fixture.SensitiveAbsolutePath, output + console.ReadErrorString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
        }
    }

    [Fact]
    public async Task Human_delivery_failure_reports_verified_acceptance_and_reconciliation_guidance()
    {
        using var root = TestDirectory.Create();
        var requestPath = root.WriteFile("request.json", Envelope("accept", RequestFor("accept", root.Path)));
        var effects = new CoverageOperationEffects(true, true, true, false, "generation-1");
        var result = new CoverageFanInResult("accept", CoverageOperationOutcome.Succeeded, "operation-1", null,
            effects, [], [], accepted: true, verified: true);
        using var console = new FakeInMemoryConsole();

        var exception = await Assert.ThrowsAsync<CommandException>(async () =>
            await new ResultCommand("accept", result, new CommittedThenFailedDeliveryFactory())
            {
                Request = requestPath,
                Result = TestPathUtils.PathUnder(root.Path, "result.json"),
            }.ExecuteAsync(console, CancellationToken.None));

        Assert.Equal(1, exception.ExitCode);
        Assert.Contains("coverage fan-in accept: deliveryFailed", console.ReadOutputString(), StringComparison.Ordinal);
        Assert.Contains("operation outcome: succeeded; committed generation: yes", console.ReadOutputString(), StringComparison.Ordinal);
        Assert.Contains("accepted generation: independently verified", console.ReadOutputString(), StringComparison.Ordinal);
        Assert.Contains("result delivery failed; reconcile the operation before retrying", console.ReadErrorString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Incomplete_operation_keeps_its_documented_terminal_status_and_failure_exit()
    {
        using var root = TestDirectory.Create();
        var requestPath = root.WriteFile("request.json", CaptureEnvelope(root.Path));
        var effects = new CoverageOperationEffects(true, false, true, false, null);
        var result = new CoverageFanInResult("capture", CoverageOperationOutcome.Incomplete, "operation-1", null,
            effects, [], []);
        using var console = new FakeInMemoryConsole();

        var exception = await Assert.ThrowsAsync<CommandException>(async () =>
            await new ResultCommand("capture", result, SafeCoverageFanInResultDeliveryFactory.Instance)
            {
                Request = requestPath,
                Result = TestPathUtils.PathUnder(root.Path, "incomplete-result.json"),
                Json = true,
            }.ExecuteAsync(console, CancellationToken.None));

        Assert.Equal(1, exception.ExitCode);
        using var terminal = JsonDocument.Parse(console.ReadOutputString());
        Assert.Equal("incomplete", terminal.RootElement.GetProperty("status").GetString());
        Assert.Equal("incomplete", terminal.RootElement.GetProperty("operationOutcome").GetString());
        Assert.False(terminal.RootElement.GetProperty("effects").GetProperty("generationCommitted").GetBoolean());
    }

    [Fact]
    public async Task Delivery_failure_recompacts_a_terminal_document_that_crosses_the_limit()
    {
        using var root = TestDirectory.Create();
        var requestPath = root.WriteFile("request.json", CaptureEnvelope(root.Path));
        const int maximumBytes = 1024 * 1024;
        var effects = new CoverageOperationEffects(true, true, true, false, "generation-1");
        // Artifact roles are normalized by the terminal projection, so size the retained policy digest instead.
        var emptyArtifact = new CoverageArtifactReference("coverage.cobertura.xml", "/private/secret/coverage.xml", 1,
            new string('a', 64));
        var smallResult = new CoverageFanInResult("capture", CoverageOperationOutcome.Succeeded, "operation-1", string.Empty,
            effects, [emptyArtifact], []);
        var initial = new CoverageFanInCliEnvelope(2, "capture", "succeeded", "operation-1",
            CoverageOperationOutcome.Succeeded, effects, true, CoverageFanInCliResult.From(smallResult),
            new(true, "committed", true, true), []);
        var deliveryFailure = new CoverageFanInCliEnvelope(2, "capture", "deliveryFailed", "operation-1",
            CoverageOperationOutcome.Succeeded, effects, true, CoverageFanInCliResult.From(smallResult),
            new(true, "failed", true, true),
            [new("ASCOVF002", "delivery", "result",
                "Reconcile the operation using its operation ID and committed-generation status before retrying result delivery.")]);
        var serializer = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
        serializer.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        var initialLength = JsonSerializer.SerializeToUtf8Bytes(initial, serializer).Length;
        var failureLength = JsonSerializer.SerializeToUtf8Bytes(deliveryFailure, serializer).Length;
        var paddingLength = maximumBytes - initialLength - 1;
        Assert.True(paddingLength > 0);
        Assert.True(failureLength + paddingLength > maximumBytes);

        var result = new CoverageFanInResult("capture", CoverageOperationOutcome.Succeeded, "operation-1",
            new string('a', paddingLength), effects, [emptyArtifact], []);
        using var console = new FakeInMemoryConsole();

        var exception = await Assert.ThrowsAsync<CommandException>(async () =>
            await new ResultCommand("capture", result, new CommittedThenFailedDeliveryFactory())
            {
                Request = requestPath,
                Result = TestPathUtils.PathUnder(root.Path, "result.json"),
                Json = true,
            }.ExecuteAsync(console, CancellationToken.None));

        Assert.Equal(1, exception.ExitCode);
        using var terminal = JsonDocument.Parse(console.ReadOutputString());
        Assert.Equal("deliveryFailed", terminal.RootElement.GetProperty("status").GetString());
        Assert.Equal("succeeded", terminal.RootElement.GetProperty("operationOutcome").GetString());
        Assert.Equal("failed", terminal.RootElement.GetProperty("delivery").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, terminal.RootElement.GetProperty("result").ValueKind);
        Assert.Equal("ASCOVF002", terminal.RootElement.GetProperty("diagnostics")[0].GetProperty("code").GetString());
        Assert.DoesNotContain(new string('a', 128), console.ReadOutputString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Oversized_operation_result_is_compacted_with_incomplete_status_and_ASCOVF006()
    {
        using var root = TestDirectory.Create();
        var requestPath = root.WriteFile("request.json", CaptureEnvelope(root.Path));
        var effects = new CoverageOperationEffects(true, true, true, false, "generation-1");
        var result = new CoverageFanInResult("capture", CoverageOperationOutcome.Succeeded, "operation-1",
            new string('a', 1024 * 1024), effects,
            [new CoverageArtifactReference("coverage.cobertura.xml", "/private/secret/coverage.xml", 1,
                new string('b', 64))], []);
        using var console = new FakeInMemoryConsole();

        var exception = await Assert.ThrowsAsync<CommandException>(async () =>
            await new ResultCommand("capture", result, SafeCoverageFanInResultDeliveryFactory.Instance)
            {
                Request = requestPath,
                Result = TestPathUtils.PathUnder(root.Path, "oversized-result.json"),
                Json = true,
            }.ExecuteAsync(console, CancellationToken.None));

        Assert.Equal(1, exception.ExitCode);
        using var terminal = JsonDocument.Parse(console.ReadOutputString());
        Assert.Equal("incomplete", terminal.RootElement.GetProperty("status").GetString());
        Assert.Equal("succeeded", terminal.RootElement.GetProperty("operationOutcome").GetString());
        Assert.Equal(JsonValueKind.Null, terminal.RootElement.GetProperty("result").ValueKind);
        Assert.Equal("ASCOVF006", terminal.RootElement.GetProperty("diagnostics")[0].GetProperty("code").GetString());
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(console.ReadOutputString()) <= 1024 * 1024 + Environment.NewLine.Length);
    }

    [Fact]
    public async Task Current_committed_delivery_failure_redacts_artifacts_and_preserves_commit_facts()
    {
        using var fixture = await CurrentCaptureFixture.Create();
        var requestPath = fixture.WriteRequest("delivery-failure-request.json", "delivery-failure-output");
        using var console = new FakeInMemoryConsole();

        var exception = await Assert.ThrowsAsync<CommandException>(async () =>
            await new CoverageFanInCaptureCommand(new CommittedThenFailedDeliveryFactory())
            {
                Request = requestPath,
                Result = fixture.ResultPath("delivery-failure-result.json"),
                Json = true,
            }.ExecuteAsync(console, CancellationToken.None));

        Assert.Equal(1, exception.ExitCode);
        using var terminal = JsonDocument.Parse(console.ReadOutputString());
        var document = terminal.RootElement;
        Assert.Equal("deliveryFailed", document.GetProperty("status").GetString());
        Assert.Equal("succeeded", document.GetProperty("operationOutcome").GetString());
        Assert.True(document.GetProperty("effects").GetProperty("generationCommitted").GetBoolean());
        Assert.Equal("failed", document.GetProperty("delivery").GetProperty("status").GetString());
        Assert.True(document.GetProperty("delivery").GetProperty("resultFileCreated").GetBoolean());
        Assert.True(document.GetProperty("delivery").GetProperty("resultFileCommitted").GetBoolean());
        Assert.NotEmpty(document.GetProperty("result").GetProperty("artifacts").EnumerateArray());
        Assert.DoesNotContain(fixture.SensitiveAbsolutePath,
            console.ReadOutputString() + console.ReadErrorString(), StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-EXCEPTION-TEXT",
            console.ReadOutputString() + console.ReadErrorString(), StringComparison.Ordinal);
        Assert.DoesNotContain("System.IO.IOException",
            console.ReadOutputString() + console.ReadErrorString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Oversized_and_overdepth_requests_are_rejected_before_fan_in_or_result_creation()
    {
        using var root = TestDirectory.Create();
        var deep = "{\"schemaVersion\":2,\"operation\":\"capture\",\"request\":"
            + string.Concat(Enumerable.Repeat("[", 70)) + "0" + string.Concat(Enumerable.Repeat("]", 70)) + "}";
        var requests = new[]
        {
            root.WriteFile("oversized.json", new string(' ', 1024 * 1024 + 1)),
            root.WriteFile("deep.json", deep),
        };

        for (var index = 0; index < requests.Length; index++)
        {
            var resultPath = Path.Join(root.Path, $"result-{index}.json");
            using var console = new FakeInMemoryConsole();
            var exception = await Assert.ThrowsAsync<CommandException>(async () =>
                await new CoverageFanInCaptureCommand { Request = requests[index], Result = resultPath, Json = true }
                    .ExecuteAsync(console, CancellationToken.None));

            Assert.Equal(2, exception.ExitCode);
            using var output = JsonDocument.Parse(console.ReadOutputString());
            Assert.Equal("invalidInput", output.RootElement.GetProperty("status").GetString());
            Assert.False(File.Exists(resultPath));
        }
    }

    [Theory]
    [InlineData("capture", false, "capture", true)]
    [InlineData("collect", false, "capture", false)]
    [InlineData("capture", true, "capture", false)]
    [InlineData("collect", true, "collect", true)]
    public async Task Collect_selector_must_match_the_envelope_operation(string envelopeOperation,
        bool collectOption, string commandOperation, bool expectedSuccess)
    {
        using var root = TestDirectory.Create();
        var request = envelopeOperation == "collect" ? CollectRequest(root.Path) : CaptureRequest(root.Path);
        var requestPath = root.WriteFile("request.json", Envelope(envelopeOperation, request));
        var resultPath = Path.Join(root.Path, "result.json");
        using var console = new FakeInMemoryConsole();
        var command = new CoverageFanInCaptureCommand { Request = requestPath, Result = resultPath, Collect = collectOption, Json = true };

        if (!expectedSuccess)
        {
            var exception = await Assert.ThrowsAsync<CommandException>(async () => await command.ExecuteAsync(console, CancellationToken.None));
            Assert.Equal(2, exception.ExitCode);
            Assert.False(File.Exists(resultPath));
            return;
        }

        var execution = await Assert.ThrowsAsync<CommandException>(async () => await command.ExecuteAsync(console, CancellationToken.None));
        Assert.Equal(1, execution.ExitCode);
        using var output = JsonDocument.Parse(console.ReadOutputString());
        Assert.Equal(commandOperation, output.RootElement.GetProperty("operation").GetString());
        Assert.True(File.Exists(resultPath));
    }

    [Fact]
    public async Task Capture_rejects_null_or_missing_selector_paths_before_identity_io()
    {
        using var root = TestDirectory.Create();
        var node = JsonNode.Parse(CaptureEnvelope(root.Path))!.AsObject();
        node["request"]!["coveragePath"] = null;
        var requestPath = root.WriteFile("request.json", node.ToJsonString(WireOptions));
        var resultPath = Path.Join(root.Path, "result.json");
        using var console = new FakeInMemoryConsole();

        var exception = await Assert.ThrowsAsync<CommandException>(async () =>
            await new CoverageFanInCaptureCommand { Request = requestPath, Result = resultPath, Json = true }
                .ExecuteAsync(console, CancellationToken.None));

        Assert.Equal(2, exception.ExitCode);
        using var output = JsonDocument.Parse(console.ReadOutputString());
        Assert.Equal("invalidInput", output.RootElement.GetProperty("status").GetString());
        Assert.False(File.Exists(resultPath));
        Assert.DoesNotContain(root.Path, console.ReadOutputString() + console.ReadErrorString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Writing_command_requires_a_result_path_before_dispatch()
    {
        using var root = TestDirectory.Create();
        var requestPath = root.WriteFile("request.json", CaptureEnvelope(root.Path));
        using var console = new FakeInMemoryConsole();

        var exception = await Assert.ThrowsAsync<CommandException>(async () =>
            await new FaultingCommand("failed") { Request = requestPath, Json = true }
                .ExecuteAsync(console, CancellationToken.None));

        Assert.Equal(2, exception.ExitCode);
        using var terminal = JsonDocument.Parse(console.ReadOutputString());
        Assert.Equal("invalidInput", terminal.RootElement.GetProperty("status").GetString());
        Assert.Equal("notStarted", terminal.RootElement.GetProperty("delivery").GetProperty("status").GetString());
        Assert.DoesNotContain("private-exception-sentinel", console.ReadOutputString() + console.ReadErrorString());
    }

    [Fact]
    public async Task Strict_validation_rejects_null_nonnullable_fields_and_undeclared_capture_inventory()
    {
        using var root = TestDirectory.Create();
        var cases = new (string Operation, Action<JsonNode> Mutate)[]
        {
            ("collect", request => request["parallelism"] = null),
            ("compose", request => request["supplementaryCaptureDirectories"] = new JsonArray
            {
                JsonValue.Create(TestPathUtils.PathUnder(root.Path, "undeclared-capture")),
            }),
        };

        for (var index = 0; index < cases.Length; index++)
        {
            var request = JsonNode.Parse(Envelope(cases[index].Operation, RequestFor(cases[index].Operation, root.Path)))!;
            cases[index].Mutate(request["request"]!);
            var requestPath = root.WriteFile($"invalid-{index}.json", request.ToJsonString(WireOptions));
            var resultPath = TestPathUtils.PathUnder(root.Path, $"result-{index}.json");
            using var console = new FakeInMemoryConsole();

            var exception = await Assert.ThrowsAsync<CommandException>(async () =>
                await CommandFor(cases[index].Operation, requestPath, resultPath)
                    .ExecuteAsync(console, CancellationToken.None));

            Assert.Equal(2, exception.ExitCode);
            using var output = JsonDocument.Parse(console.ReadOutputString());
            Assert.Equal("invalidInput", output.RootElement.GetProperty("status").GetString());
            Assert.False(File.Exists(resultPath));
        }
    }

    [Fact]
    public async Task Result_destination_is_create_only_and_existing_content_is_preserved()
    {
        using var root = TestDirectory.Create();
        var requestPath = root.WriteFile("request.json", CaptureEnvelope(root.Path));
        var resultPath = root.WriteFile("already-there.json", "keep-this-result");
        using var console = new FakeInMemoryConsole();

        var exception = await Assert.ThrowsAsync<CommandException>(async () =>
            await new CoverageFanInCaptureCommand { Request = requestPath, Result = resultPath, Json = true }
                .ExecuteAsync(console, CancellationToken.None));

        Assert.Equal(2, exception.ExitCode);
        Assert.Equal("keep-this-result", File.ReadAllText(resultPath));
        Assert.DoesNotContain(root.Path, console.ReadOutputString() + console.ReadErrorString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Result_destination_rejects_linked_ancestor_before_operation_dispatch()
    {
        using var root = TestDirectory.Create();
        var real = root.CreateDirectory("real");
        var linked = Path.Join(root.Path, "linked");
        TestFileSystem.CreateDirectoryLinkOrSkip(linked, real);
        var requestPath = root.WriteFile("request.json", CaptureEnvelope(root.Path));
        using var console = new FakeInMemoryConsole();

        var exception = await Assert.ThrowsAsync<CommandException>(async () =>
            await new CoverageFanInCaptureCommand
            {
                Request = requestPath,
                Result = Path.Join(linked, "result.json"),
                Json = true,
            }.ExecuteAsync(console, CancellationToken.None));

        Assert.Equal(2, exception.ExitCode);
        Assert.Empty(Directory.EnumerateFileSystemEntries(real));
        Assert.DoesNotContain(real, console.ReadOutputString() + console.ReadErrorString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("capture")]
    [InlineData("collect")]
    [InlineData("compose")]
    [InlineData("accept")]
    [InlineData("verify")]
    public async Task Commands_dispatch_to_the_matching_public_facade_and_deliver_one_terminal_document(string operation)
    {
        using var root = TestDirectory.Create();
        var request = RequestFor(operation, root.Path);
        var requestPath = root.WriteFile("request.json", Envelope(operation, request));
        var resultPath = operation == "verify" ? null : Path.Join(root.Path, "terminal.json");
        using var console = new FakeInMemoryConsole();
        var command = CommandFor(operation, requestPath, resultPath);

        var exception = await Assert.ThrowsAsync<CommandException>(async () => await command.ExecuteAsync(console, CancellationToken.None));

        Assert.Equal(1, exception.ExitCode);
        var stdout = console.ReadOutputString();
        using var terminal = JsonDocument.Parse(stdout);
        Assert.Equal(2, terminal.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(operation, terminal.RootElement.GetProperty("operation").GetString());
        Assert.Equal("rejected", terminal.RootElement.GetProperty("status").GetString());
        Assert.Equal("rejected", terminal.RootElement.GetProperty("operationOutcome").GetString());
        Assert.True(terminal.RootElement.GetProperty("effectsKnown").GetBoolean());
        Assert.False(terminal.RootElement.GetProperty("effects").GetProperty("generationCommitted").GetBoolean());
        Assert.Contains("ASCOV302", console.ReadErrorString(), StringComparison.Ordinal);
        Assert.DoesNotContain("/private/secret", stdout + console.ReadErrorString(), StringComparison.Ordinal);
        if (resultPath is not null)
        {
            Assert.True(File.Exists(resultPath));
            using var delivered = JsonDocument.Parse(File.ReadAllText(resultPath));
            Assert.Equal("committed", delivered.RootElement.GetProperty("delivery").GetProperty("status").GetString());
            Assert.True(delivered.RootElement.GetProperty("delivery").GetProperty("resultFileCommitted").GetBoolean());
            Assert.DoesNotContain("/private/secret", File.ReadAllText(resultPath), StringComparison.Ordinal);
        }
        Assert.False(Directory.Exists(Path.Join(root.Path, "operation-output")));
    }

    [Fact]
    public async Task Delivery_failure_is_redacted_and_retains_operation_and_delivery_commit_facts()
    {
        using var root = TestDirectory.Create();
        var requestPath = root.WriteFile("request.json", CaptureEnvelope(root.Path));
        var factory = new CommittedThenFailedDeliveryFactory();
        using var console = new FakeInMemoryConsole();

        var exception = await Assert.ThrowsAsync<CommandException>(async () =>
            await new CoverageFanInCaptureCommand(factory)
            {
                Request = requestPath,
                Result = Path.Join(root.Path, "secret-result.json"),
                Json = true,
            }.ExecuteAsync(console, CancellationToken.None));

        Assert.Equal(1, exception.ExitCode);
        using var output = JsonDocument.Parse(console.ReadOutputString());
        var terminal = output.RootElement;
        Assert.Equal("deliveryFailed", terminal.GetProperty("status").GetString());
        Assert.Equal("rejected", terminal.GetProperty("operationOutcome").GetString());
        Assert.False(terminal.GetProperty("effects").GetProperty("generationCommitted").GetBoolean());
        Assert.Equal("failed", terminal.GetProperty("delivery").GetProperty("status").GetString());
        Assert.True(terminal.GetProperty("delivery").GetProperty("resultFileCreated").GetBoolean());
        Assert.True(terminal.GetProperty("delivery").GetProperty("resultFileCommitted").GetBoolean());
        Assert.Contains("ASCOVF002", console.ReadErrorString(), StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-EXCEPTION-TEXT", console.ReadOutputString() + console.ReadErrorString(), StringComparison.Ordinal);
        Assert.DoesNotContain(root.Path, console.ReadOutputString() + console.ReadErrorString(), StringComparison.Ordinal);
    }

    private static string CaptureEnvelope(string repositoryRoot) => Envelope("capture", CaptureRequest(repositoryRoot));

    [Theory]
    [InlineData("capture", "policy", "null")]
    [InlineData("capture", "repositoryRoot", "42")]
    [InlineData("capture", "repositoryRoot", "\" \"")]
    [InlineData("capture", "producerId", "null")]
    [InlineData("capture", "outputMode", "0")]
    [InlineData("capture", "outputMode", "\"unknown\"")]
    [InlineData("capture", "coveragePath", "false")]
    [InlineData("collect", "deadlineSeconds", "1.5")]
    [InlineData("collect", "deadlineSeconds", "2147483648")]
    [InlineData("collect", "noBuild", "1")]
    [InlineData("collect", "noRestore", "[]")]
    [InlineData("collect", "parallelism", "\"2\"")]
    [InlineData("compose", "primaryCaptureDirectory", "null")]
    [InlineData("compose", "supplementaryCaptureDirectories", "{}")]
    [InlineData("compose", "supplementaryCaptureDirectories", "[null]")]
    [InlineData("compose", "supplementaryCaptureDirectories", "[42]")]
    [InlineData("compose", "outputDirectory", "\"\"")]
    [InlineData("accept", "composedOutputDirectory", "null")]
    [InlineData("accept", "evaluationIntent", "true")]
    [InlineData("verify", "acceptedOutputDirectory", "null")]
    [InlineData("verify", "policy.limits.xmlBytes", "\"1024\"")]
    [InlineData("verify", "policy.limits.xmlBytes", "9223372036854775808")]
    [InlineData("verify", "policy.gate.minLine", "1e100")]
    [InlineData("verify", "policy.supplements", "{}")]
    public async Task TypedWireFieldsRejectWrongShapesWithoutPreparingDelivery(string operation, string field, string value)
    {
        using var root = TestDirectory.Create();
        var document = JsonNode.Parse(Envelope(operation, RequestFor(operation, root.Path)))!;
        var parts = field.Split('.');
        var current = document["request"]!;
        foreach (var part in parts[..^1])
        {
            current[part] ??= new JsonObject();
            current = current[part]!;
        }
        current[parts[^1]] = JsonNode.Parse(value);
        var request = root.WriteFile("invalid.json", document.ToJsonString());
        var result = TestPathUtils.PathUnder(root.Path, "terminal.json");
        using var console = new FakeInMemoryConsole();
        var error = await Assert.ThrowsAsync<CommandException>(async () =>
            await CommandFor(operation, request, result).ExecuteAsync(console, CancellationToken.None));
        Assert.Equal(2, error.ExitCode);
        Assert.False(File.Exists(result));
        using var terminal = JsonDocument.Parse(console.ReadOutputString());
        Assert.Equal("invalidInput", terminal.RootElement.GetProperty("status").GetString());
        Assert.False(terminal.RootElement.GetProperty("delivery").GetProperty("resultFileCreated").GetBoolean());
    }

    [Theory]
    [InlineData("capture")]
    [InlineData("collect")]
    [InlineData("compose")]
    [InlineData("accept")]
    [InlineData("verify")]
    public void RequiredWireMembersCannotBeOmittedOrReplacedWithAnotherShape(string operation)
    {
        using var root = TestDirectory.Create();
        var valid = JsonNode.Parse(Envelope(operation, RequestFor(operation, root.Path)))!;
        foreach (var name in valid["request"]!.AsObject().Select(x => x.Key).ToArray())
        {
            if (operation == "capture" && name is "coveragePath" or "timingsPath") continue;
            var missing = valid.DeepClone();
            missing["request"]!.AsObject().Remove(name);
            Assert.ThrowsAny<Exception>(() => CoverageFanInRequestReader.Parse(System.Text.Encoding.UTF8.GetBytes(missing.ToJsonString())));
        }
        foreach (var shape in new[] { "null", "[]", "true" })
        {
            valid["request"] = JsonNode.Parse(shape);
            Assert.ThrowsAny<Exception>(() => CoverageFanInRequestReader.Parse(System.Text.Encoding.UTF8.GetBytes(valid.ToJsonString())));
        }
    }

    [Theory]
    [InlineData("argument")]
    [InlineData("invalidOperation")]
    public async Task SynchronousFacadeValidationIsAnInputFailureWithNoResultFile(string failure)
    {
        using var root = TestDirectory.Create();
        var request = root.WriteFile("request.json", CaptureEnvelope(root.Path));
        var result = TestPathUtils.PathUnder(root.Path, "terminal.json");
        using var console = new FakeInMemoryConsole();
        var error = await Assert.ThrowsAsync<CommandException>(async () =>
            await new SynchronousValidationCommand(failure) { Request = request, Result = result, Json = true }
                .ExecuteAsync(console, CancellationToken.None));
        Assert.Equal(2, error.ExitCode);
        Assert.False(File.Exists(result));
        Assert.DoesNotContain("validation-secret", console.ReadOutputString() + console.ReadErrorString());
    }

    [Fact]
    public void TerminalEnumEncodingRefusesNumbersUnknownNamesAndUndefinedValues()
    {
        var options = new JsonSerializerOptions();
        var factory = new StrictCoverageEnumConverterFactory();
        options.Converters.Add(factory);
        Assert.False(factory.CanConvert(typeof(string)));
        Assert.False(StrictCoverageEnumConverterFactory.IsToken(typeof(CoverageOperationOutcome), null));
        Assert.Equal(CoverageOperationOutcome.Succeeded, JsonSerializer.Deserialize<CoverageOperationOutcome>("\"succeeded\"", options));
        Assert.Equal("\"succeeded\"", JsonSerializer.Serialize(CoverageOperationOutcome.Succeeded, options));
        foreach (var json in new[] { "0", "\"Succeeded\"", "\"unknown\"" })
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CoverageOperationOutcome>(json, options));
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize((CoverageOperationOutcome)int.MaxValue, options));
    }

    private sealed class SynchronousValidationCommand(string failure) : CoverageFanInCommandBase("capture", true)
    {
        internal override ValueTask<CoverageFanInResult> InvokeAsync(ParsedCoverageFanInRequest request,
            TextWriter diagnostics, CancellationToken cancellationToken) => throw (failure == "argument"
                ? new ArgumentException("validation-secret") : new InvalidOperationException("validation-secret"));
    }

    [Fact]
    public void TypedSelectorsRequireCompletePathsAndExactSupplementInventories()
    {
        var policy = ValidPolicy;
        foreach (var primary in new[] { "", " " })
            Assert.Throws<CoverageFanInCliInputException>(() => CoverageFanInRequestReader.ValidateOperationRequest(
                new("compose", new CoverageComposeRequest(policy, "repo", primary, [], "out"))));
        Assert.Throws<CoverageFanInCliInputException>(() => CoverageFanInRequestReader.ValidateOperationRequest(
            new("compose", new CoverageComposeRequest(policy, "repo", "primary", null!, "out"))));
        Assert.Throws<CoverageFanInCliInputException>(() => CoverageFanInRequestReader.ValidateOperationRequest(
            new("compose", new CoverageComposeRequest(policy, "repo", "primary", [], " "))));
        Assert.Throws<CoverageFanInCliInputException>(() => CoverageFanInRequestReader.ValidateOperationRequest(
            new("accept", new CoverageAcceptRequest(policy, "repo", "primary", [], "composed", " "))));
        Assert.Throws<CoverageFanInCliInputException>(() => CoverageFanInRequestReader.ValidateOperationRequest(
            new("verify", new CoverageVerifyRequest(policy, "repo", "primary", [], " ", "accepted"))));
        foreach (var request in new[]
        {
            new CoverageCaptureRequest(policy, "repo", "out", "primary", "coverage", "timings", RawCoveragePath: "raw"),
            new CoverageCaptureRequest(policy, "repo", "out", "primary", "coverage"),
            new CoverageCaptureRequest(policy, "repo", "out", "primary", "coverage", " "),
        })
            Assert.Throws<CoverageFanInCliInputException>(() => CoverageFanInRequestReader.ValidateOperationRequest(new("capture", request)));
        Assert.Throws<CoverageFanInCliInputException>(() => CoverageFanInRequestReader.ValidateOperationRequest(new("unknown", new object())));
        Assert.Throws<CoverageFanInCliInputException>(() => CoverageFanInRequestReader.ValidateCanonicalPolicy(new("unknown", new object())));
    }

    [Theory]
    [InlineData("compose")]
    [InlineData("accept")]
    [InlineData("verify")]
    public async Task EveryCommandSupportsTheSameExplicitDeliveryBoundary(string operation)
    {
        using var root = TestDirectory.Create();
        var request = root.WriteFile("request.json", Envelope(operation, RequestFor(operation, root.Path)));
        var delivery = new CountingDeliveryFactory();
        CoverageFanInCommandBase command = operation switch
        {
            "compose" => new CoverageFanInComposeCommand(delivery),
            "accept" => new CoverageFanInAcceptCommand(delivery),
            _ => new CoverageFanInVerifyCommand(delivery)
        };
        command.Request = request;
        command.Result = TestPathUtils.PathUnder(root.Path, "terminal.json");
        command.Json = true;
        using var console = new FakeInMemoryConsole();
        var error = await Assert.ThrowsAsync<CommandException>(async () => await command.ExecuteAsync(console, CancellationToken.None));
        Assert.Equal(1, error.ExitCode);
        Assert.Equal(1, delivery.PrepareCalls);
        using var terminal = JsonDocument.Parse(console.ReadOutputString());
        Assert.Equal("deliveryFailed", terminal.RootElement.GetProperty("status").GetString());
        Assert.Equal(operation, terminal.RootElement.GetProperty("operation").GetString());
    }

    [Theory]
    [InlineData("missing-parent")]
    [InlineData("request-directory")]
    [InlineData("linked-request")]
    public async Task RequestFileMustBeARegularFileInAnExistingUnlinkedDirectory(string defect)
    {
        using var root = TestDirectory.Create();
        var request = root.WriteFile("request.json", CaptureEnvelope(root.Path));
        if (defect == "missing-parent") request = Path.Join(root.Path, "missing", "request.json");
        else if (defect == "request-directory") request = root.CreateDirectory("directory.json");
        else
        {
            var target = request;
            request = Path.Join(root.Path, "linked.json");
            TestFileSystem.CreateFileLinkOrSkip(request, target);
        }
        var output = Path.Join(root.Path, "result.json");
        using var console = new FakeInMemoryConsole();
        var error = await Assert.ThrowsAsync<CommandException>(async () => await new CoverageFanInCaptureCommand
        { Request = request, Result = output, Json = true }.ExecuteAsync(console, CancellationToken.None));
        Assert.Equal(2, error.ExitCode);
        Assert.False(File.Exists(output));
        Assert.DoesNotContain(root.Path, console.ReadOutputString() + console.ReadErrorString());
    }

    [Theory]
    [InlineData("json")]
    [InlineData("unauthorized")]
    [InlineData("invalidData")]
    public async Task SynchronousDependencyInputFaultsStayRedactedAndCannotCommitAResult(string fault)
    {
        using var root = TestDirectory.Create();
        var request = root.WriteFile("request.json", CaptureEnvelope(root.Path));
        var output = Path.Join(root.Path, "result.json");
        using var console = new FakeInMemoryConsole();
        var error = await Assert.ThrowsAsync<CommandException>(async () => await new TypedInputFaultCommand(fault)
        { Request = request, Result = output, Json = true }.ExecuteAsync(console, CancellationToken.None));
        Assert.Equal(2, error.ExitCode);
        Assert.False(File.Exists(output));
        Assert.DoesNotContain("typed-input-secret", console.ReadOutputString() + console.ReadErrorString());
    }

    [Theory]
    [InlineData(CoverageOperationOutcome.Incomplete)]
    [InlineData(CoverageOperationOutcome.Rejected)]
    public async Task HumanTerminalOutputRetainsNonSuccessWithoutAcceptanceClaims(CoverageOperationOutcome outcome)
    {
        using var root = TestDirectory.Create();
        var request = root.WriteFile("request.json", CaptureEnvelope(root.Path));
        var result = new CoverageFanInResult("capture", outcome, "operation", null,
            new(false, false, true, false, null), [], []);
        using var console = new FakeInMemoryConsole();
        var error = await Assert.ThrowsAsync<CommandException>(async () => await new ResultCommand("capture", result,
            SafeCoverageFanInResultDeliveryFactory.Instance)
        { Request = request, Result = Path.Join(root.Path, "terminal.json") }
                .ExecuteAsync(console, CancellationToken.None));
        Assert.Equal(1, error.ExitCode);
        Assert.Contains(outcome.ToString(), console.ReadOutputString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("accepted generation", console.ReadOutputString(), StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TypedInputFaultCommand(string fault) : CoverageFanInCommandBase("capture", true)
    {
        internal override ValueTask<CoverageFanInResult> InvokeAsync(ParsedCoverageFanInRequest request,
            TextWriter diagnostics, CancellationToken cancellationToken) => throw (fault switch
            {
                "json" => new JsonException("typed-input-secret"),
                "unauthorized" => new UnauthorizedAccessException("typed-input-secret"),
                _ => new InvalidDataException("typed-input-secret")
            });
    }

    private static object RequestFor(string operation, string root) => operation switch
    {
        "capture" => CaptureRequest(root),
        "collect" => CollectRequest(root),
        "compose" => new
        {
            policy = ValidPolicy,
            repositoryRoot = root,
            primaryCaptureDirectory = Path.Join(root, "primary-capture"),
            supplementaryCaptureDirectories = Array.Empty<string>(),
            outputDirectory = Path.Join(root, "operation-output"),
        },
        "accept" => new
        {
            policy = ValidPolicy,
            repositoryRoot = root,
            primaryCaptureDirectory = Path.Join(root, "primary-capture"),
            supplementaryCaptureDirectories = Array.Empty<string>(),
            composedOutputDirectory = Path.Join(root, "composed"),
            outputDirectory = Path.Join(root, "operation-output"),
        },
        "verify" => new
        {
            policy = ValidPolicy,
            repositoryRoot = root,
            primaryCaptureDirectory = Path.Join(root, "primary-capture"),
            supplementaryCaptureDirectories = Array.Empty<string>(),
            composedOutputDirectory = Path.Join(root, "composed"),
            acceptedOutputDirectory = Path.Join(root, "accepted"),
        },
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    private static object CaptureRequest(string root) => new
    {
        policy = ValidPolicy,
        repositoryRoot = root,
        outputDirectory = Path.Join(root, "operation-output"),
        producerId = "primary",
        coveragePath = "/private/secret/coverage.cobertura.xml",
        timingsPath = "/private/secret/timings.json",
    };

    private static object CollectRequest(string root) => new
    {
        policy = ValidPolicy,
        repositoryRoot = root,
        outputDirectory = Path.Join(root, "operation-output"),
        collectionDirectory = Path.Join(root, "collection"),
        producerId = "primary",
        deadlineSeconds = 10,
    };

    private static string Envelope(string operation, object request) => JsonSerializer.Serialize(
        new { schemaVersion = 2, operation, request }, WireOptions);

    private static JsonSerializerOptions CreateWireOptions()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }

    private static CoverageFanInCommandBase CommandFor(string operation, string request, string? result)
    {
        CoverageFanInCommandBase command = operation switch
        {
            "capture" => new CoverageFanInCaptureCommand(),
            "collect" => new CoverageFanInCaptureCommand { Collect = true },
            "compose" => new CoverageFanInComposeCommand(),
            "accept" => new CoverageFanInAcceptCommand(),
            "verify" => new CoverageFanInVerifyCommand(),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
        command.Request = request;
        command.Result = result;
        command.Json = true;
        return command;
    }

    private sealed class CommittedThenFailedDeliveryFactory : ICoverageFanInResultDeliveryFactory
    {
        public ICoverageFanInResultDelivery Prepare(string path) => new CommittedThenFailedDelivery();
    }

    private sealed class CommittedThenFailedDelivery : ICoverageFanInResultDelivery
    {
        public bool ResultFileCreated => true;
        public bool? ResultFileCommitted => true;
        public void Write(ReadOnlySpan<byte> document) => throw new IOException("SECRET-EXCEPTION-TEXT");
        public void Dispose() { }
    }

    private sealed class CountingDeliveryFactory : ICoverageFanInResultDeliveryFactory
    {
        internal int PrepareCalls { get; private set; }

        public ICoverageFanInResultDelivery Prepare(string path)
        {
            PrepareCalls++;
            return new CommittedThenFailedDelivery();
        }
    }

    private sealed class FaultingCommand(string status) : CoverageFanInCommandBase("capture", true)
    {
        internal override ValueTask<CoverageFanInResult> InvokeAsync(ParsedCoverageFanInRequest request,
            TextWriter diagnostics, CancellationToken cancellationToken) => ValueTask.FromException<CoverageFanInResult>(status switch
            {
                "cancelled" => new OperationCanceledException("private-exception-sentinel"),
                "timedOut" => new TimeoutException("private-exception-sentinel"),
                _ => new IOException("private-exception-sentinel")
            });
    }

    private sealed class ResultCommand(string operation, CoverageFanInResult result,
        ICoverageFanInResultDeliveryFactory deliveryFactory) : CoverageFanInCommandBase(operation, true, deliveryFactory)
    {
        internal override ValueTask<CoverageFanInResult> InvokeAsync(ParsedCoverageFanInRequest request,
            TextWriter diagnostics, CancellationToken cancellationToken) => ValueTask.FromResult(result);
    }

    private sealed class CurrentCaptureFixture : IDisposable
    {
        private const string Dotnet = "dotnet";
        private readonly TestDirectory _temp = TestDirectory.Create();
        private readonly string? _previousArtifactHome = Environment.GetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME");

        private CurrentCaptureFixture()
        {
            Environment.SetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME", Path.Join(_temp.Path, "artifact-store"));
        }

        private string SecretRoot => Path.GetFullPath(Path.Join(_temp.Path, "host-secret-root"));
        internal string SensitiveAbsolutePath => SecretRoot;
        private string Repository => Path.Join(SecretRoot, "repository");
        private string Report => Path.Join(SecretRoot, "producer-inputs", "coverage.cobertura.xml");
        private string Timings => Path.Join(SecretRoot, "producer-inputs", "timings.json");
        private string Captured => Path.Join(SecretRoot, "capture-output");

        internal static async Task<CurrentCaptureFixture> Create()
        {
            var fixture = new CurrentCaptureFixture();
            fixture._temp.WriteFile("host-secret-root/repository/checks/Verification.csproj",
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            await Command(fixture.Repository, "git", "init");
            await Command(fixture.Repository, "git", "config", "user.email", "test@example.invalid");
            await Command(fixture.Repository, "git", "config", "user.name", "AppSurface tests");
            await Command(fixture.Repository, "git", "add", ".");
            await Command(fixture.Repository, "git", "commit", "-m", "fixture");
            fixture._temp.WriteFile("host-secret-root/producer-inputs/coverage.cobertura.xml",
                """<coverage lines-covered="1" lines-valid="1" branches-covered="2" branches-valid="2" line-rate="1" branch-rate="1"><packages><package name="Example"><classes><class name="Example" filename="src/Example.cs"><lines><line number="1" hits="1" branch="true" condition-coverage="100% (2/2)"/></lines></class></classes></package></packages></coverage>""");
            fixture._temp.WriteFile("host-secret-root/producer-inputs/timings.json",
                """{"configuration":"Release","coverageDriver":"collector","merge":{"exitCode":0},"artifacts":{"coverageFiles":1},"projects":[{"project":"checks/Verification.csproj","exitCode":0,"executionStatus":"completed","coverageArtifactStatus":"produced"}]}""");

            var policy = new CoverageFanInPolicy(
                new CoverageSourceIdentity(await Command(fixture.Repository, "git", "rev-parse", "HEAD"),
                    await Command(fixture.Repository, "git", "rev-parse", "HEAD^{tree}"),
                    await Command(fixture.Repository, Dotnet, "--version"), "Release"),
                new CoverageRunIdentity("local", "cli-tests", Guid.NewGuid().ToString("N"), 1),
                ["checks/Verification.csproj"], [], new CoverageGatePolicy(CoverageGateMode.AggregateOnly));
            fixture.Policy = policy;
            return fixture;
        }

        private CoverageFanInPolicy Policy { get; set; } = null!;

        internal string WriteRequest(string fileName, string outputName = "capture-output") => _temp.WriteFile(
            fileName, Envelope("capture", new
            {
                policy = Policy,
                repositoryRoot = Repository,
                outputDirectory = TestPathUtils.PathUnder(SecretRoot, outputName),
                producerId = "primary",
                coveragePath = Report,
                timingsPath = Timings,
            }));

        internal string ResultPath(string fileName) => TestPathUtils.PathUnder(_temp.Path, fileName);

        private static async Task<string> Command(string workingDirectory, string executable, params string[] arguments)
        {
            Directory.CreateDirectory(workingDirectory);
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(executable)
                {
                    WorkingDirectory = workingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                }
            };
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, await error);
            return (await output).Trim();
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME", _previousArtifactHome);
            _temp.Dispose();
        }
    }
}
