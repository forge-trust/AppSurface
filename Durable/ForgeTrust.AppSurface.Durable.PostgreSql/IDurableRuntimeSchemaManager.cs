namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>
/// Provides explicit deployment operations for the AppSurface durable PostgreSQL schema.
/// </summary>
/// <remarks>
/// Runtime service registration must call <see cref="ValidateAsync"/> only. Applying migrations is a deployment
/// operation and requires the migration owner's connection; it is never performed implicitly during startup.
/// </remarks>
public interface IDurableRuntimeSchemaManager
{
    /// <summary>
    /// Reads installed migration metadata without modifying the database.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The compatibility status.</returns>
    ValueTask<DurableRuntimeSchemaStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates a deterministic SQL script containing migrations newer than <paramref name="fromVersion"/>.
    /// </summary>
    /// <param name="fromVersion">Last migration already installed, or zero for a new database.</param>
    /// <returns>A deployment script protected by the package advisory lock.</returns>
    string GenerateScript(int fromVersion = 0);

    /// <summary>
    /// Applies pending numbered migrations while holding the package advisory lock.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The applied version range.</returns>
    ValueTask<DurableRuntimeSchemaApplyResult> ApplyAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies that the current store can be used, without creating or changing schema objects.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the store is compatible.</returns>
    /// <exception cref="DurableRuntimeSchemaException">The schema is not compatible.</exception>
    ValueTask ValidateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically rotates the store-wide recovery epoch after all old workers have been stopped.
    /// </summary>
    /// <param name="expectedActiveEpoch">Epoch currently recorded by the store.</param>
    /// <param name="newActiveEpoch">New non-empty epoch configured on the replacement fleet.</param>
    /// <param name="actorId">Authorized privacy-safe operator identity.</param>
    /// <param name="reasonCode">Privacy-safe recovery reason code.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The committed epoch rotation.</returns>
    /// <remarks>
    /// Use a deployment/operator data source authorized to update store metadata and append epoch history. Runtime
    /// application roles should only execute the one-time bootstrap function and cannot call this operation.
    /// </remarks>
    ValueTask<DurableRuntimeEpochRotationResult> RotateRuntimeEpochAsync(
        Guid expectedActiveEpoch,
        Guid newActiveEpoch,
        string actorId,
        string reasonCode,
        CancellationToken cancellationToken = default);
}
