using System.Data;
using System.Diagnostics;
using System.Text.Json.Serialization;
using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.PostgreSql;
using ForgeTrust.AppSurface.Durable.PostgreSql.Tests;
using ForgeTrust.AppSurface.Durable.Provider;
using ForgeTrust.AppSurface.Workers;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace DurablePackedExecutionPolicyConsumer;

/// <summary>Public-package proof for transaction-bound application handoffs.</summary>
public sealed partial class HandoffPostgreSqlConsumerTests
{
    internal static readonly DateTimeOffset ClockAnchor = new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
    internal static readonly DurableWorkDefinition<HandoffWork, HandoffResult> Definition =
        DurableWork.Define<HandoffWork, HandoffResult>(
            "tests.packed.handoff",
            "v1",
            PayloadCodecs.Work,
            PayloadCodecs.Result,
            DurableProviderSafety.ProviderKeyed,
            DurableWorkRetryPolicy.Default);

    [Fact]
    public async Task Handoff_PublicExecutorCommitsBeforeFakeProviderAndUsesRestrictedRlsTransaction()
    {
        await using var fixture = await HandoffPostgreSqlFixture.CreateAsync();
        var work = fixture.CreateWork("commit-visible");
        await fixture.SeedAuthorityAsync(work);

        var probe = new HandoffExecutionProbe(holdCallerCommit: true);
        var fakeProvider = new HandoffFakeProvider(fixture.RuntimeDataSource);
        await using var provider = fixture.BuildProvider(probe, fakeProvider);
        var accepted = await fixture.EnqueueAsync(provider, work);

        var running = RunOneAsync(provider);
        var prepared = await probe.Prepared.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(prepared.HandoffPrepared);
        Assert.True(prepared.CallbackUsedExactTransaction);
        Assert.Equal(IsolationLevel.ReadCommitted, prepared.IsolationLevel);
        Assert.True(prepared.CallbackSawRestrictedRole);
        Assert.True(prepared.CallbackSawBoundNativeScope);
        Assert.True(prepared.CallbackSawOneNativeWorkRow);
        Assert.True(prepared.CallbackSawOneApplicationAuthorityRow);
        Assert.Equal("caller-scope-before-handoff", prepared.NativeScopeAfterFence);

        // A separate owner connection sees no prepared row while the caller still owns the open transaction.
        Assert.Equal(0L, await fixture.AdminScalarAsync<long>(
            "SELECT count(*) FROM handoff_consumer.ledger WHERE logical_operation = @operation;",
            ("operation", work.LogicalOperation)));
        Assert.Equal(0, fakeProvider.Calls);

        probe.AllowCallerCommit();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(1, result.Processed);
        Assert.Equal(0, result.Failed);
        Assert.Equal(1, fakeProvider.Calls);
        Assert.True(fakeProvider.LastObservation is { TransactionDisposed: true, ConnectionClosed: true });
        Assert.True(fakeProvider.LastObservation!.LedgerVisibleToFreshReader);

        Assert.Equal(accepted.Value!.WorkId.Value, await fixture.AdminScalarAsync<string>(
            "SELECT work_id FROM appsurface_durable.work WHERE scope_id = @scope_id;",
            ("scope_id", work.ScopeId)));
        Assert.Equal(1L, await fixture.AdminScalarAsync<long>(
            "SELECT count(*) FROM handoff_consumer.ledger WHERE activity_id = @activity_id AND logical_operation = @operation;",
            ("activity_id", fakeProvider.LastObservation.ActivityId), ("operation", work.LogicalOperation)));
        Assert.Equal(1L, await fixture.AdminScalarAsync<long>(
            "SELECT count(*) FROM handoff_consumer.caller_marker WHERE marker_id = @marker_id;",
            ("marker_id", fakeProvider.LastObservation.MarkerId)));
    }

    [Fact]
    public async Task Handoff_FalseAfterStagingRemovesCallbackWriteButKeepsEarlierUnrelatedMarker()
    {
        await using var fixture = await HandoffPostgreSqlFixture.CreateAsync();
        var work = fixture.CreateWork("callback-denied");
        await fixture.SeedAuthorityAsync(work);

        var probe = new HandoffExecutionProbe(denyAfterLedgerInsert: true);
        var fakeProvider = new HandoffFakeProvider(fixture.RuntimeDataSource);
        await using var provider = fixture.BuildProvider(probe, fakeProvider);
        _ = await fixture.EnqueueAsync(provider, work);

        var result = await RunOneAsync(provider).WaitAsync(TimeSpan.FromSeconds(20));
        var prepared = await probe.Prepared.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(prepared.HandoffPrepared);
        Assert.True(prepared.CallbackUsedExactTransaction);
        Assert.Equal("caller-scope-before-handoff", prepared.NativeScopeAfterFence);
        Assert.Equal(0, fakeProvider.Calls);
        Assert.Equal(1, result.Failed);
        Assert.Equal(0L, await fixture.AdminScalarAsync<long>(
            "SELECT count(*) FROM handoff_consumer.ledger WHERE logical_operation = @operation;",
            ("operation", work.LogicalOperation)));
        Assert.Equal(1L, await fixture.AdminScalarAsync<long>(
            "SELECT count(*) FROM handoff_consumer.caller_marker WHERE marker_id = @marker_id;",
            ("marker_id", prepared.MarkerId)));
    }

