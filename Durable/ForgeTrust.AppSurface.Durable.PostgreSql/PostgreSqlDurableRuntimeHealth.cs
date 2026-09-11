using System.Diagnostics;
using System.Runtime.ExceptionServices;
using ForgeTrust.AppSurface.Durable.Provider;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>Implements low-cardinality PostgreSQL runtime liveness, drain, and worker-generation fencing.</summary>
internal sealed partial class PostgreSqlDurableRuntimeHealth : IDurableRuntimeHealth, IDurableRuntimeDrainControl
{
    private readonly PostgreSqlDurableRuntimeRegistration _registration;
    private readonly IDurableRuntimeSchemaManager _schemaManager;
    private readonly PostgreSqlDurableRuntimeSchemaManager _admissionSchemaManager;
    private readonly ILogger<PostgreSqlDurableRuntimeHealth> _logger;
    private readonly Func<CancellationToken, ValueTask<DateTimeOffset>> _readDatabaseTimestamp;
    private readonly bool _usesDefaultDatabaseTimestampReader;
    private readonly Action<TimeSpan>? _observeRuntimeConnectionAcquisition;

    internal PostgreSqlDurableRuntimeHealth(
        PostgreSqlDurableRuntimeRegistration registration,
        IDurableRuntimeSchemaManager schemaManager)
        : this(registration, schemaManager, NullLogger<PostgreSqlDurableRuntimeHealth>.Instance)
    {
    }

    /// <summary>Initializes runtime health with the package's structured control-plane diagnostics.</summary>
    internal PostgreSqlDurableRuntimeHealth(
        PostgreSqlDurableRuntimeRegistration registration,
        IDurableRuntimeSchemaManager schemaManager,
        ILogger<PostgreSqlDurableRuntimeHealth> logger)
        : this(registration, schemaManager, logger, readDatabaseTimestamp: null)
    {
    }

    /// <summary>Initializes runtime health with explicit database-time and connection-observation seams for tests.</summary>
    /// <param name="registration">Validated PostgreSQL runtime configuration.</param>
    /// <param name="schemaManager">Schema-status reader used before the one-statement runtime observation.</param>
    /// <param name="logger">Low-cardinality operational diagnostics sink.</param>
    /// <param name="readDatabaseTimestamp">
    /// Optional database-time reader used only for incompatible-schema observations.
    /// </param>
    /// <param name="observeRuntimeConnectionAcquisition">
    /// Optional observer invoked with the successful runtime-observation connection acquisition inside
    /// <see cref="GetAsync(CancellationToken)"/>. Production composition leaves this null; scale evidence uses it to
    /// measure the actual pool acquisition rather than a neighboring probe.
    /// </param>
    internal PostgreSqlDurableRuntimeHealth(
        PostgreSqlDurableRuntimeRegistration registration,
        IDurableRuntimeSchemaManager schemaManager,
        ILogger<PostgreSqlDurableRuntimeHealth> logger,
        Func<CancellationToken, ValueTask<DateTimeOffset>>? readDatabaseTimestamp,
        Action<TimeSpan>? observeRuntimeConnectionAcquisition = null)
    {
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _schemaManager = schemaManager ?? throw new ArgumentNullException(nameof(schemaManager));
        _admissionSchemaManager = new PostgreSqlDurableRuntimeSchemaManager(_registration.RuntimeDataSource);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _usesDefaultDatabaseTimestampReader = readDatabaseTimestamp is null;
        _readDatabaseTimestamp = readDatabaseTimestamp ?? ReadDatabaseTimestampAsync;
        _observeRuntimeConnectionAcquisition = observeRuntimeConnectionAcquisition;
    }

    public ValueTask<DurableRuntimeHealthSnapshot> GetAsync(CancellationToken cancellationToken = default)
    {
        if (_schemaManager is PostgreSqlDurableRuntimeSchemaManager schemaManager
            && schemaManager.CanShareStatusConnectionWith(_registration.RuntimeDataSource))
        {
            return GetWithSharedConnectionAsync(schemaManager, cancellationToken);
        }

        return GetCoreAsync(
            sharedConnection: null,
            sharedSchemaManager: null,
            sharedConnectionAcquisition: null,
            cancellationToken);
    }

