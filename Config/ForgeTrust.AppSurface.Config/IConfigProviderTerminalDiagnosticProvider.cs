namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// Allows a configuration provider to stop lower-priority resolution after a null value.
/// </summary>
/// <remarks>
/// <see cref="IConfigProvider.GetValue{T}"/> uses <see langword="null"/> for both missing values and failed lookups.
/// Providers that own fail-closed sources, such as local secret stores, implement this interface so
/// <see cref="IConfigManager"/> can distinguish true absence from terminal conditions like locked stores,
/// unsupported platforms, invalid identities, or posture-disabled environments.
/// Implement this interface when a provider can claim a key but cannot safely return a value, and a lower-priority
/// provider must not mask that condition with a file default or placeholder. Do not implement it for ordinary cache
/// misses, optional configuration, or providers whose unavailable state should intentionally fall through.
/// Implementations should store diagnostics per lookup key and environment, return <see langword="true"/> only for the
/// most recent terminal lookup, and keep every diagnostic display-safe. The diagnostic must explain the problem, cause,
/// fix, documentation hint, and retryability without including raw configuration values, provider exception messages,
/// machine-specific secret names, or other paste-unsafe details.
/// A common pitfall is treating every <see langword="null"/> as terminal. Only claimed-key failures should stop
/// resolution; true missing values must still return <see langword="false"/> so lower-priority providers can
/// participate.
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
