using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

internal sealed class PostgreSqlDurableRuntimeHealth : IDurableRuntimeHealth, IDurableRuntimeDrainControl
{
    private readonly PostgreSqlDurableRuntimeRegistration _registration;
    private readonly IDurableRuntimeSchemaManager _schemaManager;

    public PostgreSqlDurableRuntimeHealth(
        PostgreSqlDurableRuntimeRegistration registration,
        IDurableRuntimeSchemaManager schemaManager)
    {
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _schemaManager = schemaManager ?? throw new ArgumentNullException(nameof(schemaManager));
    }

    public async ValueTask<DurableRuntimeHealthSnapshot> GetAsync(
        CancellationToken cancellationToken = default)
    {
        var schema = await _schemaManager.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (!schema.IsCompatible)
        {
            return CreateIncompatibleSchemaSnapshot(schema);
        }

        await using var connection = await _registration.DataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        const string sql = """
            SELECT clock_timestamp(),
                   metadata.active_runtime_epoch,
                   heartbeat.instance_id,
                   heartbeat.runtime_epoch,
                   heartbeat.hosted_surfaces,
                   heartbeat.started_at,
                   heartbeat.last_heartbeat_at,
                   heartbeat.last_successful_sweep_at,
                   heartbeat.draining,
                   heartbeat.pass_active,
                   due.due_count,
                   due.oldest_due_at
            FROM appsurface_durable.store_metadata AS metadata
            LEFT JOIN appsurface_durable.runtime_heartbeat AS heartbeat
              ON heartbeat.worker_id = @worker_id
            CROSS JOIN LATERAL
            (
                SELECT count(*) AS due_count, min(due_at) AS oldest_due_at
                FROM appsurface_durable.dispatch
                WHERE state IN ('available', 'leased')
                  AND due_at <= clock_timestamp()
                  AND
                  (
                      (@include_work AND aggregate_kind = 'work')
                      OR (@include_flow AND aggregate_kind IN ('flow', 'timer'))
                      OR (@include_schedule AND aggregate_kind = 'schedule')
                  )
            ) AS due
            WHERE metadata.singleton;
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("worker_id", _registration.Options.WorkerId);
        command.Parameters.AddWithValue(
            "include_work",
            (_registration.Options.HostedSurfaces & DurableRuntimeSurface.Work) != 0);
        command.Parameters.AddWithValue(
            "include_flow",
            (_registration.Options.HostedSurfaces & DurableRuntimeSurface.Flow) != 0);
        command.Parameters.AddWithValue(
            "include_schedule",
            (_registration.Options.HostedSurfaces & DurableRuntimeSurface.Schedule) != 0);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("The durable store metadata singleton is missing.");
        }

        var observedAt = ReadUtc(reader, 0);
        Guid? activeEpoch = reader.IsDBNull(1) ? null : reader.GetGuid(1);
        Guid? instanceId = reader.IsDBNull(2) ? null : reader.GetGuid(2);
        Guid? heartbeatEpoch = reader.IsDBNull(3) ? null : reader.GetGuid(3);
        var surfaces = reader.IsDBNull(4)
            ? _registration.Options.HostedSurfaces
            : (DurableRuntimeSurface)reader.GetInt16(4);
        var startedAt = ReadNullableUtc(reader, 5);
        var lastHeartbeat = ReadNullableUtc(reader, 6);
        var lastSweep = ReadNullableUtc(reader, 7);
        var draining = !reader.IsDBNull(8) && reader.GetBoolean(8);
        var passActive = !reader.IsDBNull(9) && reader.GetBoolean(9);
        var dueCount = reader.GetInt64(10);
        var oldestDue = ReadNullableUtc(reader, 11);
        var oldestDueAge = oldestDue is { } dueAt
            ? observedAt - dueAt
            : (TimeSpan?)null;
        if (oldestDueAge < TimeSpan.Zero)
        {
            oldestDueAge = TimeSpan.Zero;
        }

        var epochCompatible = activeEpoch == _registration.RuntimeEpoch;
        var (state, problemCode) = ResolveHealthState(
            activeEpoch,
            instanceId,
            heartbeatEpoch,
            lastHeartbeat,
            lastSweep,
            draining,
            passActive,
            observedAt);
        return new DurableRuntimeHealthSnapshot(
            state,
            problemCode,
            schemaCompatible: true,
            epochCompatible,
            schema.InstalledVersion,
            schema.RequiredVersion,
            _registration.RuntimeEpoch,
            activeEpoch,
            _registration.Options.WorkerId,
            instanceId,
            surfaces,
            observedAt,
            startedAt,
            lastHeartbeat,
            lastSweep,
            draining,
            passActive,
            dueCount,
            oldestDue,
            oldestDueAge);
    }

    public async ValueTask BeginDrainAsync(CancellationToken cancellationToken = default) =>
        await SetDrainAsync(draining: true, cancellationToken).ConfigureAwait(false);

    public async ValueTask ResumeAsync(CancellationToken cancellationToken = default) =>
        await SetDrainAsync(draining: false, cancellationToken).ConfigureAwait(false);

    internal async ValueTask<bool> TryBeginPassAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _registration.DataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PostgreSqlDurableEpochFence.EnsureCurrentAsync(
                connection,
                transaction,
                _registration.RuntimeEpoch,
                cancellationToken).ConfigureAwait(false);
            var draining = await EnsureSessionAsync(
                connection,
                transaction,
                cancellationToken,
                recoverOwnOrphanedPass: true).ConfigureAwait(false);
            if (!draining)
            {
                const string beginSql = """
                    UPDATE appsurface_durable.runtime_heartbeat
                    SET pass_active = true,
                        pass_started_at = clock_timestamp(),
                        last_heartbeat_at = clock_timestamp(),
                        updated_at = clock_timestamp()
                    WHERE worker_id = @worker_id
                      AND instance_id = @instance_id
                      AND runtime_epoch = @runtime_epoch
                      AND NOT pass_active
                      AND NOT draining;
                    """;
                await using var begin = new NpgsqlCommand(beginSql, connection, transaction);
                AddIdentity(begin);
                if (await begin.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new InvalidOperationException(
                        $"{DurableProblemCodes.WorkerIdentityConflict}: This worker instance already has an active pump pass or began draining.");
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return !draining;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    internal async ValueTask RecordSuccessfulSweepAsync(
        DurableRuntimePumpResult result,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        const string sql = """
            UPDATE appsurface_durable.runtime_heartbeat
            SET last_heartbeat_at = clock_timestamp(),
                last_successful_sweep_at = clock_timestamp(),
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
              AND instance_id = @instance_id
              AND runtime_epoch = @runtime_epoch
              AND pass_active;
            """;
        await using var connection = await _registration.DataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PostgreSqlDurableEpochFence.EnsureCurrentAsync(
                connection,
                transaction,
                _registration.RuntimeEpoch,
                cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("worker_id", _registration.Options.WorkerId);
            command.Parameters.AddWithValue("instance_id", _registration.InstanceId);
            command.Parameters.AddWithValue("runtime_epoch", _registration.RuntimeEpoch);
            command.Parameters.AddWithValue("discovered", result.Discovered);
            command.Parameters.AddWithValue("claimed", result.Claimed);
            command.Parameters.AddWithValue("processed", result.Processed);
            command.Parameters.AddWithValue("deferred", result.Deferred);
            command.Parameters.AddWithValue("failed", result.Failed);
            command.Parameters.AddWithValue("elapsed_ms", result.Elapsed.TotalMilliseconds);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException(
                    $"{DurableProblemCodes.WorkerIdentityConflict}: The configured durable worker identity is no longer owned by this process instance.");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    internal async ValueTask RecordFailedPassAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _registration.DataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PostgreSqlDurableEpochFence.EnsureCurrentAsync(
                connection,
                transaction,
                _registration.RuntimeEpoch,
                cancellationToken).ConfigureAwait(false);
            const string sql = """
                UPDATE appsurface_durable.runtime_heartbeat
                SET pass_active = false,
                    pass_started_at = NULL,
                    last_heartbeat_at = clock_timestamp(),
                    updated_at = clock_timestamp()
                WHERE worker_id = @worker_id
                  AND instance_id = @instance_id
                  AND runtime_epoch = @runtime_epoch
                  AND pass_active;
                """;
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("worker_id", _registration.Options.WorkerId);
            command.Parameters.AddWithValue("instance_id", _registration.InstanceId);
            command.Parameters.AddWithValue("runtime_epoch", _registration.RuntimeEpoch);
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    internal async ValueTask RecordHeartbeatAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _registration.DataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PostgreSqlDurableEpochFence.EnsureCurrentAsync(
                connection,
                transaction,
                _registration.RuntimeEpoch,
                cancellationToken).ConfigureAwait(false);
            const string sql = """
                UPDATE appsurface_durable.runtime_heartbeat
                SET last_heartbeat_at = clock_timestamp(),
                    updated_at = clock_timestamp()
                WHERE worker_id = @worker_id
                  AND instance_id = @instance_id
                  AND runtime_epoch = @runtime_epoch
                  AND pass_active;
                """;
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("worker_id", _registration.Options.WorkerId);
            command.Parameters.AddWithValue("instance_id", _registration.InstanceId);
            command.Parameters.AddWithValue("runtime_epoch", _registration.RuntimeEpoch);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException(
                    $"{DurableProblemCodes.WorkerIdentityConflict}: The configured durable worker identity is no longer owned by this process instance.");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask SetDrainAsync(bool draining, CancellationToken cancellationToken)
    {
        await _schemaManager.ValidateAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _registration.DataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PostgreSqlDurableEpochFence.EnsureCurrentAsync(
                connection,
                transaction,
                _registration.RuntimeEpoch,
                cancellationToken).ConfigureAwait(false);
            _ = await EnsureSessionAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            const string sql = """
                UPDATE appsurface_durable.runtime_heartbeat
                SET draining = @draining,
                    last_heartbeat_at = clock_timestamp(),
                    updated_at = clock_timestamp()
                WHERE worker_id = @worker_id
                  AND instance_id = @instance_id
                  AND runtime_epoch = @runtime_epoch;
                """;
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("draining", draining);
            command.Parameters.AddWithValue("worker_id", _registration.Options.WorkerId);
            command.Parameters.AddWithValue("instance_id", _registration.InstanceId);
            command.Parameters.AddWithValue("runtime_epoch", _registration.RuntimeEpoch);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException(
                    $"{DurableProblemCodes.WorkerIdentityConflict}: The configured durable worker identity is no longer owned by this process instance.");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask<bool> EnsureSessionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken,
        bool recoverOwnOrphanedPass = false)
    {
        const string insertSql = """
            INSERT INTO appsurface_durable.runtime_heartbeat
                (worker_id, instance_id, runtime_epoch, hosted_surfaces)
            VALUES
                (@worker_id, @instance_id, @runtime_epoch, @hosted_surfaces)
            ON CONFLICT (worker_id) DO NOTHING;
            """;
        await using (var insert = new NpgsqlCommand(insertSql, connection, transaction))
        {
            AddIdentity(insert);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        const string lockSql = """
            SELECT instance_id, runtime_epoch, last_heartbeat_at, draining, pass_active, clock_timestamp()
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
        await using (var select = new NpgsqlCommand(lockSql, connection, transaction))
        {
            select.Parameters.AddWithValue("worker_id", _registration.Options.WorkerId);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException("The durable worker heartbeat could not be registered.");
            }

            existingInstance = reader.GetGuid(0);
            existingEpoch = reader.GetGuid(1);
            lastHeartbeat = ReadUtc(reader, 2);
            draining = reader.GetBoolean(3);
            passActive = reader.GetBoolean(4);
            observedAt = ReadUtc(reader, 5);
        }

