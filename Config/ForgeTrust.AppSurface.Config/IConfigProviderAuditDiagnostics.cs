namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// Allows external configuration providers to expose source-aware audit details without depending on Config internals.
/// </summary>
/// <remarks>
/// Implement this interface when a provider can resolve a known audit key with richer provenance or diagnostics than the
/// generic <see cref="IConfigProvider.GetValue{T}"/> fallback can provide. Return display-safe diagnostics only:
/// messages, source records, and metadata must not include raw configuration values, secret payloads, credentials, or raw
/// provider exception messages.
/// </remarks>
public interface IConfigProviderAuditDiagnostics
{
    /// <summary>
    /// Resolves a known audit key with source metadata for a configuration audit report.
    /// </summary>
    /// <param name="environment">The environment being audited.</param>
    /// <param name="key">The logical AppSurface configuration key.</param>
    /// <param name="valueType">The expected value type.</param>
    /// <param name="role">The role the provider plays in final resolution.</param>
    /// <returns>The provider audit resolution.</returns>
    ConfigProviderAuditResolution ResolveForAudit(
        string environment,
        string key,
        Type valueType,
        ConfigAuditSourceRole role);

    /// <summary>
    /// Gets report-level diagnostics for <paramref name="environment"/>.
    /// </summary>
    /// <param name="environment">The environment being audited.</param>
    /// <returns>Display-safe diagnostics that are not tied to one key.</returns>
    IReadOnlyList<ConfigAuditDiagnostic> GetReportDiagnostics(string environment);
}
