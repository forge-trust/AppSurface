using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.Provider;
using Npgsql;
using Xunit.Abstractions;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

[Collection("PostgreSQL scale")]
public sealed class PostgreSqlDurableExecutionDiscoveryScaleTests(ITestOutputHelper output)
{
    private const int SeededWorkCount = 100_000;
    private const int SelectedExpiredCount = 16;
    private const int ExcludedExpiredCount = 16;
    private const int BoundedPageSize = 7;
    private const int ScopeCount = 100;
    private const string ScopePrefix = "execution-discovery-scale-";
    private const string SelectedWorkName = "tests.execution-discovery.selected";
    private const string ExcludedWorkName = "tests.execution-discovery.excluded";
    private static readonly DateTimeOffset Anchor = new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public async Task SelectedDispatcherDiscovery_UsesExecutionIndexAndDrainsExpiredFutureWorkInBoundedPages()
    {
        await using var lab = await PostgreSqlDurableWorkExecutionPolicyTests.Lab.CreateAsync();
        await lab.Database.SetExecutionTimeAsync(Anchor);
        await SeedScaleRowsAsync(lab.Database.DataSource, lab.Epoch);

        var roleName = $"appsurface_disc_{Guid.NewGuid():N}";
        var password = Guid.NewGuid().ToString("N");
        await CreateDispatcherRoleAsync(lab.Database.DataSource, roleName, password);

        try
        {
            await using var dispatcherDataSource = await CreateDispatcherDataSourceAsync(
                lab.Database.ConnectionString,
                roleName,
                password);
            var roleEvidence = await ReadDispatcherEvidenceAsync(dispatcherDataSource);
            Assert.Equal(roleName, roleEvidence.CurrentRole);
            Assert.False(roleEvidence.IsSuperuser);
            Assert.False(roleEvidence.BypassesRowLevelSecurity);
            Assert.True(roleEvidence.WorkRowSecurityActive);
            Assert.True(roleEvidence.DispatchRowSecurityActive);
            Assert.False(roleEvidence.CanSelectWorkDirectly);
            Assert.False(roleEvidence.CanSelectDispatchDirectly);

            var seedCounts = await ReadSeedCountsAsync(lab.Database.DataSource);
            Assert.Equal(SeededWorkCount, seedCounts.WorkCount);
            Assert.Equal(ScopeCount, seedCounts.ScopeCount);
            Assert.Equal(SeededWorkCount, seedCounts.DispatchCount);
            var discoveryIndexBytes = await ReadDiscoveryIndexSizeAsync(lab.Database.DataSource);
            Assert.True(discoveryIndexBytes > 0);

            var store = new PostgreSqlDurableWorkStore(
                dispatcherDataSource,
                lab.Database.DataSource,
                lab.Epoch);
            var selected = CreateSelection(SelectedWorkName);
            var excluded = CreateSelection(ExcludedWorkName);

            var selectedCandidates = await store.DiscoverAsync(selected, maximumCandidates: 1_000);
            Assert.Equal(SelectedExpiredCount, selectedCandidates.Count);
            Assert.All(
                selectedCandidates,
                candidate =>
                {
                    Assert.StartsWith("selected-expired-", candidate.WorkId.Value, StringComparison.Ordinal);
                    Assert.Equal(Anchor.AddHours(1), candidate.DueAtUtc);
                });
            Assert.Equal(
                Enumerable.Range(1, SelectedExpiredCount)
                    .Select(static value => $"selected-expired-{value}"),
                selectedCandidates.Select(static candidate => candidate.WorkId.Value));
            Assert.Equal(SelectedExpiredCount, selectedCandidates.Select(static candidate => candidate.ScopeId).Distinct().Count());

            var excludedCandidates = await store.DiscoverAsync(excluded, maximumCandidates: 1_000);
            Assert.Equal(ExcludedExpiredCount, excludedCandidates.Count);
            Assert.All(
                excludedCandidates,
                candidate => Assert.StartsWith("excluded-expired-", candidate.WorkId.Value, StringComparison.Ordinal));

            var firstBoundedPage = await store.DiscoverAsync(selected, BoundedPageSize);
            Assert.Equal(BoundedPageSize, firstBoundedPage.Count);
            Assert.Equal("selected-expired-1", firstBoundedPage[0].WorkId.Value);
            Assert.Equal("selected-expired-2", firstBoundedPage[1].WorkId.Value);
            Assert.Equal("selected-expired-3", firstBoundedPage[2].WorkId.Value);
            Assert.True(firstBoundedPage[0].Priority > firstBoundedPage[1].Priority);
            Assert.True(firstBoundedPage[1].Priority > firstBoundedPage[2].Priority);
            Assert.DoesNotContain(
                firstBoundedPage,
                static candidate => candidate.WorkId.Value.StartsWith("selected-held-future-", StringComparison.Ordinal));

            var plan = await ReadNestedPlanAsync(
                lab.Database.DataSource,
                """
                SELECT *
                FROM appsurface_durable.discover_work_dispatch(
                    @work_names,
                    @work_versions,
                    @maximum_candidates);
                """,
                command => selected.AddDiscoveryParameters(command.Parameters, BoundedPageSize),
                "discover_work_dispatch");
            Assert.Contains("ix_dispatch_execution_discovery", plan, StringComparison.Ordinal);

            var indexWriteAcceptance = await lab.Client.EnqueueAsync(
                PostgreSqlDurableWorkExecutionPolicyTests.Request("scale-index-write", circuitMinutes: 30, offsets: [0, 5]));
            Assert.True(indexWriteAcceptance.IsSuccess, indexWriteAcceptance.Problem?.Problem);
            var indexWriteWorkId = indexWriteAcceptance.Value!.WorkId;
            Assert.Equal(Anchor, await ReadExecutionDiscoveryAtAsync(
                lab.Database.DataSource,
                indexWriteWorkId));
            var indexWriteCandidate = Assert.Single(
                await lab.Store.DiscoverAsync(1_000),
                candidate => candidate.WorkId == indexWriteWorkId);
            var indexWriteClaim = await lab.Store.TryClaimAsync(indexWriteCandidate, "execution-discovery-index-worker");
            Assert.NotNull(indexWriteClaim);
            Assert.Equal(Anchor.AddMinutes(30), await ReadExecutionDiscoveryAtAsync(
                lab.Database.DataSource,
                indexWriteWorkId));
            var indexWriteRetry = await lab.Store.RecordCompletionAsync(
                indexWriteClaim!,
                new(PostgreSqlWorkCompletionKind.Retry, "scale_retry", "{}"));
            Assert.Equal(DurableWorkState.Ready, indexWriteRetry.State);
            Assert.Equal(Anchor.AddMinutes(5), await ReadExecutionDiscoveryAtAsync(
                lab.Database.DataSource,
                indexWriteWorkId));

            var pages = 0;
            var drainedCandidates = 0;
            while (true)
            {
                var page = await store.DiscoverAsync(selected, BoundedPageSize);
                Assert.InRange(page.Count, 0, BoundedPageSize);
                if (page.Count == 0)
                {
                    break;
                }

                pages++;
                drainedCandidates += page.Count;
                foreach (var candidate in page)
                {
                    Assert.StartsWith("selected-expired-", candidate.WorkId.Value, StringComparison.Ordinal);
                    Assert.Equal(Anchor.AddHours(1), candidate.DueAtUtc);
                    Assert.Null(await store.TryClaimAsync(candidate, "execution-discovery-scale-worker"));
                }

                Assert.True(pages <= 3, "Selected discovery exceeded the expected bounded drain page count.");
            }

            Assert.Equal(3, pages);
            Assert.Equal(SelectedExpiredCount, drainedCandidates);
            Assert.Empty(await store.DiscoverAsync(selected, BoundedPageSize));
            Assert.Empty(await store.DiscoverAsync(selected, BoundedPageSize));

            var terminalCounts = await ReadTerminalCountsAsync(lab.Database.DataSource);
            Assert.Equal(SelectedExpiredCount, terminalCounts.SelectedFailed);
            Assert.Equal(ExcludedExpiredCount, terminalCounts.ExcludedStillAvailable);

            output.WriteLine(
                $"Seeded {SeededWorkCount:N0} opted-in Work rows across {ScopeCount} scopes; selected contract returned {SelectedExpiredCount} expired future-due candidates, while the excluded contract returned {ExcludedExpiredCount} only through its own selection.");
            output.WriteLine($"The 100,000-row execution-discovery index occupies {discoveryIndexBytes:N0} bytes; production acceptance, claim, and retry completion updated its indexed hint.");
            output.WriteLine(
                $"Dispatcher role {roleName}: NOBYPASSRLS, forced RLS active on Work and dispatch, and no direct SELECT grant on either table.");
            output.WriteLine(
                $"Bounded drain: {drainedCandidates} candidates across {pages} pages of at most {BoundedPageSize}, then two empty polls.");
            output.WriteLine("Production acceptance, claim, and retry completion wrote indexed execution_discovery_at values at the acceptance, lease, and next-slot boundaries.");
            output.WriteLine($"Installed selected-contract discovery plan:{Environment.NewLine}{plan}");
        }
        finally
        {
            await DropDispatcherRoleAsync(lab.Database.DataSource, roleName);
        }
    }

