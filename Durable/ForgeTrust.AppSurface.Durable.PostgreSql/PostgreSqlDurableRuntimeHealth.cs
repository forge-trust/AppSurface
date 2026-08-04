using ForgeTrust.AppSurface.Durable.Provider;
using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>Implements low-cardinality PostgreSQL runtime liveness, drain, and worker-generation fencing.</summary>
internal sealed class PostgreSqlDurableRuntimeHealth : IDurableRuntimeHealth, IDurableRuntimeDrainControl
{
    private readonly PostgreSqlDurableRuntimeRegistration _registration;
    private readonly IDurableRuntimeSchemaManager _schemaManager;

    internal PostgreSqlDurableRuntimeHealth(
        PostgreSqlDurableRuntimeRegistration registration,
        IDurableRuntimeSchemaManager schemaManager)
    {
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _schemaManager = schemaManager ?? throw new ArgumentNullException(nameof(schemaManager));
    }

    public async ValueTask<DurableRuntimeHealthSnapshot> GetAsync(CancellationToken cancellationToken = default)
    {
        DurableRuntimeSchemaStatus schema;
        try
        {
            schema = await _schemaManager.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException)
        {
            return CreateIncompatibleSnapshot(
                DurableProblemCodes.SchemaInconsistent,
                installedVersion: 0,
                requiredVersion: PostgreSqlDurableRuntimeSchemaManager.RequiredVersion);
        }

        if (!schema.IsCompatible)
        {
            return CreateIncompatibleSnapshot(
                ProblemForSchema(schema.Compatibility),
                schema.InstalledVersion,
                schema.RequiredVersion);
        }

        try
        {
            var worker = await ReadWorkerAsync(cancellationToken).ConfigureAwait(false);
            var due = await ReadDueDispatchesAsync(cancellationToken).ConfigureAwait(false);
            var epochCompatible = worker.ActiveEpoch == _registration.WorkOptions.RuntimeEpoch;
            var (state, problemCode) = ResolveState(worker, epochCompatible);
            return new DurableRuntimeHealthSnapshot(
                state,
                problemCode,
                schemaCompatible: true,
                epochCompatible,
                schema.InstalledVersion,
                schema.RequiredVersion,
                _registration.WorkOptions.RuntimeEpoch,
                worker.ActiveEpoch,
                _registration.Options.WorkerId,
                worker.WorkerInstanceId,
                worker.HostedSurfaces ?? _registration.Options.HostedSurfaces,
                worker.ObservedAtUtc,
                worker.StartedAtUtc,
                worker.LastHeartbeatAtUtc,
                worker.LastSuccessfulSweepAtUtc,
                worker.IsDraining,
                worker.IsPassActive,
                due.Count,
                due.OldestDueAtUtc,
                due.OldestDueAtUtc is { } oldest ? Max(TimeSpan.Zero, worker.ObservedAtUtc - oldest) : null);
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException)
        {
            return CreateIncompatibleSnapshot(
                DurableProblemCodes.SchemaInconsistent,
                schema.InstalledVersion,
                schema.RequiredVersion);
        }
    }

    public ValueTask BeginDrainAsync(CancellationToken cancellationToken = default) =>
        SetDrainAsync(draining: true, cancellationToken);

    public ValueTask ResumeAsync(CancellationToken cancellationToken = default) =>
        SetDrainAsync(draining: false, cancellationToken);

