namespace ForgeTrust.AppSurface.Config.LocalSecrets;

/// <summary>Describes a value-free file migration recovery operation.</summary>
/// <param name="Status">Whether the requested recovery operation succeeded.</param>
/// <param name="MigrationId">The exact journal identifier supplied by the operator.</param>
/// <param name="State">The saved progress state of the original migration, retained after a recovery transition for diagnosis.</param>
/// <param name="SourceStoredKey">The exact source identifier, never its value.</param>
/// <param name="DestinationStoredKey">The exact destination identifier, never its value.</param>
/// <param name="SourcePresent">Whether the exact source currently exists in the journal namespace.</param>
/// <param name="DestinationPresent">Whether the exact destination currently exists in the journal namespace.</param>
/// <param name="Retained">Whether the journal is retained as unresolved recovery evidence.</param>
/// <param name="Diagnostic">A display-safe failure, if any.</param>
public sealed record AppSurfaceLocalSecretMigrationRecoveryResult(
    LocalSecretResultStatus Status,
    string MigrationId,
    AppSurfaceLocalSecretMigrationState? State,
    string? SourceStoredKey,
    string? DestinationStoredKey,
    bool? SourcePresent,
    bool? DestinationPresent,
    bool Retained,
    AppSurfaceLocalSecretDiagnostic? Diagnostic);

/// <summary>Offers explicit, value-preserving recovery for stores with a shared exact-key migration journal.</summary>
/// <remarks>
/// Preview is read-only. Retain durably protects the journal's exact identifiers while freeing its active slot;
/// release lifts that protection only after operator reconciliation. Neither operation changes secret values.
/// Stores without a shared journal need not implement this optional capability.
/// </remarks>
public interface IAppSurfaceLocalSecretMigrationRecoveryStore
{
    /// <summary>Previews, retains, or releases one journal under the shared maintenance lease.</summary>
    /// <param name="applicationName">The pinned application namespace.</param>
    /// <param name="environment">The target environment namespace.</param>
    /// <param name="keyPrefix">The optional namespace prefix.</param>
    /// <param name="migrationId">The exact journal identifier to compare under the lease.</param>
    /// <param name="apply">Whether to perform a durable transition after preview.</param>
    /// <param name="release">Whether to release already-retained protection after reconciliation.</param>
    /// <param name="expectedState">The state shown by preview; required for apply so a changed journal cannot be acted on.</param>
    /// <returns>Value-free journal identities and presence, or a display-safe failure.</returns>
    AppSurfaceLocalSecretMigrationRecoveryResult RecoverKeyMigration(
        string applicationName, string environment, string? keyPrefix, string migrationId, bool apply, bool release,
        AppSurfaceLocalSecretMigrationState? expectedState = null);
}
