namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// Allows a configuration provider to stop lower-priority resolution after a null value.
/// </summary>
/// <remarks>
/// <see cref="IConfigProvider.GetValue{T}"/> uses <see langword="null"/> for both missing values and failed lookups.
/// Providers that own fail-closed sources, such as local secret stores, implement this interface so
/// <see cref="IConfigManager"/> can distinguish true absence from terminal conditions like locked stores,
/// unsupported platforms, invalid identities, or posture-disabled environments.
/// </remarks>
public interface IConfigProviderTerminalDiagnosticProvider
{
    /// <summary>
    /// Attempts to get the terminal diagnostic for the most recent lookup of <paramref name="key"/>.
    /// </summary>
    /// <param name="environment">The environment that was resolved.</param>
    /// <param name="key">The configuration key that was resolved.</param>
    /// <param name="diagnostic">The display-safe terminal diagnostic when resolution must stop.</param>
    /// <returns><see langword="true"/> when lower-priority providers must not be queried.</returns>
    bool TryGetTerminalDiagnostic(
        string environment,
        string key,
        out ConfigProviderTerminalDiagnostic diagnostic);
}