    private static PostgreSqlDurableWorkContractSelection CreateSelection(string workName) =>
        new(new DiscoveryWorkRegistry([new DurableWorkContractIdentity(workName, "v1")]));

    private static async Task SeedScaleRowsAsync(NpgsqlDataSource dataSource, Guid epoch)
    {
        await using var command = dataSource.CreateCommand(
            """
            INSERT INTO appsurface_durable.scope (scope_id)
            SELECT @scope_prefix || value
            FROM generate_series(1, @scope_count) AS generated(value);

            INSERT INTO appsurface_durable.work
            (
                scope_id, work_id, activity_id, command_id, idempotency_key,
                work_name, work_version, contract_id, payload_schema_version, codec_id,
                payload, payload_sha256, payload_classification, payload_retention,
                request_fingerprint_schema, request_fingerprint_sha256,
                state, provider_safety, accepted_at, due_at, updated_at,
                attempt_number, lease_generation, lease_owner, lease_started_at, lease_expires_at,
                scope_generation, runtime_epoch, revision,
                maximum_attempts, maximum_elapsed, backoff_algorithm,
                initial_retry_delay, maximum_retry_delay, lease_duration,
                lease_renewal_cadence, maximum_lease_lifetime,
                execution_policy_schema, attempt_plan_version, attempt_plan_offsets,
                maximum_circuit_microseconds
            )
            SELECT
                @scope_prefix || (((value - 1) % @scope_count) + 1),
                CASE
                    WHEN value BETWEEN 1 AND 16 THEN 'selected-expired-' || value
                    WHEN value BETWEEN 17 AND 32 THEN 'excluded-expired-' || value
                    WHEN value BETWEEN 33 AND 64 THEN 'selected-held-future-' || value
                    WHEN value <= 70000 THEN 'selected-future-' || value
                    ELSE 'excluded-future-' || value
                END,
                'execution-discovery-activity-' || value,
                'execution-discovery-command-' || value,
                'execution-discovery-key-' || value,
                CASE
                    WHEN value BETWEEN 17 AND 32 OR value > 70000 THEN @excluded_work_name
                    ELSE @selected_work_name
                END,
                'v1', 'execution-discovery-scale', 'v1', 'application/json',
                decode('00', 'hex'), decode(repeat('00', 32), 'hex'), 'internal', 'default',
                'durable-work-request-v1', repeat('0', 64),
                CASE WHEN value IN (1, 3) OR value BETWEEN 33 AND 64 THEN 'leased' ELSE 'pending' END,
                'idempotent',
                CASE WHEN value <= 32 THEN @anchor - interval '31 minutes' ELSE @anchor - interval '5 minutes' END,
                @anchor + interval '1 hour',
                @anchor,
                CASE WHEN value IN (1, 3) OR value BETWEEN 33 AND 64 THEN 1 ELSE 0 END,
                CASE WHEN value IN (1, 3) OR value BETWEEN 33 AND 64 THEN 1 ELSE 0 END,
                CASE WHEN value IN (1, 3) OR value BETWEEN 33 AND 64 THEN 'held-worker' ELSE NULL END,
                CASE WHEN value IN (1, 3) OR value BETWEEN 33 AND 64 THEN @anchor - interval '30 seconds' ELSE NULL END,
                CASE WHEN value IN (1, 3) OR value BETWEEN 33 AND 64 THEN @anchor + interval '4 minutes 30 seconds' ELSE NULL END,
                1, @runtime_epoch,
                CASE WHEN value IN (1, 3) OR value BETWEEN 33 AND 64 THEN 2 ELSE 1 END,
                2, interval '1 hour', 'exponential-v1',
                interval '1 second', interval '15 minutes', interval '30 seconds',
                interval '10 seconds', interval '5 minutes',
                'work-execution-v1', 'attempt-plan-v1', ARRAY[0::bigint, 600000000::bigint],
                1800000000
            FROM generate_series(1, @seeded_work_count) AS generated(value);

            INSERT INTO appsurface_durable.dispatch
                (dispatch_id, scope_id, aggregate_kind, aggregate_id, due_at, state,
                 expected_revision, priority, execution_discovery_at, updated_at)
            SELECT
                md5('execution-discovery-dispatch-' || value)::uuid,
                work.scope_id,
                'work',
                work.work_id,
                work.due_at,
                CASE WHEN work.state = 'leased' THEN 'leased' ELSE 'available' END,
                work.revision,
                CASE
                    WHEN value BETWEEN 1 AND 16 THEN (100 - value)::smallint
                    WHEN value BETWEEN 17 AND 32 THEN (200 - value)::smallint
                    WHEN value BETWEEN 33 AND 64 THEN 32767::smallint
                    ELSE 0::smallint
                END,
                CASE
                    WHEN value <= 32 THEN @anchor - interval '1 minute'
                    WHEN value BETWEEN 33 AND 64 THEN @anchor + interval '4 minutes 30 seconds'
                    ELSE @anchor + interval '25 minutes'
                END,
                @anchor
            FROM generate_series(1, @seeded_work_count) AS generated(value)
            JOIN appsurface_durable.work AS work
              ON work.scope_id = @scope_prefix || (((value - 1) % @scope_count) + 1)
             AND work.work_id = CASE
                    WHEN value BETWEEN 1 AND 16 THEN 'selected-expired-' || value
                    WHEN value BETWEEN 17 AND 32 THEN 'excluded-expired-' || value
                    WHEN value BETWEEN 33 AND 64 THEN 'selected-held-future-' || value
                    WHEN value <= 70000 THEN 'selected-future-' || value
                    ELSE 'excluded-future-' || value
                 END;
            ANALYZE appsurface_durable.work;
            ANALYZE appsurface_durable.dispatch;
            """);
        command.Parameters.AddWithValue("scope_prefix", ScopePrefix);
        command.Parameters.AddWithValue("scope_count", ScopeCount);
        command.Parameters.AddWithValue("selected_work_name", SelectedWorkName);
        command.Parameters.AddWithValue("excluded_work_name", ExcludedWorkName);
        command.Parameters.AddWithValue("anchor", Anchor);
        command.Parameters.AddWithValue("runtime_epoch", epoch);
        command.Parameters.AddWithValue("seeded_work_count", SeededWorkCount);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<SeedCounts> ReadSeedCountsAsync(NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT (SELECT count(*) FROM appsurface_durable.work WHERE scope_id LIKE @scope_prefix || '%'),
                   (SELECT count(DISTINCT scope_id) FROM appsurface_durable.work WHERE scope_id LIKE @scope_prefix || '%'),
                   (SELECT count(*) FROM appsurface_durable.dispatch WHERE scope_id LIKE @scope_prefix || '%');
            """);
        command.Parameters.AddWithValue("scope_prefix", ScopePrefix);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new SeedCounts(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    private static async Task<long> ReadDiscoveryIndexSizeAsync(NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT pg_relation_size('appsurface_durable.ix_dispatch_execution_discovery'::regclass);");
        return (long)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("Execution-discovery index was not found."));
    }

    private static async Task CreateDispatcherRoleAsync(
        NpgsqlDataSource dataSource,
        string roleName,
        string password)
    {
        await using var command = dataSource.CreateCommand(
            $"""
            CREATE ROLE "{roleName}" LOGIN PASSWORD '{password}'
                NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
            GRANT USAGE ON SCHEMA appsurface_durable TO "{roleName}";
            GRANT EXECUTE ON FUNCTION appsurface_durable.discover_work_dispatch(text[], text[], integer)
                TO "{roleName}";
            """);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropDispatcherRoleAsync(NpgsqlDataSource dataSource, string roleName)
    {
        await using var command = dataSource.CreateCommand(
            $"""
            REVOKE EXECUTE ON FUNCTION appsurface_durable.discover_work_dispatch(text[], text[], integer)
                FROM "{roleName}";
            REVOKE USAGE ON SCHEMA appsurface_durable FROM "{roleName}";
            DROP ROLE "{roleName}";
            """);
        await command.ExecuteNonQueryAsync();
    }

    private static Task<NpgsqlDataSource> CreateDispatcherDataSourceAsync(
        string connectionString,
        string roleName,
        string password)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Username = roleName,
            Password = password,
            Pooling = false,
        };
        return Task.FromResult(NpgsqlDataSource.Create(builder.ConnectionString));
    }

    private static async Task<DateTimeOffset> ReadExecutionDiscoveryAtAsync(
        NpgsqlDataSource dataSource,
        DurableWorkId workId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var scope = new NpgsqlCommand(
                         "SELECT set_config('appsurface_durable.scope_id', 'execution-tests', true);",
                         connection,
                         transaction))
        {
            await scope.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(
            """
            SELECT execution_discovery_at
            FROM appsurface_durable.dispatch
            WHERE scope_id = 'execution-tests'
              AND aggregate_kind = 'work'
              AND aggregate_id = @work_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("work_id", workId.Value);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException("Opted-in dispatch row was not found.");
        }

        return reader.GetFieldValue<DateTimeOffset>(0);
    }

