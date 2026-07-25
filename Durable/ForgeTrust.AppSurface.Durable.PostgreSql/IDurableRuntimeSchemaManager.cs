namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>Provides explicit deployment operations for the AppSurface durable PostgreSQL schema.</summary>
/// <remarks>Use a migration-owner data source. Runtime registration may validate status but must never apply DDL.</remarks>
public interface IDurableRuntimeSchemaManager
{
    /// <summary>Reads installed migration metadata without modifying the database.</summary>
    /// <param name="cancellationToken">Token that cancels the database read.</param>
    /// <returns>An immutable status snapshot. Missing and incompatible schemas are returned as status, not exceptions.</returns>
    /// <exception cref="Npgsql.NpgsqlException">PostgreSQL rejects the read or the connection fails.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled.</exception>
    ValueTask<DurableRuntimeSchemaStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Generates deterministic SQL for migrations newer than the exact reviewed <paramref name="fromVersion"/>.</summary>
    /// <remarks>The result is forward-only and is not safe to rerun after any selected migration commits.</remarks>
    /// <param name="fromVersion">Last installed migration version, from zero through <see cref="PostgreSqlDurableRuntimeSchemaManager.RequiredVersion"/>.</param>
    /// <returns>A migration-owner script that acquires and releases the package advisory lock.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="fromVersion"/> is outside the supported range.</exception>
    string GenerateScript(int fromVersion = 0);

    /// <summary>Applies pending migrations while holding the package session advisory lock.</summary>
    /// <param name="cancellationToken">Token that cancels lock acquisition or migration application.</param>
    /// <returns>The version range observed before and after application and the ordered versions applied by this call.</returns>
    /// <exception cref="DurableRuntimeSchemaException">The installed schema is inconsistent or newer than this package.</exception>
    /// <exception cref="Npgsql.NpgsqlException">PostgreSQL rejects a migration or the connection fails.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled.</exception>
    ValueTask<DurableRuntimeSchemaApplyResult> ApplyAsync(CancellationToken cancellationToken = default);

    /// <summary>Fails when the installed schema cannot be used without changing it.</summary>
    /// <param name="cancellationToken">Token that cancels the database read.</param>
    /// <returns>A task that completes when the installed schema is compatible.</returns>
    /// <exception cref="DurableRuntimeSchemaException">The schema is not compatible.</exception>
    /// <exception cref="Npgsql.NpgsqlException">PostgreSQL rejects the read or the connection fails.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled.</exception>
    ValueTask ValidateAsync(CancellationToken cancellationToken = default);

    /// <summary>Activates the first non-empty store recovery epoch exactly once.</summary>
    /// <param name="initialEpoch">Deployment-selected initial epoch.</param>
    /// <param name="actorId">Privacy-safe operator code of 1-200 ASCII letters, digits, <c>-</c>, <c>_</c>, <c>.</c>, or <c>:</c>.</param>
    /// <param name="reasonCode">Privacy-safe activation code of 1-120 ASCII letters, digits, <c>-</c>, <c>_</c>, <c>.</c>, or <c>:</c>.</param>
    /// <param name="cancellationToken">Token that cancels lock acquisition or activation.</param>
    /// <returns>The activated epoch and database observation time.</returns>
    /// <exception cref="ArgumentException">The epoch is empty, or an operator code is empty, too long, or outside the opaque-code grammar.</exception>
    /// <exception cref="DurableRuntimeSchemaException">The installed schema is not compatible.</exception>
    /// <exception cref="InvalidOperationException">A runtime epoch is already active.</exception>
    /// <exception cref="Npgsql.NpgsqlException">PostgreSQL rejects the mutation or the connection fails.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled.</exception>
    ValueTask<DurableRuntimeEpochActivationResult> InitializeRuntimeEpochAsync(
        Guid initialEpoch,
        string actorId,
        string reasonCode,
        CancellationToken cancellationToken = default);

    /// <summary>Atomically rotates the recovery epoch when the expected epoch remains active.</summary>
    /// <param name="expectedActiveEpoch">Epoch that must still be active when the mutation commits.</param>
    /// <param name="newActiveEpoch">Distinct, non-empty replacement epoch.</param>
    /// <param name="actorId">Privacy-safe operator code of 1-200 ASCII letters, digits, <c>-</c>, <c>_</c>, <c>.</c>, or <c>:</c>.</param>
    /// <param name="reasonCode">Privacy-safe rotation code of 1-120 ASCII letters, digits, <c>-</c>, <c>_</c>, <c>.</c>, or <c>:</c>.</param>
    /// <param name="cancellationToken">Token that cancels lock acquisition or rotation.</param>
    /// <returns>The previous and active epochs and database observation time.</returns>
    /// <exception cref="ArgumentException">An epoch is empty, the epochs match, or an operator code violates its bounds or grammar.</exception>
    /// <exception cref="DurableRuntimeSchemaException">The installed schema is not compatible.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="expectedActiveEpoch"/> is no longer active.</exception>
    /// <exception cref="Npgsql.NpgsqlException">PostgreSQL rejects the mutation or the connection fails.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled.</exception>
    ValueTask<DurableRuntimeEpochRotationResult> RotateRuntimeEpochAsync(
        Guid expectedActiveEpoch,
        Guid newActiveEpoch,
        string actorId,
        string reasonCode,
        CancellationToken cancellationToken = default);
}
