namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>Provides explicit deployment operations for the AppSurface durable PostgreSQL schema.</summary>
/// <remarks>Use a migration-owner data source. Runtime registration may validate status but must never apply DDL.</remarks>
public interface IDurableRuntimeSchemaManager
{
    /// <summary>Reads installed migration metadata without modifying the database.</summary>
    ValueTask<DurableRuntimeSchemaStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Generates deterministic SQL for migrations newer than the exact reviewed <paramref name="fromVersion"/>.</summary>
    /// <remarks>The result is forward-only and is not safe to rerun after any selected migration commits.</remarks>
    string GenerateScript(int fromVersion = 0);

    /// <summary>Applies pending migrations while holding the package session advisory lock.</summary>
    ValueTask<DurableRuntimeSchemaApplyResult> ApplyAsync(CancellationToken cancellationToken = default);

    /// <summary>Fails when the installed schema cannot be used without changing it.</summary>
    /// <exception cref="DurableRuntimeSchemaException">The schema is not compatible.</exception>
    ValueTask ValidateAsync(CancellationToken cancellationToken = default);

    /// <summary>Activates the first non-empty store recovery epoch exactly once.</summary>
    /// <param name="initialEpoch">Deployment-selected initial epoch.</param>
    /// <param name="actorId">Privacy-safe operator identity.</param>
    /// <param name="reasonCode">Privacy-safe activation reason.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<DurableRuntimeEpochActivationResult> InitializeRuntimeEpochAsync(
        Guid initialEpoch,
        string actorId,
        string reasonCode,
        CancellationToken cancellationToken = default);

    /// <summary>Atomically rotates the recovery epoch when the expected epoch remains active.</summary>
    ValueTask<DurableRuntimeEpochRotationResult> RotateRuntimeEpochAsync(
        Guid expectedActiveEpoch,
        Guid newActiveEpoch,
        string actorId,
        string reasonCode,
        CancellationToken cancellationToken = default);
}