    [Fact]
    public async Task Handoff_RevocationLockWinsAndNativeCallbackObservesDeniedAuthority()
    {
        await using var fixture = await HandoffPostgreSqlFixture.CreateAsync();
        var work = fixture.CreateWork("revocation-wins");
        await fixture.SeedAuthorityAsync(work);

        var probe = new HandoffExecutionProbe();
        var fakeProvider = new HandoffFakeProvider(fixture.RuntimeDataSource);
        await using var provider = fixture.BuildProvider(probe, fakeProvider);
        _ = await fixture.EnqueueAsync(provider, work);

        await using var revocationConnection = await fixture.AdminDataSource.OpenConnectionAsync();
        await using var revocationTransaction = await revocationConnection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var revocationPid = await BackendPidAsync(revocationConnection, revocationTransaction);
        Assert.Equal(1, await UpdateAuthorityAsync(revocationConnection, revocationTransaction, work, enabled: false));

        var running = RunOneAsync(provider);
        var callback = await probe.CallbackWaitingForAuthority.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await WaitForBlockerAsync(fixture.AdminDataSource, callback.BackendPid, revocationPid, running);

        await revocationTransaction.CommitAsync();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(20));
        var prepared = await probe.Prepared.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(prepared.HandoffPrepared);
        Assert.Equal(0, fakeProvider.Calls);
        Assert.Equal(1, result.Failed);
        Assert.Equal(0L, await fixture.AdminScalarAsync<long>(
            "SELECT count(*) FROM handoff_consumer.ledger WHERE logical_operation = @operation;",
            ("operation", work.LogicalOperation)));
        Assert.Equal(1L, await fixture.AdminScalarAsync<long>(
            "SELECT count(*) FROM handoff_consumer.caller_marker WHERE marker_id = @marker_id;",
            ("marker_id", prepared.MarkerId)));
    }

    [Fact]
    public async Task Handoff_OuterCommitLockWinsAndRevocationWaitsForPreparedHandoff()
    {
        await using var fixture = await HandoffPostgreSqlFixture.CreateAsync();
        var work = fixture.CreateWork("handoff-wins");
        await fixture.SeedAuthorityAsync(work);

        var probe = new HandoffExecutionProbe(holdCallerCommit: true);
        var fakeProvider = new HandoffFakeProvider(fixture.RuntimeDataSource);
        await using var provider = fixture.BuildProvider(probe, fakeProvider);
        _ = await fixture.EnqueueAsync(provider, work);

        var running = RunOneAsync(provider);
        var prepared = await probe.Prepared.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(prepared.HandoffPrepared);

        await using var revocationConnection = await fixture.AdminDataSource.OpenConnectionAsync();
        await using var revocationTransaction = await revocationConnection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var revocationPid = await BackendPidAsync(revocationConnection, revocationTransaction);
        var revocation = UpdateAuthorityAsync(revocationConnection, revocationTransaction, work, enabled: false);
        await WaitForBlockerAsync(fixture.AdminDataSource, revocationPid, prepared.BackendPid, running);

        probe.AllowCallerCommit();
        Assert.Equal(1, await revocation.WaitAsync(TimeSpan.FromSeconds(15)));
        await revocationTransaction.CommitAsync();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(1, result.Processed);
        Assert.Equal(1, fakeProvider.Calls);
        Assert.True(fakeProvider.LastObservation is { TransactionDisposed: true, ConnectionClosed: true, LedgerVisibleToFreshReader: true });
        Assert.Equal(1L, await fixture.AdminScalarAsync<long>(
            "SELECT count(*) FROM handoff_consumer.ledger WHERE activity_id = @activity_id AND logical_operation = @operation;",
            ("activity_id", fakeProvider.LastObservation!.ActivityId), ("operation", work.LogicalOperation)));
        Assert.False(await fixture.AdminScalarAsync<bool>(
            "SELECT is_authorized FROM handoff_consumer.authority WHERE scope_id = @scope_id AND operation_key = @operation;",
            ("scope_id", work.ScopeId), ("operation", work.AuthorityKey)));
    }

    private static async Task<DurableRuntimePumpResult> RunOneAsync(ServiceProvider provider) =>
        await provider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Work));

    internal static async Task<int> BackendPidAsync(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        await using var command = new NpgsqlCommand("SELECT pg_backend_pid();", connection, transaction);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<int> UpdateAuthorityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        HandoffWork work,
        bool enabled)
    {
        await using var command = new NpgsqlCommand("""
            UPDATE handoff_consumer.authority
            SET is_authorized = @enabled
            WHERE scope_id = @scope_id AND operation_key = @operation;
            """, connection, transaction);
        command.Parameters.AddWithValue("enabled", enabled);
        command.Parameters.AddWithValue("scope_id", work.ScopeId);
        command.Parameters.AddWithValue("operation", work.AuthorityKey);
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task WaitForBlockerAsync(
        NpgsqlDataSource observer,
        int blockedPid,
        int expectedBlockerPid,
        Task pendingOperation)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            if (pendingOperation.IsCompleted)
            {
                await pendingOperation;
                throw new InvalidOperationException("The operation completed before PostgreSQL reported the expected lock blocker.");
            }

            await using var command = observer.CreateCommand(
                "SELECT @blocker = ANY(pg_catalog.pg_blocking_pids(@blocked));");
            command.Parameters.AddWithValue("blocker", expectedBlockerPid);
            command.Parameters.AddWithValue("blocked", blockedPid);
            if ((bool)(await command.ExecuteScalarAsync(timeout.Token))!)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }

    private static class PayloadCodecs
    {
        internal static readonly IDurablePayloadCodec<HandoffWork> Work =
            new SystemTextJsonDurablePayloadCodec<HandoffWork>(
                "tests.packed.handoff.work",
                "v1",
                DurableDataClassification.ApprovedApplication,
                HandoffJsonContext.Default.HandoffWork,
                static _ => true);

        internal static readonly IDurablePayloadCodec<HandoffResult> Result =
            new SystemTextJsonDurablePayloadCodec<HandoffResult>(
                "tests.packed.handoff.result",
                "v1",
                DurableDataClassification.ApprovedApplication,
                HandoffJsonContext.Default.HandoffResult,
                static _ => true);
    }

    [JsonSerializable(typeof(HandoffWork))]
    [JsonSerializable(typeof(HandoffResult))]
    private sealed partial class HandoffJsonContext : JsonSerializerContext;
}