    private static async Task<DispatcherEvidence> ReadDispatcherEvidenceAsync(NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT current_user,
                   row_security_active('appsurface_durable.work'::regclass),
                   row_security_active('appsurface_durable.dispatch'::regclass),
                   has_table_privilege(current_user, 'appsurface_durable.work', 'SELECT'),
                   has_table_privilege(current_user, 'appsurface_durable.dispatch', 'SELECT'),
                   rolsuper,
                   rolbypassrls
            FROM pg_catalog.pg_roles
            WHERE rolname = current_user;
            """);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new DispatcherEvidence(
            reader.GetString(0),
            reader.GetBoolean(1),
            reader.GetBoolean(2),
            reader.GetBoolean(3),
            reader.GetBoolean(4),
            reader.GetBoolean(5),
            reader.GetBoolean(6));
    }

    private static async Task<TerminalCounts> ReadTerminalCountsAsync(NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT
                count(*) FILTER
                    (WHERE work.work_name = @selected_work_name
                       AND work.work_id LIKE 'selected-expired-%'
                       AND work.state = 'failed'
                       AND dispatch.state = 'terminal'),
                count(*) FILTER
                    (WHERE work.work_name = @excluded_work_name
                       AND work.work_id LIKE 'excluded-expired-%'
                       AND work.state = 'pending'
                       AND dispatch.state = 'available')
            FROM appsurface_durable.work AS work
            JOIN appsurface_durable.dispatch AS dispatch
              ON dispatch.scope_id = work.scope_id
             AND dispatch.aggregate_kind = 'work'
             AND dispatch.aggregate_id = work.work_id
            WHERE work.scope_id LIKE @scope_prefix || '%';
            """);
        command.Parameters.AddWithValue("scope_prefix", ScopePrefix);
        command.Parameters.AddWithValue("selected_work_name", SelectedWorkName);
        command.Parameters.AddWithValue("excluded_work_name", ExcludedWorkName);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new TerminalCounts(reader.GetInt64(0), reader.GetInt64(1));
    }

    private static async ValueTask<string> ReadNestedPlanAsync(
        NpgsqlDataSource dataSource,
        string commandText,
        Action<NpgsqlCommand> configureCommand,
        string expectedFunctionName)
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
            command.CommandText = commandText;
            command.CommandTimeout = 120;
            configureCommand(command);
            await command.ExecuteNonQueryAsync();
        }

        Assert.True(
            notices.Count >= 2,
            $"auto_explain returned {notices.Count} plan notice(s); expected the outer call and a nested statement.");
        var plan = string.Join(Environment.NewLine, notices);
        Assert.Contains(expectedFunctionName, plan, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"Shared Hit Blocks\"", plan, StringComparison.Ordinal);
        return plan;
    }

    private sealed record DispatcherEvidence(
        string CurrentRole,
        bool WorkRowSecurityActive,
        bool DispatchRowSecurityActive,
        bool CanSelectWorkDirectly,
        bool CanSelectDispatchDirectly,
        bool IsSuperuser,
        bool BypassesRowLevelSecurity);

    private sealed record SeedCounts(long WorkCount, long ScopeCount, long DispatchCount);

    private sealed record TerminalCounts(long SelectedFailed, long ExcludedStillAvailable);

    private sealed class DiscoveryWorkRegistry(IReadOnlyList<DurableWorkContractIdentity> contracts)
        : IDurableWorkRegistry
    {
        public IReadOnlyList<DurableWorkContractIdentity> RegisteredContracts => contracts;

        public DurableWorkRegistration GetRequired(string workName, string workVersion) =>
            throw new NotSupportedException();
    }
}
