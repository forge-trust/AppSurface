using System.Diagnostics;
using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

[Collection("PostgreSQL scale")]
public sealed class PostgreSqlScaleIntegrationTests
{
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
        Assert.Equal(1_000, (await store.DiscoverAsync(selection, 1_000)).Count);
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
                CASE WHEN value <= 1000 THEN 'scale-work' ELSE 'unselected-scale-work-' || value END,
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

    private sealed class ScaleWorkRegistry(IReadOnlyList<DurableWorkContractIdentity> contracts) : IDurableWorkRegistry
    {
        public IReadOnlyList<DurableWorkContractIdentity> RegisteredContracts => contracts;

        public DurableWorkRegistration GetRequired(string workName, string workVersion) =>
            throw new NotSupportedException();
    }
}

[CollectionDefinition("PostgreSQL scale", DisableParallelization = true)]
public sealed class PostgreSqlScaleCollection;