internal sealed class HandoffPostgreSqlFixture : IAsyncDisposable
{
    private const string DispatcherPassword = "handoff-dispatcher-test-password";
    private const string RuntimePassword = "handoff-runtime-test-password";
    private const string TestConnectionOptions = "-c lock_timeout=20s -c statement_timeout=30s -c idle_in_transaction_session_timeout=45s";
    private static long _nextTestId;

    private HandoffPostgreSqlFixture(
        PostgreSqlIntegrationTestDatabase database,
        NpgsqlDataSource adminDataSource,
        NpgsqlDataSource dispatcherDataSource,
        NpgsqlDataSource runtimeDataSource,
        Guid epoch,
        Guid storeId,
        string dispatcherRole,
        string runtimeRole)
    {
        Database = database;
        AdminDataSource = adminDataSource;
        DispatcherDataSource = dispatcherDataSource;
        RuntimeDataSource = runtimeDataSource;
        Epoch = epoch;
        StoreId = storeId;
        DispatcherRole = dispatcherRole;
        RuntimeRole = runtimeRole;
    }

    internal PostgreSqlIntegrationTestDatabase Database { get; }
    internal NpgsqlDataSource AdminDataSource { get; }
    internal NpgsqlDataSource DispatcherDataSource { get; }
    internal NpgsqlDataSource RuntimeDataSource { get; }
    internal Guid Epoch { get; }
    internal Guid StoreId { get; }
    private string DispatcherRole { get; }
    internal string RuntimeRole { get; }

    internal static async Task<HandoffPostgreSqlFixture> CreateAsync()
    {
        var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        NpgsqlDataSource? adminDataSource = null;
        string? dispatcherRole = null;
        string? runtimeRole = null;
        try
        {
            var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
            await schema.ApplyAsync();
            await database.SetExecutionTimeAsync(HandoffPostgreSqlConsumerTests.ClockAnchor);
            var epoch = Guid.NewGuid();
            await schema.InitializeRuntimeEpochAsync(epoch, "packed-handoff-consumer", "public-executor-proof");
            var status = await schema.GetStatusAsync();
            if (!status.IsCompatible || status.StoreId == Guid.Empty)
            {
                throw new InvalidOperationException("The packed handoff consumer could not initialize its isolated PostgreSQL store.");
            }

            var roleSuffix = Guid.NewGuid().ToString("N");
            dispatcherRole = $"handoff_dispatcher_{roleSuffix}";
            runtimeRole = $"handoff_runtime_{roleSuffix}";
            await CreateRestrictedRolesAsync(database.DataSource, dispatcherRole, runtimeRole);
            await CreateApplicationSchemaAsync(database.DataSource, runtimeRole);

            adminDataSource = CreateAdminDataSource(database.ConnectionString);
            var dispatcher = CreateRoleDataSource(database.ConnectionString, dispatcherRole, DispatcherPassword, "dispatcher");
            var runtime = CreateRoleDataSource(database.ConnectionString, runtimeRole, RuntimePassword, "runtime");
            return new HandoffPostgreSqlFixture(
                database,
                adminDataSource,
                dispatcher,
                runtime,
                epoch,
                status.StoreId,
                dispatcherRole,
                runtimeRole);
        }
        catch
        {
            if (adminDataSource is not null)
            {
                await adminDataSource.DisposeAsync();
            }

            if (runtimeRole is not null)
            {
                await DropRoleAsync(database.DataSource, runtimeRole);
            }

            if (dispatcherRole is not null)
            {
                await DropRoleAsync(database.DataSource, dispatcherRole);
            }

            await database.DisposeAsync();
            throw;
        }
    }

    internal HandoffWork CreateWork(string logicalOperation)
    {
        var id = Interlocked.Increment(ref _nextTestId);
        return new HandoffWork(
            $"handoff-scope-{id}-{Guid.NewGuid():N}",
            "delivery-authority",
            logicalOperation,
            Guid.NewGuid());
    }