        if (existingInstance != _registration.InstanceId || existingEpoch != _registration.RuntimeEpoch)
        {
            var stale = observedAt - lastHeartbeat > _registration.Options.HeartbeatStaleAfter;
            var oldEpochIsFenced = existingEpoch != _registration.RuntimeEpoch;
            var cleanDrainCompleted = draining && !passActive;
            if (!oldEpochIsFenced && !cleanDrainCompleted && !stale)
            {
                throw new InvalidOperationException(
                    $"{DurableProblemCodes.WorkerIdentityConflict}: Another live process owns worker id '{_registration.Options.WorkerId}'. Configure a unique worker id per live replica or wait for its stale-heartbeat bound.");
            }

            const string takeoverSql = """
                UPDATE appsurface_durable.runtime_heartbeat
                SET instance_id = @instance_id,
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
                WHERE worker_id = @worker_id;
                """;
            await using var takeover = new NpgsqlCommand(takeoverSql, connection, transaction);
            AddIdentity(takeover);
            await takeover.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (passActive && recoverOwnOrphanedPass)
        {
            const string recoverSql = """
                UPDATE appsurface_durable.runtime_heartbeat
                SET pass_active = false,
                    pass_started_at = NULL,
                    updated_at = clock_timestamp()
                WHERE worker_id = @worker_id
                  AND instance_id = @instance_id
                  AND runtime_epoch = @runtime_epoch
                  AND pass_active;
                """;
            await using var recover = new NpgsqlCommand(recoverSql, connection, transaction);
            AddIdentity(recover);
            if (await recover.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException(
                    $"{DurableProblemCodes.WorkerIdentityConflict}: The orphaned local pump pass changed during recovery.");
            }
        }

        const string heartbeatSql = """
            UPDATE appsurface_durable.runtime_heartbeat
            SET hosted_surfaces = @hosted_surfaces,
                last_heartbeat_at = clock_timestamp(),
                updated_at = clock_timestamp()
            WHERE worker_id = @worker_id
              AND instance_id = @instance_id
              AND runtime_epoch = @runtime_epoch;
            """;
        await using var heartbeat = new NpgsqlCommand(heartbeatSql, connection, transaction);
        AddIdentity(heartbeat);
        await heartbeat.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return draining;
    }

