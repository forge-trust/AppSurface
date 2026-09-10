using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using ForgeTrust.AppSurface.Durable.Provider;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit.Abstractions;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

[Collection("PostgreSQL scale")]
public sealed class PostgreSqlScaleIntegrationTests
{
    private const int ConstrainedPoolSize = 8;
    private const int WarmBatchCount = 5;
    private const int WarmSamplesPerBatch = 16;
    private const int MixedConcurrency = 32;
    private readonly ITestOutputHelper _output;

    public PostgreSqlScaleIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task WorkDiscovery_UsesScopedFunctionAndDueIndexAcrossOneHundredThousandRowsAndOneHundredScopes()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var epoch = Guid.NewGuid();
        await SeedPendingWorkAsync(database.DataSource, epoch, workCount: 100_000, scopeCount: 100);

        await using var command = database.DataSource.CreateCommand(
            """
            EXPLAIN (FORMAT JSON)
            WITH requested(work_name, work_version) AS
            (
                VALUES ('scale-work'::text, '1'::text)
            )
            SELECT dispatch.dispatch_id,
                   dispatch.scope_id,
                   dispatch.aggregate_id,
                   dispatch.due_at,
                   dispatch.expected_revision,
                   dispatch.priority
            FROM requested
            JOIN appsurface_durable.work AS work
              ON work.work_name COLLATE "C" = requested.work_name COLLATE "C"
             AND work.work_version COLLATE "C" = requested.work_version COLLATE "C"
            JOIN appsurface_durable.dispatch AS dispatch
              ON dispatch.scope_id = work.scope_id
             AND dispatch.aggregate_kind = 'work'
             AND dispatch.aggregate_id = work.work_id
            WHERE dispatch.state IN ('available', 'leased')
              AND dispatch.due_at <= clock_timestamp()
            ORDER BY dispatch.due_at, dispatch.priority DESC, dispatch.dispatch_id
            LIMIT 1000;
            """);
        var plan = (string)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("PostgreSQL returned no discovery plan."));

        Assert.Contains("ix_work_contract_dispatch_lookup", plan, StringComparison.Ordinal);
        var selection = new PostgreSqlDurableWorkContractSelection(new ScaleWorkRegistry(
            [new DurableWorkContractIdentity("scale-work", "1")]));
        var store = new PostgreSqlDurableWorkStore(database.DataSource, epoch);
        Assert.Equal(900, (await store.DiscoverAsync(selection, 1_000)).Count);
    }

    [Fact]
    public async Task FlowDiscovery_UsesDueIndexAcrossOneHundredThousandRowsAndOneHundredScopes()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var epoch = Guid.NewGuid();
        await SeedReadyFlowsAsync(database.DataSource, epoch, flowCount: 100_000, scopeCount: 100);

        await using var command = database.DataSource.CreateCommand(
            """
            EXPLAIN (FORMAT JSON)
            SELECT dispatch_id, scope_id, kind, flow_instance_id, timer_id,
                   due_at, expected_revision, priority
            FROM appsurface_durable.flow_dispatch
            WHERE state IN ('available', 'leased')
              AND due_at <= clock_timestamp()
            ORDER BY due_at, priority DESC, dispatch_id
            LIMIT 1000;
            """);
        var plan = (string)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("PostgreSQL returned no Flow discovery plan."));

        Assert.Contains("ix_flow_dispatch_due", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("Seq Scan", plan, StringComparison.Ordinal);
        Assert.Equal(1_000, await CountAsync(
            database.DataSource,
            """
            SELECT count(*)
            FROM
            (
                SELECT dispatch_id
                FROM appsurface_durable.flow_dispatch
                WHERE state IN ('available', 'leased')
                  AND due_at <= clock_timestamp()
                ORDER BY due_at, priority DESC, dispatch_id
                LIMIT 1000
            ) AS candidates;
            """));
    }

    [Fact]
    public async Task RuntimeHealth_UsesSelectiveIndexesAndKeepsSparseAndDenseWarmP95BelowOneSecond()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        const int rowsPerSurface = 100_000;
        const int sparseDueRowsPerSurface = 100;
        await SeedRuntimeHealthBacklogAsync(
            database.DataSource,
            epoch,
            rowsPerSurface,
            sparseDueRowsPerSurface);
        await schema.InitializeRuntimeEpochAsync(epoch, "scale-tests", "runtime-health");
        var status = await schema.GetStatusAsync();
        var dispatcherConnection = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            ApplicationName = "appsurface-runtime-health-scale-dispatcher",
            MaxPoolSize = ConstrainedPoolSize,
        };
        var runtimeConnection = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            ApplicationName = "appsurface-runtime-health-scale-runtime",
            MaxPoolSize = ConstrainedPoolSize,
        };
        await using var dispatcherDataSource = NpgsqlDataSource.Create(dispatcherConnection.ConnectionString);
        await using var runtimeDataSource = NpgsqlDataSource.Create(runtimeConnection.ConnectionString);
        var statusConnectionAcquisitions = new ConcurrentQueue<TimeSpan>();
        var runtimeConnectionAcquisitions = new ConcurrentQueue<TimeSpan>();
        var services = new ServiceCollection();
        services.AddSingleton<IDurableRuntimeSchemaManager>(
            new PostgreSqlDurableRuntimeSchemaManager(
                runtimeDataSource,
                DurablePostgreSqlMigrationCatalog.Load(),
                observeStatusConnectionAcquisition: statusConnectionAcquisitions.Enqueue));
        services.AddSingleton<PostgreSqlDurableRuntimeHealth>(provider => new PostgreSqlDurableRuntimeHealth(
            provider.GetRequiredService<PostgreSqlDurableRuntimeRegistration>(),
            provider.GetRequiredService<IDurableRuntimeSchemaManager>(),
            NullLogger<PostgreSqlDurableRuntimeHealth>.Instance,
            readDatabaseTimestamp: null,
            observeRuntimeConnectionAcquisition: runtimeConnectionAcquisitions.Enqueue));
        services.AddAppSurfaceDurablePostgreSql(
            dispatcherDataSource,
            runtimeDataSource,
            new PostgreSqlDurableWorkOptions(epoch, status.StoreId),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options =>
            {
                options.WorkerId = "runtime-health-scale-worker";
                options.HostedSurfaces = DurableRuntimeSurface.All;
                options.SendWakeNotifications = false;
            });
        await using var provider = services.BuildServiceProvider();
        var health = provider.GetRequiredService<IDurableRuntimeHealth>();
        var pump = provider.GetRequiredService<IDurableRuntimePumpAdmission>();
        await AssertRuntimeHealthFunctionDefinitionAsync(database.DataSource);
        await ProveConstrainedPoolRecoveryAsync(
            health,
            runtimeDataSource,
            statusConnectionAcquisitions,
            runtimeConnectionAcquisitions,
            sparseDueRowsPerSurface * 3L);
        WriteRunnerProfile();

        foreach (var (surfaces, expectedIndexes) in new[]
                 {
                     (1, new[] { "ix_dispatch_due" }),
                     (2, new[] { "ix_flow_dispatch_due" }),
                     (4, new[] { "ix_schedule_dispatch_due", "ix_schedule_dispatch_lease_expiry_due" }),
                     (7, new[]
                     {
                         "ix_dispatch_due",
                         "ix_flow_dispatch_due",
                         "ix_schedule_dispatch_due",
                         "ix_schedule_dispatch_lease_expiry_due",
                     }),
                 })
        {
            var plan = await ReadRuntimeHealthNestedPlanAsync(database.DataSource, surfaces);
            foreach (var expectedIndex in expectedIndexes)
            {
                Assert.True(
                    plan.Contains(expectedIndex, StringComparison.Ordinal),
                    $"Sparse runtime-health plan for mask {surfaces} did not use {expectedIndex}:{Environment.NewLine}{plan}");
            }

            Assert.True(
                !plan.Contains("\"Node Type\": \"Seq Scan\"", StringComparison.Ordinal),
                $"Sparse runtime-health plan for mask {surfaces} used a sequential scan:{Environment.NewLine}{plan}");
            Assert.True(
                plan.Contains("\"Shared Hit Blocks\"", StringComparison.Ordinal),
                $"Sparse runtime-health plan for mask {surfaces} omitted buffer evidence:{Environment.NewLine}{plan}");
            _output.WriteLine($"runtime-health installed-function nested plan for mask {surfaces}:{Environment.NewLine}{plan}");
        }

        var sparseEvidence = await MeasureWarmHealthBatchesAsync(
            health,
            statusConnectionAcquisitions,
            runtimeConnectionAcquisitions,
            database.DataSource,
            expectedDueCount: sparseDueRowsPerSurface * 3L);
        AssertWarmBatches("sparse", sparseEvidence);

        await MakeRuntimeHealthBacklogDenseAsync(database.DataSource, rowsPerSurface);
        foreach (var surfaces in new[] { 1, 2, 4, 7 })
        {
            var densePlan = await ReadRuntimeHealthNestedPlanAsync(database.DataSource, surfaces);
            Assert.Contains(
                "\"Shared Hit Blocks\"",
                densePlan,
                StringComparison.Ordinal);
            _output.WriteLine(
                $"runtime-health installed-function nested dense plan for mask {surfaces}:{Environment.NewLine}{densePlan}");
        }

        var denseEvidence = await MeasureWarmHealthBatchesAsync(
            health,
            statusConnectionAcquisitions,
            runtimeConnectionAcquisitions,
            database.DataSource,
            expectedDueCount: rowsPerSurface * 3L);
        AssertWarmBatches("dense", denseEvidence);

        var pumpRequest = new DurableRuntimePumpRequest(
            maximumItems: 1,
            timeBudget: TimeSpan.FromSeconds(1),
            surfaces: DurableRuntimeSurface.Work);
        var warmPump = await pump.TryRunOnceAsync(pumpRequest);
        Assert.Equal(DurableRuntimePumpAttemptKind.Completed, warmPump.Kind);

        var mixedWorkloadStart = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var allocationsBefore = GC.GetTotalAllocatedBytes(precise: true);
        var lockWaitsBefore = await ReadLockWaitCountAsync(database.DataSource);
        using var lockWaitMonitorCancellation = new CancellationTokenSource();
        var maximumLockWaits = MonitorMaximumLockWaitCountAsync(
            database.DataSource,
            mixedWorkloadStart.Task,
            lockWaitMonitorCancellation.Token);
        Clear(statusConnectionAcquisitions);
        Clear(runtimeConnectionAcquisitions);
        var mixedHealthReads = Enumerable.Range(0, MixedConcurrency)
            .Select(async _ =>
            {
                await mixedWorkloadStart.Task;
                var started = Stopwatch.GetTimestamp();
                var snapshot = await health.GetAsync();
                return (
                    Snapshot: snapshot,
                    Elapsed: Stopwatch.GetElapsedTime(started));
            })
            .ToArray();
        var claim = RunAfterAsync(
            mixedWorkloadStart.Task,
            () => ClaimOneScheduleDispatchAsync(database.DataSource));
        var heartbeat = RunAfterAsync(
            mixedWorkloadStart.Task,
            () => UpdateScaleHeartbeatAsync(database.DataSource));
        var concurrentPump = RunAfterAsync(
            mixedWorkloadStart.Task,
            () => pump.TryRunOnceAsync(pumpRequest));
        mixedWorkloadStart.SetResult();
        (DurableRuntimeHealthSnapshot Snapshot, TimeSpan Elapsed)[] mixedSnapshots;
        long claimResult;
        int heartbeatResult;
        DurableRuntimePumpAttempt pumpResult;
        try
        {
            mixedSnapshots = await Task.WhenAll(mixedHealthReads);
            claimResult = await claim;
            heartbeatResult = await heartbeat;
            pumpResult = await concurrentPump;
        }
        finally
        {
            await lockWaitMonitorCancellation.CancelAsync();
        }

        var maximumObservedLockWaits = await maximumLockWaits;
        process.Refresh();
        var cpuDelta = process.TotalProcessorTime - cpuBefore;
        var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocationsBefore;
        var lockWaitsAfter = await ReadLockWaitCountAsync(database.DataSource);
        Assert.All(
            mixedSnapshots,
            run =>
            {
                Assert.InRange(
                    run.Snapshot.DueDispatchCount,
                    rowsPerSurface * 3L - 1,
                    rowsPerSurface * 3L);
            });
        Assert.Equal(1L, claimResult);
        Assert.Equal(1, heartbeatResult);
        Assert.Equal(DurableRuntimePumpAttemptKind.Completed, pumpResult.Kind);
        ReportMixedEvidence(
            mixedSnapshots,
            Drain(statusConnectionAcquisitions, MixedConcurrency),
            Drain(runtimeConnectionAcquisitions, MixedConcurrency),
            cpuDelta,
            allocatedBytes,
            lockWaitsBefore,
            lockWaitsAfter,
            maximumObservedLockWaits);
        AssertWarmP95BelowOneSecond(
            "mixed concurrent",
            mixedSnapshots.Select(run => run.Elapsed).ToArray());
        Assert.Equal(
            rowsPerSurface * 3L - 1,
            (await health.GetAsync()).DueDispatchCount);
        Assert.Equal(0, lockWaitsAfter);
        Assert.Equal(0, maximumObservedLockWaits);
    }

    [Fact]
    public async Task FlowTransitions_RecordBoundedWalGrowthWithoutLockWaits()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var epoch = Guid.NewGuid();
        const int flowCount = 1_000;
        await SeedReadyFlowsAsync(database.DataSource, epoch, flowCount, scopeCount: 10);

        var before = await ReadWalLocationAsync(database.DataSource);
        var stopwatch = Stopwatch.StartNew();
        await using (var command = database.DataSource.CreateCommand(
            """
            UPDATE appsurface_durable.flow_instance
            SET state = 'evaluating',
                lease_generation = lease_generation + 1,
                lease_owner = 'scale-worker',
                lease_started_at = clock_timestamp(),
                lease_expires_at = clock_timestamp() + interval '1 minute',
                revision = revision + 1,
                updated_at = clock_timestamp()
            WHERE state = 'ready';

            UPDATE appsurface_durable.flow_dispatch
            SET state = 'leased',
                expected_revision = expected_revision + 1,
                updated_at = clock_timestamp()
            WHERE state = 'available';

            INSERT INTO appsurface_durable.flow_history
                (scope_id, flow_instance_id, aggregate_revision, transition_kind, details)
            SELECT scope_id, flow_instance_id, revision, 'scale_claimed', '{}'::jsonb
            FROM appsurface_durable.flow_instance;
            """))
        {
            await command.ExecuteNonQueryAsync();
        }

        stopwatch.Stop();
        var after = await ReadWalLocationAsync(database.DataSource);
        var walBytes = await ReadWalDifferenceAsync(database.DataSource, after, before);

        Assert.True(walBytes > 0, "Flow transitions must produce measurable WAL.");
        Assert.True(
            walBytes / flowCount < 32 * 1024,
            $"Flow claim/history transitions wrote {walBytes / flowCount:N0} WAL bytes per Flow.");
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(30),
            $"Flow claim/history transitions took {stopwatch.Elapsed} for {flowCount:N0} Flows.");
        Assert.Equal(0, await CountAsync(
            database.DataSource,
            """
            SELECT count(*)
            FROM pg_stat_activity
            WHERE wait_event_type = 'Lock'
              AND datname = current_database()
              AND pid <> pg_backend_pid();
            """));
    }

    [Fact]
    public async Task DisableScope_ProjectsTenThousandWorkItemsWithinThirtySeconds()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var epoch = Guid.NewGuid();
        await SeedPendingWorkAsync(database.DataSource, epoch, workCount: 10_000, scopeCount: 1);
        await InitializeEpochAsync(database.DataSource, epoch);
        var store = new PostgreSqlDurableWorkStore(database.DataSource, epoch);

        var stopwatch = Stopwatch.StartNew();
        var result = await store.DisableScopeAsync(
            new DurableScopeId("scope-1"),
            "scale-test",
            "scope-disable",
            expectedGeneration: 1);
        stopwatch.Stop();

        Assert.Equal(PostgreSqlScopeMutationOutcome.Applied, result.Outcome);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(30),
            $"Scope disable took {stopwatch.Elapsed} for 10,000 Work items.");
        Assert.Equal(10_000, await CountAsync(
            database.DataSource,
            "SELECT count(*) FROM appsurface_durable.work WHERE state = 'canceled_before_effect';"));
        Assert.Equal(10_000, await CountAsync(
            database.DataSource,
            "SELECT count(*) FROM appsurface_durable.dispatch WHERE state = 'terminal';"));
        Assert.Equal(10_000, await CountAsync(
            database.DataSource,
            "SELECT count(*) FROM appsurface_durable.work_history WHERE event_type = 'scope_disabled';"));
        Assert.Equal(1, await CountAsync(
            database.DataSource,
            "SELECT count(*) FROM appsurface_durable.scope_history WHERE event_type = 'disabled';"));
    }

    private static async ValueTask ApplySchemaAsync(PostgreSqlIntegrationTestDatabase database)
    {
        var manager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await manager.ApplyAsync();
    }

    private static async ValueTask InitializeEpochAsync(NpgsqlDataSource dataSource, Guid epoch)
    {
        await using var command = dataSource.CreateCommand(
            "UPDATE appsurface_durable.store_metadata SET active_runtime_epoch = @epoch WHERE singleton;");
        command.Parameters.AddWithValue("epoch", epoch);
        await command.ExecuteNonQueryAsync();
    }

    private static async ValueTask SeedPendingWorkAsync(
        NpgsqlDataSource dataSource,
        Guid epoch,
        int workCount,
        int scopeCount)
    {
        await using var command = dataSource.CreateCommand(
            """
            INSERT INTO appsurface_durable.scope (scope_id)
            SELECT 'scope-' || value
            FROM generate_series(1, @scope_count) AS value;

            INSERT INTO appsurface_durable.work
            (
                scope_id, work_id, activity_id, command_id, idempotency_key,
                work_name, work_version, contract_id, payload_schema_version, codec_id,
                payload, payload_sha256, payload_classification, payload_retention,
                request_fingerprint_schema, request_fingerprint_sha256,
                state, provider_safety, due_at, scope_generation, runtime_epoch,
                maximum_attempts, maximum_elapsed, backoff_algorithm,
                initial_retry_delay, maximum_retry_delay,
                lease_duration, lease_renewal_cadence, maximum_lease_lifetime
            )
            SELECT
                'scope-' || (((value - 1) % @scope_count) + 1),
                'work-' || value,
                'activity-' || value,
                'command-' || value,
                'idempotency-' || value,
                CASE WHEN value <= 900 THEN 'scale-work' ELSE 'unselected-scale-work-' || value END,
                '1', 'scale-contract', '1', 'application/json',
                decode('00', 'hex'), decode(repeat('00', 32), 'hex'), 'internal', 'default',
                'durable-work-request-v1', repeat('0', 64),
                'pending', 'idempotent', clock_timestamp() - interval '1 minute', 1, @epoch,
                3, interval '1 hour', 'exponential-v1',
                interval '1 second', interval '1 minute',
                interval '30 seconds', interval '10 seconds', interval '5 minutes'
            FROM generate_series(1, @work_count) AS value;

            INSERT INTO appsurface_durable.dispatch
                (dispatch_id, scope_id, aggregate_kind, aggregate_id, due_at, state, expected_revision)
            SELECT md5('dispatch-' || row_number() OVER ())::uuid,
                   scope_id,
                   'work',
                   work_id,
                   due_at,
                   'available',
                   revision
            FROM appsurface_durable.work;

            ANALYZE appsurface_durable.work;
            ANALYZE appsurface_durable.dispatch;
            """);
        command.Parameters.AddWithValue("scope_count", scopeCount);
        command.Parameters.AddWithValue("work_count", workCount);
        command.Parameters.AddWithValue("epoch", epoch);
        await command.ExecuteNonQueryAsync();
    }

    private static async ValueTask SeedReadyFlowsAsync(
        NpgsqlDataSource dataSource,
        Guid epoch,
        int flowCount,
        int scopeCount)
    {
        await using var command = dataSource.CreateCommand(
            """
            INSERT INTO appsurface_durable.scope (scope_id)
            SELECT 'flow-scope-' || value
            FROM generate_series(1, @scope_count) AS value;

            INSERT INTO appsurface_durable.flow_instance
            (
                scope_id, flow_instance_id, flow_id, flow_version, manifest_id, authoring_model,
                definition_fingerprint_schema, definition_fingerprint_sha256, current_node_id,
                state, revision, scope_generation, runtime_epoch
            )
            SELECT
                'flow-scope-' || (((value - 1) % @scope_count) + 1),
                'flow-' || value,
                'scale-flow',
                'v1',
                'scale-manifest',
                'generated-v1',
                'durable-flow-definition-v1',
                repeat('0', 64),
                'start',
                'ready',
                1,
                1,
                @epoch
            FROM generate_series(1, @flow_count) AS value;

            INSERT INTO appsurface_durable.flow_dispatch
                (dispatch_id, scope_id, kind, flow_instance_id, due_at, state, expected_revision)
            SELECT md5('flow-dispatch-' || row_number() OVER ())::uuid,
                   scope_id,
                   'flow',
                   flow_instance_id,
                   clock_timestamp() - interval '1 minute',
                   'available',
                   revision
            FROM appsurface_durable.flow_instance;

            ANALYZE appsurface_durable.flow_dispatch;
            """);
        command.Parameters.AddWithValue("scope_count", scopeCount);
        command.Parameters.AddWithValue("flow_count", flowCount);
        command.Parameters.AddWithValue("epoch", epoch);
        await command.ExecuteNonQueryAsync();
    }

    private static async ValueTask SeedRuntimeHealthBacklogAsync(
        NpgsqlDataSource dataSource,
        Guid epoch,
        int rowsPerSurface,
        int dueRowsPerSurface)
    {
        await using var command = dataSource.CreateCommand(
            """
            INSERT INTO appsurface_durable.scope (scope_id)
            VALUES ('health-scale-work'), ('health-scale-flow'), ('health-scale-schedule');

            INSERT INTO appsurface_durable.work
            (
                scope_id, work_id, activity_id, command_id, idempotency_key,
                work_name, work_version, contract_id, payload_schema_version, codec_id,
                payload, payload_sha256, payload_classification, payload_retention,
                request_fingerprint_schema, request_fingerprint_sha256,
                state, provider_safety, due_at, scope_generation, runtime_epoch,
                maximum_attempts, maximum_elapsed, backoff_algorithm,
                initial_retry_delay, maximum_retry_delay,
                lease_duration, lease_renewal_cadence, maximum_lease_lifetime
            )
            SELECT
                'health-scale-work',
                'health-work-' || value,
                'health-activity-' || value,
                'health-command-' || value,
                'health-key-' || value,
                'health-work',
                'v1',
                'health-contract',
                'v1',
                'application/json',
                decode('00', 'hex'),
                decode(repeat('00', 32), 'hex'),
                'internal',
                'default',
                'health-request-v1',
                repeat('0', 64),
                'pending',
                'idempotent',
                CASE
                    WHEN value <= @due_rows THEN timestamp with time zone '2000-01-01 00:00:00+00'
                    ELSE timestamp with time zone '2100-01-01 00:00:00+00'
                END,
                1,
                @epoch,
                3,
                interval '1 hour',
                'exponential-v1',
                interval '1 second',
                interval '1 minute',
                interval '30 seconds',
                interval '10 seconds',
                interval '5 minutes'
            FROM generate_series(1, @rows_per_surface) AS value;

            INSERT INTO appsurface_durable.dispatch
                (dispatch_id, scope_id, aggregate_kind, aggregate_id, due_at, state, expected_revision)
            SELECT
                md5('health-work-dispatch-' || value)::uuid,
                'health-scale-work',
                'work',
                'health-work-' || value,
                CASE
                    WHEN value <= @due_rows THEN timestamp with time zone '2000-01-01 00:00:00+00'
                    ELSE timestamp with time zone '2100-01-01 00:00:00+00'
                END,
                CASE WHEN value % 2 = 0 THEN 'leased' ELSE 'available' END,
                1
            FROM generate_series(1, @rows_per_surface) AS value;

            INSERT INTO appsurface_durable.flow_instance
            (
                scope_id, flow_instance_id, flow_id, flow_version, manifest_id, authoring_model,
                definition_fingerprint_schema, definition_fingerprint_sha256, current_node_id,
                state, revision, scope_generation, runtime_epoch
            )
            SELECT
                'health-scale-flow',
                'health-flow-' || value,
                'health-flow',
                'v1',
                'health-manifest',
                'tests',
                'health-definition-v1',
                repeat('0', 64),
                'start',
                'ready',
                1,
                1,
                @epoch
            FROM generate_series(1, @rows_per_surface) AS value;

            INSERT INTO appsurface_durable.flow_dispatch
                (dispatch_id, scope_id, kind, flow_instance_id, due_at, state, expected_revision)
            SELECT
                md5('health-flow-dispatch-' || value)::uuid,
                'health-scale-flow',
                'flow',
                'health-flow-' || value,
                CASE
                    WHEN value <= @due_rows THEN timestamp with time zone '2001-01-01 00:00:00+00'
                    ELSE timestamp with time zone '2100-01-01 00:00:00+00'
                END,
                CASE WHEN value % 2 = 0 THEN 'leased' ELSE 'available' END,
                1
            FROM generate_series(1, @rows_per_surface) AS value;

            INSERT INTO appsurface_durable.schedule_definition
            (
                scope_id, schedule_id, state, active_generation, revision, accepted_at_utc,
                cursor_utc, next_due_utc, scope_generation, runtime_epoch
            )
            SELECT
                'health-scale-schedule',
                'health-schedule-' || value,
                'active',
                1,
                1,
                timestamp with time zone '2000-01-01 00:00:00+00',
                timestamp with time zone '2000-01-01 00:00:00+00',
                timestamp with time zone '2100-01-01 00:00:00+00',
                1,
                @epoch
            FROM generate_series(1, @rows_per_surface) AS value;

            INSERT INTO appsurface_durable.schedule_dispatch
                (scope_id, schedule_id, dispatch_revision, due_at, state,
                 lease_owner, lease_generation, lease_expires_at)
            SELECT
                'health-scale-schedule',
                'health-schedule-' || value,
                value,
                CASE
                    WHEN value <= @due_rows / 2 THEN timestamp with time zone '2002-01-01 00:00:00+00'
                    ELSE timestamp with time zone '2100-01-01 00:00:00+00'
                END,
                CASE
                    WHEN value <= @due_rows / 2 THEN 'available'
                    WHEN value <= @due_rows THEN 'leased'
                    WHEN value % 2 = 0 THEN 'leased'
                    ELSE 'available'
                END,
                CASE
                    WHEN value > @due_rows / 2 AND (value <= @due_rows OR value % 2 = 0)
                        THEN 'health-scale-worker'
                    ELSE NULL
                END,
                CASE
                    WHEN value > @due_rows / 2 AND (value <= @due_rows OR value % 2 = 0) THEN 1
                    ELSE 0
                END,
                CASE
                    WHEN value > @due_rows / 2 AND value <= @due_rows
                        THEN timestamp with time zone '2002-01-02 00:00:00+00'
                    WHEN value > @due_rows AND value % 2 = 0
                        THEN timestamp with time zone '2100-01-01 00:00:00+00'
                    ELSE NULL
                END
            FROM generate_series(1, @rows_per_surface) AS value;

            ANALYZE appsurface_durable.dispatch;
            ANALYZE appsurface_durable.flow_dispatch;
            ANALYZE appsurface_durable.schedule_dispatch;
            """);
        command.CommandTimeout = 120;
        command.Parameters.AddWithValue("epoch", epoch);
        command.Parameters.AddWithValue("rows_per_surface", rowsPerSurface);
        command.Parameters.AddWithValue("due_rows", dueRowsPerSurface);
        await command.ExecuteNonQueryAsync();
    }

    private static async ValueTask AssertRuntimeHealthFunctionDefinitionAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT pg_catalog.pg_get_functiondef(routine.oid)
            FROM pg_catalog.pg_proc AS routine
            WHERE routine.oid = pg_catalog.to_regprocedure(
                'appsurface_durable.runtime_due_dispatch_health(integer)');
            """);
        var definition = (string?)(await command.ExecuteScalarAsync());
        Assert.False(
            string.IsNullOrWhiteSpace(definition),
            "The installed runtime_due_dispatch_health(integer) function definition was not found.");
        Assert.Contains("SECURITY DEFINER", definition, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("statement_timestamp()", definition, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dispatch.lease_expires_at", definition, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dispatch.aggregate_kind = 'work'", definition, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dispatch.state = 'available'", definition, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dispatch.state = 'leased'", definition, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("clock_timestamp()", definition, StringComparison.OrdinalIgnoreCase);
    }

    private static async ValueTask<string> ReadRuntimeHealthNestedPlanAsync(
        NpgsqlDataSource dataSource,
        int surfaces)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        var notices = new List<string>();
        connection.Notice += (_, args) => notices.Add(args.Notice.MessageText);

        await using (var configure = connection.CreateCommand())
        {
            configure.CommandText =
                """
                LOAD 'auto_explain';
                SET auto_explain.log_min_duration = 0;
                SET auto_explain.log_analyze = on;
                SET auto_explain.log_buffers = on;
                SET auto_explain.log_format = 'json';
                SET auto_explain.log_level = 'notice';
                SET auto_explain.log_nested_statements = on;
                SET auto_explain.log_timing = off;
                SET auto_explain.log_verbose = on;
                """;
            await configure.ExecuteNonQueryAsync();
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT *
                FROM appsurface_durable.runtime_due_dispatch_health(@surfaces);
                """;
            command.CommandTimeout = 120;
            command.Parameters.AddWithValue("surfaces", surfaces);
            await command.ExecuteNonQueryAsync();
        }

        Assert.True(
            notices.Count >= 2,
            $"auto_explain returned {notices.Count} plan notice(s); expected the outer call and at least one nested statement.");
        var plan = string.Join(Environment.NewLine, notices);
        Assert.Contains(
            "runtime_due_dispatch_health",
            plan,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "\"Shared Hit Blocks\"",
            plan,
            StringComparison.Ordinal);
        return plan;
    }

    private static async ValueTask ProveConstrainedPoolRecoveryAsync(
        IDurableRuntimeHealth health,
        NpgsqlDataSource runtimeDataSource,
        ConcurrentQueue<TimeSpan> statusConnectionAcquisitions,
        ConcurrentQueue<TimeSpan> runtimeConnectionAcquisitions,
        long expectedDueCount)
    {
        var heldConnections = new List<NpgsqlConnection>(ConstrainedPoolSize);
        try
        {
            for (var index = 0; index < ConstrainedPoolSize; index++)
            {
                heldConnections.Add(await runtimeDataSource.OpenConnectionAsync());
            }

            Clear(statusConnectionAcquisitions);
            Clear(runtimeConnectionAcquisitions);
            var healthRead = health.GetAsync().AsTask();
            await Task.Delay(TimeSpan.FromMilliseconds(150));
            Assert.False(
                healthRead.IsCompleted,
                $"A public health read acquired a ninth connection from a pool capped at {ConstrainedPoolSize}.");

            await heldConnections[^1].DisposeAsync();
            heldConnections.RemoveAt(heldConnections.Count - 1);
            var snapshot = await healthRead.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(expectedDueCount, snapshot.DueDispatchCount);
            Assert.True(
                statusConnectionAcquisitions.TryDequeue(out var constrainedWait),
                "The pool-exhaustion proof did not observe the blocked schema-status acquisition.");
            Assert.True(
                constrainedWait >= TimeSpan.FromMilliseconds(100),
                $"The constrained pool acquisition reported only {constrainedWait.TotalMilliseconds:N1} ms of wait.");
            Assert.True(
                runtimeConnectionAcquisitions.TryDequeue(out _),
                "The pool-exhaustion proof did not observe the runtime-health acquisition after recovery.");
        }
        finally
        {
            foreach (var connection in heldConnections)
            {
                await connection.DisposeAsync();
            }

            Clear(statusConnectionAcquisitions);
            Clear(runtimeConnectionAcquisitions);
        }
    }

    private async ValueTask<HealthEvidence[]> MeasureWarmHealthBatchesAsync(
        IDurableRuntimeHealth health,
        ConcurrentQueue<TimeSpan> statusConnectionAcquisitions,
        ConcurrentQueue<TimeSpan> runtimeConnectionAcquisitions,
        NpgsqlDataSource evidenceDataSource,
        long expectedDueCount)
    {
        _ = await health.GetAsync();
        var batches = new HealthEvidence[WarmBatchCount];
        for (var batchIndex = 0; batchIndex < batches.Length; batchIndex++)
        {
            Clear(statusConnectionAcquisitions);
            Clear(runtimeConnectionAcquisitions);
            var process = Process.GetCurrentProcess();
            process.Refresh();
            var cpuBefore = process.TotalProcessorTime;
            var allocationsBefore = GC.GetTotalAllocatedBytes(precise: true);
            var healthRuns = new TimeSpan[WarmSamplesPerBatch];
            var statusPoolRuns = new TimeSpan[WarmSamplesPerBatch];
            var runtimePoolRuns = new TimeSpan[WarmSamplesPerBatch];
            for (var sampleIndex = 0; sampleIndex < healthRuns.Length; sampleIndex++)
            {
                var started = Stopwatch.GetTimestamp();
                var snapshot = await health.GetAsync();
                healthRuns[sampleIndex] = Stopwatch.GetElapsedTime(started);
                Assert.True(
                    statusConnectionAcquisitions.TryDequeue(out statusPoolRuns[sampleIndex]),
                    "The public health read did not report its schema-status connection acquisition.");
                Assert.True(
                    runtimeConnectionAcquisitions.TryDequeue(out runtimePoolRuns[sampleIndex]),
                    "The public health read did not report its runtime-observation connection acquisition.");
                Assert.Equal(expectedDueCount, snapshot.DueDispatchCount);
            }

            process.Refresh();
            batches[batchIndex] = new HealthEvidence(
                healthRuns,
                statusPoolRuns,
                runtimePoolRuns,
                process.TotalProcessorTime - cpuBefore,
                GC.GetTotalAllocatedBytes(precise: true) - allocationsBefore,
                await ReadLockWaitCountAsync(evidenceDataSource));
        }

        return batches;
    }

    private void AssertWarmBatches(string distribution, HealthEvidence[] batches)
    {
        Assert.Equal(WarmBatchCount, batches.Length);
        for (var batchIndex = 0; batchIndex < batches.Length; batchIndex++)
        {
            var evidence = batches[batchIndex];
            ReportEvidence(distribution, batchIndex + 1, evidence);
            AssertWarmP95BelowOneSecond(
                $"{distribution} batch {batchIndex + 1}",
                evidence.HealthRuns);
            Assert.Equal(0, evidence.LockWaitCount);
        }
    }

    private void WriteRunnerProfile()
    {
        _output.WriteLine(
            "runtime-health-scale-evidence: " +
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                kind = "runner-profile",
                postgresImage = PostgreSqlTestContainerImage.Reference,
                os = RuntimeInformation.OSDescription,
                architecture = RuntimeInformation.OSArchitecture.ToString(),
                framework = RuntimeInformation.FrameworkDescription,
                processorCount = Environment.ProcessorCount,
                constrainedPoolSize = ConstrainedPoolSize,
                warmBatchCount = WarmBatchCount,
                samplesPerBatch = WarmSamplesPerBatch,
            }));
    }

    private void ReportEvidence(string distribution, int batch, HealthEvidence evidence)
    {
        _output.WriteLine(
            "runtime-health-scale-evidence: " +
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                kind = "warm-health-batch",
                distribution,
                batch,
                sampleCount = evidence.HealthRuns.Length,
                healthP50Milliseconds = Percentile(evidence.HealthRuns, 0.50).TotalMilliseconds,
                healthP95Milliseconds = Percentile(evidence.HealthRuns, 0.95).TotalMilliseconds,
                healthP99Milliseconds = Percentile(evidence.HealthRuns, 0.99).TotalMilliseconds,
                statusPoolP50Milliseconds = Percentile(evidence.StatusPoolRuns, 0.50).TotalMilliseconds,
                statusPoolP95Milliseconds = Percentile(evidence.StatusPoolRuns, 0.95).TotalMilliseconds,
                statusPoolP99Milliseconds = Percentile(evidence.StatusPoolRuns, 0.99).TotalMilliseconds,
                runtimePoolP50Milliseconds = Percentile(evidence.RuntimePoolRuns, 0.50).TotalMilliseconds,
                runtimePoolP95Milliseconds = Percentile(evidence.RuntimePoolRuns, 0.95).TotalMilliseconds,
                runtimePoolP99Milliseconds = Percentile(evidence.RuntimePoolRuns, 0.99).TotalMilliseconds,
                cpuMilliseconds = evidence.CpuDelta.TotalMilliseconds,
                evidence.AllocatedBytes,
                evidence.LockWaitCount,
            }));
    }

    private void ReportMixedEvidence(
        (DurableRuntimeHealthSnapshot Snapshot, TimeSpan Elapsed)[] runs,
        TimeSpan[] statusConnectionAcquisitions,
        TimeSpan[] runtimeConnectionAcquisitions,
        TimeSpan cpuDelta,
        long allocatedBytes,
        long lockWaitsBefore,
        long lockWaitsAfter,
        long maximumObservedLockWaits)
    {
        var healthRuns = runs.Select(run => run.Elapsed).ToArray();
        _output.WriteLine(
            "runtime-health-scale-evidence: " +
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                kind = "mixed-concurrency",
                sampleCount = runs.Length,
                healthP50Milliseconds = Percentile(healthRuns, 0.50).TotalMilliseconds,
                healthP95Milliseconds = Percentile(healthRuns, 0.95).TotalMilliseconds,
                healthP99Milliseconds = Percentile(healthRuns, 0.99).TotalMilliseconds,
                statusPoolP50Milliseconds = Percentile(statusConnectionAcquisitions, 0.50).TotalMilliseconds,
                statusPoolP95Milliseconds = Percentile(statusConnectionAcquisitions, 0.95).TotalMilliseconds,
                statusPoolP99Milliseconds = Percentile(statusConnectionAcquisitions, 0.99).TotalMilliseconds,
                runtimePoolP50Milliseconds = Percentile(runtimeConnectionAcquisitions, 0.50).TotalMilliseconds,
                runtimePoolP95Milliseconds = Percentile(runtimeConnectionAcquisitions, 0.95).TotalMilliseconds,
                runtimePoolP99Milliseconds = Percentile(runtimeConnectionAcquisitions, 0.99).TotalMilliseconds,
                cpuMilliseconds = cpuDelta.TotalMilliseconds,
                allocatedBytes,
                lockWaitsBefore,
                lockWaitsAfter,
                maximumObservedLockWaits,
            }));
    }

    private static void Clear(ConcurrentQueue<TimeSpan> samples)
    {
        while (samples.TryDequeue(out _))
        {
        }
    }

    private static TimeSpan[] Drain(ConcurrentQueue<TimeSpan> samples, int expectedCount)
    {
        var result = new List<TimeSpan>(expectedCount);
        while (samples.TryDequeue(out var sample))
        {
            result.Add(sample);
        }

        Assert.Equal(expectedCount, result.Count);
        return result.ToArray();
    }

    private static TimeSpan Percentile(TimeSpan[] samples, double percentile)
    {
        var ordered = samples.Order().ToArray();
        var index = Math.Clamp((int)Math.Ceiling(ordered.Length * percentile) - 1, 0, ordered.Length - 1);
        return ordered[index];
    }

    private static void AssertWarmP95BelowOneSecond(string distribution, TimeSpan[] runs)
    {
        var ordered = runs.Order().ToArray();
        var p95 = ordered[(int)Math.Ceiling(ordered.Length * 0.95) - 1];
        Assert.True(
            p95 < TimeSpan.FromSeconds(1),
            $"{distribution} full-health warm p95 was {p95.TotalMilliseconds:N1} ms; " +
            $"runs: {string.Join(", ", runs.Select(run => $"{run.TotalMilliseconds:N1} ms"))}.");
    }

    private static ValueTask<long> ReadLockWaitCountAsync(NpgsqlDataSource dataSource) => CountAsync(
        dataSource,
        """
        SELECT count(*)
        FROM pg_catalog.pg_stat_activity
        WHERE wait_event_type = 'Lock'
          AND datname = current_database()
          AND pid <> pg_backend_pid();
        """);

    private static async Task<long> MonitorMaximumLockWaitCountAsync(
        NpgsqlDataSource dataSource,
        Task start,
        CancellationToken cancellationToken)
    {
        await start;
        var maximum = 0L;
        while (!cancellationToken.IsCancellationRequested)
        {
            maximum = Math.Max(maximum, await ReadLockWaitCountAsync(dataSource));
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        return maximum;
    }

    private static async ValueTask MakeRuntimeHealthBacklogDenseAsync(
        NpgsqlDataSource dataSource,
        int rowsPerSurface)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE appsurface_durable.dispatch
            SET due_at = timestamp with time zone '2000-01-01 00:00:00+00';

            UPDATE appsurface_durable.flow_dispatch
            SET due_at = timestamp with time zone '2001-01-01 00:00:00+00';

            UPDATE appsurface_durable.schedule_dispatch
            SET due_at = timestamp with time zone '2002-01-01 00:00:00+00',
                state = 'available',
                lease_owner = NULL,
                lease_generation = 0,
                lease_expires_at = NULL
            WHERE dispatch_revision <= @rows_per_surface / 2;

            UPDATE appsurface_durable.schedule_dispatch
            SET due_at = timestamp with time zone '2100-01-01 00:00:00+00',
                state = 'leased',
                lease_owner = 'health-scale-worker',
                lease_generation = 1,
                lease_expires_at = timestamp with time zone '2002-01-02 00:00:00+00'
            WHERE dispatch_revision > @rows_per_surface / 2;

            ANALYZE appsurface_durable.dispatch;
            ANALYZE appsurface_durable.flow_dispatch;
            ANALYZE appsurface_durable.schedule_dispatch;
            """);
        command.CommandTimeout = 120;
        command.Parameters.AddWithValue("rows_per_surface", rowsPerSurface);
        await command.ExecuteNonQueryAsync();
    }

    private static async ValueTask<long> ClaimOneScheduleDispatchAsync(NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT count(*)
            FROM appsurface_durable.claim_schedule_dispatch(
                'runtime-health-scale-claimer',
                interval '10 minutes');
            """);
        return (long)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("PostgreSQL returned no Schedule claim count."));
    }

    private static async ValueTask<int> UpdateScaleHeartbeatAsync(NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE appsurface_durable.runtime_heartbeat
            SET last_heartbeat_at = statement_timestamp(),
                updated_at = statement_timestamp()
            WHERE worker_id = 'runtime-health-scale-worker';
            """);
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> RunAfterAsync<T>(
        Task start,
        Func<ValueTask<T>> operation)
    {
        await start;
        return await operation();
    }

    private static async ValueTask<string> ReadWalLocationAsync(NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand("SELECT pg_current_wal_insert_lsn()::text;");
        return (string)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("PostgreSQL returned no WAL location."));
    }

    private static async ValueTask<long> ReadWalDifferenceAsync(
        NpgsqlDataSource dataSource,
        string later,
        string earlier)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT pg_wal_lsn_diff(@later::pg_lsn, @earlier::pg_lsn)::bigint;");
        command.Parameters.AddWithValue("later", later);
        command.Parameters.AddWithValue("earlier", earlier);
        return (long)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("PostgreSQL returned no WAL difference."));
    }

    private static async ValueTask<long> CountAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        return (long)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("PostgreSQL returned no count."));
    }

    private sealed record HealthEvidence(
        TimeSpan[] HealthRuns,
        TimeSpan[] StatusPoolRuns,
        TimeSpan[] RuntimePoolRuns,
        TimeSpan CpuDelta,
        long AllocatedBytes,
        long LockWaitCount);

    private sealed class ScaleWorkRegistry(IReadOnlyList<DurableWorkContractIdentity> contracts) : IDurableWorkRegistry
    {
        public IReadOnlyList<DurableWorkContractIdentity> RegisteredContracts => contracts;

        public DurableWorkRegistration GetRequired(string workName, string workVersion) =>
            throw new NotSupportedException();
    }
}

[CollectionDefinition("PostgreSQL scale", DisableParallelization = true)]
public sealed class PostgreSqlScaleCollection;
