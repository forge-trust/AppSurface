using System.Diagnostics;
using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

[Collection("PostgreSQL scale")]
public sealed class PostgreSqlScaleIntegrationTests
{
    [Fact]
    public async Task Discovery_UsesDueIndexAcrossOneHundredThousandRowsAndOneHundredScopes()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var epoch = Guid.NewGuid();
        await SeedPendingWorkAsync(database.DataSource, epoch, workCount: 100_000, scopeCount: 100);

        await using var command = database.DataSource.CreateCommand(
            """
            EXPLAIN (FORMAT JSON)
            SELECT dispatch_id, scope_id, aggregate_id, due_at, expected_revision, priority
            FROM appsurface_durable.dispatch
            WHERE aggregate_kind = 'work'
              AND state IN ('available', 'leased')
              AND due_at <= clock_timestamp()
            ORDER BY due_at, priority DESC, dispatch_id
            LIMIT 1000;
            """);
        var plan = (string)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("PostgreSQL returned no discovery plan."));

        Assert.Contains("ix_dispatch_due", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("Seq Scan", plan, StringComparison.Ordinal);
        var store = new PostgreSqlDurableWorkStore(database.DataSource, epoch);
        Assert.Equal(1_000, (await store.DiscoverAsync(1_000)).Count);
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
                'scale-work', '1', 'scale-contract', '1', 'application/json',
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

            ANALYZE appsurface_durable.dispatch;
            """);
        command.Parameters.AddWithValue("scope_count", scopeCount);
        command.Parameters.AddWithValue("work_count", workCount);
        command.Parameters.AddWithValue("epoch", epoch);
        await command.ExecuteNonQueryAsync();
    }

    private static async ValueTask<long> CountAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        return (long)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("PostgreSQL returned no count."));
    }
}

[CollectionDefinition("PostgreSQL scale", DisableParallelization = true)]
public sealed class PostgreSqlScaleCollection;
