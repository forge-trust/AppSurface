using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Cli;
using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.PostgreSql;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Locks down the Durable doctor v1 byte contract and rejects unsafe or contradictory result seams.</summary>
public sealed class DurableDoctorRendererTests
{
    private static readonly Guid ConfiguredEpoch = Guid.Parse("88164257-2a2f-42b4-9832-18a649888801");
    private static readonly Guid StoreId = Guid.Parse("88164257-2a2f-42b4-9832-18a649888802");
    private static readonly Guid OtherEpoch = Guid.Parse("88164257-2a2f-42b4-9832-18a649888803");
    private static readonly DateTimeOffset ObservedAt = DateTimeOffset.Parse("2026-10-03T12:00:00.0000000+00:00");
    private const string StoreOnlyJsonSnapshot = "{\"schemaVersion\":1,\"status\":\"passed\",\"exitCode\":0,\"requestedChecks\":[{\"name\":\"credential\",\"requested\":true,\"status\":\"passed\"},{\"name\":\"schema\",\"requested\":true,\"status\":\"passed\"},{\"name\":\"epoch\",\"requested\":true,\"status\":\"passed\"},{\"name\":\"retention\",\"requested\":true,\"status\":\"passed\"},{\"name\":\"worker\",\"requested\":false,\"status\":\"not-requested\"}],\"observedAtUtc\":\"2026-10-03T12:00:00.0000000+00:00\",\"schema\":{\"compatibility\":\"compatible\",\"installedVersion\":11,\"requiredVersion\":11,\"minimumReaderVersion\":1,\"maximumReaderVersion\":11,\"minimumWriterVersion\":1,\"maximumWriterVersion\":11,\"appliedVersions\":[1,2,3,4,5,6,7,8,9,10,11],\"pendingVersions\":[]},\"storeId\":\"88164257-2a2f-42b4-9832-18a649888802\",\"configuredRuntimeEpoch\":\"88164257-2a2f-42b4-9832-18a649888801\",\"activeRuntimeEpoch\":\"88164257-2a2f-42b4-9832-18a649888801\",\"credential\":{\"restricted\":true,\"failedChecks\":[]},\"retention\":{\"available\":true,\"failedChecks\":[]},\"worker\":null,\"findings\":[],\"nextAction\":{\"kind\":\"application-verifier\",\"command\":null,\"requiredInputs\":[\"consumer verifier command\"],\"documentationUrl\":\"https://github.com/forge-trust/AppSurface/blob/main/examples/durable-external-activation/README.md\"}}\n";
    private const string StoreOnlyTextSnapshot =
        "Diagnosis: passed (exit 0)\n" +
        "Observed at (UTC): 2026-10-03T12:00:00.0000000+00:00\n" +
        "Checks:\n" +
        "  credential: passed\n" +
        "  schema: passed\n" +
        "  epoch: passed\n" +
        "  retention: passed\n" +
        "  worker: not-requested\n" +
        "Store/runtime checks passed.\n" +
        "Worker check: not requested; no heartbeat claim is made.\n" +
        "Boundary: These checks passed only for this captured observation; they do not prove deployment authorization, process ownership, future readiness, or successful application execution.\n" +
        "Next action: Run the application's composition verifier; this standalone doctor cannot infer its command.\n" +
        "  Required input: consumer verifier command\n" +
        "  Documentation: https://github.com/forge-trust/AppSurface/blob/main/examples/durable-external-activation/README.md\n" +
        "Store ID: 88164257-2a2f-42b4-9832-18a649888802\n" +
        "Configured runtime epoch: 88164257-2a2f-42b4-9832-18a649888801\n" +
        "Active runtime epoch: 88164257-2a2f-42b4-9832-18a649888801\n" +
        "Schema: compatible; installed=11; required=11\n" +
        "Credential: restricted=true; failed checks=none\n" +
        "Retention: available=true; failed checks=none\n";

