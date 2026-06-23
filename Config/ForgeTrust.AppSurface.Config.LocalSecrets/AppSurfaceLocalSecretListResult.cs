namespace ForgeTrust.AppSurface.Config.LocalSecrets;

/// <summary>
/// Describes a local secret list operation.
/// </summary>
/// <param name="Status">The list status.</param>
/// <param name="Keys">The display-safe logical config keys that the store could verify as currently retrievable.</param>
/// <param name="Diagnostic">The display-safe diagnostic for non-success states.</param>
/// <param name="Source">The display-safe source name.</param>
public sealed record AppSurfaceLocalSecretListResult(
    LocalSecretResultStatus Status,
    IReadOnlyList<string> Keys,
    AppSurfaceLocalSecretDiagnostic? Diagnostic,
    string Source)
{
    /// <summary>
    /// Creates a successful list result.
    /// </summary>
    /// <param name="keys">The display-safe logical config keys that the store could verify as currently retrievable.</param>
    /// <param name="source">The display-safe source name.</param>
    /// <returns>A list result.</returns>
    public static AppSurfaceLocalSecretListResult Found(IEnumerable<string> keys, string source) =>
        new(
            LocalSecretResultStatus.Found,
            keys.OrderBy(static key => key, StringComparer.OrdinalIgnoreCase).ThenBy(static key => key, StringComparer.Ordinal).ToArray(),
            null,
            source);

    /// <summary>
    /// Creates a non-success list result.
    /// </summary>
    /// <param name="status">The non-success status.</param>
    /// <param name="diagnostic">The display-safe diagnostic.</param>
    /// <param name="source">The display-safe source name.</param>
    /// <returns>A list result.</returns>
    public static AppSurfaceLocalSecretListResult Failed(
        LocalSecretResultStatus status,
        AppSurfaceLocalSecretDiagnostic diagnostic,
        string source) =>
        new(status, [], diagnostic, source);
}