    internal ServiceProvider BuildProvider(HandoffExecutionProbe probe, HandoffFakeProvider fakeProvider)
    {
        var services = new ServiceCollection();
        services.AddSingleton(probe);
        services.AddSingleton(fakeProvider);
        services.AddSingleton(RuntimeDataSource);
        services.AddDurableWork(HandoffPostgreSqlConsumerTests.Definition.ExecutedByExit<HandoffExecutor>());
        services.AddAppSurfaceDurablePostgreSql(
            DispatcherDataSource,
            RuntimeDataSource,
            new PostgreSqlDurableWorkOptions(Epoch, StoreId),
            new PostgreSqlDurableScheduleOptions("packed-handoff-consumer"),
            options =>
            {
                options.WorkerId = $"handoff-proof-{Guid.NewGuid():N}";
                options.SendWakeNotifications = false;
                options.EnableHeartbeatMaintenance = false;
                options.HostedSurfaces = DurableRuntimeSurface.Work;
            });
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    internal async Task<DurableOperationResult<DurableWorkAcceptance>> EnqueueAsync(
        ServiceProvider provider,
        HandoffWork work)
    {
        var accepted = await provider.GetRequiredService<IDurableWorkClient>().EnqueueAsync(
            HandoffPostgreSqlConsumerTests.Definition.CreateRequest(
                new DurableScopeId(work.ScopeId),
                new DurableCommandId($"handoff-command-{Guid.NewGuid():N}"),
                $"handoff-idempotency-{Guid.NewGuid():N}",
                work));
        if (!accepted.IsSuccess)
        {
            throw new InvalidOperationException(accepted.Problem?.Problem ?? "The packed consumer Work request was not accepted.");
        }

        return accepted;
    }

    internal async Task SeedAuthorityAsync(HandoffWork work)
    {
        await using var command = AdminDataSource.CreateCommand("""
            INSERT INTO handoff_consumer.authority (scope_id, operation_key, is_authorized)
            VALUES (@scope_id, @operation, true);
            """);
        command.Parameters.AddWithValue("scope_id", work.ScopeId);
        command.Parameters.AddWithValue("operation", work.AuthorityKey);
        await command.ExecuteNonQueryAsync();
    }

    internal Task<T> AdminScalarAsync<T>(string sql, params (string Name, object Value)[] parameters) =>
        ScalarAsync<T>(AdminDataSource, sql, parameters);

    internal Task<T> RuntimeScalarAsync<T>(string sql, params (string Name, object Value)[] parameters) =>
        ScalarAsync<T>(RuntimeDataSource, sql, parameters);

    private static async Task<T> ScalarAsync<T>(
        NpgsqlDataSource dataSource,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = dataSource.CreateCommand(sql);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task CreateApplicationSchemaAsync(NpgsqlDataSource admin, string runtimeRole)
    {
        var sql = """
            CREATE SCHEMA handoff_consumer;
            REVOKE ALL ON SCHEMA handoff_consumer FROM PUBLIC;
            CREATE TABLE handoff_consumer.authority
            (
                scope_id text NOT NULL,
                operation_key text NOT NULL,
                is_authorized boolean NOT NULL,
                PRIMARY KEY (scope_id, operation_key)
            );
            CREATE TABLE handoff_consumer.ledger
            (
                scope_id text NOT NULL,
                activity_id text NOT NULL,
                logical_operation text NOT NULL,
                prepared_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
                PRIMARY KEY (scope_id, activity_id),
                UNIQUE (scope_id, logical_operation)
            );
            CREATE TABLE handoff_consumer.caller_marker
            (
                marker_id uuid PRIMARY KEY,
                scope_id text NOT NULL,
                marker_value text NOT NULL
            );
            ALTER TABLE handoff_consumer.authority ENABLE ROW LEVEL SECURITY;
            ALTER TABLE handoff_consumer.authority FORCE ROW LEVEL SECURITY;
            CREATE POLICY authority_scope_isolation ON handoff_consumer.authority
                USING (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''))
                WITH CHECK (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''));
            ALTER TABLE handoff_consumer.ledger ENABLE ROW LEVEL SECURITY;
            ALTER TABLE handoff_consumer.ledger FORCE ROW LEVEL SECURITY;
            CREATE POLICY ledger_scope_isolation ON handoff_consumer.ledger
                USING (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''))
                WITH CHECK (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''));
            GRANT USAGE ON SCHEMA handoff_consumer TO handoff_consumer_runtime;
            GRANT SELECT ON handoff_consumer.authority, handoff_consumer.ledger TO handoff_consumer_runtime;
            GRANT UPDATE (is_authorized) ON handoff_consumer.authority TO handoff_consumer_runtime;
            GRANT INSERT ON handoff_consumer.ledger, handoff_consumer.caller_marker TO handoff_consumer_runtime;
            """;
        sql = sql.Replace("handoff_consumer_runtime", QuoteIdentifier(runtimeRole), StringComparison.Ordinal);
        await using var command = admin.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task CreateRestrictedRolesAsync(NpgsqlDataSource admin, string dispatcherRole, string runtimeRole)
    {
        var sql = """
            CREATE ROLE handoff_consumer_dispatcher LOGIN PASSWORD 'handoff-dispatcher-test-password'
                NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;
            CREATE ROLE handoff_consumer_runtime LOGIN PASSWORD 'handoff-runtime-test-password'
                NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;

            GRANT USAGE ON SCHEMA appsurface_durable TO handoff_consumer_dispatcher, handoff_consumer_runtime;
            GRANT EXECUTE ON FUNCTION appsurface_durable.discover_work_dispatch(text[], text[], integer)
                TO handoff_consumer_dispatcher;
            GRANT EXECUTE ON FUNCTION appsurface_durable.runtime_due_dispatch_health(integer),
                appsurface_durable.prune_runtime_heartbeats(interval, integer, text, uuid),
                appsurface_durable.work_execution_now(),
                appsurface_durable.attempt_plan_offsets_are_valid(bigint[])
                TO handoff_consumer_runtime;
            GRANT SELECT ON appsurface_durable.flow_wait, appsurface_durable.flow_trace_context,
                appsurface_durable.schedule_definition, appsurface_durable.schedule_occurrence,
                appsurface_durable.schedule_dispatch TO handoff_consumer_runtime;
            GRANT UPDATE (revision) ON appsurface_durable.schedule_definition TO handoff_consumer_runtime;
            GRANT UPDATE (dispatch_revision, due_at, state, lease_owner, lease_expires_at, updated_at)
                ON appsurface_durable.schedule_dispatch TO handoff_consumer_runtime;
            GRANT INSERT ON appsurface_durable.schedule_history TO handoff_consumer_runtime;
            GRANT SELECT ON appsurface_durable.store_metadata,
                appsurface_durable.schema_migration,
                appsurface_durable.runtime_heartbeat
                TO handoff_consumer_runtime;
            GRANT SELECT, INSERT ON appsurface_durable.scope,
                appsurface_durable.work,
                appsurface_durable.dispatch
                TO handoff_consumer_runtime;
            GRANT UPDATE (generation, state, updated_at) ON appsurface_durable.scope TO handoff_consumer_runtime;
            GRANT UPDATE (state, due_at, updated_at, terminal_at, cancellation_requested_at,
                attempt_number, lease_generation, lease_owner, lease_started_at, lease_expires_at,
                runtime_epoch, revision, result_contract_id, result_schema_version, result_codec_id,
                result_classification, result_retention_policy_id, result_payload, result_sha256,
                terminal_code, execution_admission_closed_at, execution_admission_closed_reason,
                execution_deadline_reached_at, trace_context_id)
                ON appsurface_durable.work TO handoff_consumer_runtime;
            GRANT UPDATE (due_at, state, expected_revision, updated_at, execution_discovery_at)
                ON appsurface_durable.dispatch TO handoff_consumer_runtime;
            GRANT INSERT, UPDATE (worker_instance_id, runtime_epoch, hosted_surfaces, started_at,
                last_heartbeat_at, last_successful_sweep_at, draining, pass_active, pass_started_at,
                last_discovered, last_claimed, last_processed, last_deferred, last_failed,
                last_pass_elapsed_ms, updated_at)
                ON appsurface_durable.runtime_heartbeat TO handoff_consumer_runtime;
            GRANT SELECT, INSERT ON appsurface_durable.work_operator_command,
                appsurface_durable.effect_permit TO handoff_consumer_runtime;
            GRANT UPDATE (status, resulting_state, resulting_revision, resolution_kind, completed_at)
                ON appsurface_durable.work_operator_command TO handoff_consumer_runtime;
            GRANT UPDATE (status, observed_at, details, runtime_epoch, invocation_admitted_at)
                ON appsurface_durable.effect_permit TO handoff_consumer_runtime;
            GRANT SELECT, INSERT ON appsurface_durable.scope_history,
                appsurface_durable.work_history TO handoff_consumer_runtime;
            GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA appsurface_durable TO handoff_consumer_runtime;
            """;
        sql = sql
            .Replace("handoff_consumer_dispatcher", QuoteIdentifier(dispatcherRole), StringComparison.Ordinal)
            .Replace("handoff_consumer_runtime", QuoteIdentifier(runtimeRole), StringComparison.Ordinal);
        await using var command = admin.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private static string QuoteIdentifier(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static NpgsqlDataSource CreateAdminDataSource(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = $"packed-handoff-admin-{Guid.NewGuid():N}",
            Pooling = true,
            MaxPoolSize = 8,
            Options = TestConnectionOptions,
        };
        return NpgsqlDataSource.Create(builder.ConnectionString);
    }

    private static async Task DropRoleAsync(NpgsqlDataSource admin, string role)
    {
        await using (var exists = admin.CreateCommand("SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = @role);"))
        {
            exists.Parameters.AddWithValue("role", role);
            if (!(bool)(await exists.ExecuteScalarAsync())!)
            {
                return;
            }
        }

        var quotedRole = QuoteIdentifier(role);
        await using var cleanup = admin.CreateCommand($"DROP OWNED BY {quotedRole}; DROP ROLE {quotedRole};");
        await cleanup.ExecuteNonQueryAsync();
    }

    private static NpgsqlDataSource CreateRoleDataSource(
        string connectionString,
        string role,
        string password,
        string lane)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Username = role,
            Password = password,
            ApplicationName = $"packed-handoff-{lane}-{Guid.NewGuid():N}",
            Pooling = true,
            MaxPoolSize = 32,
            Options = TestConnectionOptions,
        };
        return NpgsqlDataSource.Create(builder.ConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        await DispatcherDataSource.DisposeAsync();
        await RuntimeDataSource.DisposeAsync();
        await DropRoleAsync(AdminDataSource, RuntimeRole);
        await DropRoleAsync(AdminDataSource, DispatcherRole);
        await AdminDataSource.DisposeAsync();
        await Database.DisposeAsync();
    }
}

internal sealed class HandoffExecutionProbe(
    bool holdCallerCommit = false,
    bool denyAfterLedgerInsert = false,
    bool holdBeforeFence = false,
    bool holdBeforeProvider = false,
    HandoffCommitObservationMode commitMode = HandoffCommitObservationMode.Acknowledged)
{
    private readonly TaskCompletionSource<bool> _allowCallerCommit = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource<HandoffCallbackObservation> CallbackWaitingForAuthority { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource<HandoffPreparedObservation> Prepared { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly TaskCompletionSource<bool> _allowFence = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _allowProvider = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource<IDurableWorkHandoffFence> BeforeFence { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource<bool> ResourcesReleased { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource<Exception> Failure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource<bool> CommitLedgerVisible { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal IDurableWorkHandoffFence? RetainedFence;
    internal string? CrossScope;
    internal bool CrossScopeRowsHidden;
    internal int CallbackCalls;
    internal TaskCompletionSource<string?> PreparationProblem { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int PreparationCallbackCalls;
    internal string? ScopeAfterFailure;
    internal Exception? CommitFailure;
    internal HandoffCommitObservationMode CommitMode { get; } = commitMode;
    internal bool DenyAfterLedgerInsert { get; } = denyAfterLedgerInsert;
    internal Task WaitForFenceAsync(CancellationToken token) => (holdBeforeFence ? _allowFence.Task : Task.CompletedTask).WaitAsync(token);
    internal Task WaitForProviderAsync(CancellationToken token) => (holdBeforeProvider ? _allowProvider.Task : Task.CompletedTask).WaitAsync(token);
    internal void AllowFence() => _allowFence.TrySetResult(true);
    internal void AllowProvider() => _allowProvider.TrySetResult(true);

    internal Task WaitForCallerCommitAsync(CancellationToken cancellationToken) =>
        (holdCallerCommit ? _allowCallerCommit.Task : Task.CompletedTask).WaitAsync(cancellationToken);

    internal void AllowCallerCommit() => _allowCallerCommit.TrySetResult(true);
}

internal sealed record HandoffWork(
    string ScopeId,
    string AuthorityKey,
    string LogicalOperation,
    Guid MarkerId);

internal sealed record HandoffResult(string ActivityId);

internal sealed record HandoffCallbackObservation(int BackendPid);

internal sealed record HandoffPreparedObservation(
    bool HandoffPrepared,
    bool CallbackUsedExactTransaction,
    IsolationLevel IsolationLevel,
    bool CallbackSawRestrictedRole,
    bool CallbackSawBoundNativeScope,
    bool CallbackSawOneNativeWorkRow,
    bool CallbackSawOneApplicationAuthorityRow,
    string? NativeScopeAfterFence,
    int BackendPid,
    Guid MarkerId);

internal sealed record HandoffProviderObservation(
    bool TransactionDisposed,
    bool ConnectionClosed,
    bool LedgerVisibleToFreshReader,
    string ActivityId,
    Guid MarkerId);

internal sealed class HandoffFakeProvider(NpgsqlDataSource runtimeDataSource)
{
    internal int Calls;
    internal HandoffProviderObservation? LastObservation;

    internal async ValueTask SendAsync(
        HandoffWork work,
        string activityId,
        NpgsqlTransaction callerTransaction,
        NpgsqlConnection callerConnection,
        bool callerTransactionDisposed)
    {
        Interlocked.Increment(ref Calls);
        var connectionClosed = callerConnection.State == ConnectionState.Closed;
        if (!callerTransactionDisposed || !connectionClosed)
        {
            throw new InvalidOperationException("The fake provider ran before the caller transaction and connection were released.");
        }

        _ = callerTransaction;
        await using var connection = await runtimeDataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await using (var setScope = new NpgsqlCommand(
            "SELECT set_config('appsurface_durable.scope_id', @scope_id, true);",
            connection,
            transaction))
        {
            setScope.Parameters.AddWithValue("scope_id", work.ScopeId);
            await setScope.ExecuteNonQueryAsync();
        }

        await using var read = new NpgsqlCommand("""
            SELECT count(*)
            FROM handoff_consumer.ledger
            WHERE scope_id = @scope_id AND activity_id = @activity_id AND logical_operation = @operation;
            """, connection, transaction);
        read.Parameters.AddWithValue("scope_id", work.ScopeId);
        read.Parameters.AddWithValue("activity_id", activityId);
        read.Parameters.AddWithValue("operation", work.LogicalOperation);
        var visible = (long)(await read.ExecuteScalarAsync())! == 1;
        await transaction.CommitAsync();
        LastObservation = new HandoffProviderObservation(
            callerTransactionDisposed,
            connectionClosed,
            visible,
            activityId,
            work.MarkerId);
        if (!visible)
        {
            throw new InvalidOperationException("The post-commit provider reader could not see the prepared application ledger row.");
        }
    }
}

// docs:snippet durable-handoff-application-flow:start
internal sealed class HandoffExecutor(
    IDurableWorkHandoffFence handoffFence,
    NpgsqlDataSource runtimeDataSource,
    HandoffExecutionProbe probe,
    HandoffFakeProvider fakeProvider)
    : IDurableWorkExitExecutor<HandoffWork, HandoffResult>
{
    private readonly Task _preparation = ObservePreparationAsync(handoffFence, runtimeDataSource, probe);

    private static async Task ObservePreparationAsync(IDurableWorkHandoffFence fence, NpgsqlDataSource source, HandoffExecutionProbe observation)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var result = await fence.ExecuteAsync(transaction, (_, _) =>
        {
            Interlocked.Increment(ref observation.PreparationCallbackCalls);
            return ValueTask.FromResult(true);
        });
        observation.PreparationProblem.TrySetResult(result.Problem?.Code);
    }

    public async ValueTask<DurableWorkExit<HandoffResult>> ExecuteAsync(
        DurableWorkerEnvelope<HandoffWork> envelope,
        CancellationToken cancellationToken = default)
    {
        try { await _preparation; return await ExecuteCoreAsync(envelope, cancellationToken); }
        catch (Exception exception) { probe.Failure.TrySetResult(exception); throw; }
    }

    private async ValueTask<DurableWorkExit<HandoffResult>> ExecuteCoreAsync(
        DurableWorkerEnvelope<HandoffWork> envelope, CancellationToken cancellationToken)
    {
        var work = envelope.Payload ?? throw new InvalidOperationException("The packed handoff Work payload was missing.");
        var identity = envelope.ExecutionIdentity ?? throw new InvalidOperationException("The native executor received no execution identity.");
        NpgsqlConnection? callerConnection = null;
        NpgsqlTransaction? callerTransaction = null;
        bool handoffPrepared;
        int backendPid;
        string? nativeScopeAfterFence;
        bool callbackUsedExactTransaction = false;
        bool callbackSawRestrictedRole = false;
        bool callbackSawBoundNativeScope = false;
        bool callbackSawOneNativeWorkRow = false;
        bool callbackSawOneApplicationAuthorityRow = false;
        bool callerTransactionDisposed = false;

        await using (var connection = await runtimeDataSource.OpenConnectionAsync(cancellationToken))
        {
            callerConnection = connection;
            await using (var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken))
            {
                callerTransaction = transaction;
                backendPid = await HandoffPostgreSqlConsumerTests.BackendPidAsync(connection, transaction);
                await using (var setPriorScope = new NpgsqlCommand(
                    "SELECT set_config('appsurface_durable.scope_id', 'caller-scope-before-handoff', true);",
                    connection,
                    transaction))
                {
                    await setPriorScope.ExecuteNonQueryAsync(cancellationToken);
                }

                await using (var marker = new NpgsqlCommand("""
                    INSERT INTO handoff_consumer.caller_marker (marker_id, scope_id, marker_value)
                    VALUES (@marker_id, @scope_id, 'caller-owned-before-fence');
                    """, connection, transaction))
                {
                    marker.Parameters.AddWithValue("marker_id", work.MarkerId);
                    marker.Parameters.AddWithValue("scope_id", work.ScopeId);
                    await marker.ExecuteNonQueryAsync(cancellationToken);
                }

                probe.RetainedFence = handoffFence;
                probe.BeforeFence.TrySetResult(handoffFence);
                await probe.WaitForFenceAsync(cancellationToken);
                DurableOperationResult<DurableWorkHandoffResult> result;
                try
                {
                    result = await handoffFence.ExecuteAsync(
                    transaction,
                    async (suppliedTransaction, token) =>
                    {
                        Interlocked.Increment(ref probe.CallbackCalls);
                        if (probe.CrossScope is { } otherScope)
                        {
                            await using var crossRead = new NpgsqlCommand("""
                                SELECT (SELECT count(*) FROM appsurface_durable.work WHERE scope_id=@other_scope)
                                     + (SELECT count(*) FROM handoff_consumer.authority WHERE scope_id=@other_scope);
                                """, connection, suppliedTransaction);
                            crossRead.Parameters.AddWithValue("other_scope", otherScope);
                            probe.CrossScopeRowsHidden = (long)(await crossRead.ExecuteScalarAsync(token))! == 0;
                            await using var forbidden = new NpgsqlCommand("""
                                INSERT INTO handoff_consumer.ledger(scope_id, activity_id, logical_operation)
                                VALUES (@other_scope, @activity_id, @operation);
                                """, connection, suppliedTransaction);
                            forbidden.Parameters.AddWithValue("other_scope", otherScope);
                            forbidden.Parameters.AddWithValue("activity_id", identity.ActivityId);
                            forbidden.Parameters.AddWithValue("operation", work.LogicalOperation);
                            await forbidden.ExecuteNonQueryAsync(token);
                        }
                        callbackUsedExactTransaction = ReferenceEquals(suppliedTransaction, transaction)
                            && ReferenceEquals(suppliedTransaction.Connection, connection);
                        if (!callbackUsedExactTransaction)
                        {
                            throw new InvalidOperationException("The handoff callback did not receive the caller's exact transaction and connection.");
                        }

                        await using (var inspect = new NpgsqlCommand("""
                            SELECT current_user,
                                   current_setting('appsurface_durable.scope_id', true),
                                   (SELECT count(*) FROM appsurface_durable.work WHERE scope_id = @scope_id),
                                   (SELECT count(*) FROM handoff_consumer.authority WHERE scope_id = @scope_id),
                                   (SELECT rolsuper OR rolbypassrls FROM pg_catalog.pg_roles WHERE rolname = current_user);
                            """, connection, suppliedTransaction))
                        {
                            inspect.Parameters.AddWithValue("scope_id", work.ScopeId);
                            await using var reader = await inspect.ExecuteReaderAsync(token);
                            if (!await reader.ReadAsync(token))
                            {
                                throw new InvalidOperationException("The handoff callback could not inspect the restricted execution context.");
                            }

                            callbackSawRestrictedRole = reader.GetString(0).StartsWith("handoff_runtime_", StringComparison.Ordinal) && !reader.GetBoolean(4);
                            callbackSawBoundNativeScope = reader.GetString(1) == work.ScopeId;
                            callbackSawOneNativeWorkRow = reader.GetInt64(2) == 1;
                            callbackSawOneApplicationAuthorityRow = reader.GetInt64(3) == 1;
                        }

                        probe.CallbackWaitingForAuthority.TrySetResult(new HandoffCallbackObservation(backendPid));
                        bool authorized;
                        await using (var authority = new NpgsqlCommand("""
                            SELECT is_authorized
                            FROM handoff_consumer.authority
                            WHERE scope_id = @scope_id AND operation_key = @authority_key
                            FOR UPDATE;
                            """, connection, suppliedTransaction))
                        {
                            authority.Parameters.AddWithValue("scope_id", work.ScopeId);
                            authority.Parameters.AddWithValue("authority_key", work.AuthorityKey);
                            var value = await authority.ExecuteScalarAsync(token);
                            authorized = value is true;
                        }

                        if (!authorized)
                        {
                            return false;
                        }

                        // The callback writes conditionally on the locked authority row. Both immutable identities are unique.
                        await using var ledger = new NpgsqlCommand("""
                            INSERT INTO handoff_consumer.ledger (scope_id, activity_id, logical_operation)
                            VALUES (@scope_id, @activity_id, @operation)
                            ON CONFLICT DO NOTHING
                            RETURNING activity_id;
                            """, connection, suppliedTransaction);
                        ledger.Parameters.AddWithValue("scope_id", work.ScopeId);
                        ledger.Parameters.AddWithValue("activity_id", identity.ActivityId);
                        ledger.Parameters.AddWithValue("operation", work.LogicalOperation);
                        var inserted = await ledger.ExecuteScalarAsync(token);
                        if (inserted is null)
                        {
                            return false;
                        }

                        // This late test-only denial exercises removal of a staged callback write by the callback savepoint.
                        return !probe.DenyAfterLedgerInsert;
                    },
                    cancellationToken);
                }
                catch
                {
                    await using var scopeAfterFailure = new NpgsqlCommand("SELECT current_setting('appsurface_durable.scope_id', true);", connection, transaction);
                    probe.ScopeAfterFailure = await scopeAfterFailure.ExecuteScalarAsync(CancellationToken.None) as string;
                    throw;
                }

                if (!result.IsSuccess || result.Value is null)
                {
                    throw new InvalidOperationException(result.Problem?.Code ?? "The public handoff fence refused without a problem code.");
                }

                handoffPrepared = result.Value.HandoffPrepared;
                await using (var readRestoredScope = new NpgsqlCommand(
                    "SELECT current_setting('appsurface_durable.scope_id', true);",
                    connection,
                    transaction))
                {
                    nativeScopeAfterFence = await readRestoredScope.ExecuteScalarAsync(cancellationToken) as string;
                }

                probe.Prepared.TrySetResult(new HandoffPreparedObservation(
                    handoffPrepared,
                    callbackUsedExactTransaction,
                    transaction.IsolationLevel,
                    callbackSawRestrictedRole,
                    callbackSawBoundNativeScope,
                    callbackSawOneNativeWorkRow,
                    callbackSawOneApplicationAuthorityRow,
                    nativeScopeAfterFence,
                    backendPid,
                    work.MarkerId));

                await probe.WaitForCallerCommitAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (probe.CommitMode == HandoffCommitObservationMode.EstablishedRollback)
                    await transaction.RollbackAsync(cancellationToken);
                else if (probe.CommitMode == HandoffCommitObservationMode.Unresolved)
                {
                    // A fresh scoped reader cannot see this uncommitted row. Absence does not settle its outcome.
                    probe.CommitLedgerVisible.TrySetResult(await ReadLedgerAsync(work, identity.ActivityId));
                    await transaction.RollbackAsync(cancellationToken);
                }
                else
                {
                    try
                    {
                        await transaction.CommitAsync(cancellationToken);
                        if (probe.CommitMode == HandoffCommitObservationMode.AcknowledgementLost)
                            throw new IOException("Test adapter lost acknowledgement after a real PostgreSQL commit.");
                    }
                    catch (IOException failure) when (probe.CommitMode == HandoffCommitObservationMode.AcknowledgementLost)
                    {
                        probe.CommitFailure = failure;
                    }
                }
            }

            callerTransactionDisposed = true;
        }

        probe.ResourcesReleased.TrySetResult(callerTransactionDisposed && callerConnection!.State == ConnectionState.Closed);
        if (probe.CommitMode != HandoffCommitObservationMode.Acknowledged)
        {
            if (probe.CommitMode != HandoffCommitObservationMode.Unresolved)
                probe.CommitLedgerVisible.TrySetResult(await ReadLedgerAsync(work, identity.ActivityId));
            // The test adapter reports acknowledgement loss after a real commit, established rollback, or unresolved
            // outcome. Every uncertain path pauses provider I/O and leaves recovery to ledger/provider policy.
            return DurableWorkExit<HandoffResult>.AmbiguousExternalOutcome("handoff_commit_requires_reconciliation");
        }
        await probe.WaitForProviderAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!handoffPrepared)
        {
            return DurableWorkExit<HandoffResult>.RetryBeforeEffect("handoff_denied");
        }

        await fakeProvider.SendAsync(
            work,
            identity.ActivityId,
            callerTransaction!,
            callerConnection!,
            callerTransactionDisposed);
        return DurableWorkExit<HandoffResult>.Succeeded(new HandoffResult(identity.ActivityId));
    }
    private async Task<bool> ReadLedgerAsync(HandoffWork work, string activityId)
    {
        await using var connection = await runtimeDataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await using (var scope = new NpgsqlCommand("SELECT set_config('appsurface_durable.scope_id', @scope, true);", connection, transaction))
        {
            scope.Parameters.AddWithValue("scope", work.ScopeId);
            await scope.ExecuteNonQueryAsync();
        }
        await using var read = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM handoff_consumer.ledger WHERE scope_id=@scope AND activity_id=@activity);", connection, transaction);
        read.Parameters.AddWithValue("scope", work.ScopeId);
        read.Parameters.AddWithValue("activity", activityId);
        return (bool)(await read.ExecuteScalarAsync())!;
    }

}

// docs:snippet durable-handoff-application-flow:end

internal enum HandoffCommitObservationMode { Acknowledged, AcknowledgementLost, EstablishedRollback, Unresolved }
