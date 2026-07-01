namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// Describes one external provider-discovered configuration key before public classification and redaction.
/// </summary>
/// <param name="Key">The discovered configuration key.</param>
/// <param name="RawValue">The scalar value used for redaction, or <see langword="null"/> for object or array parents.</param>
/// <param name="ValueKind">The provider value shape.</param>
/// <param name="Sources">Source records associated with the key.</param>
/// <param name="Diagnostics">Display-safe diagnostics specific to this key.</param>
public sealed record ConfigProviderAuditDiscoveredKey(
    string Key,
    object? RawValue,
    ConfigAuditDiscoveredValueKind ValueKind,
    IReadOnlyList<ConfigAuditSourceRecord> Sources,
    IReadOnlyList<ConfigAuditDiagnostic> Diagnostics);