    [Fact]
    public void Store_only_json_and_text_match_exact_utf8_snapshots_and_json_property_order()
    {
        var request = DurableDoctorClassificationTests.CreateRequest(workerPair: false);
        var result = DurableDoctorClassifier.Classify(request, DurableDoctorClassificationTests.CreateStoreOnlyObservation());

        var json = DurableDoctorRenderer.Render(result, request, "json");
        var text = DurableDoctorRenderer.Render(result, request, "text");

        Assert.Equal(Encoding.UTF8.GetBytes(ExpectedStoreOnlyJson), Encoding.UTF8.GetBytes(json));
        Assert.Equal(Encoding.UTF8.GetBytes(StoreOnlyTextSnapshot), Encoding.UTF8.GetBytes(text));
        Assert.InRange(Encoding.UTF8.GetByteCount(json), 1, DurableDoctorRenderer.MaximumOutputBytes);
        Assert.InRange(Encoding.UTF8.GetByteCount(text), 1, DurableDoctorRenderer.MaximumOutputBytes);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(
            ["schemaVersion", "status", "exitCode", "requestedChecks", "observedAtUtc", "schema", "storeId",
                "configuredRuntimeEpoch", "activeRuntimeEpoch", "credential", "retention", "worker", "findings", "nextAction"],
            document.RootElement.EnumerateObject().Select(static property => property.Name));

        var pairedRequest = DurableDoctorClassificationTests.CreateRequest(workerPair: true);
        var pairedObservedAt = DateTimeOffset.Parse("2026-10-03T12:00:00.0000000+00:00", CultureInfo.InvariantCulture);
        var pairedResult = DurableDoctorClassifier.Classify(
            pairedRequest,
            new DurableDoctorObservation(
                [],
                new DurableRuntimeSchemaStatus(
                    DurableRuntimeSchemaCompatibility.Compatible,
                    Guid.Parse("88164257-2a2f-42b4-9832-18a649888802"),
                    pairedRequest.ConfiguredRuntimeEpoch,
                    11, 11, 1, 11, 1, 11,
                    Enumerable.Range(1, 11).ToArray(), [], null),
                [],
                pairedObservedAt,
                Guid.Parse("88164257-2a2f-42b4-9832-18a649888802"),
                pairedRequest.ConfiguredRuntimeEpoch,
                new DurableDoctorHeartbeat(
                    true,
                    pairedRequest.ConfiguredRuntimeEpoch,
                    pairedObservedAt.AddSeconds(-5),
                    false)));
        var pairedText = DurableDoctorRenderer.Render(pairedResult, pairedRequest, "text");
        var workerCheckIndex = pairedText.IndexOf("  worker: passed\n", StringComparison.Ordinal);
        var checksEnd = workerCheckIndex + "  worker: passed\n".Length;
        var passedIndex = pairedText.IndexOf("Store/runtime checks passed.", StringComparison.Ordinal);
        var workerIntentIndex = pairedText.IndexOf(
            "Worker check: requested for 'doctor-fixture-801'; its heartbeat was observed against the supplied stale threshold 00:00:15.",
            StringComparison.Ordinal);
        var boundaryIndex = pairedText.IndexOf("Boundary:", StringComparison.Ordinal);
        var verifierIndex = pairedText.IndexOf("Next action: Run the application's composition verifier", StringComparison.Ordinal);
        var factsIndex = pairedText.IndexOf("Store ID:", StringComparison.Ordinal);

        Assert.Equal("passed", pairedResult.Status);
        Assert.True(workerCheckIndex >= 0 && checksEnd == passedIndex && passedIndex < workerIntentIndex
            && workerIntentIndex < boundaryIndex && boundaryIndex < verifierIndex && verifierIndex < factsIndex,
            "Clean text must state the verdict, worker scope, boundary, and verifier handoff before facts.");
        Assert.Equal(1, pairedText.Split("Worker check:", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void Documentation_store_only_sample_matches_the_production_renderer_semantically()
    {
        var request = DurableDoctorClassificationTests.CreateRequest(workerPair: false);
        var result = DurableDoctorClassifier.Classify(request, DurableDoctorClassificationTests.CreateStoreOnlyObservation());
        var rendered = DurableDoctorRenderer.Render(result, request, "json");
        var repositoryRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var documentationPath = TestPathUtils.PathUnder(repositoryRoot, "Durable", "runtime-doctor.md");
        var documentation = File.ReadAllText(documentationPath).Replace("\r\n", "\n", StringComparison.Ordinal);
        var openingFence = documentation.IndexOf("```json\n", StringComparison.Ordinal);
        Assert.True(openingFence >= 0, "The Durable doctor documentation must contain its version-one JSON sample.");
        var sampleStart = openingFence + "```json\n".Length;
        var sampleEnd = documentation.IndexOf("\n```", sampleStart, StringComparison.Ordinal);
        Assert.True(sampleEnd > sampleStart, "The Durable doctor documentation JSON sample must close its code fence.");

        var sample = documentation[sampleStart..sampleEnd];
        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(rendered), JsonNode.Parse(sample)),
            "The documented sample must match the production-rendered fixed store-only observation.");
    }

    [Fact]
    public void Output_is_byte_identical_under_non_invariant_cultures()
    {
        var request = DurableDoctorClassificationTests.CreateRequest(workerPair: false);
        var result = DurableDoctorClassifier.Classify(request, DurableDoctorClassificationTests.CreateStoreOnlyObservation());
        var priorCulture = CultureInfo.CurrentCulture;
        var priorUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.Equal(ExpectedStoreOnlyJson, DurableDoctorRenderer.Render(result, request, "json"));
            Assert.Equal(StoreOnlyTextSnapshot, DurableDoctorRenderer.Render(result, request, "text"));
        }
        finally
        {
            CultureInfo.CurrentCulture = priorCulture;
            CultureInfo.CurrentUICulture = priorUiCulture;
        }
    }

