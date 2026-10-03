using System.Diagnostics;
using System.Text.Json;
using ForgeTrust.AppSurface.Cli;
using Npgsql;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Proves the shared v1 diagnosis matrix against disposable PostgreSQL stores and the real CLI pipeline.</summary>
[Collection(ProgramEntryPointCollection.Name)]
public sealed class DurableDoctorIntegrationTests
{
    private const string ConnectionEnvironmentName = "ISSUE801_PRIVATE_CONNECTION";
    private const string EpochEnvironmentName = "ISSUE801_PRIVATE_RUNTIME_EPOCH";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);
    private static readonly IReadOnlyDictionary<string, string> CheckStatus = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["P"] = "passed",
        ["F"] = "finding",
        ["NC"] = "not-checked",
        ["NR"] = "not-requested",
    };

    [Fact]
    public async Task Compatible_epoch_and_worker_rows_match_the_live_service_and_cli_matrix()
    {
        var rows = Rows("D01", "D02", "D03", "D04", "D05", "D06", "D07", "D08", "D09", "D10");
        await using var fixture = await DurableDoctorFixture.CreateAsync();
        await fixture.TrapPruneFunctionBodyAsync();

        foreach (var row in rows)
        {
            await ResetRuntimeFactsAsync(fixture);
            await ArrangeRuntimeRowAsync(fixture, row);
            await AssertLiveRowAsync(fixture, row);

            if (row.Id == "D03")
            {
                // Reuse the same ten-second database heartbeat: it is current at 15s and stale at 5s.
                await AssertLiveRowAsync(fixture, MatrixRow("D05"), TimeSpan.FromSeconds(5));
            }
        }
    }

    [Fact]
    public async Task Schema_compatibility_rows_short_circuit_later_database_checks()
    {
        var families = new (string[] Ids, string Mutation)[]
        {
            (["D11", "D12"], "DROP SCHEMA appsurface_durable CASCADE"),
            (["D13", "D14"], "DELETE FROM appsurface_durable.schema_migration WHERE version = 11; UPDATE appsurface_durable.store_metadata SET schema_version = 10, minimum_reader_version = 10, maximum_reader_version = 10, minimum_writer_version = 10, maximum_writer_version = 10 WHERE singleton"),
            (["D15", "D16"], "UPDATE appsurface_durable.store_metadata SET minimum_reader_version = 1, maximum_reader_version = 10, minimum_writer_version = 1, maximum_writer_version = 10 WHERE singleton"),
            (["D17", "D18"], "UPDATE appsurface_durable.schema_migration SET sha256 = repeat('f', 64) WHERE version = 11"),
        };

        foreach (var (ids, mutation) in families)
        {
            await using var fixture = await DurableDoctorFixture.CreateAsync();
            await fixture.TrapPruneFunctionBodyAsync();
            await fixture.MutateAsync(mutation);
            foreach (var row in Rows(ids))
            {
                await AssertLiveRowAsync(fixture, row);
            }
        }
    }

    [Fact]
    public async Task Retention_and_credential_rows_match_the_matrix_without_pruning_or_partial_facts()
    {
        var functionRow = MatrixRow("D19");
        await using (var fixture = await DurableDoctorFixture.CreateAsync())
        {
            await fixture.TrapPruneFunctionBodyAsync();
            await fixture.MutateAsync(
                "ALTER FUNCTION appsurface_durable.prune_runtime_heartbeats(interval,integer,text,uuid) RENAME TO issue801_missing_prune");
            await AssertLiveRowAsync(fixture, functionRow);
        }

        var absentIndex = MatrixRow("D20");
        await using (var fixture = await DurableDoctorFixture.CreateAsync())
        {
            await fixture.TrapPruneFunctionBodyAsync();
            await fixture.MutateAsync("DROP INDEX appsurface_durable.ix_runtime_heartbeat_retention");
            await fixture.SeedHeartbeatAsync(age: TimeSpan.FromSeconds(10));
            await AssertLiveRowAsync(fixture, absentIndex);
        }

        var invalidIndex = MatrixRow("D21");
        await using (var fixture = await DurableDoctorFixture.CreateAsync())
        {
            await fixture.TrapPruneFunctionBodyAsync();
            await fixture.MutateAsync("DROP INDEX appsurface_durable.ix_runtime_heartbeat_retention; CREATE INDEX ix_runtime_heartbeat_retention ON appsurface_durable.runtime_heartbeat (last_heartbeat_at DESC, worker_id)");
            await AssertLiveRowAsync(fixture, invalidIndex);
        }

        var unsafeRows = Rows("D22", "D23");
        await using (var fixture = await DurableDoctorFixture.CreateAsync())
        {
            await fixture.TrapPruneFunctionBodyAsync();
            await fixture.MutateAsync($"GRANT DELETE ON appsurface_durable.runtime_heartbeat TO {QuoteIdentifier(fixture.RuntimeRole)}");
            foreach (var row in unsafeRows)
            {
                var outcome = await AssertLiveRowAsync(fixture, row);
                Assert.NotNull(outcome.Observation);
                Assert.Null(outcome.Observation.Schema);
                Assert.Null(outcome.Observation.RetentionFailures);
                Assert.Null(outcome.Observation.ObservedAtUtc);
                Assert.Null(outcome.Observation.StoreId);
                Assert.Null(outcome.Observation.ActiveRuntimeEpoch);
                Assert.Null(outcome.Observation.Heartbeat);
                Assert.Null(outcome.Result.Schema);
                Assert.Null(outcome.Result.Retention);
                Assert.Null(outcome.Result.Worker);
            }
        }
    }

    [Fact]
    public async Task Terminal_dependency_rows_discard_all_database_facts_for_store_only_and_paired_requests()
    {
        var rows = Rows("D24", "D25");
        await using var fixture = await DurableDoctorFixture.CreateAsync();
        await fixture.TrapPruneFunctionBodyAsync();
        var databaseName = new NpgsqlConnectionStringBuilder(fixture.AdministrativeConnectionString).Database
            ?? throw new InvalidOperationException("The test fixture did not select a database.");
        await fixture.MutateAsync($"REVOKE CONNECT ON DATABASE {QuoteIdentifier(databaseName)} FROM PUBLIC, {QuoteIdentifier(fixture.RuntimeRole)}");

        foreach (var row in rows)
        {
            var result = await AssertLiveRowAsync(fixture, row);
            Assert.Equal("unavailable", result.Result.Status);
            Assert.Contains("dependency", result.Result.Findings.Single().FailedChecks);
            Assert.Null(result.Observation);
        }
    }

    [Fact]
    public async Task Unexpected_catalog_contract_failure_is_a_secret_free_terminal_matrix_result()
    {
        var row = MatrixRow("D27");
        await using var fixture = await DurableDoctorFixture.CreateAsync();
        await fixture.TrapPruneFunctionBodyAsync();
        await fixture.MutateAsync("ALTER TABLE appsurface_durable.store_metadata RENAME COLUMN minimum_reader_version TO issue801_hidden_reader_version");

        var outcome = await AssertLiveRowAsync(fixture, row);

        Assert.Equal("failed", outcome.Result.Status);
        Assert.Equal(["catalog-contract"], outcome.Result.Findings.Single().FailedChecks);
        Assert.Null(outcome.Observation);
    }

    [Fact]
    public async Task Operator_workflow_probe_records_real_cli_timings_with_the_selected_connection_environment()
    {
        var evidencePath = Environment.GetEnvironmentVariable("APPSURFACE_DOCTOR_WORKFLOW_EVIDENCE_PATH");
        await using var fixture = await DurableDoctorFixture.CreateAsync();
        await fixture.TrapPruneFunctionBodyAsync();

        var epochTimer = Stopwatch.StartNew();
        var epochStarted = DateTimeOffset.UtcNow;
        var wrongEpoch = Guid.NewGuid();
        await fixture.MutateAsync($"UPDATE appsurface_durable.store_metadata SET active_runtime_epoch = '{wrongEpoch:D}' WHERE singleton");
        var epochDiagnosis = await RunTimedDoctorAsync(fixture, MatrixRow("D08"), "json");
        AssertCliMatrix(MatrixRow("D08"), epochDiagnosis.Run, null, "json");
        await fixture.MutateAsync($"UPDATE appsurface_durable.store_metadata SET active_runtime_epoch = '{fixture.RuntimeEpoch:D}' WHERE singleton");
        var epochRecovery = await RunTimedDoctorAsync(fixture, MatrixRow("D01"), "json");
        AssertCliMatrix(MatrixRow("D01"), epochRecovery.Run, null, "json");
        var epochRecoveryText = await RunTimedDoctorAsync(fixture, MatrixRow("D01"), "text");
        AssertTextMatrix(MatrixRow("D01"), epochRecoveryText.Run, null, "text");
        AssertStoreId(fixture.StoreId, epochDiagnosis);
        AssertStoreId(fixture.StoreId, epochRecovery);
        Assert.Contains(fixture.StoreId.ToString("D"), epochRecoveryText.Run.StandardOutput, StringComparison.Ordinal);
        epochTimer.Stop();
        var epochEnded = DateTimeOffset.UtcNow;

        var history = await ReadMigrationElevenAsync(fixture);
        var schemaTimer = Stopwatch.StartNew();
        var schemaStarted = DateTimeOffset.UtcNow;
        await fixture.MutateAsync("DELETE FROM appsurface_durable.schema_migration WHERE version = 11; UPDATE appsurface_durable.store_metadata SET schema_version = 10, minimum_reader_version = 10, maximum_reader_version = 10, minimum_writer_version = 10, maximum_writer_version = 10 WHERE singleton");
        var schemaDiagnosis = await RunTimedDoctorAsync(fixture, MatrixRow("D13"), "json");
        AssertCliMatrix(MatrixRow("D13"), schemaDiagnosis.Run, null, "json");
        var schemaStatus = await RunTimedAsync(
            fixture,
            ["durable", "schema", "status", "--connection-env", ConnectionEnvironmentName],
            configuredEpoch: fixture.RuntimeEpoch);
        Assert.Equal(0, schemaStatus.Run.ExitCode);
        Assert.Contains("Compatibility: UpgradeRequired", schemaStatus.Run.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Installed: 10", schemaStatus.Run.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Required: 11", schemaStatus.Run.StandardOutput, StringComparison.Ordinal);
        AssertSafeSinks(schemaStatus.Run, fixture.RuntimeConnectionString);
        await RestoreMigrationElevenAsync(fixture, history);
        var schemaRecovery = await RunTimedDoctorAsync(fixture, MatrixRow("D01"), "json");
        AssertCliMatrix(MatrixRow("D01"), schemaRecovery.Run, null, "json");
        var schemaRecoveryText = await RunTimedDoctorAsync(fixture, MatrixRow("D01"), "text");
        AssertTextMatrix(MatrixRow("D01"), schemaRecoveryText.Run, null, "text");
        AssertStoreId(fixture.StoreId, schemaDiagnosis);
        AssertStoreId(fixture.StoreId, schemaRecovery);
        Assert.Contains(fixture.StoreId.ToString("D"), schemaRecoveryText.Run.StandardOutput, StringComparison.Ordinal);
        schemaTimer.Stop();
        var schemaEnded = DateTimeOffset.UtcNow;

        if (!string.IsNullOrWhiteSpace(evidencePath))
        {
            var evidence = new
            {
                schemaVersion = 1,
                evidenceKind = "maintainer-postgresql-operator-workflow-probe",
                recordedAtUtc = DateTimeOffset.UtcNow,
                environment = new
                {
                    dotnet = "preinstalled on the existing host",
                    docker = "preinstalled and available on the existing host",
                    coldHostMeasured = false,
                    packageInstallMeasured = false,
                    buildTimeMeasuredSeparately = false,
                    originalGoalElapsed = "14:41:51Z; implementation-task clock, not adopter onboarding",
                },
                measurementBoundary = "AppSurfaceCliApp.RunAsync invocations against one disposable PostgreSQL store; per-command UTC timings and aggregate CLI elapsed are recorded. Workflow wall time also includes fixture state edits and recovery. This is not a published-tool install, cold-host trial, operator-comprehension study, or adopter-onboarding measurement.",
                selectedEnvironment = new
                {
                    connectionEnvironmentName = ConnectionEnvironmentName,
                    connectionEnvironmentValue = "<redacted; identical fixture runtime connection for every run>",
                    connectionEnvironmentConsistency = "Every CLI call used the same fixture RuntimeConnectionString; the shared fixture verifies each call restores the prior process environment.",
                    runtimeEpochEnvironmentName = EpochEnvironmentName,
                    runtimeEpochEnvironmentValue = fixture.RuntimeEpoch.ToString("D"),
                    connectionEnvironmentPreservedAcrossRecovery = true,
                },
                priorManualInterpretationContext = new
                {
                    workflow = "Previously combine schema status, runtime health/assessment, process logs, and tests; compare installed and required schema versions, epoch compatibility, and heartbeat freshness manually.",
                    sources = new[]
                    {
                        "docs/designs/issue-801-durable-runtime-doctor.md#problem-and-useful-outcome",
                        "Durable/operational-assessments.md#health-predicates",
                        "Durable/heartbeat-retention-operations.md#deploy-schema-11",
                    },
                },
                cleanBoundaryFromActualTextOutput = ExtractBoundary(epochRecoveryText.Run.StandardOutput),
                workflows = new[]
                {
                    Workflow("custom-environment-store-epoch-mismatch", epochStarted, epochEnded, epochTimer.Elapsed.TotalMilliseconds, fixture.StoreId,
                        "Synthetic fixture changed active_runtime_epoch; same isolated store and configured environment were restored before the clean rerun.",
                        [epochDiagnosis], [epochRecovery, epochRecoveryText]),
                    Workflow("custom-environment-incompatible-schema", schemaStarted, schemaEnded, schemaTimer.Elapsed.TotalMilliseconds, fixture.StoreId,
                        "Synthetic fixture removed only the schema-11 history row and lowered schema_version; the captured row and canonical metadata were restored on the same store. This is not a migration execution claim.",
                        [schemaDiagnosis, schemaStatus], [schemaRecovery, schemaRecoveryText]),
                },
            };
            var fullPath = Path.GetFullPath(evidencePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        }
    }

    private static async Task<LiveOutcome> AssertLiveRowAsync(
        DurableDoctorFixture fixture,
        DurableDoctorMatrixRow row,
        TimeSpan? staleAfterOverride = null,
        string? privateConnection = null)
    {
        var request = fixture.CreateRequest(
            row.WorkerPair ? DurableDoctorFixture.RuntimeWorkerId : null,
            row.WorkerPair ? staleAfterOverride ?? DurableDoctorFixture.ReferenceStaleAfter : null,
            ConnectionEnvironmentName,
            EpochEnvironmentName,
            format: "json",
            timeout: CommandTimeout);
        var connection = privateConnection ?? fixture.RuntimeConnectionString;
        var fingerprintBefore = await fixture.ReadDurableStateFingerprintAsync();
        DurableDoctorObservation? observation = null;
        DurableDoctorResult result;
        try
        {
            observation = await new DurableDoctorService().InspectAsync(connection, request, CancellationToken.None);
            result = DurableDoctorClassifier.Classify(request, observation);
        }
        catch (DurableDoctorFailureException failure)
        {
            result = DurableDoctorClassifier.Terminal(request, failure.Kind switch
            {
                DurableDoctorFailureKind.Unavailable => "unavailable",
                DurableDoctorFailureKind.Canceled => "canceled",
                _ => "failed",
            }, failure.Categories);
        }

        AssertMatrixResult(row, result, request.StaleAfter);
        if (row.TerminalCategories.Count > 0)
        {
            Assert.All(result.Findings.Single().FailedChecks,
                category => Assert.Contains(category, row.TerminalCategories));
        }

        var jsonRun = await fixture.RunCliAsync(
            DoctorArguments(request, "json"), ConnectionEnvironmentName, EpochEnvironmentName,
            connection, fixture.RuntimeEpoch);
        AssertCliMatrix(row, jsonRun, request.StaleAfter, "json");
        AssertSafeSinks(jsonRun, connection);

        var textRun = await fixture.RunCliAsync(
            DoctorArguments(request, "text"), ConnectionEnvironmentName, EpochEnvironmentName,
            connection, fixture.RuntimeEpoch);
        AssertTextMatrix(row, textRun, request.StaleAfter, "text");
        AssertSafeSinks(textRun, connection);
        Assert.Equal(fingerprintBefore, await fixture.ReadDurableStateFingerprintAsync());
        return new LiveOutcome(observation, result);
    }

    private static async Task ArrangeRuntimeRowAsync(DurableDoctorFixture fixture, DurableDoctorMatrixRow row)
    {
        switch (row.Id)
        {
            case "D01":
            case "D02":
            case "D08":
                if (row.Id == "D08")
                {
                    var mismatch = Guid.NewGuid();
                    await fixture.MutateAsync($"UPDATE appsurface_durable.store_metadata SET active_runtime_epoch = '{mismatch:D}' WHERE singleton");
                }
                return;
            case "D03":
                await fixture.SeedHeartbeatAsync(age: TimeSpan.FromSeconds(10));
                return;
            case "D04":
                // The exact equality boundary is deterministic in the pure matrix; live PG verifies captured-clock age below 15s.
                await fixture.SeedHeartbeatAsync(age: TimeSpan.FromSeconds(12));
                return;
            case "D05":
                await fixture.SeedHeartbeatAsync(age: TimeSpan.FromSeconds(20));
                return;
            case "D06":
                await fixture.SeedHeartbeatAsync(age: TimeSpan.FromSeconds(20), draining: true);
                return;
            case "D07":
                var storeMismatch = Guid.NewGuid();
                await fixture.MutateAsync($"UPDATE appsurface_durable.store_metadata SET active_runtime_epoch = '{storeMismatch:D}' WHERE singleton");
                await fixture.SeedHeartbeatAsync(age: TimeSpan.FromSeconds(10), runtimeEpoch: storeMismatch);
                return;
            case "D09":
                await fixture.SeedHeartbeatAsync(age: TimeSpan.FromSeconds(10), runtimeEpoch: Guid.NewGuid());
                return;
            case "D10":
                await fixture.SeedHeartbeatAsync(age: TimeSpan.FromSeconds(-30));
                return;
            default:
                throw new InvalidOperationException($"No live PostgreSQL setup exists for matrix row {row.Id}.");
        }
    }

    private static async Task ResetRuntimeFactsAsync(DurableDoctorFixture fixture) => await fixture.MutateAsync(
        $"DELETE FROM appsurface_durable.runtime_heartbeat; UPDATE appsurface_durable.store_metadata SET active_runtime_epoch = '{fixture.RuntimeEpoch:D}' WHERE singleton");

    private static async Task<MigrationEleven> ReadMigrationElevenAsync(DurableDoctorFixture fixture)
    {
        await using var connection = new NpgsqlConnection(fixture.AdministrativeConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT name, sha256, applied_at FROM appsurface_durable.schema_migration WHERE version = 11", connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new MigrationEleven(reader.GetString(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2));
    }

    private static async Task RestoreMigrationElevenAsync(DurableDoctorFixture fixture, MigrationEleven migration)
    {
        await using var connection = new NpgsqlConnection(fixture.AdministrativeConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            WITH restored AS
            (
                INSERT INTO appsurface_durable.schema_migration (version, name, sha256, applied_at)
                VALUES (11, @name, @sha, @applied_at)
                RETURNING version
            )
            UPDATE appsurface_durable.store_metadata
            SET schema_version = 11,
                minimum_reader_version = 1,
                maximum_reader_version = 11,
                minimum_writer_version = 1,
                maximum_writer_version = 11
            WHERE singleton AND EXISTS (SELECT 1 FROM restored)
            """, connection);
        command.Parameters.AddWithValue("name", migration.Name);
        command.Parameters.AddWithValue("sha", migration.Sha256);
        command.Parameters.AddWithValue("applied_at", migration.AppliedAtUtc);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task<TimedCliRun> RunTimedDoctorAsync(
        DurableDoctorFixture fixture,
        DurableDoctorMatrixRow row,
        string format)
    {
        var request = fixture.CreateRequest(
            row.WorkerPair ? DurableDoctorFixture.RuntimeWorkerId : null,
            row.WorkerPair ? DurableDoctorFixture.ReferenceStaleAfter : null,
            ConnectionEnvironmentName,
            EpochEnvironmentName,
            format,
            CommandTimeout);
        return await RunTimedAsync(fixture, DoctorArguments(request, format), fixture.RuntimeEpoch, row);
    }

    private static async Task<TimedCliRun> RunTimedAsync(
        DurableDoctorFixture fixture,
        string[] arguments,
        Guid configuredEpoch,
        DurableDoctorMatrixRow? matrixRow = null)
    {
        var stateBefore = await fixture.ReadDurableStateFingerprintAsync();
        var timer = Stopwatch.StartNew();
        var started = DateTimeOffset.UtcNow;
        var run = await fixture.RunCliAsync(arguments, ConnectionEnvironmentName, EpochEnvironmentName,
            configuredEpoch: configuredEpoch);
        var ended = DateTimeOffset.UtcNow;
        timer.Stop();
        AssertSafeSinks(run, fixture.RuntimeConnectionString);
        Assert.Equal(stateBefore, await fixture.ReadDurableStateFingerprintAsync());
        return new TimedCliRun(started, ended, timer.Elapsed.TotalMilliseconds, arguments, run, matrixRow);
    }

    private static string[] DoctorArguments(DurableDoctorRequest request, string format)
    {
        var arguments = new List<string>
        {
            "durable", "doctor",
            "--connection-env", request.ConnectionEnvironmentName,
            "--runtime-epoch-env", request.EpochEnvironmentName,
        };
        if (request.WorkerId is not null)
        {
            arguments.AddRange(["--worker-id", request.WorkerId, "--stale-after", FormatSeconds(request.StaleAfter!.Value)]);
        }
        arguments.AddRange(["--timeout", FormatSeconds(request.Timeout), "--format", format]);
        return arguments.ToArray();
    }

    private static void AssertMatrixResult(DurableDoctorMatrixRow row, DurableDoctorResult result, TimeSpan? staleAfter)
    {
        Assert.Equal(row.ExitCode, result.ExitCode);
        Assert.Equal(StatusForExit(row.ExitCode), result.Status);
        Assert.Equal(row.ExpectedChecks.Select(code => CheckStatus[code]), result.RequestedChecks.Select(check => check.Status));
        Assert.Equal(row.WorkerPair, result.RequestedChecks[^1].Requested);
        Assert.Equal(row.ExpectedCodes, result.Findings.Select(finding => finding.Code));
        AssertAction(row, result.NextAction, staleAfter, "json");

        if (row.Scenario.StartsWith("schema-", StringComparison.Ordinal))
        {
            Assert.Null(result.Retention);
            Assert.Null(result.Worker);
            Assert.Null(result.ObservedAtUtc);
            Assert.Equal(row.Scenario switch
            {
                var value when value.StartsWith("schema-missing", StringComparison.Ordinal) => "missing",
                var value when value.StartsWith("schema-upgrade-required", StringComparison.Ordinal) => "upgrade-required",
                var value when value.StartsWith("schema-store-too-new", StringComparison.Ordinal) => "store-too-new",
                _ => "inconsistent",
            }, result.Schema?.Compatibility);
        }

        if (result.Worker is { Found: true } worker)
        {
            Assert.NotNull(result.ObservedAtUtc);
            Assert.NotNull(worker.LastHeartbeatAtUtc);
            var recomputedAge = Math.Max(0L, (result.ObservedAtUtc!.Value - worker.LastHeartbeatAtUtc!.Value).Ticks);
            Assert.Equal(recomputedAge, worker.AgeTicks);
            Assert.Equal(staleAfter!.Value.Ticks, worker.StaleAfterTicks);
        }
    }

    private static void AssertCliMatrix(DurableDoctorMatrixRow row, DurableDoctorCliRun run, TimeSpan? staleAfter, string format)
    {
        Assert.Equal(row.ExitCode, run.ExitCode);
        Assert.EndsWith("\n", run.StandardOutput, StringComparison.Ordinal);
        Assert.False(run.StandardOutput.EndsWith("\n\n", StringComparison.Ordinal));
        using var document = JsonDocument.Parse(run.StandardOutput);
        var root = document.RootElement;
        Assert.Equal(StatusForExit(row.ExitCode), root.GetProperty("status").GetString());
        Assert.Equal(row.ExitCode, root.GetProperty("exitCode").GetInt32());
        Assert.Equal(row.ExpectedChecks.Select(code => CheckStatus[code]),
            root.GetProperty("requestedChecks").EnumerateArray().Select(check => check.GetProperty("status").GetString()));
        Assert.Equal(row.ExpectedCodes,
            root.GetProperty("findings").EnumerateArray().Select(finding => finding.GetProperty("code").GetString()));
        Assert.Equal(row.WorkerPair, root.GetProperty("requestedChecks").EnumerateArray().Last().GetProperty("requested").GetBoolean());
        AssertJsonAge(root);
        AssertJsonAction(row, root.GetProperty("nextAction"), staleAfter, format);
        if (row.TerminalCategories.Count > 0)
        {
            var categories = root.GetProperty("findings")[0].GetProperty("failedChecks").EnumerateArray()
                .Select(category => category.GetString()!).ToArray();
            Assert.All(categories, category => Assert.Contains(category, row.TerminalCategories));
        }
        if (row.Id is "D22" or "D23")
        {
            Assert.Equal(JsonValueKind.Null, root.GetProperty("schema").ValueKind);
            Assert.Equal(JsonValueKind.Null, root.GetProperty("retention").ValueKind);
            Assert.Equal(JsonValueKind.Null, root.GetProperty("worker").ValueKind);
            Assert.Equal(JsonValueKind.Null, root.GetProperty("observedAtUtc").ValueKind);
            Assert.Equal(JsonValueKind.Null, root.GetProperty("activeRuntimeEpoch").ValueKind);
        }
    }

    private static void AssertTextMatrix(DurableDoctorMatrixRow row, DurableDoctorCliRun run, TimeSpan? staleAfter, string format)
    {
        Assert.Equal(row.ExitCode, run.ExitCode);
        Assert.EndsWith("\n", run.StandardOutput, StringComparison.Ordinal);
        Assert.False(run.StandardOutput.EndsWith("\n\n", StringComparison.Ordinal));
        Assert.Contains($"Diagnosis: {StatusForExit(row.ExitCode)} (exit {row.ExitCode})", run.StandardOutput, StringComparison.Ordinal);
        foreach (var (name, state) in new[] { "credential", "schema", "epoch", "retention", "worker" }
                     .Zip(row.ExpectedChecks.Select(code => CheckStatus[code])))
        {
            Assert.Contains($"  {name}: {state}", run.StandardOutput, StringComparison.Ordinal);
        }
        var codes = run.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("  Code: ASDUR", StringComparison.Ordinal))
            .Select(line => line[8..]);
        Assert.Equal(row.ExpectedCodes, codes);
        AssertTextAction(row, run.StandardOutput, staleAfter, format);
    }

    private static void AssertJsonAge(JsonElement root)
    {
        var worker = root.GetProperty("worker");
        if (worker.ValueKind != JsonValueKind.Object || !worker.GetProperty("found").GetBoolean())
        {
            return;
        }

        var observed = root.GetProperty("observedAtUtc").GetDateTimeOffset();
        var lastHeartbeat = worker.GetProperty("lastHeartbeatAtUtc").GetDateTimeOffset();
        Assert.Equal(Math.Max(0L, (observed - lastHeartbeat).Ticks), worker.GetProperty("ageTicks").GetInt64());
    }

    private static void AssertAction(DurableDoctorMatrixRow row, DurableDoctorAction action, TimeSpan? staleAfter, string format)
    {
        if (row.NextAction == "verify")
        {
            Assert.Equal("application-verifier", action.Kind);
            Assert.Null(action.Command);
            return;
        }

        Assert.Equal("command", action.Kind);
        AssertActionArguments(row, action.Command!.Arguments, staleAfter, format);
    }

    private static void AssertActionArguments(DurableDoctorMatrixRow row, IReadOnlyList<string> arguments, TimeSpan? staleAfter, string format)
    {
        if (row.NextAction == "status")
        {
            Assert.Equal(["durable", "schema", "status", "--connection-env", ConnectionEnvironmentName], arguments);
            return;
        }
        if (row.NextAction == "help")
        {
            Assert.Equal(["durable", "doctor", "--help"], arguments);
            return;
        }

        Assert.Contains(ConnectionEnvironmentName, arguments);
        Assert.Contains(EpochEnvironmentName, arguments);
        Assert.Contains("--format", arguments);
        Assert.Contains(format, arguments);
        Assert.Contains("--timeout", arguments);
        Assert.Contains(FormatSeconds(CommandTimeout), arguments);
        if (row.WorkerPair)
        {
            Assert.Contains(DurableDoctorFixture.RuntimeWorkerId, arguments);
            Assert.Contains(FormatSeconds(staleAfter!.Value), arguments);
        }
    }

    private static void AssertJsonAction(DurableDoctorMatrixRow row, JsonElement action, TimeSpan? staleAfter, string format)
    {
        Assert.Equal(row.NextAction == "verify" ? "application-verifier" : "command", action.GetProperty("kind").GetString());
        var command = action.GetProperty("command");
        if (row.NextAction == "verify")
        {
            Assert.Equal(JsonValueKind.Null, command.ValueKind);
            return;
        }
        Assert.Equal("appsurface", command.GetProperty("executable").GetString());
        var arguments = command.GetProperty("arguments").EnumerateArray().Select(argument => argument.GetString()!).ToArray();
        AssertActionArguments(row, arguments, staleAfter, format);
    }

    private static void AssertTextAction(DurableDoctorMatrixRow row, string output, TimeSpan? staleAfter, string format)
    {
        if (row.NextAction == "verify")
        {
            Assert.Contains("Next action: Run the application's composition verifier", output, StringComparison.Ordinal);
        }
        else if (row.NextAction == "status")
        {
            Assert.Contains(
                $"Next command: {ShellQuote("appsurface")} {ShellQuote("durable")} {ShellQuote("schema")} {ShellQuote("status")} {ShellQuote("--connection-env")} {ShellQuote(ConnectionEnvironmentName)}",
                output,
                StringComparison.Ordinal);
        }
        else if (row.NextAction == "help")
        {
            Assert.Contains(
                $"Next command: {ShellQuote("appsurface")} {ShellQuote("durable")} {ShellQuote("doctor")} {ShellQuote("--help")}",
                output,
                StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains($"{ShellQuote("--connection-env")} {ShellQuote(ConnectionEnvironmentName)}", output, StringComparison.Ordinal);
            Assert.Contains($"{ShellQuote("--runtime-epoch-env")} {ShellQuote(EpochEnvironmentName)}", output, StringComparison.Ordinal);
            Assert.Contains($"{ShellQuote("--timeout")} {ShellQuote(FormatSeconds(CommandTimeout))}", output, StringComparison.Ordinal);
            Assert.Contains($"{ShellQuote("--format")} {ShellQuote(format)}", output, StringComparison.Ordinal);
            if (row.WorkerPair)
            {
                Assert.Contains($"{ShellQuote("--worker-id")} {ShellQuote(DurableDoctorFixture.RuntimeWorkerId)}", output, StringComparison.Ordinal);
                Assert.Contains($"{ShellQuote("--stale-after")} {ShellQuote(FormatSeconds(staleAfter!.Value))}", output, StringComparison.Ordinal);
            }
        }
    }

    private static string ShellQuote(string value) => $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";

    private static void AssertSafeSinks(DurableDoctorCliRun run, string connectionString)
    {
        Assert.DoesNotContain(DurableDoctorFixture.PasswordSentinel, run.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(DurableDoctorFixture.PasswordSentinel, run.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain(connectionString, run.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(connectionString, run.StandardError, StringComparison.Ordinal);
    }

    private static string ExtractBoundary(string output) => output.Split('\n')
        .Single(line => line.StartsWith("Boundary: These checks passed only for this captured observation", StringComparison.Ordinal));

    private static object Workflow(
        string name,
        DateTimeOffset startedAtUtc,
        DateTimeOffset endedAtUtc,
        double elapsedMilliseconds,
        Guid storeId,
        string simulatedState,
        IReadOnlyList<TimedCliRun> diagnoses,
        IReadOnlyList<TimedCliRun> recoveries) => new
        {
            name,
            startedAtUtc,
            endedAtUtc,
            workflowElapsedMilliseconds = elapsedMilliseconds,
            cliElapsedMilliseconds = diagnoses.Concat(recoveries).Sum(static run => run.ElapsedMilliseconds),
            storeId,
            sameStoreRecovery = true,
            simulatedState,
            runs = diagnoses.Concat(recoveries).Select(SummarizeRun).ToArray(),
        };

    private static object SummarizeRun(TimedCliRun timed)
    {
        if (timed.MatrixRow is not null)
        {
            return timed.Run.StandardOutput.TrimStart().StartsWith('{')
                ? SummarizeJsonRun(timed)
                : SummarizeTextDoctorRun(timed);
        }

        return new
        {
            startedAtUtc = timed.StartedAtUtc,
            endedAtUtc = timed.EndedAtUtc,
            elapsedMilliseconds = timed.ElapsedMilliseconds,
            executable = "appsurface",
            kind = "schema-status",
            arguments = timed.Arguments,
            exitCode = timed.Run.ExitCode,
            standardOutput = timed.Run.StandardOutput,
        };
    }

    private static object SummarizeTextDoctorRun(TimedCliRun timed)
    {
        var row = timed.MatrixRow!;
        var lines = timed.Run.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var checks = lines
            .Where(static line => line.StartsWith("  ", StringComparison.Ordinal) && line.Contains(": ", StringComparison.Ordinal))
            .Select(static line => line.Trim())
            .Select(static line => line.Split(": ", 2, StringSplitOptions.None))
            .Where(static parts => CheckStatus.Values.Contains(parts[1], StringComparer.Ordinal))
            .Select(static parts => new { name = parts[0], requested = parts[1] != "not-requested", status = parts[1] })
            .ToArray();
        var codes = lines
            .Where(static line => line.StartsWith("  Code: ASDUR", StringComparison.Ordinal))
            .Select(static line => line[8..])
            .ToArray();
        var nextAction = row.NextAction == "verify"
            ? new { kind = "application-verifier", executable = (string?)null, arguments = Array.Empty<string?>() }
            : new
            {
                kind = "command",
                executable = (string?)"appsurface",
                arguments = timed.Run.StandardOutput.Split("  Next command: ", StringSplitOptions.None).Last()
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Skip(1).Select(static argument => argument.Trim('\'')).ToArray() ?? Array.Empty<string?>(),
            };
        return new
        {
            startedAtUtc = timed.StartedAtUtc,
            endedAtUtc = timed.EndedAtUtc,
            elapsedMilliseconds = timed.ElapsedMilliseconds,
            executable = "appsurface",
            kind = "doctor",
            arguments = timed.Arguments,
            exitCode = timed.Run.ExitCode,
            expectedMatrix = ExpectedMatrix(row),
            status = StatusForExit(timed.Run.ExitCode),
            storeId = lines.Single(static line => line.StartsWith("Store ID: ", StringComparison.Ordinal))["Store ID: ".Length..],
            checks,
            codes,
            nextAction,
            standardOutput = timed.Run.StandardOutput,
        };
    }

    private static object SummarizeJsonRun(TimedCliRun timed)
    {
        using var document = JsonDocument.Parse(timed.Run.StandardOutput);
        var root = document.RootElement;
        var action = root.GetProperty("nextAction");
        return new
        {
            startedAtUtc = timed.StartedAtUtc,
            endedAtUtc = timed.EndedAtUtc,
            elapsedMilliseconds = timed.ElapsedMilliseconds,
            executable = "appsurface",
            kind = "doctor",
            arguments = timed.Arguments,
            exitCode = timed.Run.ExitCode,
            expectedMatrix = ExpectedMatrix(timed.MatrixRow),
            status = root.GetProperty("status").GetString(),
            storeId = root.GetProperty("storeId").ValueKind == JsonValueKind.String ? root.GetProperty("storeId").GetString() : null,
            checks = root.GetProperty("requestedChecks").EnumerateArray().Select(check => new
            {
                name = check.GetProperty("name").GetString(),
                requested = check.GetProperty("requested").GetBoolean(),
                status = check.GetProperty("status").GetString(),
            }).ToArray(),
            codes = root.GetProperty("findings").EnumerateArray().Select(finding => finding.GetProperty("code").GetString()).ToArray(),
            nextAction = new
            {
                kind = action.GetProperty("kind").GetString(),
                executable = action.GetProperty("command").ValueKind == JsonValueKind.Object
                    ? action.GetProperty("command").GetProperty("executable").GetString() : null,
                arguments = action.GetProperty("command").ValueKind == JsonValueKind.Object
                    ? action.GetProperty("command").GetProperty("arguments").EnumerateArray().Select(argument => argument.GetString()).ToArray()
                    : Array.Empty<string?>(),
            },
            standardOutput = timed.Run.StandardOutput,
        };
    }

    private static string StatusForExit(int exitCode) => exitCode switch
    {
        0 => "passed",
        1 => "failed",
        2 => "findings",
        3 => "invalid-input",
        4 => "unavailable",
        _ => throw new ArgumentOutOfRangeException(nameof(exitCode)),
    };

    private static object? ExpectedMatrix(DurableDoctorMatrixRow? row) => row is null ? null : new
    {
        id = row.Id,
        checkStatuses = row.ExpectedChecks.Select(code => CheckStatus[code]).ToArray(),
        codes = row.ExpectedCodes,
        exitCode = row.ExitCode,
        nextAction = row.NextAction,
    };

    private static void AssertStoreId(Guid expected, TimedCliRun run)
    {
        using var document = JsonDocument.Parse(run.Run.StandardOutput);
        Assert.Equal(expected.ToString("D"), document.RootElement.GetProperty("storeId").GetString());
    }

    private static string FormatSeconds(TimeSpan value) =>
        value.Ticks % TimeSpan.TicksPerSecond == 0
            ? $"{value.Ticks / TimeSpan.TicksPerSecond}s"
            : $"{value.TotalSeconds.ToString("0.#######", System.Globalization.CultureInfo.InvariantCulture)}s";

    private static DurableDoctorMatrixRow MatrixRow(string id) =>
        DurableDoctorV1Matrix.Load().Single(row => row.Id == id);

    private static IReadOnlyList<DurableDoctorMatrixRow> Rows(params string[] ids)
    {
        var rows = DurableDoctorV1Matrix.Load().ToDictionary(row => row.Id, StringComparer.Ordinal);
        return ids.Select(id => rows[id]).ToArray();
    }

    private static string QuoteIdentifier(string value) => new NpgsqlCommandBuilder().QuoteIdentifier(value);

    private sealed record LiveOutcome(DurableDoctorObservation? Observation, DurableDoctorResult Result);

    private sealed record MigrationEleven(string Name, string Sha256, DateTimeOffset AppliedAtUtc);

    private sealed record TimedCliRun(
        DateTimeOffset StartedAtUtc,
        DateTimeOffset EndedAtUtc,
        double ElapsedMilliseconds,
        string[] Arguments,
        DurableDoctorCliRun Run,
        DurableDoctorMatrixRow? MatrixRow);
}