    private void AddIdentity(NpgsqlCommand command)
    {
        command.Parameters.AddWithValue("worker_id", _registration.Options.WorkerId);
        command.Parameters.AddWithValue("instance_id", _registration.InstanceId);
        command.Parameters.AddWithValue("runtime_epoch", _registration.RuntimeEpoch);
        command.Parameters.AddWithValue("hosted_surfaces", (short)_registration.Options.HostedSurfaces);
    }

    private (DurableRuntimeHealthState State, string? ProblemCode) ResolveHealthState(
        Guid? activeEpoch,
        Guid? instanceId,
        Guid? heartbeatEpoch,
        DateTimeOffset? lastHeartbeat,
        DateTimeOffset? lastSuccessfulSweep,
        bool draining,
        bool passActive,
        DateTimeOffset observedAt)
    {
        if (activeEpoch is { } active && active != _registration.RuntimeEpoch)
        {
            return (DurableRuntimeHealthState.Incompatible, DurableProblemCodes.RecoveryEpochRequired);
        }

        if (instanceId is null || lastHeartbeat is null || activeEpoch is null)
        {
            return (DurableRuntimeHealthState.NotStarted, DurableProblemCodes.ActivatorStale);
        }

        if (instanceId != _registration.InstanceId || heartbeatEpoch != _registration.RuntimeEpoch)
        {
            return (DurableRuntimeHealthState.Stale, DurableProblemCodes.WorkerIdentityConflict);
        }

        if (draining)
        {
            return (DurableRuntimeHealthState.Draining, null);
        }

        if (!passActive
            && (lastSuccessfulSweep is null
                || observedAt - lastSuccessfulSweep > _registration.Options.HeartbeatStaleAfter))
        {
            return (DurableRuntimeHealthState.Stale, DurableProblemCodes.ActivatorStale);
        }

        return observedAt - lastHeartbeat > _registration.Options.HeartbeatStaleAfter
            ? (DurableRuntimeHealthState.Stale, DurableProblemCodes.ActivatorStale)
            : (DurableRuntimeHealthState.Healthy, null);
    }

    private DurableRuntimeHealthSnapshot CreateIncompatibleSchemaSnapshot(DurableRuntimeSchemaStatus schema)
    {
        var problemCode = schema.Compatibility switch
        {
            DurableRuntimeSchemaCompatibility.Missing => DurableProblemCodes.SchemaMissing,
            DurableRuntimeSchemaCompatibility.UpgradeRequired => DurableProblemCodes.SchemaUpgradeRequired,
            DurableRuntimeSchemaCompatibility.StoreTooNew => DurableProblemCodes.SchemaVersionUnsupported,
            _ => DurableProblemCodes.SchemaInconsistent,
        };
        return new DurableRuntimeHealthSnapshot(
            DurableRuntimeHealthState.Incompatible,
            problemCode,
            schemaCompatible: false,
            epochCompatible: false,
            schema.InstalledVersion,
            schema.RequiredVersion,
            _registration.RuntimeEpoch,
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
    }

    private static DateTimeOffset ReadUtc(NpgsqlDataReader reader, int ordinal) =>
        new(reader.GetFieldValue<DateTime>(ordinal), TimeSpan.Zero);

    private static DateTimeOffset? ReadNullableUtc(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ReadUtc(reader, ordinal);
}
