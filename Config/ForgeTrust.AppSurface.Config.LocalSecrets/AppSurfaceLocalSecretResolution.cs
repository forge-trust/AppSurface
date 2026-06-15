namespace ForgeTrust.AppSurface.Config.LocalSecrets;

/// <summary>
/// Describes typed LocalSecrets provider resolution before it is adapted to <see cref="ForgeTrust.AppSurface.Config.IConfigProvider"/>.
/// </summary>
/// <typeparam name="T">The requested configuration value type.</typeparam>
/// <param name="Status">The structured LocalSecrets resolution status.</param>
/// <param name="Value">The converted secret value only when <paramref name="Status"/> is <see cref="LocalSecretResultStatus.Found"/>.</param>
/// <param name="Diagnostic">The display-safe diagnostic for non-success states.</param>
/// <param name="Source">The display-safe source name that handled the lookup.</param>
public sealed record AppSurfaceLocalSecretResolution<T>(
    LocalSecretResultStatus Status,
    T? Value,
    AppSurfaceLocalSecretDiagnostic? Diagnostic,
    string Source)
{
    /// <summary>
    /// Creates a found resolution.
    /// </summary>
    /// <param name="value">The converted value.</param>
    /// <param name="source">The display-safe source name.</param>
    /// <returns>A found resolution.</returns>
    public static AppSurfaceLocalSecretResolution<T> Found(T? value, string source) =>
        new(LocalSecretResultStatus.Found, value, null, source);

    /// <summary>
    /// Creates a non-found resolution with a display-safe diagnostic.
    /// </summary>
    /// <param name="status">The non-found status.</param>
    /// <param name="diagnostic">The display-safe diagnostic.</param>
    /// <param name="source">The display-safe source name.</param>
    /// <returns>A non-found resolution.</returns>
    public static AppSurfaceLocalSecretResolution<T> NotFound(
        LocalSecretResultStatus status,
        AppSurfaceLocalSecretDiagnostic diagnostic,
        string source)
    {
        if (status == LocalSecretResultStatus.Found)
        {
            throw new ArgumentException("Use Found for successful local secret resolutions.", nameof(status));
        }

        return new AppSurfaceLocalSecretResolution<T>(status, default, diagnostic, source);
    }

    /// <inheritdoc />
    public override string ToString() =>
        Diagnostic == null
            ? $"Status: {Status}; Source: {Source}; Value: {(Value == null ? "none" : "[redacted]")}"
            : $"Status: {Status}; Source: {Source}; {Diagnostic.ToDisplayString()}";
}
