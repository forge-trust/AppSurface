using System.Security.Cryptography;
using System.Text;
using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.PostgreSql;
using ForgeTrust.AppSurface.Durable.Provider;
using ForgeTrust.AppSurface.Flow;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

internal sealed record LaneProofRolePair(
    string DispatcherRole,
    string DispatcherPassword,
    string RuntimeRole,
    string RuntimePassword);

internal static class LaneProof
{
    private const string WorkName = "tests.postgresql-preflight.lane-proof";
    private const string WorkVersion = "v1";
    private const string InsufficientPrivilegeSqlState = PostgresErrorCodes.InsufficientPrivilege;

    internal static async Task VerifyForwarderAsync(
        string ownerConnectionString,
        LaneProofRolePair forwarder,
        Guid runtimeEpoch,
        Guid storeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerConnectionString);
        ArgumentNullException.ThrowIfNull(forwarder);
        ValidatePair(forwarder, nameof(forwarder));
        ValidateRuntimeIdentity(runtimeEpoch, storeId);
        var ownerBuilder = new NpgsqlConnectionStringBuilder(ownerConnectionString) { Pooling = false };
        await using var ownerDataSource = NpgsqlDataSource.Create(ownerBuilder.ConnectionString);
        await AssertForwarderDispatcherCapabilitiesAsync(ownerDataSource, forwarder.DispatcherRole, cancellationToken);
        await RunWorkThroughProviderAsync(ownerConnectionString, forwarder, runtimeEpoch, storeId,
            "single-full-pair", cancellationToken);
        await RunFlowScheduleThroughProviderAsync(ownerConnectionString, forwarder, runtimeEpoch, storeId,
            cancellationToken);
    }

    internal static async Task VerifyAsync(
        string ownerConnectionString,
        LaneProofRolePair forwarder,
        LaneProofRolePair sourceWorkOnly,
        Guid runtimeEpoch,
        Guid storeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerConnectionString);
        ArgumentNullException.ThrowIfNull(forwarder);
        ArgumentNullException.ThrowIfNull(sourceWorkOnly);
        ValidatePair(forwarder, nameof(forwarder));
        ValidatePair(sourceWorkOnly, nameof(sourceWorkOnly));
        ValidateRuntimeIdentity(runtimeEpoch, storeId);

        var roleNames = new[]
        {
            forwarder.DispatcherRole,
            forwarder.RuntimeRole,
            sourceWorkOnly.DispatcherRole,
            sourceWorkOnly.RuntimeRole,
        };
        if (roleNames.Distinct(StringComparer.Ordinal).Count() != roleNames.Length)
        {
            throw new ArgumentException("Forwarder and work_only dispatcher/runtime roles must be distinct.");
        }

        await VerifyForwarderAsync(ownerConnectionString, forwarder, runtimeEpoch, storeId, cancellationToken);
        await RunWorkThroughProviderAsync(
            ownerConnectionString,
            sourceWorkOnly,
            runtimeEpoch,
            storeId,
            "work-only",
            cancellationToken);

        await AssertSourceRestrictionsAsync(ownerConnectionString, forwarder, sourceWorkOnly,
            runtimeEpoch, storeId, cancellationToken);
    }

    private static async Task AssertForwarderDispatcherCapabilitiesAsync(
        NpgsqlDataSource ownerDataSource,
        string dispatcherRole,
        CancellationToken cancellationToken)
    {
        await using var command = ownerDataSource.CreateCommand(
            """
            SELECT has_function_privilege(
                       @dispatcher_role,
                       'appsurface_durable.discover_work_dispatch(text[], text[], integer)',
                       'EXECUTE'),
                   has_table_privilege(
                       @dispatcher_role,
                       'appsurface_durable.flow_dispatch',
                       'SELECT'),
                   has_function_privilege(
                       @dispatcher_role,
                       'appsurface_durable.claim_schedule_dispatch(text, interval)',
                       'EXECUTE');
            """);
        command.Parameters.AddWithValue("dispatcher_role", dispatcherRole);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("The forwarder dispatcher capability query returned no row.");
        }

        var capabilityNames = new[] { "Work discovery", "Flow dispatch", "Schedule dispatch" };
        for (var ordinal = 0; ordinal < capabilityNames.Length; ordinal++)
        {
            if (!reader.GetBoolean(ordinal))
            {
                throw new InvalidOperationException(
                    $"The forwarder dispatcher is missing {capabilityNames[ordinal]} access.");
            }
        }

        if (await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("The forwarder dispatcher capability query returned multiple rows.");
        }
    }

    private static async Task RunWorkThroughProviderAsync(
        string ownerConnectionString,
        LaneProofRolePair pair,
        Guid runtimeEpoch,
        Guid storeId,
        string proofLabel,
        CancellationToken cancellationToken)
    {
        await using var dispatcherDataSource = CreateRoleDataSource(
            ownerConnectionString,
            pair.DispatcherRole,
            pair.DispatcherPassword);
        await using var runtimeDataSource = CreateRoleDataSource(
            ownerConnectionString,
            pair.RuntimeRole,
            pair.RuntimePassword);

        var registration = new LaneProofWorkRegistration();
        await using var provider = BuildProvider(dispatcherDataSource, runtimeDataSource, pair.RuntimeRole,
            runtimeEpoch, storeId, registration);
        var client = provider.GetRequiredService<IDurableWorkClient>();
        var scope = $"lane-proof-{proofLabel}-{Guid.NewGuid():N}";
        var accepted = await client.EnqueueAsync(
            new DurableWorkRequest(
                new DurableScopeId(scope),
                DurableCommandId.New(),
                $"{scope}-key",
                LaneProof.WorkName,
                LaneProof.WorkVersion,
                registration.WorkCodec.EncodeObject(Encoding.UTF8.GetBytes("lane-proof")),
                DurableProviderSafety.Idempotent),
            cancellationToken);
        if (!accepted.IsSuccess)
        {
            throw new InvalidOperationException(
                $"The {proofLabel} lane proof could not enqueue Work.");
        }

        var result = await provider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Work),
            cancellationToken);
        if (result.Discovered != 1
            || result.Claimed != 1
            || result.Processed != 1
            || result.Deferred != 0
            || result.Failed != 0)
        {
            throw new InvalidOperationException(
                $"The {proofLabel} Work lane did not complete exactly one item " +
                $"(discovered={result.Discovered}, claimed={result.Claimed}, processed={result.Processed}, " +
                $"deferred={result.Deferred}, failed={result.Failed}).");
        }
        await using var owner = CreateOwnerDataSource(ownerConnectionString);
        await AssertCompletedWorkAsync(provider, owner, new DurableScopeId(scope), accepted.Value!.WorkId,
            cancellationToken);
    }

    // Each invocation creates new scoped facts, so the controller's before/after scenario calls
    // must independently execute all three lanes rather than reuse a previous terminal receipt.
    private static async Task RunFlowScheduleThroughProviderAsync(
        string ownerConnectionString,
        LaneProofRolePair pair,
        Guid runtimeEpoch,
        Guid storeId,
        CancellationToken cancellationToken)
    {
        await using var dispatcher = CreateRoleDataSource(ownerConnectionString, pair.DispatcherRole, pair.DispatcherPassword);
        await using var runtime = CreateRoleDataSource(ownerConnectionString, pair.RuntimeRole, pair.RuntimePassword);
        await using var owner = CreateOwnerDataSource(ownerConnectionString);
        var work = new LaneProofWorkRegistration();
        await using var provider = BuildProvider(dispatcher, runtime, pair.RuntimeRole, runtimeEpoch, storeId, work);
        var scope = new DurableScopeId($"lane-proof-full-{Guid.NewGuid():N}");
        var instance = new DurableFlowInstanceId($"{scope.Value}-flow");
        var schedule = new DurableScheduleId($"{scope.Value}-schedule");
        await SeedFlowScheduleAsync(provider, work, scope, instance, schedule, cancellationToken);
        await CompleteFlowScheduleAsync(provider, owner, scope, instance, schedule, cancellationToken);
    }

    private static ServiceProvider BuildProvider(
        NpgsqlDataSource dispatcher,
        NpgsqlDataSource runtime,
        string runtimeRole,
        Guid runtimeEpoch,
        Guid storeId,
        LaneProofWorkRegistration work)
    {
        var contextCodec = new LaneProofPayloadCodec("tests.postgresql-preflight.lane-proof.context");
        var services = new ServiceCollection();
        services.AddSingleton<DurableWorkRegistration>(work);
        services.AddSingleton<IDurablePayloadCodec>(contextCodec);
        services.AddSingleton<DurableFlowRegistration>(new LaneProofFlowRegistration(contextCodec));
        services.AddAppSurfaceDurablePostgreSql(dispatcher, runtime,
            new PostgreSqlDurableWorkOptions(runtimeEpoch, storeId),
            new PostgreSqlDurableScheduleOptions(runtimeRole),
            options =>
            {
                options.WorkerId = $"lane-proof-{Guid.NewGuid():N}";
                options.SendWakeNotifications = false;
            });
        return services.BuildServiceProvider();
    }

    private static async Task SeedFlowScheduleAsync(
        IServiceProvider provider,
        LaneProofWorkRegistration work,
        DurableScopeId scope,
        DurableFlowInstanceId instance,
        DurableScheduleId schedule,
        CancellationToken cancellationToken)
    {
        var flow = provider.GetRequiredService<IDurableFlowRegistry>().GetRequired(LaneProofFlowRegistration.Name, WorkVersion);
        var accepted = await provider.GetRequiredService<IDurableFlowClient>().StartAsync(new DurableFlowStartRequest(
            scope, DurableCommandId.New(), $"{scope.Value}-flow-key", instance, flow.FlowId, flow.FlowVersion,
            flow.ContextCodec.EncodeObject(Encoding.UTF8.GetBytes("initial"))), cancellationToken);
        if (!accepted.IsSuccess)
        {
            throw new InvalidOperationException("The forwarding lane could not accept the registered Flow.");
        }
        var created = await provider.GetRequiredService<IDurableScheduleClient>().CreateAsync(
            ScheduleRequest(work, scope, schedule), cancellationToken);
        if (!created.IsSuccess)
        {
            throw new InvalidOperationException("The forwarding lane could not accept a due Work schedule.");
        }
        var initialFlow = await provider.GetRequiredService<IDurableFlowClient>().GetAsync(
            new DurableFlowGetRequest(scope, instance), cancellationToken);
        var initialSchedule = await provider.GetRequiredService<IDurableScheduleClient>().GetAsync(scope, schedule, cancellationToken);
        if (!initialFlow.IsSuccess || initialFlow.Value!.State != DurableFlowState.Ready
            || !initialSchedule.IsSuccess || initialSchedule.Value!.State != DurableScheduleState.Active)
        {
            throw new InvalidOperationException("The lane proof requires a nonempty ready Flow and active due schedule.");
        }
    }

    private static DurableScheduleCreateRequest ScheduleRequest(
        LaneProofWorkRegistration work, DurableScopeId scope, DurableScheduleId schedule) => new(
            scope, DurableCommandId.New(), $"{schedule.Value}-key", schedule,
            DurableSchedule.At(DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1)),
            DurableScheduleTarget.Work(WorkName, WorkVersion, Encoding.UTF8.GetBytes("scheduled"), work.InputCodec));

    private static async Task CompleteFlowScheduleAsync(
        IServiceProvider provider,
        NpgsqlDataSource owner,
        DurableScopeId scope,
        DurableFlowInstanceId instance,
        DurableScheduleId schedule,
        CancellationToken cancellationToken)
    {
        var pump = provider.GetRequiredService<IDurableRuntimePump>();
        AssertPump(await pump.RunOnceAsync(new DurableRuntimePumpRequest(maximumItems: 1,
            surfaces: DurableRuntimeSurface.Flow), cancellationToken), 1, "Flow");
        var flow = await provider.GetRequiredService<IDurableFlowClient>().GetAsync(
            new DurableFlowGetRequest(scope, instance), cancellationToken);
        if (!flow.IsSuccess || flow.Value!.State != DurableFlowState.Completed || flow.Value.TerminalAtUtc is null)
        {
            throw new InvalidOperationException("The forwarding Flow has no durable completed public snapshot.");
        }
        await using var scopedOwner = await OpenScopedOwnerAsync(owner, scope, cancellationToken);
        await using (var effect = new NpgsqlCommand(
            """
            SELECT count(*) = 1 FROM appsurface_durable.flow_instance
            WHERE scope_id = @scope AND flow_instance_id = @instance AND state = 'completed'
              AND terminal_at IS NOT NULL AND context_payload = @context;
            """, scopedOwner))
        {
            effect.Parameters.AddWithValue("scope", scope.Value);
            effect.Parameters.AddWithValue("instance", instance.Value);
            effect.Parameters.AddWithValue("context", Encoding.UTF8.GetBytes("flow-completed"));
            if (await effect.ExecuteScalarAsync(cancellationToken) is not true)
            {
                throw new InvalidOperationException("The forwarding Flow did not persist its evaluated final context.");
            }
        }
        AssertPump(await pump.RunOnceAsync(new DurableRuntimePumpRequest(maximumItems: 1,
            surfaces: DurableRuntimeSurface.Schedule), cancellationToken), 2, "Schedule");
        DurableWorkId scheduledWork;
        await using (var occurrence = new NpgsqlCommand(
            """
            SELECT occurrence.target_id FROM appsurface_durable.schedule_occurrence occurrence
            JOIN appsurface_durable.work work ON work.scope_id = occurrence.scope_id AND work.work_id = occurrence.target_id
            WHERE occurrence.scope_id = @scope AND occurrence.schedule_id = @schedule
              AND occurrence.state = 'materialized' AND occurrence.target_kind = 'work'
              AND work.work_name = @work_name AND work.work_version = @work_version;
            """, scopedOwner))
        {
            occurrence.Parameters.AddWithValue("scope", scope.Value);
            occurrence.Parameters.AddWithValue("schedule", schedule.Value);
            occurrence.Parameters.AddWithValue("work_name", WorkName);
            occurrence.Parameters.AddWithValue("work_version", WorkVersion);
            await using var reader = await occurrence.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("The due schedule did not persist a materialized Work target.");
            }
            scheduledWork = new DurableWorkId(reader.GetString(0));
            if (await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("The one-shot schedule materialized multiple Work targets.");
            }
        }
        AssertPump(await pump.RunOnceAsync(new DurableRuntimePumpRequest(maximumItems: 1,
            surfaces: DurableRuntimeSurface.Work), cancellationToken), 1, "scheduled Work");
        await AssertCompletedWorkAsync(provider, owner, scope, scheduledWork, cancellationToken);
    }

    private static void AssertPump(DurableRuntimePumpResult result, int processed, string lane)
    {
        if (result.Discovered != 1 || result.Claimed != 1 || result.Processed != processed
            || result.Failed != 0 || result.Deferred != 0)
        {
            throw new InvalidOperationException($"The {lane} lane did not commit its expected nonempty provider pass.");
        }
    }

    private static async Task AssertCompletedWorkAsync(
        IServiceProvider provider, NpgsqlDataSource owner, DurableScopeId scope, DurableWorkId workId,
        CancellationToken cancellationToken)
    {
        var snapshot = await provider.GetRequiredService<IDurableWorkControlClient>().GetAsync(
            new DurableWorkGetRequest(scope, workId), cancellationToken);
        if (!snapshot.IsSuccess || snapshot.Value!.State != DurableWorkState.Succeeded)
        {
            throw new InvalidOperationException("The lane Work has no durable succeeded public snapshot.");
        }
        await using var scopedOwner = await OpenScopedOwnerAsync(owner, scope, cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*) = 1 FROM appsurface_durable.work
            WHERE scope_id = @scope AND work_id = @work AND state = 'succeeded'
              AND terminal_at IS NOT NULL AND result_payload = @result;
            """, scopedOwner);
        command.Parameters.AddWithValue("scope", scope.Value);
        command.Parameters.AddWithValue("work", workId.Value);
        command.Parameters.AddWithValue("result", Encoding.UTF8.GetBytes("processed"));
        if (await command.ExecuteScalarAsync(cancellationToken) is not true)
        {
            throw new InvalidOperationException("The lane Work did not persist its executor result and terminal state.");
        }
    }

    // work_only restricts the dispatcher, not the paired runtime's scoped API rights. Test
    // dispatcher-backed clients explicitly; the ordinary split-role Source pump must also fail.
    private static async Task AssertSourceRestrictionsAsync(
        string ownerConnectionString,
        LaneProofRolePair forwarder,
        LaneProofRolePair sourceWorkOnly,
        Guid runtimeEpoch,
        Guid storeId,
        CancellationToken cancellationToken)
    {
        await using var owner = CreateOwnerDataSource(ownerConnectionString);
        await using var fullDispatcher = CreateRoleDataSource(ownerConnectionString, forwarder.DispatcherRole, forwarder.DispatcherPassword);
        await using var fullRuntime = CreateRoleDataSource(ownerConnectionString, forwarder.RuntimeRole, forwarder.RuntimePassword);
        await using var sourceDispatcher = CreateRoleDataSource(ownerConnectionString, sourceWorkOnly.DispatcherRole, sourceWorkOnly.DispatcherPassword);
        await using var sourceScopedClient = CreateRoleDataSource(ownerConnectionString, sourceWorkOnly.DispatcherRole, sourceWorkOnly.DispatcherPassword);
        var work = new LaneProofWorkRegistration();
        await using var full = BuildProvider(fullDispatcher, fullRuntime, forwarder.RuntimeRole, runtimeEpoch, storeId, work);
        // Use separate data sources as the public registration requires. Deliberately run the
        // scoped clients under the Source dispatcher login to prove its database privileges;
        // matching Schedule's configured login also reaches the SQL permission check.
        await using var denied = BuildProvider(sourceDispatcher, sourceScopedClient, sourceWorkOnly.DispatcherRole,
            runtimeEpoch, storeId, work);
        var scope = new DurableScopeId($"lane-proof-denied-{Guid.NewGuid():N}");
        var instance = new DurableFlowInstanceId($"{scope.Value}-flow");
        var schedule = new DurableScheduleId($"{scope.Value}-schedule");
        await SeedFlowScheduleAsync(full, work, scope, instance, schedule, cancellationToken);
        var tables = await ReadScopedTablesAsync(owner, cancellationToken);
        foreach (var table in tables)
        {
            // Identifiers come solely from the catalog and are quoted; all input values are parameters.
            var qualified = "appsurface_durable." + QuoteIdentifier(table);
            foreach (var statement in new[]
            {
                $"INSERT INTO {qualified} (scope_id) VALUES (@scope);",
                $"UPDATE {qualified} SET scope_id = @scope WHERE scope_id = @scope;",
                $"DELETE FROM {qualified} WHERE scope_id = @scope;",
            })
            {
                await AssertDeniedWithoutScopedMutationAsync(owner, fullRuntime, scope, tables, async () =>
                {
                    await using var connection = await sourceDispatcher.OpenConnectionAsync(cancellationToken);
                    await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                    await using (var setScope = new NpgsqlCommand(
                        "SELECT set_config('appsurface_durable.scope_id', @scope, true);", connection, transaction))
                    {
                        setScope.Parameters.AddWithValue("scope", scope.Value);
                        await setScope.ExecuteNonQueryAsync(cancellationToken);
                    }
                    // Rollback on disposal even if an unexpected grant admits a mutation.
                    await using var command = new NpgsqlCommand(statement, connection, transaction);
                    command.Parameters.AddWithValue("scope", scope.Value);
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }, "Source dispatcher direct scoped SQL", cancellationToken);
            }
        }
        await AssertDeniedWithoutScopedMutationAsync(owner, fullRuntime, scope, tables, async () =>
        {
            _ = await denied.GetRequiredService<IDurableFlowClient>().StartAsync(new DurableFlowStartRequest(
                scope, DurableCommandId.New(), $"{scope.Value}-denied-flow-key",
                new DurableFlowInstanceId($"{scope.Value}-denied-flow"), LaneProofFlowRegistration.Name, WorkVersion,
                full.GetRequiredService<IDurableFlowRegistry>().GetRequired(LaneProofFlowRegistration.Name, WorkVersion)
                    .ContextCodec.EncodeObject(Encoding.UTF8.GetBytes("initial"))), cancellationToken);
        }, "Source dispatcher Flow API", cancellationToken);
        await AssertDeniedWithoutScopedMutationAsync(owner, fullRuntime, scope, tables, async () =>
        {
            _ = await denied.GetRequiredService<IDurableScheduleClient>().CreateAsync(
                ScheduleRequest(work, scope, new DurableScheduleId($"{scope.Value}-denied-schedule")), cancellationToken);
        }, "Source dispatcher Schedule API", cancellationToken);
        foreach (var surface in new[] { DurableRuntimeSurface.Flow, DurableRuntimeSurface.Schedule, DurableRuntimeSurface.All })
        {
            var before = await ReadScopedSnapshotAsync(owner, fullRuntime, scope, tables, cancellationToken);
            await AssertRuntimePumpDeniedAsync(ownerConnectionString, sourceWorkOnly, runtimeEpoch, storeId, surface, cancellationToken);
            if (!string.Equals(before, await ReadScopedSnapshotAsync(owner, fullRuntime, scope, tables, cancellationToken), StringComparison.Ordinal))
            {
                throw new InvalidOperationException("A denied Source pump changed scoped durable facts.");
            }
        }
        // Discharge the real pending candidates through the full lane so later before/after
        // scenarios cannot accidentally process work left behind by a previous denial proof.
        await CompleteFlowScheduleAsync(full, owner, scope, instance, schedule, cancellationToken);
    }

    private static async Task AssertDeniedWithoutScopedMutationAsync(
        NpgsqlDataSource owner, NpgsqlDataSource runtime, DurableScopeId scope, IReadOnlyList<string> tables, Func<Task> action,
        string label, CancellationToken cancellationToken)
    {
        var before = await ReadScopedSnapshotAsync(owner, runtime, scope, tables, cancellationToken);
        var denied = false;
        try
        {
            await action();
        }
        catch (PostgresException exception) when (exception.SqlState == InsufficientPrivilegeSqlState)
        {
            denied = true;
        }
        if (!denied)
        {
            throw new InvalidOperationException($"{label} unexpectedly succeeded without an insufficient-privilege failure.");
        }
        if (!string.Equals(before, await ReadScopedSnapshotAsync(owner, runtime, scope, tables, cancellationToken), StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{label} changed scoped durable facts despite its denial.");
        }
    }

    private static async Task<IReadOnlyList<string>> ReadScopedTablesAsync(NpgsqlDataSource owner, CancellationToken cancellationToken)
    {
        await using var command = owner.CreateCommand(
            """
            SELECT relation.relname FROM pg_catalog.pg_class relation
            JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
            WHERE namespace.nspname = 'appsurface_durable' AND relation.relkind IN ('r', 'p')
              AND NOT relation.relispartition AND EXISTS (SELECT 1 FROM pg_catalog.pg_attribute attribute
                WHERE attribute.attrelid = relation.oid AND attribute.attname = 'scope_id' AND NOT attribute.attisdropped)
            ORDER BY relation.relname COLLATE "C";
            """);
        var tables = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tables.Add(reader.GetString(0));
            if (tables.Count > 128)
            {
                throw new InvalidOperationException("The scoped lane-proof table inventory exceeded its bound.");
            }
        }
        if (!new[] { "work", "flow_instance", "schedule_definition", "schedule_occurrence" }.All(tables.Contains))
        {
            throw new InvalidOperationException("The scoped lane-proof table inventory is incomplete.");
        }
        return tables;
    }

    private static async Task<string> ReadScopedSnapshotAsync(
        NpgsqlDataSource owner, NpgsqlDataSource runtime, DurableScopeId scope, IReadOnlyList<string> tables, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var scopedOwner = await OpenScopedOwnerAsync(owner, scope, cancellationToken);
        await using var scopedRuntime = await OpenScopedOwnerAsync(runtime, scope, cancellationToken);
        foreach (var table in tables)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(table + "\n"));
            // The recipe narrows dispatch SELECT to dispatcher/runtime role sets. Even the
            // forced-RLS owner cannot observe those rows; use the full scoped runtime there.
            var observer = table is "flow_dispatch" or "schedule_dispatch" ? scopedRuntime : scopedOwner;
            await using var command = new NpgsqlCommand(
                $"SELECT to_jsonb(fact)::text FROM appsurface_durable.{QuoteIdentifier(table)} fact " +
                "WHERE scope_id = @scope ORDER BY to_jsonb(fact)::text COLLATE \"C\";", observer);
            command.Parameters.AddWithValue("scope", scope.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                hash.AppendData(Encoding.UTF8.GetBytes(reader.GetString(0) + "\n"));
            }
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static NpgsqlDataSource CreateOwnerDataSource(string connectionString) => NpgsqlDataSource.Create(
        new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);

    // The recipe's migration owner is still subject to forced scoped RLS. Evidence reads
    // select the same authorized scope explicitly rather than rely on owner bypass rights.
    private static async Task<NpgsqlConnection> OpenScopedOwnerAsync(
        NpgsqlDataSource owner, DurableScopeId scope, CancellationToken cancellationToken)
    {
        var connection = await owner.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = new NpgsqlCommand(
                "SELECT set_config('appsurface_durable.scope_id', @scope, false);", connection);
            command.Parameters.AddWithValue("scope", scope.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static string QuoteIdentifier(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static async Task AssertRuntimePumpDeniedAsync(
        string ownerConnectionString,
        LaneProofRolePair sourceWorkOnly,
        Guid runtimeEpoch,
        Guid storeId,
        DurableRuntimeSurface surface,
        CancellationToken cancellationToken)
    {
        await using var dispatcherDataSource = CreateRoleDataSource(
            ownerConnectionString,
            sourceWorkOnly.DispatcherRole,
            sourceWorkOnly.DispatcherPassword);
        await using var runtimeDataSource = CreateRoleDataSource(
            ownerConnectionString,
            sourceWorkOnly.RuntimeRole,
            sourceWorkOnly.RuntimePassword);

        await using var provider = BuildProvider(dispatcherDataSource, runtimeDataSource,
            sourceWorkOnly.RuntimeRole, runtimeEpoch, storeId, new LaneProofWorkRegistration());
        try
        {
            _ = await provider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
                new DurableRuntimePumpRequest(maximumItems: 1, surfaces: surface),
                cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == InsufficientPrivilegeSqlState)
        {
            return;
        }

        throw new InvalidOperationException(
            $"The source work_only runtime pump unexpectedly accepted the {surface} surface.");
    }

    private static NpgsqlDataSource CreateRoleDataSource(
        string connectionString,
        string username,
        string password)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Username = username,
            Password = password,
            Pooling = false,
        };
        return NpgsqlDataSource.Create(builder.ConnectionString);
    }

    private static void ValidatePair(LaneProofRolePair pair, string parameterName)
    {
        ValidateRole(pair.DispatcherRole, nameof(pair.DispatcherRole), parameterName);
        ValidateRole(pair.RuntimeRole, nameof(pair.RuntimeRole), parameterName);
        if (string.Equals(pair.DispatcherRole, pair.RuntimeRole, StringComparison.Ordinal))
        {
            throw new ArgumentException("Dispatcher and runtime roles must be distinct.", parameterName);
        }

        if (string.IsNullOrEmpty(pair.DispatcherPassword) || string.IsNullOrEmpty(pair.RuntimePassword))
        {
            throw new ArgumentException("Dispatcher and runtime passwords must be nonempty.", parameterName);
        }
    }

    private static void ValidateRuntimeIdentity(Guid runtimeEpoch, Guid storeId)
    {
        if (runtimeEpoch == Guid.Empty)
        {
            throw new ArgumentException("The lane proof requires a nonempty active runtime epoch.", nameof(runtimeEpoch));
        }

        if (storeId == Guid.Empty)
        {
            throw new ArgumentException("The lane proof requires a nonempty store id.", nameof(storeId));
        }
    }

    private static void ValidateRole(string value, string rolePartName, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || Encoding.UTF8.GetByteCount(value) > 63
            || value.Any(char.IsControl))
        {
            throw new ArgumentException("PostgreSQL role names must contain 1 to 63 non-control UTF-8 bytes.", parameterName + "." + rolePartName);
        }
    }

    private sealed class LaneProofFlowRegistration(IDurablePayloadCodec contextCodec) : DurableFlowRegistration
    {
        internal const string Name = "tests.postgresql-preflight.lane-proof.flow";

        public override string FlowId => Name;
        public override string FlowVersion => WorkVersion;
        public override string ImplementationVersion => "lane-proof-v1";
        public override string StartNodeId => "start";
        public override string DefinitionFingerprint => Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes("lane-proof-v1:start:complete:flow-completed")));
        public override IDurablePayloadCodec ContextCodec { get; } = contextCodec;
        public override IReadOnlyList<DurableFlowEventBinding> EventBindings => [];
        public override IReadOnlyList<DurableWorkRegistration> ActivityWorkRegistrations => [];

        public override ValueTask<DurableFlowEvaluationResult> EvaluateAsync(
            DurableFlowEvaluationInput input,
            IDurablePayloadCodecRegistry payloadCodecs,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var context = (byte[])ContextCodec.DecodeObject(input.Context);
            if (input.NodeId != StartNodeId || !context.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes("initial")))
            {
                throw new InvalidOperationException("The lane-proof Flow did not receive its registered initial context.");
            }
            return ValueTask.FromResult(new DurableFlowEvaluationResult(
                FlowTransitionKind.Complete, input.NodeId,
                ContextCodec.EncodeObject(Encoding.UTF8.GetBytes("flow-completed")),
                nextNodeId: null, eventName: null, timeout: null, fault: null, activity: null));
        }
    }

    private sealed class LaneProofWorkRegistration : DurableWorkRegistration
    {
        private readonly LaneProofPayloadCodec _resultCodec;

        internal LaneProofWorkRegistration()
            : this(
                new LaneProofPayloadCodec("tests.postgresql-preflight.lane-proof.input"),
                new LaneProofPayloadCodec("tests.postgresql-preflight.lane-proof.result"))
        {
        }

        private LaneProofWorkRegistration(
            LaneProofPayloadCodec inputCodec,
            LaneProofPayloadCodec resultCodec)
            : base(
                LaneProof.WorkName,
                LaneProof.WorkVersion,
                DurableProviderSafety.Idempotent,
                inputCodec,
                resultCodec)
        {
            _resultCodec = resultCodec;
        }

        public override bool CanReconcile => false;

        internal IDurablePayloadCodec<byte[]> InputCodec => (IDurablePayloadCodec<byte[]>)WorkCodec;

        public override DurablePreparedWork Prepare(IServiceProvider services, DurableWorkExecutionContext work)
        {
            _ = WorkCodec.DecodeObject(work.Payload);
            return new LaneProofPreparedWork(_resultCodec.Encode(Encoding.UTF8.GetBytes("processed")));
        }

        public override ValueTask<DurableEncodedPayload> InvokeAsync(
            IServiceProvider services,
            DurableWorkExecutionContext work,
            CancellationToken cancellationToken = default) =>
            Prepare(services, work).InvokeAsync(cancellationToken);

        public override ValueTask<DurableEncodedEffectReconciliation> ReconcileAsync(
            IServiceProvider services,
            DurableWorkExecutionContext work,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Idempotent lane-proof work does not reconcile.");
    }

    private sealed class LaneProofPreparedWork(DurableEncodedPayload result) : DurablePreparedWork
    {
        public override ValueTask<DurableEncodedPayload> InvokeAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(result);
    }

    private sealed class LaneProofPayloadCodec(string contractName) : IDurablePayloadCodec<byte[]>
    {
        public Type PayloadType => typeof(byte[]);

        public string ContractName { get; } = contractName;

        public string ContractVersion => WorkVersion;

        public DurableDataClassification Classification => DurableDataClassification.ApprovedApplication;

        public string RetentionPolicyId => DurableEncodedPayload.DefaultRetentionPolicyId;

        public DurableEncodedPayload Encode(byte[] value) => new(
            ContractName,
            ContractVersion,
            Classification,
            value,
            RetentionPolicyId);

        public byte[] Decode(DurableEncodedPayload payload)
        {
            ArgumentNullException.ThrowIfNull(payload);
            if (!string.Equals(payload.ContractName, ContractName, StringComparison.Ordinal)
                || !string.Equals(payload.ContractVersion, ContractVersion, StringComparison.Ordinal)
                || payload.Classification != Classification
                || !string.Equals(payload.RetentionPolicyId, RetentionPolicyId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The lane-proof payload does not match its registered contract.");
            }

            return payload.Content.ToArray();
        }

        public DurableEncodedPayload EncodeObject(object value) => value is byte[] bytes
            ? Encode(bytes)
            : throw new ArgumentException("The lane-proof codec accepts byte arrays only.", nameof(value));

        public object DecodeObject(DurableEncodedPayload payload) => Decode(payload);
    }
}
