namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// Allows a configuration provider to stop lower-priority resolution after a null value.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IConfigProvider.GetValue{T}"/> uses <see langword="null"/> for both missing values and failed lookups.
/// Providers that own fail-closed sources, such as local secret stores, implement this interface so
/// <see cref="IConfigManager"/> can distinguish true absence from terminal conditions like locked stores,
/// unsupported platforms, invalid identities, or posture-disabled environments.
/// </para>
/// <para>
/// <b>When to implement:</b> implement this interface when a provider wraps a fail-closed source that can distinguish
/// "not found" from access denied, locked, unavailable, unsupported platform, invalid identity, or other claimed-key
/// states that must not be masked by file defaults. Prefer a plain <see cref="IConfigProvider"/> when missing and
/// unavailable values should both fall through.
/// </para>
/// <para>
/// <b>Usage pattern:</b> <see cref="IConfigProvider.GetValue{T}"/> returns <see langword="null"/> for the lookup, then
/// <see cref="IConfigManager"/> calls <see cref="TryGetTerminalDiagnostic"/> with the same environment and key. Return
/// <see langword="true"/> only when that last lookup produced a terminal diagnostic.
/// </para>
/// <para>
/// <b>Pitfall:</b> cache diagnostics by environment and key for the most recent lookup because
/// <see cref="TryGetTerminalDiagnostic"/> is called after <see cref="IConfigProvider.GetValue{T}"/> has already
/// returned. A common mistake is treating every <see langword="null"/> as terminal; only claimed-key failures should
/// stop resolution, while true missing values must return <see langword="false"/>.
/// </para>
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