    [Fact]
    public void Renderer_uses_json_only_for_the_exact_token_and_keeps_maximum_valid_intent_bounded()
    {
        var request = new DurableDoctorRequest(
            ConfiguredEpoch,
            new string('C', 200),
            new string('E', 200),
            new string('w', 200),
            TimeSpan.FromHours(1),
            TimeSpan.FromMinutes(2),
            "json");
        var result = CreateCompatibleResult(
            request,
            ConfiguredEpoch,
            new DurableDoctorHeartbeat(true, ConfiguredEpoch, ObservedAt.AddSeconds(-5), false),
            ["retention-index-presence"]);

        var json = DurableDoctorRenderer.Render(result, request, "json");
        var text = DurableDoctorRenderer.Render(result, request, "text");

        Assert.StartsWith("{", json, StringComparison.Ordinal);
        Assert.EndsWith("\n", json, StringComparison.Ordinal);
        Assert.InRange(Encoding.UTF8.GetByteCount(json), 1, DurableDoctorRenderer.MaximumOutputBytes);
        Assert.StartsWith("Diagnosis:", text, StringComparison.Ordinal);
        Assert.EndsWith("\n", text, StringComparison.Ordinal);
        Assert.InRange(Encoding.UTF8.GetByteCount(text), 1, DurableDoctorRenderer.MaximumOutputBytes);
        Assert.Equal(text, DurableDoctorRenderer.Render(result, request, "JSON"));
        Assert.Equal(text, DurableDoctorRenderer.Render(result, request, "json "));
        Assert.Equal(text, DurableDoctorRenderer.Render(result, request, null!));
    }