    internal async ValueTask<bool> TryBeginPassAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _registration.RuntimeDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureCurrentEpochAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            if (await EnsureSessionAsync(connection, transaction, cancellationToken).ConfigureAwait(false))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            const string sql = """
                UPDATE appsurface_durable.runtime_heartbeat
                SET pass_active = true,
                    pass_started_at = clock_timestamp(),
                    last_heartbeat_at = clock_timestamp(),
                    updated_at = clock_timestamp()
                WHERE worker_id = @worker_id
                  AND worker_instance_id = @worker_instance_id
                  AND runtime_epoch = @runtime_epoch
                  AND NOT draining
                  AND NOT pass_active;
                """;
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            AddIdentity(command);
            var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (affected == 0)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    internal async ValueTask RecordHeartbeatAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _registration.RuntimeDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureCurrentEpochAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            const string sql = """
                UPDATE appsurface_durable.runtime_heartbeat
                SET last_heartbeat_at = clock_timestamp(), updated_at = clock_timestamp()
                WHERE worker_id = @worker_id
                  AND worker_instance_id = @worker_instance_id
                  AND runtime_epoch = @runtime_epoch
                  AND pass_active;
                """;
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            AddIdentity(command);
            EnsureOneRow(await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    internal async ValueTask RecordSuccessfulSweepAsync(
        DurableRuntimePumpResult result,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        await CompletePassAsync(result, completed: true, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask RecordFailedPassAsync(CancellationToken cancellationToken)
    {
        await CompletePassAsync(result: null, completed: false, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask CompletePassAsync(
        DurableRuntimePumpResult? result,
        bool completed,
        CancellationToken cancellationToken)
    {
        await using var connection = await _registration.RuntimeDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureCurrentEpochAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            const string sql = """
                UPDATE appsurface_durable.runtime_heartbeat
                SET last_heartbeat_at = clock_timestamp(),
                    last_successful_sweep_at = CASE WHEN @completed THEN clock_timestamp() ELSE last_successful_sweep_at END,
                    pass_active = false,
                    pass_started_at = NULL,
                    last_discovered = @discovered,
                    last_claimed = @claimed,
                    last_processed = @processed,
                    last_deferred = @deferred,
                    last_failed = @failed,
                    last_pass_elapsed_ms = @elapsed_ms,
                    updated_at = clock_timestamp()
                WHERE worker_id = @worker_id
                  AND worker_instance_id = @worker_instance_id
                  AND runtime_epoch = @runtime_epoch
                  AND pass_active;
                """;
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            AddIdentity(command);
            command.Parameters.AddWithValue("completed", completed);
            command.Parameters.AddWithValue("discovered", result?.Discovered ?? 0);
            command.Parameters.AddWithValue("claimed", result?.Claimed ?? 0);
            command.Parameters.AddWithValue("processed", result?.Processed ?? 0);
            command.Parameters.AddWithValue("deferred", result?.Deferred ?? 0);
            command.Parameters.AddWithValue("failed", result?.Failed ?? 1);
            command.Parameters.AddWithValue("elapsed_ms", result?.Elapsed.TotalMilliseconds ?? 0d);
            EnsureOneRow(await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask SetDrainAsync(bool draining, CancellationToken cancellationToken)
    {
        await _schemaManager.ValidateAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _registration.RuntimeDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureCurrentEpochAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            _ = await EnsureSessionAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            const string sql = """
                UPDATE appsurface_durable.runtime_heartbeat
                SET draining = @draining,
                    last_heartbeat_at = clock_timestamp(),
                    updated_at = clock_timestamp()
                WHERE worker_id = @worker_id
                  AND worker_instance_id = @worker_instance_id
                  AND runtime_epoch = @runtime_epoch;
                """;
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            AddIdentity(command);
            command.Parameters.AddWithValue("draining", draining);
            EnsureOneRow(await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Creates or verifies this process generation while holding the worker row lock.</summary>
    /// <returns>Whether the verified generation is currently draining.</returns>
    private async ValueTask<bool> EnsureSessionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string insertSql = """
            INSERT INTO appsurface_durable.runtime_heartbeat
                (worker_id, worker_instance_id, runtime_epoch, hosted_surfaces)
            VALUES (@worker_id, @worker_instance_id, @runtime_epoch, @hosted_surfaces)
            ON CONFLICT (worker_id) DO NOTHING;
            """;
        await using (var insert = new NpgsqlCommand(insertSql, connection, transaction))
        {
            AddIdentity(insert);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        const string selectSql = """
            SELECT worker_instance_id, runtime_epoch, last_heartbeat_at, draining, pass_active, clock_timestamp()
            FROM appsurface_durable.runtime_heartbeat
            WHERE worker_id = @worker_id
            FOR UPDATE;
            """;
        Guid existingInstance;
        Guid existingEpoch;
        DateTimeOffset lastHeartbeat;
        bool draining;
        bool passActive;
        DateTimeOffset observedAt;
        await using (var select = new NpgsqlCommand(selectSql, connection, transaction))
        {
            select.Parameters.AddWithValue("worker_id", _registration.Options.WorkerId);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException("The durable runtime heartbeat could not be registered.");
            }

            existingInstance = reader.GetGuid(0);
            existingEpoch = reader.GetGuid(1);
            lastHeartbeat = ReadUtc(reader, 2);
            draining = reader.GetBoolean(3);
            passActive = reader.GetBoolean(4);
            observedAt = ReadUtc(reader, 5);
        }

        if (existingInstance != _registration.InstanceId || existingEpoch != _registration.WorkOptions.RuntimeEpoch)
        {
            var canTakeOver = existingEpoch != _registration.WorkOptions.RuntimeEpoch
                || (draining && !passActive)
                || observedAt - lastHeartbeat > _registration.Options.HeartbeatStaleAfter;
            if (!canTakeOver)
            {
                throw LostWorkerIdentity();
            }

            const string takeoverSql = """
                UPDATE appsurface_durable.runtime_heartbeat
                SET worker_instance_id = @worker_instance_id,
                    runtime_epoch = @runtime_epoch,
                    hosted_surfaces = @hosted_surfaces,
                    started_at = clock_timestamp(),
                    last_heartbeat_at = clock_timestamp(),
                    last_successful_sweep_at = NULL,
                    draining = false,
                    pass_active = false,
                    pass_started_at = NULL,
                    last_discovered = NULL,
                    last_claimed = NULL,
                    last_processed = NULL,
                    last_deferred = NULL,
                    last_failed = NULL,
                    last_pass_elapsed_ms = NULL,
                    updated_at = clock_timestamp()
                WHERE worker_id = @worker_id
                  AND worker_instance_id = @previous_instance_id
                  AND runtime_epoch = @previous_runtime_epoch;
                """;
            await using var takeover = new NpgsqlCommand(takeoverSql, connection, transaction);
            AddIdentity(takeover);
            takeover.Parameters.AddWithValue("previous_instance_id", existingInstance);
            takeover.Parameters.AddWithValue("previous_runtime_epoch", existingEpoch);
            EnsureOneRow(await takeover.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
            return false;
        }

        const string heartbeatSql = """
            UPDATE appsurface_durable.runtime_heartbeat
            SET hosted_surfaces = @hosted_surfaces,
                last_heartbeat_at = clock_timestamp(),
                updated_at = clock_timestamp()
            WHERE worker_id = @worker_id
              AND worker_instance_id = @worker_instance_id
              AND runtime_epoch = @runtime_epoch;
            """;
        await using var heartbeat = new NpgsqlCommand(heartbeatSql, connection, transaction);
        AddIdentity(heartbeat);
        EnsureOneRow(await heartbeat.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
        return draining;
    }

    private async ValueTask EnsureCurrentEpochAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using (var fence = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock_shared(@lock_id);",
            connection,
            transaction))
        {
            fence.Parameters.AddWithValue("lock_id", PostgreSqlDurableRuntimeSchemaManager.MigrationAdvisoryLock);
            await fence.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var epoch = new NpgsqlCommand(
            "SELECT active_runtime_epoch FROM appsurface_durable.store_metadata WHERE singleton;",
            connection,
            transaction);
        if (await epoch.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not Guid active
            || active != _registration.WorkOptions.RuntimeEpoch)
        {
            throw new InvalidOperationException(
                $"{DurableProblemCodes.RecoveryEpochRequired}: The configured runtime epoch is not active in PostgreSQL.");
        }
    }

    private async ValueTask<WorkerObservation> ReadWorkerAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _registration.RuntimeDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        const string sql = """
            SELECT clock_timestamp(), metadata.active_runtime_epoch,
                   heartbeat.worker_instance_id, heartbeat.runtime_epoch, heartbeat.hosted_surfaces,
                   heartbeat.started_at, heartbeat.last_heartbeat_at, heartbeat.last_successful_sweep_at,
                   heartbeat.draining, heartbeat.pass_active
            FROM appsurface_durable.store_metadata AS metadata
            LEFT JOIN appsurface_durable.runtime_heartbeat AS heartbeat
              ON heartbeat.worker_id = @worker_id
            WHERE metadata.singleton;
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("worker_id", _registration.Options.WorkerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("The durable store metadata singleton is missing.");
        }

        return new WorkerObservation(
            ReadUtc(reader, 0),
            reader.IsDBNull(1) ? null : reader.GetGuid(1),
            reader.IsDBNull(2) ? null : reader.GetGuid(2),
            reader.IsDBNull(3) ? null : reader.GetGuid(3),
            reader.IsDBNull(4) ? null : (DurableRuntimeSurface)reader.GetInt16(4),
            ReadNullableUtc(reader, 5),
            ReadNullableUtc(reader, 6),
            ReadNullableUtc(reader, 7),
            !reader.IsDBNull(8) && reader.GetBoolean(8),
            !reader.IsDBNull(9) && reader.GetBoolean(9));
    }

    private async ValueTask<DueObservation> ReadDueDispatchesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _registration.RuntimeDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        const string sql = """
            SELECT due_count, oldest_due_at
            FROM appsurface_durable.runtime_due_dispatch_health(@surfaces);
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("surfaces", (short)_registration.Options.HostedSurfaces);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new DueObservation(reader.GetInt64(0), ReadNullableUtc(reader, 1));
    }

    private (DurableRuntimeHealthState State, string? ProblemCode) ResolveState(
        WorkerObservation worker,
        bool epochCompatible)
    {
        if (!epochCompatible)
        {
            return (DurableRuntimeHealthState.Incompatible, DurableProblemCodes.RecoveryEpochRequired);
        }

        if (worker.WorkerInstanceId is null || worker.LastHeartbeatAtUtc is null || worker.ActiveEpoch is null)
        {
            return (DurableRuntimeHealthState.NotStarted, DurableProblemCodes.ActivatorStale);
        }

        if (worker.WorkerInstanceId != _registration.InstanceId || worker.HeartbeatEpoch != _registration.WorkOptions.RuntimeEpoch)
        {
            return (DurableRuntimeHealthState.Stale, DurableProblemCodes.WorkerIdentityConflict);
        }

        if (worker.IsDraining)
        {
            return (DurableRuntimeHealthState.Draining, null);
        }

        if (worker.ObservedAtUtc - worker.LastHeartbeatAtUtc > _registration.Options.HeartbeatStaleAfter)
        {
            return (DurableRuntimeHealthState.Stale, DurableProblemCodes.ActivatorStale);
        }

        return (DurableRuntimeHealthState.Healthy, null);
    }

    private DurableRuntimeHealthSnapshot CreateIncompatibleSnapshot(string problemCode, int installedVersion, int requiredVersion) =>
        new(
            DurableRuntimeHealthState.Incompatible,
            problemCode,
            schemaCompatible: false,
            epochCompatible: false,
            installedVersion,
            requiredVersion,
            _registration.WorkOptions.RuntimeEpoch,
            activeRuntimeEpoch: null,
            _registration.Options.WorkerId,
            workerInstanceId: null,
            _registration.Options.HostedSurfaces,
            DateTimeOffset.UtcNow,
            startedAtUtc: null,
            lastHeartbeatAtUtc: null,
            lastSuccessfulSweepAtUtc: null,
            isDraining: false,
            isPassActive: false,
            dueDispatchCount: 0,
            oldestDueAtUtc: null,
            oldestDueAge: null);

    private void AddIdentity(NpgsqlCommand command)
    {
        command.Parameters.AddWithValue("worker_id", _registration.Options.WorkerId);
        command.Parameters.AddWithValue("worker_instance_id", _registration.InstanceId);
        command.Parameters.AddWithValue("runtime_epoch", _registration.WorkOptions.RuntimeEpoch);
        command.Parameters.AddWithValue("hosted_surfaces", (short)_registration.Options.HostedSurfaces);
    }

    private static string ProblemForSchema(DurableRuntimeSchemaCompatibility compatibility) => compatibility switch
    {
        DurableRuntimeSchemaCompatibility.Missing => DurableProblemCodes.SchemaMissing,
        DurableRuntimeSchemaCompatibility.UpgradeRequired => DurableProblemCodes.SchemaUpgradeRequired,
        DurableRuntimeSchemaCompatibility.StoreTooNew => DurableProblemCodes.SchemaVersionUnsupported,
        _ => DurableProblemCodes.SchemaInconsistent,
    };

    private static InvalidOperationException LostWorkerIdentity() => new(
        $"{DurableProblemCodes.WorkerIdentityConflict}: This worker generation no longer owns the configured worker identity.");

    private static void EnsureOneRow(int affected)
    {
        if (affected != 1)
        {
            throw LostWorkerIdentity();
        }
    }

    private static async ValueTask TryRollbackAsync(NpgsqlTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (PostgreSqlDurableExceptionFilters.IsExpectedCleanupFailure(exception))
        {
            // Preserve the original processing exception; disposal owns cleanup after transport loss.
        }
    }

    private static DateTimeOffset ReadUtc(NpgsqlDataReader reader, int ordinal) =>
        reader.GetFieldValue<DateTimeOffset>(ordinal).ToUniversalTime();

    private static DateTimeOffset? ReadNullableUtc(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ReadUtc(reader, ordinal);

    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left > right ? left : right;

    private sealed record WorkerObservation(
        DateTimeOffset ObservedAtUtc,
        Guid? ActiveEpoch,
        Guid? WorkerInstanceId,
        Guid? HeartbeatEpoch,
        DurableRuntimeSurface? HostedSurfaces,
        DateTimeOffset? StartedAtUtc,
        DateTimeOffset? LastHeartbeatAtUtc,
        DateTimeOffset? LastSuccessfulSweepAtUtc,
        bool IsDraining,
        bool IsPassActive);

    private sealed record DueObservation(long Count, DateTimeOffset? OldestDueAtUtc);
}
