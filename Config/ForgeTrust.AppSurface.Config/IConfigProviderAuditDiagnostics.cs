namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// Allows external configuration providers to expose source-aware audit details without depending on Config internals.
/// </summary>
/// <remarks>
/// Implement this interface when a provider can resolve a known audit key with richer provenance or diagnostics than the
/// generic <see cref="IConfigProvider.GetValue{T}"/> fallback can provide, or when calling the generic path would lose
/// provider-specific source records. Providers with ordinary scalar values and no custom diagnostics should rely on the
/// generic audit path instead. Return display-safe diagnostics only: messages, source records, and metadata must not
/// include raw configuration values, secret payloads, credentials, or raw provider exception messages.
/// Expected provider failures, such as missing remote secrets or access-denied responses, should be returned as failed
/// <see cref="ConfigProviderAuditResolution"/> instances so the reporter can preserve provider ownership. Throw only for
/// unexpected programming or infrastructure failures that cannot be represented safely. The reporter invokes providers in
/// priority order while building a point-in-time audit report, so implementations should avoid writes and other
/// order-sensitive side effects.
/// </remarks>
public interface IConfigProviderAuditDiagnostics
{
    /// <summary>
    /// Resolves a known audit key with source metadata for a configuration audit report.
    /// </summary>
    /// <remarks>
    /// Return a <see cref="ConfigProviderAuditResolution"/> whose key matches <paramref name="key"/>. Failed claimed-key
    /// lookups should use <see cref="ConfigAuditEntryState.Invalid"/> with display-safe diagnostics instead of throwing
    /// when the failure is an expected provider outcome. Do not include raw secret values in diagnostics or source
    /// metadata.
    /// </remarks>
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
    /// <remarks>
    /// Use this for provider-wide warnings or failures that are not tied to one key. The returned diagnostics may be
    /// rendered before or after key-level diagnostics, so consumers must not depend on ordering for correctness.
    /// </remarks>
    /// <param name="environment">The environment being audited.</param>
    /// <returns>Display-safe diagnostics that are not tied to one key.</returns>
    IReadOnlyList<ConfigAuditDiagnostic> GetReportDiagnostics(string environment);
}
