using System.Runtime.ExceptionServices;
using ForgeTrust.AppSurface.Durable.PostgreSql;
using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

internal sealed partial class PostgreSqlIntegrationTestDatabase
{
    /// <summary>Applies the embedded migrations and verifies a virgin, inactive template database.</summary>
    /// <param name="cancellationToken">Cancels migration or template validation before clone creation.</param>
    /// <returns>The store ID copied into databases created from this template.</returns>
    /// <exception cref="PostgreSqlFixtureException">The database is not a live template or its schema is not ready.</exception>
    internal async ValueTask<Guid> PrepareTemplateAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var identityVerified = false;
        try
        {
            ObjectDisposedException.ThrowIf(_closing || _disposed, this);
            if (!_isTemplate)
            {
                throw new PostgreSqlFixtureException("Template preparation requires a registered template database.");
            }

            var manager = new PostgreSqlDurableRuntimeSchemaManager(DataSource);
            await using (var maintenance = new NpgsqlConnection(_maintenanceConnectionString))
            {
                await maintenance.OpenAsync(cancellationToken).ConfigureAwait(false);
                var databaseExists = await _lease.ExistsAsync(maintenance, cancellationToken).ConfigureAwait(false);
                if (!databaseExists)
                {
                    throw new PostgreSqlFixtureException("The registered template database is absent before preparation. Preserve the disposable server state.");
                }
            }

            await using var connection = await DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using (var identityTransaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await VerifyConnectedIdentityAsync(connection, identityTransaction, cancellationToken).ConfigureAwait(false);
                    await identityTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    await RollbackAsync(identityTransaction, error).ConfigureAwait(false);
                    throw;
                }
            }

            identityVerified = true;
            await manager.ApplyAsync(cancellationToken).ConfigureAwait(false);

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await AcquireExclusiveMigrationLockAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                await VerifyConnectedIdentityAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                await DisableRowSecurityFilteringAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

                var status = await manager.ReadStatusInTransactionAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                var metadata = await ReadMetadataAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                RequireCurrentCompatibleSchema(status, metadata);
                if (metadata.ActiveRuntimeEpoch is not null)
                {
                    throw new PostgreSqlFixtureException("Template metadata already has an active runtime epoch.");
                }

                await RequireDurableTablesEmptyAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                if (_preparedTemplateStoreId is { } preparedStoreId && preparedStoreId != status.StoreId)
                {
                    throw new PostgreSqlFixtureException("The registered template store identity changed after preparation.");
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                _preparedTemplateStoreId = status.StoreId;
                return status.StoreId;
            }
            catch (Exception error)
            {
                await RollbackAsync(transaction, error).ConfigureAwait(false);
                throw;
            }
        }
        catch (Exception error) when (error is not (PostgreSqlFixtureException or OperationCanceledException))
        {
            var operation = identityVerified ? "Template preparation" : "Template identity verification";
            var action = identityVerified
                ? "Verify migration-owner access and an empty, inactive schema."
                : "Preserve the registered resource and reconcile its identity with the disposable-server owner.";
            throw new PostgreSqlFixtureException($"{operation} failed ({PostgreSqlFixtureException.Describe(error)}). {action}");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>Validates a copied template, rekeys the clone, and initializes its first runtime epoch.</summary>
    /// <remarks>
    /// The optional commit and data-source overrides support regression tests for ambiguous commit acknowledgements
    /// and restricted-role validation. A supplied data source must target this owned database. A commit callback that
    /// returns indicates confirmed success, while a callback failure is treated as an uncertain commit outcome and
    /// triggers owned-clone cleanup.
    /// </remarks>
    /// <param name="expectedTemplateStoreId">The store ID returned by <see cref="PrepareTemplateAsync"/>.</param>
    /// <param name="epoch">The fresh runtime epoch to activate for the clone.</param>
    /// <param name="actor">The opaque actor code recorded with epoch initialization.</param>
    /// <param name="reason">The opaque reason code recorded with epoch initialization.</param>
    /// <param name="afterRekey">Optional fault-injection callback invoked after the rekey commit and before epoch activation.</param>
    /// <param name="cancellationToken">Cancels validation and activation. A post-rekey failure triggers owned-clone cleanup.</param>
    /// <param name="commitRekey">Optional test callback that replaces the rekey commit operation.</param>
    /// <param name="activationDataSource">Optional data source used for validation and activation tests.</param>
    /// <returns>The verified, compatible status containing the clone's new store ID and active epoch.</returns>
    /// <exception cref="PostgreSqlFixtureException">The clone identity, migration history, metadata, visibility, or state is invalid.</exception>
    internal async ValueTask<DurableRuntimeSchemaStatus> ActivateCloneAsync(
        Guid expectedTemplateStoreId,
        Guid epoch,
        string actor,
        string reason,
        Func<CancellationToken, ValueTask>? afterRekey = null,
        CancellationToken cancellationToken = default,
        Func<NpgsqlTransaction, CancellationToken, ValueTask>? commitRekey = null,
        NpgsqlDataSource? activationDataSource = null)
    {
        if (expectedTemplateStoreId == Guid.Empty)
        {
            throw new ArgumentException("The expected template store ID must not be empty.", nameof(expectedTemplateStoreId));
        }

        if (epoch == Guid.Empty)
        {
            throw new ArgumentException("The runtime epoch must not be empty.", nameof(epoch));
        }

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Exception? postCommitFailure = null;
        DurableRuntimeSchemaStatus? activatedStatus = null;
        var rekeyCommitAttempted = false;
        try
        {
            ObjectDisposedException.ThrowIf(_closing || _disposed, this);
            if (_isTemplate || _copiedTemplateStoreId is null)
            {
                throw new PostgreSqlFixtureException("Clone activation requires a registered clone database.");
            }

            if (_copiedTemplateStoreId is not { } copiedTemplateStoreId || copiedTemplateStoreId != expectedTemplateStoreId)
            {
                throw new PostgreSqlFixtureException("The supplied template store ID does not match this clone's registered source identity.");
            }

            var validationDataSource = activationDataSource ?? DataSource;
            var manager = new PostgreSqlDurableRuntimeSchemaManager(validationDataSource);
            Guid newStoreId;
            await using (var connection = await validationDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
            await using (var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await AcquireExclusiveMigrationLockAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                    await VerifyConnectedIdentityAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                    await DisableRowSecurityFilteringAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

                    var status = await manager.ReadStatusInTransactionAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                    var metadata = await ReadMetadataAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                    RequireCurrentCompatibleSchema(status, metadata);
                    if (status.StoreId != copiedTemplateStoreId)
                    {
                        throw new PostgreSqlFixtureException("Clone metadata does not contain the registered source template store ID.");
                    }

                    if (metadata.ActiveRuntimeEpoch is not null)
                    {
                        throw new PostgreSqlFixtureException("Clone metadata already has an active runtime epoch.");
                    }

                    await RequireDurableTablesEmptyAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

                    newStoreId = await RekeyCloneAsync(
                        connection,
                        transaction,
                        copiedTemplateStoreId,
                        cancellationToken).ConfigureAwait(false);
                    if (newStoreId == Guid.Empty || newStoreId == copiedTemplateStoreId)
                    {
                        throw new PostgreSqlFixtureException("Clone rekey did not produce a distinct, nonempty store ID.");
                    }

                    rekeyCommitAttempted = true;
                    if (commitRekey is null)
                    {
                        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await commitRekey(transaction, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception error) when (!rekeyCommitAttempted)
                {
                    await RollbackAsync(transaction, error).ConfigureAwait(false);
                    throw;
                }
            }

            if (afterRekey is not null)
            {
                await afterRekey(cancellationToken).ConfigureAwait(false);
            }

            var activation = await manager.InitializeRuntimeEpochAsync(
                epoch,
                actor,
                reason,
                cancellationToken).ConfigureAwait(false);
            if (activation.ActiveEpoch != epoch)
            {
                throw new PostgreSqlFixtureException("Runtime epoch initialization returned a different epoch.");
            }

            activatedStatus = await manager.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (!activatedStatus.IsCompatible
                || activatedStatus.InstalledVersion != activatedStatus.RequiredVersion
                || activatedStatus.PendingVersions.Count != 0
                || activatedStatus.StoreId != newStoreId
                || activatedStatus.ActiveRuntimeEpoch != epoch)
            {
                throw new PostgreSqlFixtureException("Clone identity or runtime epoch failed post-commit verification.");
            }
        }
        catch (Exception error) when (rekeyCommitAttempted)
        {
            _closing = true;
            postCommitFailure = error;
        }
        catch (Exception error) when (error is not (PostgreSqlFixtureException or OperationCanceledException))
        {
            throw new PostgreSqlFixtureException(
                $"Clone schema validation failed ({PostgreSqlFixtureException.Describe(error)}). Confirm migration checksums, metadata, visibility, and empty Durable tables.");
        }
        finally
        {
            _lifecycleGate.Release();
        }

        if (postCommitFailure is not null)
        {
            try
            {
                await DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupError)
            {
                throw new PostgreSqlFixtureException(
                    $"Clone activation failed after rekey ({PostgreSqlFixtureException.Describe(postCommitFailure)}). " +
                    $"Owned-clone cleanup failed ({PostgreSqlFixtureException.Describe(cleanupError)}). Reconcile the dedicated server.");
            }

            if (postCommitFailure is OperationCanceledException)
            {
                ExceptionDispatchInfo.Capture(postCommitFailure).Throw();
            }

            throw new PostgreSqlFixtureException(
                $"Clone activation failed after rekey ({PostgreSqlFixtureException.Describe(postCommitFailure)}). The owned clone was removed.");
        }

        return activatedStatus!;
    }

    private static async ValueTask AcquireExclusiveMigrationLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_catalog.pg_advisory_xact_lock(@lock_id);",
            connection,
            transaction);
        command.Parameters.AddWithValue("lock_id", PostgreSqlDurableRuntimeSchemaManager.MigrationAdvisoryLock);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask DisableRowSecurityFilteringAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SET LOCAL row_security = off;", connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<MetadataIdentity> ReadMetadataAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT store_id, active_runtime_epoch FROM appsurface_durable.store_metadata WHERE singleton;",
            connection,
            transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var count = 0;
        Guid storeId = default;
        Guid? activeRuntimeEpoch = null;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            count++;
            if (count == 1)
            {
                storeId = reader.GetGuid(0);
                activeRuntimeEpoch = reader.IsDBNull(1) ? null : reader.GetGuid(1);
            }
        }

        if (count != 1)
        {
            throw new PostgreSqlFixtureException("Durable store metadata must contain exactly one singleton row.");
        }

        return new MetadataIdentity(storeId, activeRuntimeEpoch);
    }

    private static void RequireCurrentCompatibleSchema(DurableRuntimeSchemaStatus status, MetadataIdentity metadata)
    {
        if (!status.IsCompatible
            || status.InstalledVersion != status.RequiredVersion
            || status.PendingVersions.Count != 0
            || status.StoreId == Guid.Empty
            || status.StoreId != metadata.StoreId
            || status.ActiveRuntimeEpoch != metadata.ActiveRuntimeEpoch)
        {
            throw new PostgreSqlFixtureException(
                $"Durable schema is not at its current compatible version ({status.Compatibility}). Verify migrations and singleton metadata.");
        }
    }

    private static async ValueTask RequireDurableTablesEmptyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var relations = new List<string>();
        await using (var enumerate = new NpgsqlCommand(
            """
            SELECT relation.relname
            FROM pg_catalog.pg_class AS relation
            JOIN pg_catalog.pg_namespace AS schema ON schema.oid = relation.relnamespace
            WHERE schema.nspname = 'appsurface_durable'
              AND relation.relkind IN ('r', 'p')
              AND relation.relname NOT IN ('schema_migration', 'store_metadata')
            ORDER BY relation.relname;
            """,
            connection,
            transaction))
        await using (var reader = await enumerate.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                relations.Add(reader.GetString(0));
            }
        }

        using var builder = new NpgsqlCommandBuilder();
        foreach (var relation in relations)
        {
            var quotedRelation = builder.QuoteIdentifier(relation);
            var qualifiedName = $"\"appsurface_durable\".{quotedRelation}";
            await using var probe = new NpgsqlCommand(
                $"SELECT EXISTS (SELECT 1 FROM {qualifiedName} LIMIT 1);",
                connection,
                transaction);
            bool containsRows;
            try
            {
                containsRows = (bool)(await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
            }
            catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.InsufficientPrivilege)
            {
                throw new PostgreSqlFixtureException(
                    "Clone source contains Durable runtime rows or row security prevents a complete emptiness check. Rebuild it from a virgin migrated template.");
            }

            if (containsRows)
            {
                throw new PostgreSqlFixtureException("Clone source contains Durable runtime rows. Rebuild it from a virgin migrated template.");
            }
        }
    }

    private static async ValueTask<Guid> RekeyCloneAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid expectedTemplateStoreId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE appsurface_durable.store_metadata
            SET store_id = DEFAULT
            WHERE singleton
              AND store_id = @expected_store_id
              AND active_runtime_epoch IS NULL
            RETURNING store_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("expected_store_id", expectedTemplateStoreId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new PostgreSqlFixtureException("The conditional clone rekey did not update exactly one virgin metadata row.");
        }

        var storeId = reader.GetGuid(0);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new PostgreSqlFixtureException("The conditional clone rekey updated multiple metadata rows.");
        }

        return storeId;
    }

    private static async ValueTask RollbackAsync(NpgsqlTransaction transaction, Exception originalError)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception rollbackError)
        {
            throw new PostgreSqlFixtureException(
                $"Schema validation failed ({PostgreSqlFixtureException.Describe(originalError)}). " +
                $"Transaction rollback failed ({PostgreSqlFixtureException.Describe(rollbackError)}). Inspect the owned database before reuse.");
        }
    }

    private sealed record MetadataIdentity(Guid StoreId, Guid? ActiveRuntimeEpoch);
}
