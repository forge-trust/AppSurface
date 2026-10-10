using System.Data;
using ForgeTrust.AppSurface.Durable.PostgreSql;
using Npgsql;
using NpgsqlTypes;

namespace ForgeTrust.AppSurface.Cli;

/// <summary>Observation stages exposed internally for deterministic pause, denial and cleanup verification.</summary>
internal enum DurableDoctorStage
{
    Open, Fence, Affinity, Begin, Credential, Schema, Retention, Runtime, Commit, Rollback, Unlock, Close, Closed
}

/// <summary>Owns a nonpooled physical session and one fenced, read-only repeatable-read doctor snapshot.</summary>
/// <remarks>
/// One monotonic budget reserves min(2s, timeout/5) for rollback, unlock and physical disposal.
/// The original caller token remains distinct from that deadline. A terminal outcome discards every database fact.
/// The stage seam is internal and production composition supplies no observer. It never starts background work.
/// See the doctor reference and the separate complete-manifest deployment preflight.
/// </remarks>
internal sealed class DurableDoctorService : IDurableDoctorService
{
    /// <summary>Shares the provider migration fence identity with internal command and lifetime test consumers.</summary>
    internal const long MigrationAdvisoryLock = PostgreSqlDurableRuntimeSchemaManager.MigrationAdvisoryLock;

    private readonly TimeProvider _timeProvider;
    private readonly Func<DurableDoctorStage, CancellationToken, ValueTask>? _stage;

    /// <summary>Creates the passive production service; no connection is opened during registration.</summary>
    public DurableDoctorService() : this(TimeProvider.System, null) { }

