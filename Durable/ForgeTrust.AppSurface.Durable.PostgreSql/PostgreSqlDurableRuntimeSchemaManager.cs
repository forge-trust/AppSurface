using System.Diagnostics;
using System.Globalization;
using System.Text;
using ForgeTrust.AppSurface.Durable;
using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>Implements explicit package-owned durable schema operations for PostgreSQL.</summary>
/// <remarks>
/// Migration and epoch mutations hold one session advisory lock across their individual transactions. The
/// session scope is required to prevent another migration owner from interleaving between migrations; lock
/// acquisition is nevertheless bounded and cancellation-aware, and the owning connection is always disposed
/// after the mutation so PostgreSQL releases the lock even when explicit cleanup cannot run.
/// </remarks>
public sealed class PostgreSqlDurableRuntimeSchemaManager : IDurableRuntimeSchemaManager
{
    internal const long MigrationAdvisoryLock = 0x415344555241424C;

    /// <summary>Bounds programmatic and generated-script waits for the migration session lock.</summary>
    internal const int MigrationLockAcquireTimeoutSeconds = 30;

    /// <summary>Controls the polling cadence used by programmatic non-blocking lock acquisition.</summary>
    internal const int MigrationLockRetryDelayMilliseconds = 100;

    /// <summary>
    /// Identifies the bounded index-build migration that requires an extended client deadline.
    /// </summary>
    internal const int ExtendedDeadlineMigrationVersion = 10;

    /// <summary>
    /// Keeps the client alive beyond migration 0010's five-minute server-side statement deadline.
    /// </summary>
    internal const int ExtendedMigrationCommandTimeoutSeconds = 330;
    private readonly NpgsqlDataSource _dataSource;
    private readonly IReadOnlyList<DurablePostgreSqlMigration> _migrations;
    private readonly TimeSpan _migrationLockAcquireTimeout;
    private readonly TimeSpan _migrationLockRetryDelay;
    private readonly Action<bool>? _observeMigrationLockAttempt;
    private readonly Action? _observeStatusConnectionOpenStarted;
    private readonly Action<TimeSpan>? _observeStatusConnectionAcquisition;

    /// <summary>Initializes a schema manager using a migration-owner data source.</summary>
    public PostgreSqlDurableRuntimeSchemaManager(NpgsqlDataSource dataSource)
        : this(dataSource, DurablePostgreSqlMigrationCatalog.Load())
    {
    }