    [Fact]
    public void Result_collections_are_defensively_copied_before_either_format_is_rendered()
    {
        var request = DurableDoctorClassificationTests.CreateRequest(workerPair: false);
        var source = CreateCompatibleResult(request, ConfiguredEpoch, heartbeat: null, ["retention-index-presence"]);
        var checks = source.RequestedChecks.ToArray();
        var appliedVersions = source.Schema!.AppliedVersions!.ToArray();
        var retentionFailures = source.Retention!.FailedChecks.ToArray();
        var sourceFinding = source.Findings.Single();
        var findingFailures = sourceFinding.FailedChecks.ToArray();
        var findingArguments = sourceFinding.NextAction.Command!.Arguments.ToArray();
        var topLevelArguments = source.NextAction.Command!.Arguments.ToArray();
        var findings = new[]
        {
            sourceFinding with
            {
                FailedChecks = findingFailures,
                NextAction = sourceFinding.NextAction with
                {
                    Command = sourceFinding.NextAction.Command with { Arguments = findingArguments },
                },
            },
        };
        var copy = Copy(
            source,
            requestedChecks: checks,
            schema: source.Schema with { AppliedVersions = appliedVersions },
            retention: source.Retention with { FailedChecks = retentionFailures },
            findings: findings,
            nextAction: source.NextAction with
            {
                Command = source.NextAction.Command with { Arguments = topLevelArguments },
            });
        var jsonBeforeMutation = DurableDoctorRenderer.Render(copy, request, "json");
        var textBeforeMutation = DurableDoctorRenderer.Render(copy, request, "text");

        checks[0] = checks[0] with { Name = "mutated-after-copy" };
        appliedVersions[0] = 64;
        retentionFailures[0] = "mutated-after-copy";
        findingFailures[0] = "mutated-after-copy";
        findingArguments[0] = "mutated-after-copy";
        topLevelArguments[0] = "mutated-after-copy";
        findings[0] = sourceFinding with { Problem = "mutated-after-copy" };

        Assert.Equal(jsonBeforeMutation, DurableDoctorRenderer.Render(copy, request, "json"));
        Assert.Equal(textBeforeMutation, DurableDoctorRenderer.Render(copy, request, "text"));
        Assert.Throws<NotSupportedException>(() =>
            ((IList<DurableDoctorCheckResult>)copy.RequestedChecks)[0] = new("changed", false, "not-requested"));
        Assert.Throws<NotSupportedException>(() =>
            ((IList<int>)copy.Schema!.AppliedVersions!)[0] = 64);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<string>)copy.Findings.Single().NextAction.Command!.Arguments)[0] = "changed");

        var verifierSource = CreateCompatibleResult(request: DurableDoctorClassificationTests.CreateRequest(workerPair: false),
            ConfiguredEpoch, heartbeat: null);
        var verifierInputs = verifierSource.NextAction.RequiredInputs.ToArray();
        var verifierCopy = Copy(verifierSource, nextAction: verifierSource.NextAction with { RequiredInputs = verifierInputs });
        var verifierJson = DurableDoctorRenderer.Render(verifierCopy, DurableDoctorClassificationTests.CreateRequest(workerPair: false), "json");
        verifierInputs[0] = "mutated-after-copy";
        Assert.Equal(verifierJson, DurableDoctorRenderer.Render(
            verifierCopy,
            DurableDoctorClassificationTests.CreateRequest(workerPair: false),
            "json"));
    }

    [Fact]
    public void Result_constructor_rejects_null_contract_members_and_invalid_collection_bounds()
    {
        var request = DurableDoctorClassificationTests.CreateRequest(workerPair: false);
        var seed = CreateCompatibleResult(request, ConfiguredEpoch, heartbeat: null);
        var finding = seed.Findings.SingleOrDefault()
            ?? DurableDoctorClassifier.Terminal(request, "unavailable", ["dependency"]).Findings.Single();
        var action = finding.NextAction;

        Assert.Throws<ArgumentNullException>(() => ConstructResult(null!, [], [], action));
        Assert.Throws<ArgumentNullException>(() => ConstructResult("passed", null!, [], action));
        Assert.Throws<ArgumentNullException>(() => ConstructResult("passed", [], null!, action));
        Assert.Throws<ArgumentException>(() => ConstructResult("findings", [], [null!], action));
        Assert.Throws<ArgumentNullException>(() => ConstructResult("passed", [], [], null!));
        Assert.Throws<ArgumentNullException>(() => ConstructResult(
            "findings", [], [finding with { NextAction = null! }], action));
        Assert.Throws<ArgumentNullException>(() => ConstructResult(
            "findings", [], [finding with { FailedChecks = null! }], action));

        Assert.Throws<ArgumentNullException>(() => DurableDoctorResultCollections.Copy<int>(null!, 1, "values"));
        Assert.Throws<ArgumentOutOfRangeException>(() => DurableDoctorResultCollections.Copy([1], -1, "values"));
        Assert.Equal([1], DurableDoctorResultCollections.Copy([1], 1, "values"));
        Assert.Throws<ArgumentException>(() => DurableDoctorResultCollections.Copy([1, 2], 1, "values"));
        Assert.Throws<ArgumentException>(() => ConstructResult(
            "findings", [], Enumerable.Repeat(finding, 13), action));
    }

    [Theory]
    [InlineData("json")]
    [InlineData("text")]
    public void Renderer_rejects_schema_and_retention_facts_that_contradict_the_observation(string format)
    {
        var request = DurableDoctorClassificationTests.CreateRequest(workerPair: false);
        var valid = CreateCompatibleResult(request, ConfiguredEpoch, heartbeat: null);
        var schema = valid.Schema!;
        var malformedSchemas = new (string Case, DurableDoctorSchemaResult Value)[]
        {
            ("required version below v1 range", schema with { RequiredVersion = 0 }),
            ("required version above v1 range", schema with { RequiredVersion = 65 }),
            ("unknown compatibility", schema with { Compatibility = "future-state" }),
            ("inconsistent schema carrying version metadata", schema with { Compatibility = "inconsistent" }),
            ("missing schema with an installed version", schema with { Compatibility = "missing" }),
            ("negative installed version", schema with { InstalledVersion = -1 }),
            ("missing installed version for a compatible schema", schema with { InstalledVersion = null }),
            ("missing minimum reader", schema with { MinimumReaderVersion = null }),
            ("inverted reader range", schema with { MaximumReaderVersion = 0 }),
            ("negative minimum writer", schema with { MinimumWriterVersion = -1 }),
            ("inverted writer range", schema with { MaximumWriterVersion = 0 }),
            ("missing applied versions", schema with { AppliedVersions = null }),
            ("missing pending versions", schema with { PendingVersions = null }),
            ("zero applied version", schema with { AppliedVersions = [0] }),
            ("unsorted applied versions", schema with { AppliedVersions = [2, 1, 3, 4, 5, 6, 7, 8, 9, 10, 11] }),
            ("duplicate applied version", schema with { AppliedVersions = [1, 1, 3, 4, 5, 6, 7, 8, 9, 10, 11] }),
            ("applied version beyond installed", schema with { AppliedVersions = [.. Enumerable.Range(1, 12)] }),
            ("pending version not beyond installed", schema with { PendingVersions = [11] }),
            ("unsorted pending versions", schema with { PendingVersions = [13, 12] }),
            ("duplicate pending version", schema with { PendingVersions = [12, 12] }),
            ("compatible version below required", schema with
            {
                InstalledVersion = 10,
                AppliedVersions = [.. Enumerable.Range(1, 10)],
                PendingVersions = [11],
            }),
            ("compatible package outside reader range", schema with { MaximumReaderVersion = 10 }),
            ("compatible package outside writer range", schema with { MaximumWriterVersion = 10 }),
        };

        foreach (var (caseName, malformedSchema) in malformedSchemas)
        {
            var candidate = Copy(valid, schema: malformedSchema);
            var exception = Assert.Throws<InvalidOperationException>(
                () => DurableDoctorRenderer.Render(candidate, request, format));
            Assert.Equal("Durable doctor result does not satisfy the v1 rendering contract.", exception.Message);
            Assert.DoesNotContain(caseName, exception.ToString(), StringComparison.Ordinal);
        }

        var retention = valid.Retention!;
        var malformedRetention = new[]
        {
            Copy(valid, retention: retention with { Available = false }),
            Copy(valid, retention: retention with { Available = true, FailedChecks = ["retention-index-presence"] }),
            Copy(valid, retention: retention with { Available = false, FailedChecks = ["not-a-retention-check"] }),
            Copy(valid, retention: retention with { Available = false, FailedChecks = ["function-owner", "function-signature"] }),
            Copy(valid, retention: retention with { Available = false, FailedChecks = ["function-owner", "function-owner"] }),
            Copy(valid, requestedChecks: ReplaceCheck(valid, 3, check => check with { Status = "finding" })),
            Copy(valid, retention: null, replaceRetention: true),
            Copy(valid, storeId: Guid.Empty),
            Copy(valid, activeRuntimeEpoch: Guid.Empty),
            Copy(valid, observedAtUtc: DateTimeOffset.MinValue, replaceObservedAtUtc: true),
            Copy(valid, observedAtUtc: DateTimeOffset.MaxValue, replaceObservedAtUtc: true),
        };

        foreach (var candidate in malformedRetention)
        {
            var exception = Assert.Throws<InvalidOperationException>(() => DurableDoctorRenderer.Render(candidate, request, format));
            Assert.Equal("Durable doctor result does not satisfy the v1 rendering contract.", exception.Message);
        }
    }

    [Theory]
    [InlineData("json")]
    [InlineData("text")]
    public void Renderer_rejects_malformed_or_contradictory_results_before_emitting_any_output(string format)
    {
        var request = DurableDoctorClassificationTests.CreateRequest(workerPair: true);
        var valid = DurableDoctorClassifier.Classify(
            request,
            new DurableDoctorObservation(
                [],
                new DurableRuntimeSchemaStatus(
                    DurableRuntimeSchemaCompatibility.Compatible,
                    Guid.Parse("88164257-2a2f-42b4-9832-18a649888802"),
                    request.ConfiguredRuntimeEpoch,
                    11, 11, 1, 11, 1, 11,
                    Enumerable.Range(1, 11).ToArray(), [], null),
                [], DateTimeOffset.Parse("2026-10-03T12:00:00Z"),
                Guid.Parse("88164257-2a2f-42b4-9832-18a649888802"),
                request.ConfiguredRuntimeEpoch,
                new DurableDoctorHeartbeat(false, null, null, null)));
        var canonicalFinding = DurableDoctorClassifier.Terminal(request, "failed", ["catalog-contract"]).Findings.Single();
        var malformed = new[]
        {
            Copy(valid, configuredRuntimeEpoch: OtherEpoch, replaceConfiguredRuntimeEpoch: true),
            Copy(valid, requestedChecks: ReplaceCheck(valid, 0, check => check with { Name = "unexpected" })),
            Copy(valid, requestedChecks: ReplaceCheck(valid, 0, check => check with { Requested = false })),
            Copy(valid, requestedChecks: ReplaceCheck(valid, 0, check => check with { Status = "future-status" })),
            Copy(valid, requestedChecks: ReplaceCheck(valid, 0, check => check with { Status = "not-requested" })),
            Copy(valid, requestedChecks: ReplaceCheck(valid, 4, check => check with { Requested = false })),
            Copy(valid, requestedChecks: [null!, .. valid.RequestedChecks.Skip(1)]),
            Copy(valid, status: "passed", exitCode: 7),
            Copy(valid, status: "unknown", exitCode: 0),
            Copy(valid, findings: []),
            Copy(valid, findings: [canonicalFinding, canonicalFinding]),
            Copy(valid, findings: [canonicalFinding with { Code = null! }]),
            Copy(valid, worker: valid.Worker! with { WorkerId = "unvalidated-injected-label" }),
            Copy(valid, worker: valid.Worker! with { AgeTicks = -1 }),
            Copy(valid, worker: valid.Worker! with { State = "current" }),
            Copy(valid, worker: valid.Worker! with { RuntimeEpoch = Guid.Empty }),
            Copy(valid, worker: valid.Worker! with { LastHeartbeatAtUtc = DateTimeOffset.MinValue }),
            Copy(valid, worker: valid.Worker! with { LastHeartbeatAtUtc = DateTimeOffset.MaxValue }),
            Copy(valid, worker: valid.Worker! with { AgeTicks = 1 }),
            Copy(valid, status: "passed", exitCode: 2),
            Copy(valid, findings: [canonicalFinding with { Problem = "caller-controlled output" }]),
            Copy(valid, nextAction: valid.NextAction with { Command = new DurableDoctorCommandAction("sh", ["-c", "unsafe"]) }),
            Copy(valid, nextAction: valid.NextAction with { Kind = "application-verifier" }),
            Copy(valid, nextAction: valid.NextAction with
            {
                Command = valid.NextAction.Command! with
                {
                    Arguments = [.. valid.NextAction.Command.Arguments.SkipLast(1), "text"],
                },
            }),
            Copy(valid, nextAction: valid.NextAction with { RequiredInputs = ["unexpected-input"] }),
            Copy(valid, nextAction: valid.NextAction with { DocumentationUrl = new Uri("https://example.invalid/") }),
            Copy(valid, nextAction: valid.NextAction with
            {
                Command = new DurableDoctorCommandAction(null!, []),
            }),
        };

        foreach (var (candidate, index) in malformed.Select((candidate, index) => (candidate, index)))
        {
            var failure = Record.Exception(() => DurableDoctorRenderer.Render(candidate, request, format));
            Assert.True(failure is InvalidOperationException, $"Malformed rendering case {index} returned {failure?.GetType().Name ?? "no exception"}.");
            var exception = Assert.IsType<InvalidOperationException>(failure);
            Assert.Equal("Durable doctor result does not satisfy the v1 rendering contract.", exception.Message);
            Assert.DoesNotContain("caller-controlled", exception.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("unvalidated-injected-label", exception.ToString(), StringComparison.Ordinal);
        }

        var terminal = DurableDoctorClassifier.Terminal(request, "unavailable", ["dependency"]);
        var malformedTerminal = new[]
        {
            Copy(terminal, observedAtUtc: ObservedAt, replaceObservedAtUtc: true),
            Copy(terminal, schema: valid.Schema, replaceSchema: true),
            Copy(terminal, storeId: StoreId, replaceStoreId: true),
            Copy(terminal, activeRuntimeEpoch: ConfiguredEpoch, replaceActiveRuntimeEpoch: true),
            Copy(terminal, credential: valid.Credential, replaceCredential: true),
            Copy(terminal, retention: valid.Retention, replaceRetention: true),
            Copy(terminal, worker: valid.Worker, replaceWorker: true),
            Copy(terminal, findings: [terminal.Findings.Single() with { FailedChecks = ["cleanup", "dependency"] }]),
            Copy(terminal, findings: [terminal.Findings.Single() with { FailedChecks = ["dependency", "dependency"] }]),
            Copy(terminal, findings: [terminal.Findings.Single() with { FailedChecks = ["unknown-category"] }]),
            Copy(terminal, findings: [terminal.Findings.Single() with { Code = DurableProblemCodes.DoctorCanceled }]),
            Copy(terminal, configuredRuntimeEpoch: OtherEpoch, replaceConfiguredRuntimeEpoch: true),
        };
        foreach (var candidate in malformedTerminal)
        {
            var exception = Assert.Throws<InvalidOperationException>(() => DurableDoctorRenderer.Render(candidate, request, format));
            Assert.Equal("Durable doctor result does not satisfy the v1 rendering contract.", exception.Message);
        }

        var requestedWithNullRequest = Copy(valid, requestedChecks: [.. valid.RequestedChecks]);
        var exceptionWithoutRequest = Assert.Throws<InvalidOperationException>(
            () => DurableDoctorRenderer.Render(requestedWithNullRequest, null, format));
        Assert.Equal("Durable doctor result does not satisfy the v1 rendering contract.", exceptionWithoutRequest.Message);

        var invalidInput = DurableDoctorClassifier.Terminal(null, "invalid-input", ["input"]);
        foreach (var categories in new[] { Array.Empty<string>(), new[] { "input", "input" }, new[] { "dependency" } })
        {
            var candidate = Copy(invalidInput, findings: [invalidInput.Findings.Single() with { FailedChecks = categories }]);
            var exception = Assert.Throws<InvalidOperationException>(() => DurableDoctorRenderer.Render(candidate, null, format));
            Assert.Equal("Durable doctor result does not satisfy the v1 rendering contract.", exception.Message);
        }
    }

    [Fact]
    public void Canonical_command_text_quotes_every_argv_token_as_a_separate_shell_argument()
    {
        var request = DurableDoctorClassificationTests.CreateRequest(
            workerPair: true,
            connectionEnvironmentName: "CONNÉCTION_Δ",
            epochEnvironmentName: "EPOCH_東京");
        var result = DurableDoctorClassifier.Classify(
            request,
            new DurableDoctorObservation(
                ["role-ownership"]));
        var text = DurableDoctorRenderer.Render(result, request, "text");

        Assert.Contains(
            "  Next command: 'appsurface' 'durable' 'doctor' '--connection-env' 'CONNÉCTION_Δ' '--runtime-epoch-env' 'EPOCH_東京' '--worker-id' 'doctor-fixture-801' '--stale-after' '15s' '--timeout' '10s' '--format' 'json'",
            text,
            StringComparison.Ordinal);
        Assert.Contains("Failed checks: role-ownership", text, StringComparison.Ordinal);
    }

    private static DurableDoctorResult Copy(
        DurableDoctorResult value,
        string? status = null,
        int? exitCode = null,
        IReadOnlyList<DurableDoctorCheckResult>? requestedChecks = null,
        DateTimeOffset? observedAtUtc = null,
        bool replaceObservedAtUtc = false,
        DurableDoctorSchemaResult? schema = null,
        bool replaceSchema = false,
        Guid? storeId = null,
        bool replaceStoreId = false,
        Guid? configuredRuntimeEpoch = null,
        bool replaceConfiguredRuntimeEpoch = false,
        Guid? activeRuntimeEpoch = null,
        bool replaceActiveRuntimeEpoch = false,
        DurableDoctorCredentialResult? credential = null,
        bool replaceCredential = false,
        DurableDoctorRetentionResult? retention = null,
        bool replaceRetention = false,
        DurableDoctorWorkerResult? worker = null,
        bool replaceWorker = false,
        IReadOnlyList<DurableDoctorFinding>? findings = null,
        DurableDoctorAction? nextAction = null) =>
        new(
            status ?? value.Status,
            exitCode ?? value.ExitCode,
            requestedChecks ?? value.RequestedChecks,
            replaceObservedAtUtc ? observedAtUtc : observedAtUtc ?? value.ObservedAtUtc,
            replaceSchema ? schema : schema ?? value.Schema,
            replaceStoreId ? storeId : storeId ?? value.StoreId,
            replaceConfiguredRuntimeEpoch ? configuredRuntimeEpoch : configuredRuntimeEpoch ?? value.ConfiguredRuntimeEpoch,
            replaceActiveRuntimeEpoch ? activeRuntimeEpoch : activeRuntimeEpoch ?? value.ActiveRuntimeEpoch,
            replaceCredential ? credential : credential ?? value.Credential,
            replaceRetention ? retention : retention ?? value.Retention,
            replaceWorker ? worker : worker ?? value.Worker,
            findings ?? value.Findings,
            nextAction ?? value.NextAction);

    private static DurableDoctorResult ConstructResult(
        string? status,
        IEnumerable<DurableDoctorCheckResult>? requestedChecks,
        IEnumerable<DurableDoctorFinding>? findings,
        DurableDoctorAction? nextAction) =>
        new(
            status!,
            0,
            requestedChecks!,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            findings!,
            nextAction!);

    private static DurableDoctorResult CreateCompatibleResult(
        DurableDoctorRequest request,
        Guid? activeEpoch,
        DurableDoctorHeartbeat? heartbeat,
        IReadOnlyList<string>? retentionFailures = null)
    {
        var schema = new DurableRuntimeSchemaStatus(
            DurableRuntimeSchemaCompatibility.Compatible,
            StoreId,
            activeEpoch,
            11,
            11,
            1,
            11,
            1,
            11,
            Enumerable.Range(1, 11).ToArray(),
            [],
            null);
        return DurableDoctorClassifier.Classify(
            request,
            new DurableDoctorObservation(
                [],
                schema,
                retentionFailures ?? [],
                ObservedAt,
                StoreId,
                activeEpoch,
                heartbeat));
    }

    private static IReadOnlyList<DurableDoctorCheckResult> ReplaceCheck(
        DurableDoctorResult result,
        int index,
        Func<DurableDoctorCheckResult, DurableDoctorCheckResult> replace) =>
        result.RequestedChecks.Select((check, currentIndex) => currentIndex == index ? replace(check) : check).ToArray();

    private static string ExpectedStoreOnlyJson =>
        StoreOnlyJsonSnapshot.Replace("+00:00", "\\u002B00:00", StringComparison.Ordinal);
}