    internal DurableDoctorService(TimeProvider? timeProvider,
        Func<DurableDoctorStage, CancellationToken, ValueTask>? stage)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _stage = stage;
    }

    /// <inheritdoc />
    public async ValueTask<DurableDoctorObservation> InspectAsync(
        string connectionString, DurableDoctorRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var budget = new DurableDoctorBudget(request.Timeout, _timeProvider);
        using var deadline = new CancellationTokenSource(budget.WorkTimeout, _timeProvider);
        using var online = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var settings = CreateSessionSettings(connectionString, budget.WorkTimeout);
        var dataSource = NpgsqlDataSource.Create(settings.ConnectionString);
        var connection = dataSource.CreateConnection();
        NpgsqlTransaction? transaction = null;
        var ownsFence = false;
        var transactionCompleted = false;
        var cleanupFailed = false;
        DurableDoctorObservation? observation = null;
        DurableDoctorFailureException? failure = null;
        try
        {
            await StageAsync(DurableDoctorStage.Open, online.Token).ConfigureAwait(false);
            await connection.OpenAsync(online.Token).ConfigureAwait(false);
            await StageAsync(DurableDoctorStage.Fence, online.Token).ConfigureAwait(false);
            await using (var fence = CreateCommand("SELECT pg_catalog.pg_advisory_lock_shared(@key)", connection, null, budget.WorkRemaining))
            {
                fence.Parameters.AddWithValue("key", MigrationAdvisoryLock);
                await fence.ExecuteNonQueryAsync(online.Token).ConfigureAwait(false);
                ownsFence = true;
            }

            await StageAsync(DurableDoctorStage.Affinity, online.Token).ConfigureAwait(false);
            await VerifyAffinityAsync(connection, null, online.Token).ConfigureAwait(false);
            await StageAsync(DurableDoctorStage.Begin, online.Token).ConfigureAwait(false);
            transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, online.Token).ConfigureAwait(false);
            await using (var readOnly = CreateCommand("SET TRANSACTION READ ONLY", connection, transaction, budget.WorkRemaining))
            {
                await readOnly.ExecuteNonQueryAsync(online.Token).ConfigureAwait(false);
            }

            await SetServerBudgetAsync(connection, transaction, budget.WorkRemaining, online.Token).ConfigureAwait(false);
            await StageAsync(DurableDoctorStage.Credential, online.Token).ConfigureAwait(false);
            var credential = await DurableDoctorCatalog.ReadCredentialAsync(connection, transaction, online.Token).ConfigureAwait(false);
            if (credential.Count != 0)
            {
                observation = new DurableDoctorObservation(credential);
            }
            else
            {
                await SetServerBudgetAsync(connection, transaction, budget.WorkRemaining, online.Token).ConfigureAwait(false);
                await StageAsync(DurableDoctorStage.Schema, online.Token).ConfigureAwait(false);
                var status = await new PostgreSqlDurableRuntimeSchemaManager(dataSource)
                    .ReadDoctorStatusInTransactionAsync(connection, transaction, online.Token).ConfigureAwait(false);
                if (!status.IsCompatible)
                {
                    observation = new DurableDoctorObservation(credential, status);
                }
                else
                {
                    await SetServerBudgetAsync(connection, transaction, budget.WorkRemaining, online.Token).ConfigureAwait(false);
                    await StageAsync(DurableDoctorStage.Retention, online.Token).ConfigureAwait(false);
                    var retention = await DurableDoctorCatalog.ReadRetentionAsync(connection, transaction, online.Token).ConfigureAwait(false);
                    await SetServerBudgetAsync(connection, transaction, budget.WorkRemaining, online.Token).ConfigureAwait(false);
                    await StageAsync(DurableDoctorStage.Runtime, online.Token).ConfigureAwait(false);
                    observation = await ReadRuntimeAsync(connection, transaction, request, status, credential, retention,
                        budget.WorkRemaining, online.Token).ConfigureAwait(false);
                }
            }

            await VerifyAffinityAsync(connection, transaction, online.Token).ConfigureAwait(false);
            await StageAsync(DurableDoctorStage.Commit, online.Token).ConfigureAwait(false);
            await transaction.CommitAsync(online.Token).ConfigureAwait(false);
            transactionCompleted = true;
        }
        catch (Exception exception) when (IsNonfatal(exception))
        {
            failure = ClassifyFailure(exception, cancellationToken,
                deadline.IsCancellationRequested || budget.WorkRemaining <= TimeSpan.Zero, online.Token);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(
                budget.TotalRemaining > TimeSpan.Zero ? budget.TotalRemaining : TimeSpan.FromTicks(1), _timeProvider);
            try
            {
                if (transaction is not null && !transactionCompleted && transaction.Connection is not null)
                {
                    await StageAsync(DurableDoctorStage.Rollback, cleanup.Token).ConfigureAwait(false);
                    await using var rollback = CreateCommand("ROLLBACK", connection, transaction, budget.TotalRemaining);
                    await rollback.ExecuteNonQueryAsync(cleanup.Token).ConfigureAwait(false);
                }
                if (ownsFence)
                {
                    await StageAsync(DurableDoctorStage.Unlock, cleanup.Token).ConfigureAwait(false);
                    await using var unlock = CreateCommand("SELECT pg_catalog.pg_advisory_unlock_shared(@key)", connection, null, budget.TotalRemaining);
                    unlock.Parameters.AddWithValue("key", MigrationAdvisoryLock);
                    if (await unlock.ExecuteScalarAsync(cleanup.Token).ConfigureAwait(false) is not true)
                    {
                        cleanupFailed = true;
                    }
                }
            }
            catch (Exception exception) when (IsNonfatal(exception))
            {
                cleanupFailed = true;
            }
            finally
            {
                // Always await physical teardown. WaitAsync would leave owned I/O running after output.
                try
                {
                    await StageAsync(DurableDoctorStage.Close, cleanup.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (IsNonfatal(exception))
                {
                    cleanupFailed = true;
                }
                try
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                    // With the physical connector closed, transaction disposal cannot retry rollback I/O.
                    if (transaction is not null)
                    {
                        await transaction.DisposeAsync().ConfigureAwait(false);
                    }
                }
                catch (Exception exception) when (IsNonfatal(exception))
                {
                    cleanupFailed = true;
                }
                finally
                {
                    try
                    {
                        await dataSource.DisposeAsync().ConfigureAwait(false);
                        await StageAsync(DurableDoctorStage.Closed, cleanup.Token).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (IsNonfatal(exception))
                    {
                        cleanupFailed = true;
                    }
                }
            }
        }

        if (cancellationToken.IsCancellationRequested)
        {
            throw new DurableDoctorFailureException(DurableDoctorFailureKind.Canceled, "caller-canceled");
        }
        if (failure?.Kind is DurableDoctorFailureKind.Failed or DurableDoctorFailureKind.Canceled)
        {
            throw failure;
        }
        if (cleanupFailed || budget.TotalRemaining <= TimeSpan.Zero)
        {
            var categories = (failure?.Categories ?? []).Concat(new[] { "cleanup" }).Distinct(StringComparer.Ordinal);
            throw new DurableDoctorFailureException(DurableDoctorFailureKind.Unavailable,
                DurableDoctorChecks.All.Where(categories.Contains).ToArray());
        }
        if (failure is not null)
        {
            throw failure;
        }
        return observation ?? throw new DurableDoctorFailureException(DurableDoctorFailureKind.Failed, "catalog-contract");
    }

    /// <summary>Applies dedicated-session settings without extending shorter configured driver waits.</summary>
    internal static NpgsqlConnectionStringBuilder CreateSessionSettings(string connectionString, TimeSpan workTimeout)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(workTimeout.TotalSeconds));
        var settings = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            Multiplexing = false,
            Enlist = false,
            KeepAlive = 0,
            CancellationTimeout = -1
        };
        settings.Timeout = settings.Timeout == 0 ? seconds : Math.Min(settings.Timeout, seconds);
        settings.CommandTimeout = settings.CommandTimeout == 0 ? seconds : Math.Min(settings.CommandTimeout, seconds);
        return settings;
    }

    /// <summary>Retains caller/deadline provenance and the provider's propagate-by-default allowlist.</summary>
    internal static DurableDoctorFailureException ClassifyFailure(Exception exception, CancellationToken caller, bool deadlineExpired,
        CancellationToken ownedToken = default)
    {
        if (caller.IsCancellationRequested)
        {
            return new(DurableDoctorFailureKind.Canceled, "caller-canceled");
        }
        if (exception is DurableDoctorFailureException known)
        {
            return known;
        }
        var classified = PostgreSqlDurableFailureClassifier.Classify(
            PostgreSqlDurableControlPlaneOperation.HealthObservation, exception, caller);
        if (exception is OperationCanceledException canceled && canceled.CancellationToken.CanBeCanceled
            && canceled.CancellationToken != ownedToken
            && !PostgreSqlDurableControlPlaneCommand.IsCancellationFrom(exception, ownedToken)
            && PostgreSqlDurableControlPlaneCommand.GetTimeoutEvidence(exception)
                != PostgreSqlDurableTimeoutEvidence.ProviderDeadlineElapsed)
        {
            return new(DurableDoctorFailureKind.Failed, "catalog-contract");
        }
        if (deadlineExpired && (exception is OperationCanceledException or TimeoutException
            || exception is PostgresException { SqlState: PostgresErrorCodes.QueryCanceled }
            || classified.Disposition == PostgreSqlDurableFailureDisposition.Unavailable))
        {
            return new(DurableDoctorFailureKind.Unavailable, "deadline");
        }
        return classified.Disposition == PostgreSqlDurableFailureDisposition.Unavailable
            ? new(DurableDoctorFailureKind.Unavailable, "dependency")
            : new(DurableDoctorFailureKind.Failed, "catalog-contract");
    }

    private async ValueTask StageAsync(DurableDoctorStage stage, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_stage is not null)
        {
            await _stage(stage, token).ConfigureAwait(false);
        }
        token.ThrowIfCancellationRequested();
    }

    private static bool IsNonfatal(Exception exception) => exception is not StackOverflowException
        and not OutOfMemoryException and not AccessViolationException;

    private static async ValueTask VerifyAffinityAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken token)
    {
        try
        {
            await DurableSchemaPreflightVerifier.VerifyFenceAffinityAsync(connection, transaction, token).ConfigureAwait(false);
        }
        catch (DurablePreflightException)
        {
            throw new DurableDoctorFailureException(DurableDoctorFailureKind.Unavailable, "session-affinity");
        }
    }

    private static NpgsqlCommand CreateCommand(string sql, NpgsqlConnection connection, NpgsqlTransaction? transaction, TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero)
        {
            throw new DurableDoctorFailureException(DurableDoctorFailureKind.Unavailable, "deadline");
        }
        // The linked token enforces subsecond remaining time; driver whole seconds never define the total budget.
        var seconds = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
        var configuredSeconds = connection.CommandTimeout;
        return new NpgsqlCommand(sql, connection, transaction)
        {
            CommandTimeout = configuredSeconds == 0 ? seconds : Math.Min(configuredSeconds, seconds)
        };
    }

    private static async ValueTask SetServerBudgetAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TimeSpan remaining, CancellationToken token)
    {
        await using var command = CreateCommand(
            "SELECT pg_catalog.set_config('statement_timeout', @milliseconds, true), pg_catalog.set_config('lock_timeout', @milliseconds, true)",
            connection, transaction, remaining);
        command.Parameters.AddWithValue("milliseconds", Math.Max(1, (long)Math.Ceiling(remaining.TotalMilliseconds)).ToString(System.Globalization.CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    /// <summary>Captures one database clock and exact selected row, then checks agreement with the authoritative schema reader.</summary>
    internal static async ValueTask<DurableDoctorObservation> ReadRuntimeAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, DurableDoctorRequest request, DurableRuntimeSchemaStatus schema,
        IReadOnlyList<string> credential, IReadOnlyList<string> retention, TimeSpan remaining, CancellationToken token)
    {
        const string sql = """
            WITH observation AS MATERIALIZED (SELECT pg_catalog.clock_timestamp() AS observed_at),
            selected_worker AS MATERIALIZED (
              SELECT runtime_epoch, last_heartbeat_at, draining
              FROM appsurface_durable.runtime_heartbeat
              WHERE @requested AND worker_id COLLATE "C" = @worker COLLATE "C" LIMIT 2),
            selected_metadata AS MATERIALIZED (
              SELECT store_id, active_runtime_epoch FROM appsurface_durable.store_metadata WHERE singleton LIMIT 2)
            SELECT observed_at, store_id, active_runtime_epoch,
                   (SELECT count(*)::integer FROM selected_worker),
                   (SELECT runtime_epoch FROM selected_worker LIMIT 1),
                   (SELECT last_heartbeat_at FROM selected_worker LIMIT 1),
                   (SELECT draining FROM selected_worker LIMIT 1)
            FROM observation CROSS JOIN selected_metadata
            """;
        await using var command = CreateCommand(sql, connection, transaction, remaining);
        command.Parameters.AddWithValue("requested", request.WorkerId is not null);
        command.Parameters.Add("worker", NpgsqlDbType.Text).Value = (object?)request.WorkerId ?? DBNull.Value;
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false))
        {
            throw new InvalidDataException("Doctor runtime metadata is missing.");
        }
        var observed = ReadTimestamp(reader, 0);
        var storeId = reader.GetGuid(1);
        var epoch = reader.IsDBNull(2) ? (Guid?)null : reader.GetGuid(2);
        var count = reader.GetInt32(3);
        if (storeId == Guid.Empty || epoch == Guid.Empty || storeId != schema.StoreId || epoch != schema.ActiveRuntimeEpoch
            || count is < 0 or > 1 || request.WorkerId is null && count != 0)
        {
            throw new InvalidDataException("Doctor runtime facts contradict the schema snapshot.");
        }
        DurableDoctorHeartbeat? heartbeat = null;
        if (request.WorkerId is not null)
        {
            if (count == 0)
            {
                if (!reader.IsDBNull(4) || !reader.IsDBNull(5) || !reader.IsDBNull(6))
                {
                    throw new InvalidDataException("Doctor missing heartbeat contains row facts.");
                }
                heartbeat = new(false, null, null, null);
            }
            else
            {
                var rowEpoch = reader.GetGuid(4);
                if (rowEpoch == Guid.Empty)
                {
                    throw new InvalidDataException("Doctor heartbeat epoch is empty.");
                }
                heartbeat = new(true, rowEpoch, ReadTimestamp(reader, 5), reader.GetBoolean(6));
            }
        }
        if (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            throw new InvalidDataException("Doctor runtime metadata is duplicated.");
        }
        return new DurableDoctorObservation(credential, schema, retention, observed, storeId, epoch, heartbeat);
    }

    private static DateTimeOffset ReadTimestamp(NpgsqlDataReader reader, int ordinal)
    {
        var value = reader.GetDateTime(ordinal);
        if (value.Kind != DateTimeKind.Utc || value == DateTime.MinValue || value == DateTime.MaxValue)
        {
            throw new InvalidDataException("Doctor timestamp is invalid.");
        }
        return new DateTimeOffset(value);
    }
}

/// <summary>Single monotonic deadline; remaining budgets shrink across every stage including cleanup.</summary>
internal sealed class DurableDoctorBudget
{
    private readonly TimeProvider _time;
    private readonly long _started;
    private readonly TimeSpan _total;

    internal DurableDoctorBudget(TimeSpan timeout, TimeProvider timeProvider)
    {
        if (timeout < TimeSpan.FromSeconds(1) || timeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
        _time = timeProvider;
        _started = _time.GetTimestamp();
        _total = timeout;
        WorkTimeout = timeout - TimeSpan.FromTicks(Math.Min(TimeSpan.FromSeconds(2).Ticks, timeout.Ticks / 5));
    }

    internal TimeSpan WorkTimeout { get; }
    internal TimeSpan WorkRemaining => WorkTimeout - _time.GetElapsedTime(_started);
    internal TimeSpan TotalRemaining => _total - _time.GetElapsedTime(_started);
}