    /// <summary>
    /// Reuses the default schema manager's connection for the compatible runtime observation, halving pool
    /// acquisitions on the full health path while preserving custom-manager composition.
    /// </summary>
    private async ValueTask<DurableRuntimeHealthSnapshot> GetWithSharedConnectionAsync(
        PostgreSqlDurableRuntimeSchemaManager schemaManager,
        CancellationToken cancellationToken)
    {
        var connectionStarted = _observeRuntimeConnectionAcquisition is null
            ? 0
            : Stopwatch.GetTimestamp();
        NpgsqlConnection connection;
        TimeSpan? connectionAcquisition;
        try
        {
            connection = await schemaManager.OpenStatusConnectionAsync(cancellationToken).ConfigureAwait(false);
            connectionAcquisition = _observeRuntimeConnectionAcquisition is null
                ? null
                : Stopwatch.GetElapsedTime(connectionStarted);
        }
        catch (Exception exception) when (TryClassifyUnavailable(
            PostgreSqlDurableControlPlaneOperation.HealthObservation,
            exception,
            cancellationToken,
            out var unavailableCause))
        {
            var unavailableObservedAtUtc = DateTimeOffset.UtcNow;
            LogUnavailable(
                PostgreSqlDurableControlPlaneOperation.HealthObservation,
                "SchemaStatus",
                unavailableCause,
                DurableProblemCodes.StoreUnavailable,
                PostgreSqlDurableDiagnostics.OperationalAssessmentTroubleshooting);
            return CreateUnavailableSnapshot(
                installedVersion: 0,
                requiredVersion: PostgreSqlDurableRuntimeSchemaManager.RequiredVersion,
                unavailableObservedAtUtc);
        }

        await using (connection.ConfigureAwait(false))
        {
            return await GetCoreAsync(
                connection,
                schemaManager,
                connectionAcquisition,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<DurableRuntimeHealthSnapshot> GetCoreAsync(
        NpgsqlConnection? sharedConnection,
        PostgreSqlDurableRuntimeSchemaManager? sharedSchemaManager,
        TimeSpan? sharedConnectionAcquisition,
        CancellationToken cancellationToken)
    {
        DurableRuntimeSchemaStatus schema;
        try
        {
            schema = sharedSchemaManager is null
                ? await _schemaManager.GetStatusAsync(cancellationToken).ConfigureAwait(false)
                : await sharedSchemaManager.GetStatusAsync(sharedConnection!, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (TryClassifyUnavailable(
            PostgreSqlDurableControlPlaneOperation.HealthObservation,
            exception,
            cancellationToken,
            out var unavailableCause))
        {
            var unavailableObservedAtUtc = DateTimeOffset.UtcNow;
            LogUnavailable(
                PostgreSqlDurableControlPlaneOperation.HealthObservation,
                "SchemaStatus",
                unavailableCause,
                DurableProblemCodes.StoreUnavailable,
                PostgreSqlDurableDiagnostics.OperationalAssessmentTroubleshooting);
            return CreateUnavailableSnapshot(
                installedVersion: 0,
                requiredVersion: PostgreSqlDurableRuntimeSchemaManager.RequiredVersion,
                unavailableObservedAtUtc);
        }

        if (!schema.IsCompatible)
        {
            DateTimeOffset observedAtUtc;
            try
            {
                observedAtUtc = sharedConnection is not null && _usesDefaultDatabaseTimestampReader
                    ? await ReadDatabaseTimestampAsync(sharedConnection, cancellationToken).ConfigureAwait(false)
                    : await _readDatabaseTimestamp(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (TryClassifyUnavailable(
                PostgreSqlDurableControlPlaneOperation.HealthObservation,
                exception,
                cancellationToken,
                out var unavailableCause))
            {
                var unavailableObservedAtUtc = DateTimeOffset.UtcNow;
                LogUnavailable(
                    PostgreSqlDurableControlPlaneOperation.HealthObservation,
                    "SchemaObservationTimestamp",
                    unavailableCause,
                    DurableProblemCodes.StoreUnavailable,
                    PostgreSqlDurableDiagnostics.OperationalAssessmentTroubleshooting);
                return CreateUnavailableSnapshot(
                    schema.InstalledVersion,
                    schema.RequiredVersion,
                    unavailableObservedAtUtc);
            }

            return CreateIncompatibleSnapshot(
                ProblemForSchema(schema.Compatibility),
                schema.InstalledVersion,
                schema.RequiredVersion,
                observedAtUtc);
        }

        try
        {
            if (sharedConnectionAcquisition is { } connectionAcquisition
                && _observeRuntimeConnectionAcquisition is { } observer)
            {
                observer(connectionAcquisition);
            }

            var observation = sharedConnection is null
                ? await ReadObservationAsync(cancellationToken).ConfigureAwait(false)
                : await ReadObservationAsync(sharedConnection, cancellationToken).ConfigureAwait(false);
            var epochCompatible = observation.ActiveEpoch == _registration.WorkOptions.RuntimeEpoch;
            var (state, problemCode) = ResolveState(observation, epochCompatible);
            return new DurableRuntimeHealthSnapshot(
                state,
                problemCode,
                schemaCompatible: true,
                epochCompatible,
                schema.InstalledVersion,
                schema.RequiredVersion,
                _registration.WorkOptions.RuntimeEpoch,
                observation.ActiveEpoch,
                _registration.Options.WorkerId,
                observation.WorkerInstanceId,
                observation.HostedSurfaces ?? _registration.Options.HostedSurfaces,
                observation.ObservedAtUtc,
                observation.StartedAtUtc,
                observation.LastHeartbeatAtUtc,
                observation.LastSuccessfulSweepAtUtc,
                observation.IsDraining,
                observation.IsPassActive,
                observation.DueCount,
                observation.OldestDueAtUtc,
                observation.OldestDueAtUtc is { } oldest
                    ? observation.ObservedAtUtc - oldest
                    : null);
        }
        catch (Exception exception) when (TryClassifyUnavailable(
            PostgreSqlDurableControlPlaneOperation.HealthObservation,
            exception,
            cancellationToken,
            out var unavailableCause))
        {
            var unavailableObservedAtUtc = DateTimeOffset.UtcNow;
            LogUnavailable(
                PostgreSqlDurableControlPlaneOperation.HealthObservation,
                "RuntimeObservation",
                unavailableCause,
                DurableProblemCodes.StoreUnavailable,
                PostgreSqlDurableDiagnostics.OperationalAssessmentTroubleshooting);
            return CreateUnavailableSnapshot(
                schema.InstalledVersion,
                schema.RequiredVersion,
                unavailableObservedAtUtc);
        }
    }

    public ValueTask BeginDrainAsync(CancellationToken cancellationToken = default) =>
        SetDrainAsync(draining: true, cancellationToken);

    public ValueTask ResumeAsync(CancellationToken cancellationToken = default) =>
        SetDrainAsync(draining: false, cancellationToken);

    /// <summary>
    /// Preserves the legacy Boolean/exception admission behavior for internal callers and compatibility tests.
    /// </summary>
    internal async ValueTask<bool> TryBeginPassAsync(CancellationToken cancellationToken)
    {
        var result = await TryBeginPassWithOutcomeAsync(cancellationToken).ConfigureAwait(false);
        switch (result.Kind)
        {
            case PostgreSqlDurableStoreAdmissionKind.Admitted:
                return true;
            case PostgreSqlDurableStoreAdmissionKind.Draining:
            case PostgreSqlDurableStoreAdmissionKind.StorePassActive:
                return false;
            case PostgreSqlDurableStoreAdmissionKind.LostWorkerGeneration:
            case PostgreSqlDurableStoreAdmissionKind.EpochMismatch:
                result.LegacyException!.Throw();
                break;
        }

        throw new InvalidDataException($"Unknown durable store admission result '{result.Kind}'.");
    }

    /// <summary>
    /// Attempts store admission and returns typed pre-execution causes without parsing legacy exception messages.
    /// </summary>
    internal async ValueTask<PostgreSqlDurableStoreAdmission> TryBeginPassWithOutcomeAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await _registration.RuntimeDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await AcquireMigrationFenceAsync(
                connection,
                transaction,
                cancellationToken,
                captureAdmissionOutcome: true).ConfigureAwait(false);
            await _admissionSchemaManager.ValidateConnectionAsync(
                connection,
                transaction,
                cancellationToken).ConfigureAwait(false);
            await EnsureCurrentEpochUnderFenceAsync(
                connection,
                transaction,
                cancellationToken,
                captureAdmissionOutcome: true).ConfigureAwait(false);
            if (await EnsureSessionAsync(
                connection,
                transaction,
                cancellationToken,
                captureAdmissionOutcome: true).ConfigureAwait(false))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return PostgreSqlDurableStoreAdmission.Refused(
                    PostgreSqlDurableStoreAdmissionKind.Draining);
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
            int affected;
            try
            {
                affected = await PostgreSqlDurableControlPlaneCommand.ExecuteNonQueryAsync(
                    command,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
            {
                PostgreSqlDurableAdmissionFailureContext.MarkIndeterminate(exception);
                throw;
            }

            if (affected == 0)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return PostgreSqlDurableStoreAdmission.Refused(
                    PostgreSqlDurableStoreAdmissionKind.StorePassActive);
            }

            try
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
            {
                PostgreSqlDurableAdmissionFailureContext.MarkIndeterminate(exception);
                throw;
            }

            return PostgreSqlDurableStoreAdmission.Admitted;
        }
        catch (PostgreSqlDurableEpochMismatchSignal exception)
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            return PostgreSqlDurableStoreAdmission.WithLegacyException(
                PostgreSqlDurableStoreAdmissionKind.EpochMismatch,
                exception.LegacyException);
        }
        catch (PostgreSqlDurableWorkerGenerationSignal exception)
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            return PostgreSqlDurableStoreAdmission.WithLegacyException(
                PostgreSqlDurableStoreAdmissionKind.LostWorkerGeneration,
                exception.LegacyException);
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
        CancellationToken cancellationToken,
        bool captureAdmissionOutcome = false)
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
            _ = await ExecuteNonQueryAsync(
                insert,
                cancellationToken,
                captureAdmissionOutcome).ConfigureAwait(false);
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
            var session = await ExecuteReaderAsync(
                select,
                static async (reader, effectiveToken) =>
                {
                    if (!await reader.ReadAsync(effectiveToken).ConfigureAwait(false))
                    {
                        throw new InvalidDataException("The durable runtime heartbeat could not be registered.");
                    }

                    var result = new SessionObservation(
                        reader.GetGuid(0),
                        reader.GetGuid(1),
                        ReadUtc(reader, 2),
                        reader.GetBoolean(3),
                        reader.GetBoolean(4),
                        ReadUtc(reader, 5));
                    if (await reader.ReadAsync(effectiveToken).ConfigureAwait(false))
                    {
                        throw new InvalidDataException("The durable runtime heartbeat query returned more than one row.");
                    }

                    return result;
                },
                cancellationToken,
                captureAdmissionOutcome).ConfigureAwait(false);
            existingInstance = session.WorkerInstanceId;
            existingEpoch = session.RuntimeEpoch;
            lastHeartbeat = session.LastHeartbeatAtUtc;
            draining = session.IsDraining;
            passActive = session.IsPassActive;
            observedAt = session.ObservedAtUtc;
        }

        if (existingInstance != _registration.InstanceId || existingEpoch != _registration.WorkOptions.RuntimeEpoch)
        {
            var canTakeOver = existingEpoch != _registration.WorkOptions.RuntimeEpoch
                || (draining && !passActive)
                || observedAt - lastHeartbeat > _registration.Options.HeartbeatStaleAfter;
            if (!canTakeOver)
            {
                var legacyException = LostWorkerIdentity();
                if (captureAdmissionOutcome)
                {
                    throw new PostgreSqlDurableWorkerGenerationSignal(legacyException);
                }

                throw legacyException;
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
            EnsureOneRow(
                await ExecuteNonQueryAsync(
                    takeover,
                    cancellationToken,
                    captureAdmissionOutcome).ConfigureAwait(false),
                captureAdmissionOutcome);
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
        EnsureOneRow(
            await ExecuteNonQueryAsync(
                heartbeat,
                cancellationToken,
                captureAdmissionOutcome).ConfigureAwait(false),
            captureAdmissionOutcome);
        return draining;
    }

    private async ValueTask EnsureCurrentEpochAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken,
        bool captureAdmissionOutcome = false)
    {
        await AcquireMigrationFenceAsync(
            connection,
            transaction,
            cancellationToken,
            captureAdmissionOutcome).ConfigureAwait(false);
        await EnsureCurrentEpochUnderFenceAsync(
            connection,
            transaction,
            cancellationToken,
            captureAdmissionOutcome).ConfigureAwait(false);
    }

    private static async ValueTask AcquireMigrationFenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken,
        bool captureAdmissionOutcome)
    {
        await using var fence = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock_shared(@lock_id);",
            connection,
            transaction);
        fence.Parameters.AddWithValue("lock_id", PostgreSqlDurableRuntimeSchemaManager.MigrationAdvisoryLock);
        _ = await ExecuteNonQueryAsync(
            fence,
            cancellationToken,
            captureAdmissionOutcome).ConfigureAwait(false);
    }

    private async ValueTask EnsureCurrentEpochUnderFenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken,
        bool captureAdmissionOutcome)
    {
        await using var epoch = new NpgsqlCommand(
            "SELECT active_runtime_epoch FROM appsurface_durable.store_metadata WHERE singleton;",
            connection,
            transaction);
        if (await ExecuteScalarAsync(
                epoch,
                cancellationToken,
                captureAdmissionOutcome).ConfigureAwait(false) is not Guid active
            || active != _registration.WorkOptions.RuntimeEpoch)
        {
            var legacyException = new InvalidOperationException(
                $"{DurableProblemCodes.RecoveryEpochRequired}: The configured runtime epoch is not active in PostgreSQL.");
            if (captureAdmissionOutcome)
            {
                throw new PostgreSqlDurableEpochMismatchSignal(legacyException);
            }

            throw legacyException;
        }
    }

    /// <summary>
    /// Reads metadata, the optional worker generation, and due facts from one statement snapshot and one result row.
    /// </summary>
    private async ValueTask<RuntimeObservation> ReadObservationAsync(CancellationToken cancellationToken)
    {
        var connectionStarted = _observeRuntimeConnectionAcquisition is null
            ? 0
            : Stopwatch.GetTimestamp();
        await using var connection = await _registration.RuntimeDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (_observeRuntimeConnectionAcquisition is { } observer)
        {
            observer(Stopwatch.GetElapsedTime(connectionStarted));
        }

        return await ReadObservationAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the one-row runtime observation through an existing compatible status connection.</summary>
    private async ValueTask<RuntimeObservation> ReadObservationAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = """
            WITH observed AS
            (
                SELECT statement_timestamp() AS observed_at_utc
            )
            SELECT observed.observed_at_utc, metadata.active_runtime_epoch,
                   heartbeat.worker_instance_id, heartbeat.runtime_epoch, heartbeat.hosted_surfaces,
                   heartbeat.started_at, heartbeat.last_heartbeat_at, heartbeat.last_successful_sweep_at,
                   heartbeat.draining, heartbeat.pass_active,
                   due.due_count, due.oldest_due_at
            FROM observed
            JOIN appsurface_durable.store_metadata AS metadata
              ON metadata.singleton
            LEFT JOIN appsurface_durable.runtime_heartbeat AS heartbeat
              ON heartbeat.worker_id = @worker_id
            CROSS JOIN LATERAL
                appsurface_durable.runtime_due_dispatch_health(@surfaces) AS due;
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("worker_id", _registration.Options.WorkerId);
        command.Parameters.AddWithValue("surfaces", (short)_registration.Options.HostedSurfaces);
        return await PostgreSqlDurableControlPlaneCommand.ExecuteReaderAsync(
            command,
            static async (reader, effectiveToken) =>
            {
                if (!await reader.ReadAsync(effectiveToken).ConfigureAwait(false))
                {
                    throw new InvalidDataException("The durable runtime health observation returned no row.");
                }

                var result = ReadObservationRow(reader);
                if (await reader.ReadAsync(effectiveToken).ConfigureAwait(false))
                {
                    throw new InvalidDataException("The durable runtime health observation returned more than one row.");
                }

                return result;
            },
            cancellationToken).ConfigureAwait(false);
    }

    private (DurableRuntimeHealthState State, string? ProblemCode) ResolveState(
        RuntimeObservation worker,
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

    /// <summary>Validates the complete one-row provider result before any public snapshot is constructed.</summary>
    private static RuntimeObservation ReadObservationRow(NpgsqlDataReader reader)
    {
        var observedAtUtc = ReadUtc(reader, 0);
        var activeEpoch = reader.IsDBNull(1) ? (Guid?)null : reader.GetGuid(1);
        var hasWorker = !reader.IsDBNull(2);
        Guid? workerInstanceId = null;
        Guid? heartbeatEpoch = null;
        DurableRuntimeSurface? hostedSurfaces = null;
        DateTimeOffset? startedAtUtc = null;
        DateTimeOffset? lastHeartbeatAtUtc = null;
        DateTimeOffset? lastSuccessfulSweepAtUtc = null;
        var isDraining = false;
        var isPassActive = false;
        if (hasWorker)
        {
            for (var ordinal = 3; ordinal <= 6; ordinal++)
            {
                if (reader.IsDBNull(ordinal))
                {
                    throw new InvalidDataException(
                        "The durable runtime health worker row contains a null required value.");
                }
            }

            if (reader.IsDBNull(8) || reader.IsDBNull(9))
            {
                throw new InvalidDataException(
                    "The durable runtime health worker row contains a null lifecycle flag.");
            }

            workerInstanceId = reader.GetGuid(2);
            heartbeatEpoch = reader.GetGuid(3);
            var hostedSurfaceValue = reader.GetInt16(4);
            if (hostedSurfaceValue <= 0
                || (hostedSurfaceValue & ~(short)DurableRuntimeSurface.All) != 0)
            {
                throw new InvalidDataException(
                    "The durable runtime health worker row contains an invalid hosted-surface mask.");
            }

            hostedSurfaces = (DurableRuntimeSurface)hostedSurfaceValue;
            startedAtUtc = ReadUtc(reader, 5);
            lastHeartbeatAtUtc = ReadUtc(reader, 6);
            lastSuccessfulSweepAtUtc = ReadNullableUtc(reader, 7);
            isDraining = reader.GetBoolean(8);
            isPassActive = reader.GetBoolean(9);
        }
        else
        {
            for (var ordinal = 3; ordinal <= 9; ordinal++)
            {
                if (!reader.IsDBNull(ordinal))
                {
                    throw new InvalidDataException(
                        "The durable runtime health result contains a partial worker row.");
                }
            }
        }

        var dueCount = reader.GetInt64(10);
        var oldestDueAtUtc = ReadNullableUtc(reader, 11);
        if (dueCount < 0
            || (dueCount == 0 && oldestDueAtUtc is not null)
            || (dueCount > 0 && oldestDueAtUtc is null)
            || oldestDueAtUtc > observedAtUtc)
        {
            throw new InvalidDataException(
                "The durable runtime health result contains contradictory due-dispatch facts.");
        }

        return new RuntimeObservation(
            observedAtUtc,
            activeEpoch,
            workerInstanceId,
            heartbeatEpoch,
            hostedSurfaces,
            startedAtUtc,
            lastHeartbeatAtUtc,
            lastSuccessfulSweepAtUtc,
            isDraining,
            isPassActive,
            dueCount,
            oldestDueAtUtc);
    }

    /// <summary>Reads authoritative database time for a schema-only incompatibility assessment.</summary>
    private async ValueTask<DateTimeOffset> ReadDatabaseTimestampAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _registration.RuntimeDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadDatabaseTimestampAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads authoritative database time through an existing compatible status connection.</summary>
    private static async ValueTask<DateTimeOffset> ReadDatabaseTimestampAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT statement_timestamp();", connection);
        return await PostgreSqlDurableControlPlaneCommand.ExecuteReaderAsync(
            command,
            static async (reader, effectiveToken) =>
            {
                if (!await reader.ReadAsync(effectiveToken).ConfigureAwait(false))
                {
                    throw new InvalidDataException("The durable schema assessment timestamp query returned no row.");
                }

                var observedAtUtc = ReadUtc(reader, 0);
                if (await reader.ReadAsync(effectiveToken).ConfigureAwait(false))
                {
                    throw new InvalidDataException(
                        "The durable schema assessment timestamp query returned more than one row.");
                }

                return observedAtUtc;
            },
            cancellationToken).ConfigureAwait(false);
    }

    private DurableRuntimeHealthSnapshot CreateIncompatibleSnapshot(
        string problemCode,
        int installedVersion,
        int requiredVersion,
        DateTimeOffset observedAtUtc) =>
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
            observedAtUtc,
            startedAtUtc: null,
            lastHeartbeatAtUtc: null,
            lastSuccessfulSweepAtUtc: null,
            isDraining: false,
            isPassActive: false,
            dueDispatchCount: 0,
            oldestDueAtUtc: null,
            oldestDueAge: null);

    private DurableRuntimeHealthSnapshot CreateUnavailableSnapshot(
        int installedVersion,
        int requiredVersion,
        DateTimeOffset observedAtUtc) =>
        new(
            DurableRuntimeHealthState.Unavailable,
            DurableProblemCodes.StoreUnavailable,
            schemaCompatible: false,
            epochCompatible: false,
            installedVersion,
            requiredVersion,
            _registration.WorkOptions.RuntimeEpoch,
            activeRuntimeEpoch: null,
            _registration.Options.WorkerId,
            workerInstanceId: null,
            _registration.Options.HostedSurfaces,
            observedAtUtc,
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

    private static string ProblemForSchema(DurableRuntimeSchemaCompatibility compatibility) =>
        PostgreSqlDurableFailureClassifier.ProblemForSchema(compatibility);

    private static InvalidOperationException LostWorkerIdentity() => new(
        $"{DurableProblemCodes.WorkerIdentityConflict}: This worker generation no longer owns the configured worker identity.");

    private static void EnsureOneRow(int affected, bool captureAdmissionOutcome = false)
    {
        if (affected != 1)
        {
            var legacyException = LostWorkerIdentity();
            if (captureAdmissionOutcome)
            {
                throw new PostgreSqlDurableWorkerGenerationSignal(legacyException);
            }

            throw legacyException;
        }
    }

    private static async ValueTask<int> ExecuteNonQueryAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken,
        bool controlPlane)
    {
        if (controlPlane)
        {
            return await PostgreSqlDurableControlPlaneCommand.ExecuteNonQueryAsync(
                command,
                cancellationToken).ConfigureAwait(false);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<object?> ExecuteScalarAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken,
        bool controlPlane)
    {
        if (controlPlane)
        {
            return await PostgreSqlDurableControlPlaneCommand.ExecuteScalarAsync(
                command,
                cancellationToken).ConfigureAwait(false);
        }

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<TResult> ExecuteReaderAsync<TResult>(
        NpgsqlCommand command,
        Func<NpgsqlDataReader, CancellationToken, ValueTask<TResult>> projector,
        CancellationToken cancellationToken,
        bool controlPlane)
    {
        if (controlPlane)
        {
            return await PostgreSqlDurableControlPlaneCommand.ExecuteReaderAsync(
                command,
                projector,
                cancellationToken).ConfigureAwait(false);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await projector(reader, cancellationToken).ConfigureAwait(false);
    }

    private static bool TryClassifyUnavailable(
        PostgreSqlDurableControlPlaneOperation operation,
        Exception exception,
        CancellationToken cancellationToken,
        out PostgreSqlDurableUnavailableCause unavailableCause)
    {
        var classification = PostgreSqlDurableFailureClassifier.Classify(
            operation,
            exception,
            cancellationToken);
        unavailableCause = classification.UnavailableCause.GetValueOrDefault();
        return classification.Disposition == PostgreSqlDurableFailureDisposition.Unavailable;
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

    [LoggerMessage(
        EventId = 4110,
        Level = LogLevel.Warning,
        Message = "{ProblemCode} durable PostgreSQL control-plane operation {Operation} at phase {Phase} was unavailable due to {Cause}. See {TroubleshootingAnchor}.")]
    private partial void LogUnavailable(
        PostgreSqlDurableControlPlaneOperation operation,
        string phase,
        PostgreSqlDurableUnavailableCause cause,
        string problemCode,
        string troubleshootingAnchor);

    private sealed record RuntimeObservation(
        DateTimeOffset ObservedAtUtc,
        Guid? ActiveEpoch,
        Guid? WorkerInstanceId,
        Guid? HeartbeatEpoch,
        DurableRuntimeSurface? HostedSurfaces,
        DateTimeOffset? StartedAtUtc,
        DateTimeOffset? LastHeartbeatAtUtc,
        DateTimeOffset? LastSuccessfulSweepAtUtc,
        bool IsDraining,
        bool IsPassActive,
        long DueCount,
        DateTimeOffset? OldestDueAtUtc);

    private sealed record SessionObservation(
        Guid WorkerInstanceId,
        Guid RuntimeEpoch,
        DateTimeOffset LastHeartbeatAtUtc,
        bool IsDraining,
        bool IsPassActive,
        DateTimeOffset ObservedAtUtc);
}

/// <summary>Identifies the authoritative store-admission outcome before application execution.</summary>
internal enum PostgreSqlDurableStoreAdmissionKind
{
    /// <summary>The store admitted the pass.</summary>
    Admitted = 0,

    /// <summary>The worker is draining.</summary>
    Draining = 1,

    /// <summary>The worker already has a store-active pass.</summary>
    StorePassActive = 2,

    /// <summary>Another live process generation owns the configured worker identity.</summary>
    LostWorkerGeneration = 3,

    /// <summary>The configured runtime epoch is not active.</summary>
    EpochMismatch = 4,
}

/// <summary>Carries a typed store-admission result and the exact legacy exception when one is required.</summary>
internal readonly record struct PostgreSqlDurableStoreAdmission(
    PostgreSqlDurableStoreAdmissionKind Kind,
    ExceptionDispatchInfo? LegacyException)
{
    /// <summary>Gets an admitted result.</summary>
    internal static PostgreSqlDurableStoreAdmission Admitted { get; } =
        new(PostgreSqlDurableStoreAdmissionKind.Admitted, null);

    /// <summary>Creates a refusal that has no legacy exception.</summary>
    internal static PostgreSqlDurableStoreAdmission Refused(
        PostgreSqlDurableStoreAdmissionKind kind) =>
        kind is PostgreSqlDurableStoreAdmissionKind.Draining
            or PostgreSqlDurableStoreAdmissionKind.StorePassActive
            ? new PostgreSqlDurableStoreAdmission(kind, null)
            : throw new ArgumentOutOfRangeException(nameof(kind));

    /// <summary>Creates a typed outcome that retains the original legacy exception.</summary>
    internal static PostgreSqlDurableStoreAdmission WithLegacyException(
        PostgreSqlDurableStoreAdmissionKind kind,
        InvalidOperationException legacyException)
    {
        ArgumentNullException.ThrowIfNull(legacyException);
        return kind is PostgreSqlDurableStoreAdmissionKind.LostWorkerGeneration
            or PostgreSqlDurableStoreAdmissionKind.EpochMismatch
            ? new PostgreSqlDurableStoreAdmission(kind, ExceptionDispatchInfo.Capture(legacyException))
            : throw new ArgumentOutOfRangeException(nameof(kind));
    }
}

/// <summary>Internal signal that lets admission retain the exact plain legacy epoch exception.</summary>
internal sealed class PostgreSqlDurableEpochMismatchSignal(InvalidOperationException legacyException) : Exception
{
    /// <summary>Gets the unmodified exception used by the legacy projection.</summary>
    internal InvalidOperationException LegacyException { get; } =
        legacyException ?? throw new ArgumentNullException(nameof(legacyException));
}

/// <summary>Internal signal that lets admission retain the exact plain legacy worker-generation exception.</summary>
internal sealed class PostgreSqlDurableWorkerGenerationSignal(InvalidOperationException legacyException) : Exception
{
    /// <summary>Gets the unmodified exception used by the legacy projection.</summary>
    internal InvalidOperationException LegacyException { get; } =
        legacyException ?? throw new ArgumentNullException(nameof(legacyException));
}