    /// <summary>Initializes a schema manager with an explicit migration catalog and lock timings for transaction-boundary verification.</summary>
    /// <param name="dataSource">Migration-owner data source.</param>
    /// <param name="migrations">Ordered, contiguous migration definitions.</param>
    /// <param name="migrationLockAcquireTimeout">Maximum time to wait for the session migration lock; <see langword="null"/> uses the 30-second production default.</param>
    /// <param name="migrationLockRetryDelay">Delay between non-blocking lock attempts; <see langword="null"/> uses the 100-millisecond production default.</param>
    /// <param name="observeMigrationLockAttempt">
    /// Optional observer invoked with each non-blocking migration-lock result.
    /// Production composition leaves this null; concurrency tests use it instead of timing guesses.
    /// </param>
    /// <param name="observeStatusConnectionOpenStarted">
    /// Optional observer invoked after the schema-status connection acquisition has started.
    /// Production composition leaves this null; constrained-pool evidence uses it as a synchronization seam.
    /// </param>
    /// <param name="observeStatusConnectionAcquisition">
    /// Optional observer for the schema-status connection acquisition inside <see cref="GetStatusAsync(CancellationToken)"/>.
    /// Production composition leaves this null; scale evidence uses it to measure the public health path.
    /// </param>
    /// <remarks>This test seam is internal so production callers always use the embedded, checksum-verified catalog.</remarks>
    internal PostgreSqlDurableRuntimeSchemaManager(
        NpgsqlDataSource dataSource,
        IReadOnlyList<DurablePostgreSqlMigration> migrations,
        TimeSpan? migrationLockAcquireTimeout = null,
        TimeSpan? migrationLockRetryDelay = null,
        Action<bool>? observeMigrationLockAttempt = null,
        Action? observeStatusConnectionOpenStarted = null,
        Action<TimeSpan>? observeStatusConnectionAcquisition = null)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        ArgumentNullException.ThrowIfNull(migrations);
        _migrations = migrations.ToArray();
        _migrationLockAcquireTimeout = RequirePositiveDuration(
            migrationLockAcquireTimeout ?? TimeSpan.FromSeconds(MigrationLockAcquireTimeoutSeconds),
            nameof(migrationLockAcquireTimeout));
        _migrationLockRetryDelay = RequirePositiveDuration(
            migrationLockRetryDelay ?? TimeSpan.FromMilliseconds(MigrationLockRetryDelayMilliseconds),
            nameof(migrationLockRetryDelay));
        _observeMigrationLockAttempt = observeMigrationLockAttempt;
        _observeStatusConnectionOpenStarted = observeStatusConnectionOpenStarted;
        _observeStatusConnectionAcquisition = observeStatusConnectionAcquisition;
    }

    /// <summary>Gets the schema version required by this package.</summary>
    public static int RequiredVersion => DurablePostgreSqlMigrationCatalog.RequiredVersion;

    /// <inheritdoc />
    public async ValueTask<DurableRuntimeSchemaStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenStatusConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await GetStatusAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns whether a runtime operation may safely reuse this manager's status connection.
    /// </summary>
    /// <param name="dataSource">The runtime data source that would reuse the connection.</param>
    /// <returns>
    /// <see langword="true"/> only when both operations use the same data-source instance and therefore the same
    /// credentials, pool, and connection policy.
    /// </returns>
    internal bool CanShareStatusConnectionWith(NpgsqlDataSource dataSource) =>
        ReferenceEquals(_dataSource, dataSource);

    /// <summary>
    /// Opens a schema-status connection while preserving the status-acquisition observation seams.
    /// </summary>
    /// <param name="cancellationToken">Cancels the pending pool or connection acquisition.</param>
    /// <returns>An open connection owned by the caller.</returns>
    internal async ValueTask<NpgsqlConnection> OpenStatusConnectionAsync(
        CancellationToken cancellationToken)
    {
        var connectionStarted = _observeStatusConnectionAcquisition is null
            ? 0
            : Stopwatch.GetTimestamp();
        var connectionOpen = _dataSource.OpenConnectionAsync(cancellationToken);
        _observeStatusConnectionOpenStarted?.Invoke();
        var connection = await connectionOpen.ConfigureAwait(false);
        try
        {
            if (_observeStatusConnectionAcquisition is { } observer)
            {
                observer(Stopwatch.GetElapsedTime(connectionStarted));
            }

            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Reads schema status through an existing connection so a compatible runtime observation can reuse one pool
    /// acquisition without changing the migration-fence transaction boundary.
    /// </summary>
    /// <param name="connection">An open connection whose credentials are valid for schema status.</param>
    /// <param name="cancellationToken">Cancels the status transaction and commands.</param>
    /// <returns>The installed schema compatibility status.</returns>
    internal async ValueTask<DurableRuntimeSchemaStatus> GetStatusAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await AcquireStatusFenceAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            var status = await ReadStatusAsync(connection, cancellationToken, transaction).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return status;
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The generated script keeps a session-scoped lock because each migration has its own transaction. Lock
    /// acquisition is bounded inside a short transaction, and callers must stop on errors and close the session
    /// if a migration fails before the final explicit unlock.
    /// </remarks>
    public string GenerateScript(int fromVersion = 0)
    {
        if (fromVersion < 0 || fromVersion > _migrations.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(fromVersion), fromVersion, $"Version must be between 0 and {_migrations.Count}.");
        }

        var builder = new StringBuilder();
        builder.AppendLine("-- Generated by ForgeTrust.AppSurface.Durable.PostgreSql.");
        builder.AppendLine("-- Apply with the migration owner. Runtime roles must not execute this script.");
        var lockTimeoutSeconds = _migrationLockAcquireTimeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        var lockRetryDelaySeconds = _migrationLockRetryDelay.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        builder.Append("-- Lock acquisition is bounded to ").Append(lockTimeoutSeconds).AppendLine(" seconds and fails before migration SQL runs if another owner holds it.");
        builder.AppendLine("-- Keep this script in one session. psql callers must pass -v ON_ERROR_STOP=1; closing that session after an error releases the session lock.");
        builder.AppendLine("DO $appsurface_durable$");
        builder.AppendLine("DECLARE");
        builder.Append("    lock_deadline timestamp with time zone := pg_catalog.clock_timestamp() + interval '")
            .Append(lockTimeoutSeconds)
            .AppendLine(" seconds';");
        builder.AppendLine("BEGIN");
        builder.AppendLine("    LOOP");
        builder.Append("        EXIT WHEN pg_catalog.pg_try_advisory_lock(")
            .Append(MigrationAdvisoryLock.ToString(CultureInfo.InvariantCulture))
            .AppendLine(");");
        builder.AppendLine("        IF pg_catalog.clock_timestamp() >= lock_deadline THEN");
        builder.AppendLine("            RAISE EXCEPTION USING");
        builder.AppendLine("                ERRCODE = '55P03',");
        builder.Append("                MESSAGE = 'Timed out after ")
            .Append(lockTimeoutSeconds)
            .Append(" seconds waiting for AppSurface Durable migration advisory lock ")
            .Append(MigrationAdvisoryLock.ToString(CultureInfo.InvariantCulture))
            .AppendLine(". Retry after the active migration owner completes.';");
        builder.AppendLine("        END IF;");
        builder.Append("        PERFORM pg_catalog.pg_sleep(")
            .Append(lockRetryDelaySeconds)
            .AppendLine(");");
        builder.AppendLine("    END LOOP;");
        builder.AppendLine("END");
        builder.AppendLine("$appsurface_durable$;");
        foreach (var migration in _migrations.Where(migration => migration.Version > fromVersion))
        {
            AppendMigrationScript(builder, migration);
        }

        builder.Append("SELECT pg_advisory_unlock(").Append(MigrationAdvisoryLock.ToString(CultureInfo.InvariantCulture)).AppendLine(");");
        return builder.ToString();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Programmatic lock acquisition uses non-blocking polling with a 30-second deadline by default. Cancellation
    /// remains distinct from lock contention; a deadline failure is reported as a <see cref="TimeoutException"/>
    /// with the lock identifier and operator guidance. The lock is released explicitly on every acquired path and
    /// by disposing the owning connection as a final safety net.
    /// </remarks>
    public async ValueTask<DurableRuntimeSchemaApplyResult> ApplyAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await AcquireMigrationLockAsync(connection, cancellationToken).ConfigureAwait(false);
        try
        {
            var before = await ReadStatusAsync(connection, cancellationToken).ConfigureAwait(false);
            if (before.Compatibility is DurableRuntimeSchemaCompatibility.Inconsistent or DurableRuntimeSchemaCompatibility.StoreTooNew)
            {
                throw new DurableRuntimeSchemaException(before);
            }

            var applied = new List<int>();
            foreach (var migration in _migrations.Where(migration => migration.Version > before.InstalledVersion))
            {
                await ApplyMigrationAsync(connection, migration, cancellationToken).ConfigureAwait(false);
                applied.Add(migration.Version);
            }

            var after = await ReadStatusAsync(connection, cancellationToken).ConfigureAwait(false);
            if (!after.IsCompatible)
            {
                throw new DurableRuntimeSchemaException(after);
            }

            return new DurableRuntimeSchemaApplyResult(before.InstalledVersion, after.InstalledVersion, applied);
        }
        finally
        {
            await ReleaseMigrationLockAsync(connection).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask ValidateAsync(CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (!status.IsCompatible)
        {
            throw new DurableRuntimeSchemaException(status);
        }
    }

    /// <inheritdoc />
    public async ValueTask<DurableRuntimeEpochActivationResult> InitializeRuntimeEpochAsync(
        Guid initialEpoch,
        string actorId,
        string reasonCode,
        CancellationToken cancellationToken = default)
    {
        if (initialEpoch == Guid.Empty)
        {
            throw new ArgumentException("The initial runtime epoch must not be empty.", nameof(initialEpoch));
        }

        actorId = RequireOperatorCode(actorId, nameof(actorId), 200);
        reasonCode = RequireOperatorCode(reasonCode, nameof(reasonCode), 120);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await AcquireMigrationLockAsync(connection, cancellationToken).ConfigureAwait(false);
        try
        {
            await ValidateConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var result = await ExecuteEpochMutationAsync(
                    connection,
                    transaction,
                    """
                    WITH changed AS
                    (
                        UPDATE appsurface_durable.store_metadata
                        SET active_runtime_epoch = @new_epoch, updated_at = clock_timestamp()
                        WHERE singleton AND active_runtime_epoch IS NULL
                        RETURNING active_runtime_epoch, updated_at
                    ), historied AS
                    (
                        INSERT INTO appsurface_durable.runtime_epoch_history
                            (previous_epoch, active_epoch, actor_id, reason_code, observed_at)
                        SELECT NULL, active_runtime_epoch, @actor_id, @reason_code, updated_at FROM changed
                        RETURNING active_epoch, observed_at
                    )
                    SELECT active_epoch, observed_at FROM historied;
                    """,
                    initialEpoch,
                    actorId,
                    reasonCode,
                    expectedEpoch: null,
                    cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new DurableRuntimeEpochActivationResult(result.Epoch, result.ObservedAt);
            }
            catch
            {
                await TryRollbackAsync(transaction).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            await ReleaseMigrationLockAsync(connection).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask<DurableRuntimeEpochRotationResult> RotateRuntimeEpochAsync(
        Guid expectedActiveEpoch,
        Guid newActiveEpoch,
        string actorId,
        string reasonCode,
        CancellationToken cancellationToken = default)
    {
        if (expectedActiveEpoch == Guid.Empty)
        {
            throw new ArgumentException("The expected runtime epoch must not be empty.", nameof(expectedActiveEpoch));
        }

        if (newActiveEpoch == Guid.Empty || newActiveEpoch == expectedActiveEpoch)
        {
            throw new ArgumentException("The new runtime epoch must be non-empty and different.", nameof(newActiveEpoch));
        }

        actorId = RequireOperatorCode(actorId, nameof(actorId), 200);
        reasonCode = RequireOperatorCode(reasonCode, nameof(reasonCode), 120);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await AcquireMigrationLockAsync(connection, cancellationToken).ConfigureAwait(false);
        try
        {
            await ValidateConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var result = await ExecuteEpochMutationAsync(
                    connection,
                    transaction,
                    """
                    WITH changed AS
                    (
                        UPDATE appsurface_durable.store_metadata
                        SET active_runtime_epoch = @new_epoch, updated_at = clock_timestamp()
                        WHERE singleton AND active_runtime_epoch = @expected_epoch
                        RETURNING active_runtime_epoch, updated_at
                    ), historied AS
                    (
                        INSERT INTO appsurface_durable.runtime_epoch_history
                            (previous_epoch, active_epoch, actor_id, reason_code, observed_at)
                        SELECT @expected_epoch, active_runtime_epoch, @actor_id, @reason_code, updated_at FROM changed
                        RETURNING active_epoch, observed_at
                    )
                    SELECT active_epoch, observed_at FROM historied;
                    """,
                    newActiveEpoch,
                    actorId,
                    reasonCode,
                    expectedActiveEpoch,
                    cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new DurableRuntimeEpochRotationResult(expectedActiveEpoch, result.Epoch, result.ObservedAt);
            }
            catch
            {
                await TryRollbackAsync(transaction).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            await ReleaseMigrationLockAsync(connection).ConfigureAwait(false);
        }
    }

    private ValueTask ValidateConnectionAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken) =>
        ValidateConnectionCoreAsync(connection, transaction: null, cancellationToken);

    /// <summary>
    /// Validates schema compatibility on the connection and transaction that already hold runtime admission's
    /// migration fence.
    /// </summary>
    internal ValueTask ValidateConnectionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (!ReferenceEquals(transaction.Connection, connection))
        {
            throw new ArgumentException(
                "The schema-validation transaction must belong to the supplied connection.",
                nameof(transaction));
        }

        return ValidateConnectionCoreAsync(connection, transaction, cancellationToken);
    }

    private async ValueTask ValidateConnectionCoreAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var status = await ReadStatusAsync(connection, cancellationToken, transaction).ConfigureAwait(false);
        if (!status.IsCompatible)
        {
            throw new DurableRuntimeSchemaException(status);
        }
    }

    private static async ValueTask<(Guid Epoch, DateTimeOffset ObservedAt)> ExecuteEpochMutationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        Guid newEpoch,
        string actorId,
        string reasonCode,
        Guid? expectedEpoch,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("new_epoch", newEpoch);
        command.Parameters.AddWithValue("actor_id", actorId);
        command.Parameters.AddWithValue("reason_code", reasonCode);
        if (expectedEpoch is { } expected)
        {
            command.Parameters.AddWithValue("expected_epoch", expected);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"{DurableProblemCodes.RecoveryEpochRequired}: The expected epoch state changed; reload deployment status before retrying.");
        }

        return (reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(1));
    }

    private async ValueTask<DurableRuntimeSchemaStatus> ReadStatusAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken,
        NpgsqlTransaction? transaction = null)
    {
        await using var existence = new NpgsqlCommand(
            """
            SELECT to_regnamespace('appsurface_durable') IS NOT NULL,
                   to_regclass('appsurface_durable.schema_migration') IS NOT NULL,
                   to_regclass('appsurface_durable.store_metadata') IS NOT NULL;
            """,
            connection,
            transaction);
        var (schemaExists, historyExists, metadataExists) =
            await PostgreSqlDurableControlPlaneCommand.ExecuteReaderAsync(
                existence,
                static async (reader, effectiveToken) =>
                {
                    if (!await reader.ReadAsync(effectiveToken).ConfigureAwait(false))
                    {
                        throw new InvalidDataException("The durable schema existence query returned no row.");
                    }

                    var result = (reader.GetBoolean(0), reader.GetBoolean(1), reader.GetBoolean(2));
                    if (await reader.ReadAsync(effectiveToken).ConfigureAwait(false))
                    {
                        throw new InvalidDataException("The durable schema existence query returned more than one row.");
                    }

                    return result;
                },
                cancellationToken).ConfigureAwait(false);
        if (!schemaExists && !historyExists && !metadataExists)
        {
            return CreateStatus(DurableRuntimeSchemaCompatibility.Missing, 0, [], "The durable schema is not installed.");
        }

        if (!schemaExists || !historyExists || !metadataExists)
        {
            return CreateStatus(DurableRuntimeSchemaCompatibility.Inconsistent, 0, [], "Schema and migration metadata are incomplete.");
        }

        var applied = new List<AppliedMigration>();
        await using (var command = new NpgsqlCommand(
            "SELECT version, name, sha256 FROM appsurface_durable.schema_migration ORDER BY version;",
            connection,
            transaction))
        {
            _ = await PostgreSqlDurableControlPlaneCommand.ExecuteReaderAsync(
                command,
                async (reader, effectiveToken) =>
                {
                    while (await reader.ReadAsync(effectiveToken).ConfigureAwait(false))
                    {
                        applied.Add(new AppliedMigration(reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
                    }

                    return true;
                },
                cancellationToken).ConfigureAwait(false);
        }

        var installed = applied.Count == 0 ? 0 : applied[^1].Version;
        var integrityProblem = FindIntegrityProblem(applied);
        if (integrityProblem is not null)
        {
            return CreateStatus(DurableRuntimeSchemaCompatibility.Inconsistent, installed, applied.Select(item => item.Version).ToArray(), integrityProblem);
        }

        const string metadataSql = """
            SELECT store_id, active_runtime_epoch, schema_version,
                   minimum_reader_version, maximum_reader_version,
                   minimum_writer_version, maximum_writer_version
            FROM appsurface_durable.store_metadata WHERE singleton;
            """;
        await using var metadata = new NpgsqlCommand(metadataSql, connection, transaction);
        var metadataObservation = await PostgreSqlDurableControlPlaneCommand.ExecuteReaderAsync(
            metadata,
            static async (reader, effectiveToken) =>
            {
                if (!await reader.ReadAsync(effectiveToken).ConfigureAwait(false))
                {
                    return null;
                }

                var result = new MetadataObservation(
                    reader.GetGuid(0),
                    reader.IsDBNull(1) ? null : reader.GetGuid(1),
                    new StoreCompatibilityRange(
                        reader.GetInt32(2),
                        reader.GetInt32(3),
                        reader.GetInt32(4),
                        reader.GetInt32(5),
                        reader.GetInt32(6)));
                if (await reader.ReadAsync(effectiveToken).ConfigureAwait(false))
                {
                    throw new InvalidDataException("The durable store metadata query returned more than one row.");
                }

                return result;
            },
            cancellationToken).ConfigureAwait(false);
        if (metadataObservation is null)
        {
            return CreateStatus(DurableRuntimeSchemaCompatibility.Inconsistent, installed, applied.Select(item => item.Version).ToArray(), "Store metadata is missing.");
        }

        var storeId = metadataObservation.StoreId;
        var activeEpoch = metadataObservation.ActiveRuntimeEpoch;
        var range = metadataObservation.Range;
        if (storeId == Guid.Empty || range.SchemaVersion != installed || !range.IsValid)
        {
            return CreateStatus(DurableRuntimeSchemaCompatibility.Inconsistent, installed, applied.Select(item => item.Version).ToArray(), "Store identity or compatibility metadata is invalid.", storeId, activeEpoch, range);
        }

        var compatibility = installed < _migrations.Count
            ? DurableRuntimeSchemaCompatibility.UpgradeRequired
            : range.Allows(_migrations.Count)
                ? DurableRuntimeSchemaCompatibility.Compatible
                : DurableRuntimeSchemaCompatibility.StoreTooNew;
        var problem = compatibility switch
        {
            DurableRuntimeSchemaCompatibility.Compatible => null,
            DurableRuntimeSchemaCompatibility.UpgradeRequired => "Apply pending package migrations before runtime use.",
            _ => "Upgrade to a package that supports the installed store version.",
        };
        return CreateStatus(compatibility, installed, applied.Select(item => item.Version).ToArray(), problem, storeId, activeEpoch, range);
    }

    private DurableRuntimeSchemaStatus CreateStatus(
        DurableRuntimeSchemaCompatibility compatibility,
        int installedVersion,
        IReadOnlyList<int> appliedVersions,
        string? problem,
        Guid storeId = default,
        Guid? activeEpoch = null,
        StoreCompatibilityRange? range = null) =>
        new(
            compatibility,
            storeId,
            activeEpoch,
            installedVersion,
            _migrations.Count,
            range?.MinimumReaderVersion ?? 0,
            range?.MaximumReaderVersion ?? 0,
            range?.MinimumWriterVersion ?? 0,
            range?.MaximumWriterVersion ?? 0,
            appliedVersions,
            _migrations.Where(migration => migration.Version > installedVersion).Select(migration => migration.Version).ToArray(),
            problem);

    private string? FindIntegrityProblem(IReadOnlyList<AppliedMigration> applied)
    {
        for (var index = 0; index < applied.Count; index++)
        {
            var actual = applied[index];
            if (actual.Version != index + 1)
            {
                return $"Migration history is not contiguous at version {index + 1}.";
            }

            if (actual.Version <= _migrations.Count)
            {
                var expected = _migrations[actual.Version - 1];
                if (!StringComparer.Ordinal.Equals(actual.Name, expected.Name)
                    || !StringComparer.OrdinalIgnoreCase.Equals(actual.Sha256, expected.Sha256))
                {
                    return $"Recorded migration {actual.Version:D4} does not match the package resource.";
                }
            }
        }

        return null;
    }

    private async ValueTask ApplyMigrationAsync(NpgsqlConnection connection, DurablePostgreSqlMigration migration, CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var command = new NpgsqlCommand(migration.Sql, connection, transaction))
            {
                if (migration.CommandTimeoutSeconds is { } commandTimeoutSeconds)
                {
                    if (commandTimeoutSeconds <= 0)
                    {
                        throw new InvalidOperationException(
                            $"Migration {migration.Version:D4} has a non-positive client command timeout.");
                    }

                    // The migration owns this override because its bounded server-side work exceeds the data-source
                    // default. Keep the client alive long enough for PostgreSQL to report the authoritative failure.
                    command.CommandTimeout = commandTimeoutSeconds;
                }

                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using var metadata = new NpgsqlCommand(
                """
                INSERT INTO appsurface_durable.schema_migration (version, name, sha256) VALUES (@version, @name, @sha256);
                UPDATE appsurface_durable.store_metadata
                SET schema_version = @version, minimum_reader_version = 1, maximum_reader_version = @version,
                    minimum_writer_version = 1, maximum_writer_version = @version, updated_at = clock_timestamp()
                WHERE singleton;
                """,
                connection,
                transaction);
            metadata.Parameters.AddWithValue("version", migration.Version);
            metadata.Parameters.AddWithValue("name", migration.Name);
            metadata.Parameters.AddWithValue("sha256", migration.Sha256);
            await metadata.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
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
            // Preserve the migration failure. Transaction disposal and connection health checks own cleanup.
        }
    }

    private static string RequireOperatorCode(string value, string parameterName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength
            || value.Any(static value => !char.IsAsciiLetterOrDigit(value) && value is not '-' and not '_' and not '.' and not ':'))
        {
            throw new ArgumentException("Operator identifiers must use the durable opaque-code grammar.", parameterName);
        }

        return value;
    }

    private async ValueTask AcquireMigrationLockAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        using var deadlineCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadlineCancellation.CancelAfter(_migrationLockAcquireTimeout);
        var lockCancellationToken = deadlineCancellation.Token;
        var started = Stopwatch.GetTimestamp();

        await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@lock_id);", connection)
        {
            CommandTimeout = Math.Max(1, (int)Math.Ceiling(_migrationLockAcquireTimeout.TotalSeconds)),
        };
        command.Parameters.AddWithValue("lock_id", MigrationAdvisoryLock);
        try
        {
            while (true)
            {
                var acquired = await command.ExecuteScalarAsync(lockCancellationToken).ConfigureAwait(false) is true;
                _observeMigrationLockAttempt?.Invoke(acquired);
                if (acquired)
                {
                    return;
                }

                var remaining = _migrationLockAcquireTimeout - Stopwatch.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero)
                {
                    throw CreateMigrationLockTimeoutException();
                }

                await Task.Delay(
                    remaining < _migrationLockRetryDelay ? remaining : _migrationLockRetryDelay,
                    lockCancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadlineCancellation.IsCancellationRequested)
        {
            throw CreateMigrationLockTimeoutException();
        }
    }

    private TimeoutException CreateMigrationLockTimeoutException() => new(
        $"Timed out after {_migrationLockAcquireTimeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} seconds acquiring PostgreSQL migration advisory lock "
        + $"{MigrationAdvisoryLock.ToString(CultureInfo.InvariantCulture)}. Another migration owner may still be applying the schema; retry after it completes or inspect pg_stat_activity for the blocking session.");

    private static TimeSpan RequirePositiveDuration(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero || value == Timeout.InfiniteTimeSpan || value > TimeSpan.FromMilliseconds(int.MaxValue))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "The duration must be positive and no longer than the cancellation-token deadline range.");
        }

        return value;
    }

    private static async ValueTask AcquireStatusFenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock_shared(@lock_id);",
            connection,
            transaction);
        command.Parameters.AddWithValue("lock_id", MigrationAdvisoryLock);
        _ = await PostgreSqlDurableControlPlaneCommand.ExecuteNonQueryAsync(
            command,
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask ReleaseMigrationLockAsync(NpgsqlConnection connection)
    {
        try
        {
            await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(@lock_id);", connection);
            command.Parameters.AddWithValue("lock_id", MigrationAdvisoryLock);
            await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (PostgreSqlDurableExceptionFilters.IsExpectedCleanupFailure(exception))
        {
            // Unlock is best-effort so cleanup never masks the schema or epoch failure. Closing the
            // physical session releases any lock that could not be explicitly released.
        }
    }

    private static void AppendMigrationScript(StringBuilder builder, DurablePostgreSqlMigration migration)
    {
        builder.AppendLine().Append("-- Migration ").Append(migration.Version.ToString("D4", CultureInfo.InvariantCulture)).Append('_').AppendLine(migration.Name);
        builder.AppendLine("BEGIN;").Append(migration.Sql).AppendLine();
        builder.Append("INSERT INTO appsurface_durable.schema_migration (version, name, sha256) VALUES (")
            .Append(migration.Version.ToString(CultureInfo.InvariantCulture)).Append(", '").Append(EscapeSqlLiteral(migration.Name))
            .Append("', '").Append(EscapeSqlLiteral(migration.Sha256)).AppendLine("');");
        builder.Append("UPDATE appsurface_durable.store_metadata SET schema_version = ")
            .Append(migration.Version.ToString(CultureInfo.InvariantCulture))
            .Append(", minimum_reader_version = 1, maximum_reader_version = ").Append(migration.Version.ToString(CultureInfo.InvariantCulture))
            .Append(", minimum_writer_version = 1, maximum_writer_version = ").Append(migration.Version.ToString(CultureInfo.InvariantCulture))
            .AppendLine(", updated_at = clock_timestamp() WHERE singleton;")
            .AppendLine("COMMIT;");
    }

    private static string EscapeSqlLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private sealed record AppliedMigration(int Version, string Name, string Sha256);

    private sealed record MetadataObservation(
        Guid StoreId,
        Guid? ActiveRuntimeEpoch,
        StoreCompatibilityRange Range);

    private sealed record StoreCompatibilityRange(
        int SchemaVersion,
        int MinimumReaderVersion,
        int MaximumReaderVersion,
        int MinimumWriterVersion,
        int MaximumWriterVersion)
    {
        internal bool IsValid => SchemaVersion > 0 && MinimumReaderVersion > 0
            && MaximumReaderVersion >= MinimumReaderVersion && MaximumReaderVersion <= SchemaVersion
            && MinimumWriterVersion > 0 && MaximumWriterVersion >= MinimumWriterVersion && MaximumWriterVersion <= SchemaVersion;

        internal bool Allows(int runtimeVersion) => runtimeVersion >= MinimumReaderVersion && runtimeVersion <= MaximumReaderVersion
            && runtimeVersion >= MinimumWriterVersion && runtimeVersion <= MaximumWriterVersion;
    }
}
