namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// Describes an external provider's audit resolution for one known configuration key.
/// </summary>
/// <remarks>
/// This public shape is intentionally smaller than Config's internal audit resolution. It gives external providers a safe
/// way to report value state, source records, and diagnostics without coupling packages to internal traversal or patching
/// details. <see cref="Value"/> is still redacted by the audit reporter before display.
/// </remarks>
public sealed class ConfigProviderAuditResolution
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigProviderAuditResolution"/> class.
    /// </summary>
    /// <param name="key">The logical AppSurface configuration key.</param>
    /// <param name="state">The resolved audit state.</param>
    /// <param name="value">The resolved value, if any.</param>
    /// <param name="sources">Source records that contributed to the value.</param>
    /// <param name="diagnostics">Display-safe diagnostics for this key.</param>
    public ConfigProviderAuditResolution(
        string key,
        ConfigAuditEntryState state,
        object? value,
        IReadOnlyList<ConfigAuditSourceRecord> sources,
        IReadOnlyList<ConfigAuditDiagnostic> diagnostics)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(diagnostics);

        Key = key;
        State = state;
        Value = value;
        Sources = sources;
        Diagnostics = diagnostics;
    }

    /// <summary>
    /// Gets the logical AppSurface configuration key.
    /// </summary>
    public string Key { get; }

    /// <summary>
    /// Gets the audit state.
    /// </summary>
    public ConfigAuditEntryState State { get; }

    /// <summary>
    /// Gets the resolved value, if any.
    /// </summary>
    public object? Value { get; }

    /// <summary>
    /// Gets the source records that contributed to the value.
    /// </summary>
    public IReadOnlyList<ConfigAuditSourceRecord> Sources { get; }

    /// <summary>
    /// Gets display-safe diagnostics for this key.
    /// </summary>
    public IReadOnlyList<ConfigAuditDiagnostic> Diagnostics { get; }

    /// <summary>
    /// Creates a missing resolution for <paramref name="key"/>.
    /// </summary>
    /// <param name="key">The logical AppSurface configuration key.</param>
    /// <returns>A missing audit resolution.</returns>
    public static ConfigProviderAuditResolution Missing(string key) =>
        new(key, ConfigAuditEntryState.Missing, null, [], []);
}
