namespace ForgeTrust.AppSurface.Config.LocalSecrets;

/// <summary>
/// Describes one value-safe local-secret migration outcome.
/// </summary>
/// <param name="Key">The logical LocalSecrets key. Secret values are never included.</param>
/// <param name="Action">The completed or failed migration action.</param>
/// <param name="Status">The store status for this key.</param>
/// <param name="Diagnostic">The display-safe diagnostic when the action failed.</param>
public sealed record AppSurfaceLocalSecretMigrationRow(
    string Key,
    AppSurfaceLocalSecretMigrationAction Action,
    LocalSecretResultStatus Status,
    AppSurfaceLocalSecretDiagnostic? Diagnostic);

/// <summary>
/// Identifies the action taken for one LocalSecrets migration row.
/// </summary>
/// <remarks>
/// Use <see cref="AppSurfaceLocalSecretMigrationActionExtensions.ToDisplayString(AppSurfaceLocalSecretMigrationAction)"/>
/// when rendering an action for an operator. The text remains stable so command output is compatible with the initial
/// macOS v2 migration workflow.
/// </remarks>
public enum AppSurfaceLocalSecretMigrationAction
{
    /// <summary>A retained legacy record was copied and verified in the v2 namespace.</summary>
    Migrated = 0,

    /// <summary>A v2 record already existed and was preserved without overwrite.</summary>
    AlreadyV2 = 1,

    /// <summary>The row could not be migrated safely.</summary>
    Failed = 2,
}

/// <summary>
/// Provides display-safe text for <see cref="AppSurfaceLocalSecretMigrationAction"/> values.
/// </summary>
public static class AppSurfaceLocalSecretMigrationActionExtensions
{
    /// <summary>Returns the stable text rendered by the LocalSecrets migration command.</summary>
    /// <param name="action">The migration action to render.</param>
    /// <returns>The stable action text, or the enum fallback text for an action introduced by a newer implementation.</returns>
    public static string ToDisplayString(this AppSurfaceLocalSecretMigrationAction action) =>
        action switch
        {
            AppSurfaceLocalSecretMigrationAction.Migrated => "Migrated",
            AppSurfaceLocalSecretMigrationAction.AlreadyV2 => "AlreadyV2",
            AppSurfaceLocalSecretMigrationAction.Failed => "Failed",
            _ => action.ToString(),
        };
}

/// <summary>
/// Describes migration of one LocalSecrets namespace without exposing secret values.
/// </summary>
/// <param name="Status">The overall migration status.</param>
/// <param name="Rows">Value-safe per-key outcomes.</param>
/// <param name="Diagnostic">A display-safe namespace-level failure diagnostic when migration could not start.</param>
/// <param name="Source">The display-safe store name.</param>
public sealed record AppSurfaceLocalSecretMigrationResult(
    LocalSecretResultStatus Status,
    IReadOnlyList<AppSurfaceLocalSecretMigrationRow> Rows,
    AppSurfaceLocalSecretDiagnostic? Diagnostic,
    string Source)
{
    /// <summary>Gets the number of records copied into current storage.</summary>
    public int Migrated => Rows.Count(static row => row.Action == AppSurfaceLocalSecretMigrationAction.Migrated);

    /// <summary>Gets the number of records already present in current storage.</summary>
    public int AlreadyV2 => Rows.Count(static row => row.Action == AppSurfaceLocalSecretMigrationAction.AlreadyV2);

    /// <summary>Gets the number of records that could not be migrated.</summary>
    public int Failed => Rows.Count(static row => row.Status != LocalSecretResultStatus.Found);

    /// <summary>Creates a completed migration result.</summary>
    /// <param name="rows">Value-safe per-key outcomes.</param>
    /// <param name="source">The display-safe store name.</param>
    /// <returns>The migration result.</returns>
    public static AppSurfaceLocalSecretMigrationResult Completed(
        IEnumerable<AppSurfaceLocalSecretMigrationRow> rows,
        string source) =>
        new(
            LocalSecretResultStatus.Found,
            rows.OrderBy(static row => row.Key, StringComparer.OrdinalIgnoreCase).ThenBy(static row => row.Key, StringComparer.Ordinal).ToArray(),
            null,
            source);

    /// <summary>Creates a migration result that could not start safely.</summary>
    /// <param name="status">The terminal status.</param>
    /// <param name="diagnostic">The display-safe diagnostic.</param>
    /// <param name="source">The display-safe store name.</param>
    /// <returns>The failed migration result.</returns>
    public static AppSurfaceLocalSecretMigrationResult FailedToStart(
        LocalSecretResultStatus status,
        AppSurfaceLocalSecretDiagnostic diagnostic,
        string source)
    {
        if (status == LocalSecretResultStatus.Found)
        {
            throw new ArgumentException("A migration that could not start cannot have a found status.", nameof(status));
        }

        return new(status, [], diagnostic, source);
    }
}
