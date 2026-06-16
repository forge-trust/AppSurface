namespace ForgeTrust.AppSurface.Config.LocalSecrets;

/// <summary>
/// Describes the result of a local secret store operation.
/// </summary>
/// <param name="Status">The operation status.</param>
/// <param name="Value">The raw secret value only when <paramref name="Status"/> is <see cref="LocalSecretResultStatus.Found"/>.</param>
/// <param name="Diagnostic">The display-safe diagnostic for non-success states.</param>
/// <param name="Source">The display-safe source name that handled the operation.</param>
public sealed record AppSurfaceLocalSecretResult(
    LocalSecretResultStatus Status,
    string? Value,
    AppSurfaceLocalSecretDiagnostic? Diagnostic,
    string Source)
{
    /// <summary>
    /// Creates a found result.
    /// </summary>
    /// <param name="value">The raw secret value.</param>
    /// <param name="source">The display-safe source name.</param>
    /// <returns>A found result.</returns>
    public static AppSurfaceLocalSecretResult Found(string value, string source) =>
        new(LocalSecretResultStatus.Found, value, null, source);

    /// <summary>
    /// Creates a missing result that may fall through to lower-priority providers.
    /// </summary>
    /// <param name="source">The display-safe source name.</param>
    /// <returns>A missing result.</returns>
    public static AppSurfaceLocalSecretResult Missing(string source) =>
        new(
            LocalSecretResultStatus.Missing,
            null,
            new AppSurfaceLocalSecretDiagnostic(
                "local-secret-missing",
                "Local secret was not found.",
                "No value exists for the requested local secret identity.",
                "Set the secret with `appsurface secrets set` or use a higher-priority provider such as an environment variable.",
                "local-secrets-without-a-remote-vault"),
            source);

    /// <summary>
    /// Creates a non-found result with a display-safe diagnostic.
    /// </summary>
    /// <param name="status">The non-found status.</param>
    /// <param name="diagnostic">The display-safe diagnostic.</param>
    /// <param name="source">The display-safe source name.</param>
    /// <returns>A non-found result.</returns>
    public static AppSurfaceLocalSecretResult NotFound(
        LocalSecretResultStatus status,
        AppSurfaceLocalSecretDiagnostic diagnostic,
        string source)
    {
        if (status == LocalSecretResultStatus.Found)
        {
            throw new ArgumentException("Use Found for successful local secret results.", nameof(status));
        }

        return new AppSurfaceLocalSecretResult(status, null, diagnostic, source);
    }

    /// <inheritdoc />
    public override string ToString() =>
        Diagnostic == null
            ? $"Status: {Status}; Source: {Source}; Value: {(Value == null ? "none" : "[redacted]")}"
            : $"Status: {Status}; Source: {Source}; {Diagnostic.ToDisplayString()}";
}
