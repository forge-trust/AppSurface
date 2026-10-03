using ForgeTrust.AppSurface.Cli;
using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.PostgreSql;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Proves the pure v1 classifier against the shared accepted diagnosis matrix and malformed evidence.</summary>
public sealed class DurableDoctorClassificationTests
{
    private const int RequiredSchemaVersion = 11;
    private static readonly Guid ConfiguredEpoch = Guid.Parse("88164257-2a2f-42b4-9832-18a649888801");
    private static readonly Guid StoreId = Guid.Parse("88164257-2a2f-42b4-9832-18a649888802");
    private static readonly Guid OtherEpoch = Guid.Parse("88164257-2a2f-42b4-9832-18a649888803");
    private static readonly DateTimeOffset ObservedAt = DateTimeOffset.Parse("2026-10-03T12:00:00.0000000+00:00");

    [Fact]
    public void All_28_shared_matrix_rows_match_checks_findings_exits_and_actions()
    {
        foreach (var row in DurableDoctorV1Matrix.Load())
        {
            var request = row.Scenario == "invalid-input" ? null : CreateRequest(row.WorkerPair);
            var result = Classify(row, request);

            Assert.True(result.ExitCode == row.ExitCode, $"{row.Id} ({row.Scenario}) exit code");
            Assert.Equal(row.ExpectedCodes, result.Findings.Select(static finding => finding.Code));
            Assert.Equal(row.ExpectedChecks.Count, result.RequestedChecks.Count);
            for (var index = 0; index < row.ExpectedChecks.Count; index++)
            {
                var expected = row.ExpectedChecks[index];
                var actual = result.RequestedChecks[index];
                Assert.Equal(expected != "NR", actual.Requested);
                Assert.Equal(expected switch
                {
                    "P" => "passed",
                    "F" => "finding",
                    "NC" => "not-checked",
                    "NR" => "not-requested",
                    _ => throw new InvalidDataException("The matrix check state was not validated."),
                }, actual.Status);
            }

            AssertAction(row, result.NextAction, request);
            if (row.TerminalCategories.Count != 0)
            {
                Assert.Equal(row.TerminalCategories, result.Findings.Single().FailedChecks);
            }

            var output = DurableDoctorRenderer.Render(result, request, "json");
            Assert.EndsWith("\n", output, StringComparison.Ordinal);
            Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(output), 1, DurableDoctorRenderer.MaximumOutputBytes);
            using var json = System.Text.Json.JsonDocument.Parse(output);
            Assert.Equal(result.Status, json.RootElement.GetProperty("status").GetString());
            Assert.Equal(result.ExitCode, json.RootElement.GetProperty("exitCode").GetInt32());
            Assert.Equal(
                result.Findings.Select(static finding => finding.Code),
                json.RootElement.GetProperty("findings").EnumerateArray()
                    .Select(static finding => finding.GetProperty("code").GetString()));
            Assert.Equal(result.Schema is null, json.RootElement.GetProperty("schema").ValueKind == System.Text.Json.JsonValueKind.Null);
            Assert.Equal(result.Credential is null, json.RootElement.GetProperty("credential").ValueKind == System.Text.Json.JsonValueKind.Null);
            Assert.Equal(result.Retention is null, json.RootElement.GetProperty("retention").ValueKind == System.Text.Json.JsonValueKind.Null);
            Assert.Equal(result.Worker is null, json.RootElement.GetProperty("worker").ValueKind == System.Text.Json.JsonValueKind.Null);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Terminal_results_discard_database_facts_but_preserve_valid_request_intent(bool workerPair)
    {
        var request = CreateRequest(workerPair, connectionEnvironmentName: "CONNÉCTION_Δ", epochEnvironmentName: "EPOCH_東京");
        var result = DurableDoctorClassifier.Terminal(request, "unavailable", ["cleanup", "dependency"]);

        Assert.Equal("unavailable", result.Status);
        Assert.Equal(4, result.ExitCode);
        Assert.Equal(ConfiguredEpoch, result.ConfiguredRuntimeEpoch);
        Assert.Null(result.ObservedAtUtc);
        Assert.Null(result.Schema);
        Assert.Null(result.StoreId);
        Assert.Null(result.ActiveRuntimeEpoch);
        Assert.Null(result.Credential);
        Assert.Null(result.Retention);
        Assert.Null(result.Worker);
        Assert.Equal(["dependency", "cleanup"], result.Findings.Single().FailedChecks);
        Assert.Equal(workerPair ? "not-checked" : "not-requested", result.RequestedChecks[4].Status);
        Assert.Contains("CONNÉCTION_Δ", result.NextAction.Command!.Arguments);
        Assert.Contains("EPOCH_東京", result.NextAction.Command.Arguments);
    }

    [Fact]
    public void Compatible_store_epoch_is_checked_without_a_worker_pair()
    {
        var request = CreateRequest(workerPair: false);
        var observation = CreateCompatibleObservation(request, activeEpoch: OtherEpoch, heartbeat: null);

        var result = DurableDoctorClassifier.Classify(request, observation);

        Assert.Equal([DurableProblemCodes.RecoveryEpochRequired], result.Findings.Select(static finding => finding.Code));
        Assert.Equal("finding", result.RequestedChecks[2].Status);
        Assert.Equal("not-requested", result.RequestedChecks[4].Status);
        Assert.Null(result.Worker);
    }

    [Fact]
    public void Row_epoch_mismatch_is_classified_before_drain_or_staleness()
    {
        var request = CreateRequest(workerPair: true);
        var heartbeat = new DurableDoctorHeartbeat(
            Found: true,
            RuntimeEpoch: OtherEpoch,
            LastHeartbeatAtUtc: ObservedAt.AddSeconds(-30),
            IsDraining: true);
        var result = DurableDoctorClassifier.Classify(
            request,
            CreateCompatibleObservation(request, ConfiguredEpoch, heartbeat));

        Assert.Equal([DurableProblemCodes.WorkerHeartbeatEpochMismatch], result.Findings.Select(static finding => finding.Code));
        Assert.Equal("epoch-incompatible", result.Worker!.State);
        Assert.Equal(30 * TimeSpan.TicksPerSecond, result.Worker.AgeTicks);
    }

    [Fact]
    public void Valid_unicode_environment_names_match_the_input_contract_and_survive_retry_action()
    {
        var request = CreateRequest(
            workerPair: true,
            connectionEnvironmentName: "CONNÉCTION_Δ",
            epochEnvironmentName: "EPOCH_東京");
        var result = DurableDoctorClassifier.Classify(
            request,
            CreateCompatibleObservation(
                request,
                ConfiguredEpoch,
                new DurableDoctorHeartbeat(false, null, null, null),
                retentionFailures: ["retention-index-presence"]));

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("CONNÉCTION_Δ", result.NextAction.Command!.Arguments);
        Assert.Contains("EPOCH_東京", result.NextAction.Command.Arguments);
        Assert.Contains("doctor-fixture-801", result.NextAction.Command.Arguments);
        Assert.Contains("15s", result.NextAction.Command.Arguments);
        var text = DurableDoctorRenderer.Render(result, request, "text");
        Assert.Contains("'CONNÉCTION_Δ'", text, StringComparison.Ordinal);
        Assert.Contains("'EPOCH_東京'", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Credential_short_circuit_rejects_extra_schema_evidence_and_unknown_categories()
    {
        var request = CreateRequest(workerPair: false);
        var schema = CompatibleSchema(ConfiguredEpoch);
        var invalidObservations = new[]
        {
            new DurableDoctorObservation(["role-ownership"], schema),
            new DurableDoctorObservation(["not-a-category"]),
            new DurableDoctorObservation(["role-ownership", "role-ownership"]),
            new DurableDoctorObservation([null!]),
        };

        foreach (var observation in invalidObservations)
        {
            var result = DurableDoctorClassifier.Classify(request, observation);
            Assert.Equal("failed", result.Status);
            Assert.Equal(1, result.ExitCode);
            Assert.Equal([DurableProblemCodes.DoctorContractFailed], result.Findings.Select(static finding => finding.Code));
            Assert.Null(result.Schema);
            Assert.Null(result.Credential);
        }
    }

    [Fact]
    public void Missing_or_compatible_schema_requires_every_fact_for_the_reached_boundary()
    {
        var request = CreateRequest(workerPair: false);
        var schemaRequired = new DurableDoctorObservation([], schema: null);
        var missingRequiredRetention = new DurableDoctorObservation([], CompatibleSchema(ConfiguredEpoch));
        var missingCaptureTime = new DurableDoctorObservation(
            [], CompatibleSchema(ConfiguredEpoch), retentionFailures: [], storeId: StoreId,
            activeRuntimeEpoch: ConfiguredEpoch);
        var incompatibleWithRuntimeEvidence = new DurableDoctorObservation(
            [], MissingSchema(), retentionFailures: [], observedAtUtc: ObservedAt, storeId: StoreId,
            activeRuntimeEpoch: ConfiguredEpoch);

        foreach (var observation in new[] { schemaRequired, missingRequiredRetention, missingCaptureTime, incompatibleWithRuntimeEvidence })
        {
            var result = DurableDoctorClassifier.Classify(request, observation);
            Assert.Equal(DurableProblemCodes.DoctorContractFailed, result.Findings.Single().Code);
            Assert.Null(result.ObservedAtUtc);
            Assert.Null(result.Schema);
            Assert.Null(result.StoreId);
            Assert.Null(result.ActiveRuntimeEpoch);
        }
    }

    [Fact]
    public void Observation_category_copies_reject_oversize_before_materializing_an_unbounded_sequence()
    {
        Assert.Throws<InvalidDataException>(() => new DurableDoctorObservation(
            Enumerable.Repeat("caller-role", 7)));
        Assert.Throws<InvalidDataException>(() => new DurableDoctorObservation(
            [], retentionFailures: Enumerable.Repeat("function-owner", 10)));
    }

    [Fact]
    public void Classifier_rejects_oversize_schema_arrays_contradictory_store_identity_and_invalid_timestamp()
    {
        var request = CreateRequest(workerPair: false);
        var overlongSchema = new DurableRuntimeSchemaStatus(
            DurableRuntimeSchemaCompatibility.Compatible,
            StoreId,
            ConfiguredEpoch,
            installedVersion: 100,
            requiredVersion: RequiredSchemaVersion,
            minimumReaderVersion: 1,
            maximumReaderVersion: 100,
            minimumWriterVersion: 1,
            maximumWriterVersion: 100,
            appliedVersions: Enumerable.Range(1, 65).ToArray(),
            pendingVersions: [],
            problem: null);
        Assert.Throws<InvalidDataException>(() => new DurableDoctorObservation(
            [], overlongSchema, [], ObservedAt, StoreId, ConfiguredEpoch));
        var mismatchedIdentity = new DurableDoctorObservation(
            [], CompatibleSchema(ConfiguredEpoch), [], ObservedAt, OtherEpoch, ConfiguredEpoch);
        var invalidTimestamp = new DurableDoctorObservation(
            [], CompatibleSchema(ConfiguredEpoch), [], DateTimeOffset.MinValue, StoreId, ConfiguredEpoch);
        var unknownCompatibility = new DurableDoctorObservation(
            [], new DurableRuntimeSchemaStatus(
                (DurableRuntimeSchemaCompatibility)99, StoreId, ConfiguredEpoch, 11, 11, 1, 11, 1, 11,
                Enumerable.Range(1, 11).ToArray(), [], "untrusted problem"));

        foreach (var observation in new[] { mismatchedIdentity, invalidTimestamp, unknownCompatibility })
        {
            var result = DurableDoctorClassifier.Classify(request, observation);
            Assert.Equal(DurableProblemCodes.DoctorContractFailed, result.Findings.Single().Code);
            Assert.Null(result.Schema);
        }
    }

    [Fact]
    public void Invalid_heartbeat_shapes_and_empty_epochs_fail_closed_without_database_projection()
    {
        var request = CreateRequest(workerPair: true);
        var invalid = new[]
        {
            CreateCompatibleObservation(request, ConfiguredEpoch, new DurableDoctorHeartbeat(false, OtherEpoch, null, null)),
            CreateCompatibleObservation(request, ConfiguredEpoch, new DurableDoctorHeartbeat(true, null, ObservedAt, false)),
            CreateCompatibleObservation(request, ConfiguredEpoch, new DurableDoctorHeartbeat(true, ConfiguredEpoch, DateTimeOffset.MaxValue, false)),
            CreateCompatibleObservation(request, Guid.Empty, new DurableDoctorHeartbeat(false, null, null, null)),
        };

        foreach (var observation in invalid)
        {
            var result = DurableDoctorClassifier.Classify(request, observation);
            Assert.Equal(DurableProblemCodes.DoctorContractFailed, result.Findings.Single().Code);
            Assert.Null(result.ObservedAtUtc);
            Assert.Null(result.Worker);
            Assert.Null(result.StoreId);
        }
    }

    [Fact]
    public void Invalid_terminal_categories_and_unknown_status_fail_closed_to_contract_failure()
    {
        var request = CreateRequest(workerPair: false);
        var result = DurableDoctorClassifier.Terminal(request, "unavailable", ["dependency", "dependency"]);
        var unknownStatus = DurableDoctorClassifier.Terminal(request, "success", ["caller-canceled"]);

        Assert.Equal("failed", result.Status);
        Assert.Equal([DurableProblemCodes.DoctorContractFailed], result.Findings.Select(static finding => finding.Code));
        Assert.Equal("failed", unknownStatus.Status);
        Assert.Equal([DurableProblemCodes.DoctorContractFailed], unknownStatus.Findings.Select(static finding => finding.Code));
    }

    [Fact]
    public void Direct_result_collection_copy_checks_the_bound_before_copying_more_items()
    {
        Assert.Throws<ArgumentException>(() => DurableDoctorResultCollections.Copy(
            Enumerable.Range(0, 6), 5, "requestedChecks"));
        Assert.Throws<ArgumentException>(() => DurableDoctorResultCollections.Copy(
            Enumerable.Range(0, 15), 14, "arguments"));
    }

    [Fact]
    public void Result_constructor_stops_after_the_first_item_beyond_its_check_bound()
    {
        var request = CreateRequest(workerPair: false);
        var template = DurableDoctorClassifier.Terminal(request, "failed", ["catalog-contract"]);
        var enumerated = 0;

        Assert.Throws<ArgumentException>(() => new DurableDoctorResult(
            template.Status,
            template.ExitCode,
            EndlessChecks(),
            template.ObservedAtUtc,
            template.Schema,
            template.StoreId,
            template.ConfiguredRuntimeEpoch,
            template.ActiveRuntimeEpoch,
            template.Credential,
            template.Retention,
            template.Worker,
            template.Findings,
            template.NextAction));
        Assert.Equal(6, enumerated);

        IEnumerable<DurableDoctorCheckResult> EndlessChecks()
        {
            while (true)
            {
                enumerated++;
                yield return template.RequestedChecks[0];
            }
        }
    }

    [Fact]
    public void Observation_schema_is_a_bounded_private_snapshot_and_getter_mutations_do_not_escape()
    {
        var request = CreateRequest(workerPair: false);
        var source = CompatibleSchema(ConfiguredEpoch);
        var sourceApplied = (int[])source.AppliedVersions;
        var observation = new DurableDoctorObservation(
            [], source, [], ObservedAt, StoreId, ConfiguredEpoch);
        sourceApplied[0] = 999;

        var exposed = observation.Schema!;
        var exposedApplied = (int[])exposed.AppliedVersions;
        exposedApplied[0] = 998;
        var reread = observation.Schema!;

        Assert.Equal(1, reread.AppliedVersions[0]);
        Assert.Null(reread.Problem);
        Assert.Equal(
            "passed",
            DurableDoctorClassifier.Classify(request, observation).Status);
    }

    internal static DurableDoctorRequest CreateRequest(
        bool workerPair,
        string connectionEnvironmentName = "APPSURFACE_DURABLE_CONNECTION",
        string epochEnvironmentName = "APPSURFACE_DURABLE_RUNTIME_EPOCH",
        string format = "json") =>
        new(
            ConfiguredEpoch,
            connectionEnvironmentName,
            epochEnvironmentName,
            workerPair ? "doctor-fixture-801" : null,
            workerPair ? TimeSpan.FromSeconds(15) : null,
            TimeSpan.FromSeconds(10),
            format);

    internal static DurableDoctorObservation CreateStoreOnlyObservation() =>
        CreateCompatibleObservation(CreateRequest(workerPair: false), ConfiguredEpoch, heartbeat: null);

    private static DurableDoctorResult Classify(DurableDoctorMatrixRow row, DurableDoctorRequest? request)
    {
        if (row.Scenario == "invalid-input")
        {
            return DurableDoctorClassifier.Terminal(null, "invalid-input", row.TerminalCategories);
        }
        var validRequest = request!;
        return row.Scenario switch
        {
            "unavailable-store-only" or "unavailable-paired" =>
                DurableDoctorClassifier.Terminal(validRequest, "unavailable", row.TerminalCategories),
            "caller-canceled" => DurableDoctorClassifier.Terminal(validRequest, "canceled", row.TerminalCategories),
            "unexpected-contract-failure" => DurableDoctorClassifier.Terminal(validRequest, "failed", row.TerminalCategories),
            _ => DurableDoctorClassifier.Classify(validRequest, CreateObservation(row, validRequest)),
        };
    }

    private static DurableDoctorObservation CreateObservation(DurableDoctorMatrixRow row, DurableDoctorRequest request)
    {
        if (row.Scenario.StartsWith("credential-unsafe", StringComparison.Ordinal))
        {
            return new DurableDoctorObservation(["role-ownership"]);
        }

        if (row.Scenario.StartsWith("schema-", StringComparison.Ordinal))
        {
            var compatibility = row.Scenario switch
            {
                var scenario when scenario.StartsWith("schema-missing", StringComparison.Ordinal) => DurableRuntimeSchemaCompatibility.Missing,
                var scenario when scenario.StartsWith("schema-upgrade-required", StringComparison.Ordinal) => DurableRuntimeSchemaCompatibility.UpgradeRequired,
                var scenario when scenario.StartsWith("schema-store-too-new", StringComparison.Ordinal) => DurableRuntimeSchemaCompatibility.StoreTooNew,
                _ => DurableRuntimeSchemaCompatibility.Inconsistent,
            };
            return new DurableDoctorObservation([], IncompatibleSchema(compatibility));
        }

        var activeEpoch = row.Scenario.StartsWith("store-epoch-mismatch", StringComparison.Ordinal)
            ? OtherEpoch
            : ConfiguredEpoch;
        var heartbeat = row.WorkerPair ? CreateHeartbeat(row.Scenario, request) : null;
        var retentionFailures = row.Scenario switch
        {
            "retention-function-absent" => new[] { "function-signature" },
            "retention-index-absent-current" => new[] { "retention-index-presence" },
            "retention-index-invalid-heartbeat-missing" => new[] { "retention-index-shape" },
            _ => Array.Empty<string>(),
        };
        var storeId = row.Scenario == "contradictory-store-identity" ? OtherEpoch : StoreId;
        return CreateCompatibleObservation(request, activeEpoch, heartbeat, retentionFailures, storeId);
    }

    private static DurableDoctorHeartbeat CreateHeartbeat(string scenario, DurableDoctorRequest request) => scenario switch
    {
        "heartbeat-missing" or "retention-index-invalid-heartbeat-missing" => new(false, null, null, null),
        "heartbeat-current-10s" or "retention-index-absent-current" => FoundHeartbeat(request.ConfiguredRuntimeEpoch, TimeSpan.FromSeconds(10)),
        "heartbeat-at-threshold" => FoundHeartbeat(request.ConfiguredRuntimeEpoch, TimeSpan.FromSeconds(15)),
        "heartbeat-one-tick-stale" => FoundHeartbeat(request.ConfiguredRuntimeEpoch, TimeSpan.FromTicks(15 * TimeSpan.TicksPerSecond + 1)),
        "heartbeat-draining" => FoundHeartbeat(request.ConfiguredRuntimeEpoch, TimeSpan.FromSeconds(20), draining: true),
        "store-epoch-mismatch-with-row" => FoundHeartbeat(OtherEpoch, TimeSpan.FromSeconds(10)),
        "row-epoch-mismatch" => FoundHeartbeat(OtherEpoch, TimeSpan.FromSeconds(10)),
        "heartbeat-future-dated" => new(true, request.ConfiguredRuntimeEpoch, ObservedAt.AddSeconds(5), false),
        _ => throw new InvalidDataException($"No heartbeat builder exists for matrix scenario '{scenario}'."),
    };

    private static DurableDoctorHeartbeat FoundHeartbeat(Guid runtimeEpoch, TimeSpan age, bool draining = false) =>
        new(true, runtimeEpoch, ObservedAt.Subtract(age), draining);

    private static DurableDoctorObservation CreateCompatibleObservation(
        DurableDoctorRequest request,
        Guid? activeEpoch,
        DurableDoctorHeartbeat? heartbeat,
        IReadOnlyList<string>? retentionFailures = null,
        Guid? storeId = null) =>
        new(
            [],
            CompatibleSchema(activeEpoch, storeId ?? StoreId),
            retentionFailures ?? [],
            ObservedAt,
            storeId ?? StoreId,
            activeEpoch,
            heartbeat);

    private static DurableRuntimeSchemaStatus CompatibleSchema(Guid? activeEpoch, Guid? storeId = null) =>
        new(
            DurableRuntimeSchemaCompatibility.Compatible,
            storeId ?? StoreId,
            activeEpoch,
            installedVersion: RequiredSchemaVersion,
            requiredVersion: RequiredSchemaVersion,
            minimumReaderVersion: 1,
            maximumReaderVersion: RequiredSchemaVersion,
            minimumWriterVersion: 1,
            maximumWriterVersion: RequiredSchemaVersion,
            appliedVersions: Enumerable.Range(1, RequiredSchemaVersion).ToArray(),
            pendingVersions: [],
            problem: null);

    private static DurableRuntimeSchemaStatus IncompatibleSchema(DurableRuntimeSchemaCompatibility compatibility) => compatibility switch
    {
        DurableRuntimeSchemaCompatibility.Missing => new(
            compatibility, Guid.Empty, null, 0, RequiredSchemaVersion, 0, 0, 0, 0,
            [], Enumerable.Range(1, RequiredSchemaVersion).ToArray(), "missing"),
        DurableRuntimeSchemaCompatibility.UpgradeRequired => new(
            compatibility, StoreId, ConfiguredEpoch, RequiredSchemaVersion - 1, RequiredSchemaVersion,
            1, RequiredSchemaVersion, 1, RequiredSchemaVersion,
            Enumerable.Range(1, RequiredSchemaVersion - 1).ToArray(), [RequiredSchemaVersion], "upgrade"),
        DurableRuntimeSchemaCompatibility.StoreTooNew => new(
            compatibility, StoreId, ConfiguredEpoch, RequiredSchemaVersion, RequiredSchemaVersion,
            1, RequiredSchemaVersion - 1, 1, RequiredSchemaVersion - 1,
            Enumerable.Range(1, RequiredSchemaVersion).ToArray(), [], "too new"),
        _ => new(compatibility, Guid.Empty, null, 0, RequiredSchemaVersion, 0, 0, 0, 0, [], [], "inconsistent"),
    };

    private static DurableRuntimeSchemaStatus MissingSchema() =>
        IncompatibleSchema(DurableRuntimeSchemaCompatibility.Missing);

    private static void AssertAction(DurableDoctorMatrixRow row, DurableDoctorAction action, DurableDoctorRequest? request)
    {
        switch (row.NextAction)
        {
            case "verify":
                Assert.Equal("application-verifier", action.Kind);
                Assert.Null(action.Command);
                Assert.Equal(["consumer verifier command"], action.RequiredInputs);
                break;
            case "status":
                Assert.Equal("command", action.Kind);
                Assert.Equal("appsurface", action.Command!.Executable);
                Assert.Equal(["durable", "schema", "status", "--connection-env", request!.ConnectionEnvironmentName], action.Command.Arguments);
                break;
            case "help":
                Assert.Equal("command", action.Kind);
                Assert.Equal(["durable", "doctor", "--help"], action.Command!.Arguments);
                break;
            case "retry":
                Assert.Equal("command", action.Kind);
                Assert.Equal("appsurface", action.Command!.Executable);
                var expectedArguments = new List<string>
                {
                    "durable", "doctor", "--connection-env", request!.ConnectionEnvironmentName,
                    "--runtime-epoch-env", request.EpochEnvironmentName,
                };
                if (request.WorkerId is not null)
                {
                    expectedArguments.AddRange(["--worker-id", request.WorkerId, "--stale-after", "15s"]);
                }
                expectedArguments.AddRange(["--timeout", "10s", "--format", "json"]);
                Assert.Equal(expectedArguments, action.Command.Arguments);
                Assert.InRange(action.Command.Arguments.Count, 1, 14);
                Assert.All(action.Command.Arguments, static argument => Assert.InRange(argument.Length, 1, 200));
                break;
            default:
                throw new InvalidDataException($"Unknown matrix next action '{row.NextAction}'.");
        }
    }
}
