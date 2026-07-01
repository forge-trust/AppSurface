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
    IReadOnlyList<ConfigAuditDiagnostic> Diagnostics)
{
    private readonly string _key = ValidateKey(Key);
    private readonly IReadOnlyList<ConfigAuditSourceRecord> _sources =
        Sources ?? throw new ArgumentNullException(nameof(Sources));
    private readonly IReadOnlyList<ConfigAuditDiagnostic> _diagnostics =
        Diagnostics ?? throw new ArgumentNullException(nameof(Diagnostics));

    /// <summary>
    /// Gets the discovered configuration key.
    /// </summary>
    public string Key
    {
        get => _key;
        init => _key = ValidateKey(value);
    }

    /// <summary>
    /// Gets source records associated with the key.
    /// </summary>
    public IReadOnlyList<ConfigAuditSourceRecord> Sources
    {
        get => _sources;
        init => _sources = value ?? throw new ArgumentNullException(nameof(Sources));
    }

    /// <summary>
    /// Gets display-safe diagnostics specific to this key.
    /// </summary>
    public IReadOnlyList<ConfigAuditDiagnostic> Diagnostics
    {
        get => _diagnostics;
        init => _diagnostics = value ?? throw new ArgumentNullException(nameof(Diagnostics));
    }

    private static string ValidateKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return key;
    }
}
