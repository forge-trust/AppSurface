namespace ForgeTrust.Runnable.Config;

/// <summary>
/// Describes the resolved configuration state for a Runnable environment.
/// </summary>
public sealed class ConfigAuditReport
{
    /// <summary>
    /// Gets the environment this report describes.
    /// </summary>
    public required string Environment { get; init; }

    /// <summary>
    /// Gets the time when this report was generated.
    /// </summary>
    public required DateTimeOffset GeneratedAt { get; init; }

    /// <summary>
    /// Gets the provider precedence used while producing this report.
    /// </summary>
    public IReadOnlyList<ConfigAuditProvider> Providers { get; init; } = [];

    /// <summary>
    /// Gets the known configuration entries resolved for this environment.
    /// </summary>
    public IReadOnlyList<ConfigAuditEntry> Entries { get; init; } = [];

    /// <summary>
    /// Gets report-level diagnostics that are not tied to a single entry.
    /// </summary>
    public IReadOnlyList<ConfigAuditDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>
    /// Gets the redaction policy applied before this report was returned.
    /// </summary>
    public required ConfigAuditRedaction Redaction { get; init; }
}

/// <summary>
/// Describes one provider in the audit report precedence list.
/// </summary>
public sealed class ConfigAuditProvider
{
    /// <summary>
    /// Gets the provider name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the provider priority.
    /// </summary>
    public required int Priority { get; init; }

    /// <summary>
    /// Gets the precedence rank used by the configuration manager. Lower ranks are checked first.
    /// </summary>
    public required int Precedence { get; init; }

    /// <summary>
    /// Gets a value indicating whether the manager treats this provider as an override outside normal priority order.
    /// </summary>
    public bool IsOverride { get; init; }
}

/// <summary>
/// Describes one known configuration entry and its source records.
/// </summary>
public sealed class ConfigAuditEntry
{
    /// <summary>
    /// Gets the configuration key.
    /// </summary>
    public required string Key { get; init; }

    /// <summary>
    /// Gets the declared value type name when known.
    /// </summary>
    public string? DeclaredType { get; init; }

    /// <summary>
    /// Gets the resolved entry state.
    /// </summary>
    public required ConfigAuditEntryState State { get; init; }

    /// <summary>
    /// Gets the display-safe value. Sensitive values are already redacted.
    /// </summary>
    public string? DisplayValue { get; init; }

    /// <summary>
    /// Gets a value indicating whether <see cref="DisplayValue"/> was redacted.
    /// </summary>
    public bool IsRedacted { get; init; }

    /// <summary>
    /// Gets the source records that contributed to this entry.
    /// </summary>
    public IReadOnlyList<ConfigAuditSourceRecord> Sources { get; init; } = [];

    /// <summary>
    /// Gets child entries for object-valued configuration.
    /// </summary>
    public IReadOnlyList<ConfigAuditEntry> Children { get; init; } = [];

    /// <summary>
    /// Gets diagnostics specific to this entry.
    /// </summary>
    public IReadOnlyList<ConfigAuditDiagnostic> Diagnostics { get; init; } = [];
}

/// <summary>
/// Describes one source that contributed to a configuration entry.
/// </summary>
public sealed class ConfigAuditSourceRecord
{
    /// <summary>
    /// Gets the source kind.
    /// </summary>
    public required ConfigAuditSourceKind Kind { get; init; }

    /// <summary>
    /// Gets the provider name when applicable.
    /// </summary>
    public string? ProviderName { get; init; }

    /// <summary>
    /// Gets the provider priority when applicable.
    /// </summary>
    public int? ProviderPriority { get; init; }

    /// <summary>
    /// Gets the file path for file-sourced values.
    /// </summary>
    public string? FilePath { get; init; }

    /// <summary>
    /// Gets the environment variable name for environment-sourced values.
    /// </summary>
    public string? EnvironmentVariableName { get; init; }

    /// <summary>
    /// Gets the source config path.
    /// </summary>
    public string? ConfigPath { get; init; }

    /// <summary>
    /// Gets the target config path affected by this source.
    /// </summary>
    public string? AppliedToPath { get; init; }

    /// <summary>
    /// Gets the role this source played in resolution.
    /// </summary>
    public required ConfigAuditSourceRole Role { get; init; }

    /// <summary>
    /// Gets the sensitivity classification supplied by the source.
    /// </summary>
    public ConfigAuditSensitivity Sensitivity { get; init; } = ConfigAuditSensitivity.Unknown;
}

/// <summary>
/// Describes a diagnostic emitted while building a configuration audit report.
/// </summary>
public sealed class ConfigAuditDiagnostic
{
    /// <summary>
    /// Gets the diagnostic severity.
    /// </summary>
    public required ConfigAuditDiagnosticSeverity Severity { get; init; }

    /// <summary>
    /// Gets a stable diagnostic code.
    /// </summary>
    public required string Code { get; init; }

    /// <summary>
    /// Gets the display-safe diagnostic message.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Gets the configuration key associated with the diagnostic, when any.
    /// </summary>
    public string? Key { get; init; }

    /// <summary>
    /// Gets the member/config path associated with the diagnostic, when any.
    /// </summary>
    public string? ConfigPath { get; init; }

    /// <summary>
    /// Gets the source associated with the diagnostic, when any.
    /// </summary>
    public ConfigAuditSourceRecord? Source { get; init; }
}

/// <summary>
/// Describes the redaction policy applied to a configuration audit report.
/// </summary>
public sealed class ConfigAuditRedaction
{
    /// <summary>
    /// Gets a value indicating whether redaction was enabled.
    /// </summary>
    public required bool Enabled { get; init; }

    /// <summary>
    /// Gets the sensitive fragments matched by the built-in redactor.
    /// </summary>
    public IReadOnlyList<string> MatchedFragments { get; init; } = [];

    /// <summary>
    /// Gets the display placeholder used for redacted values.
    /// </summary>
    public required string Placeholder { get; init; }
}

/// <summary>
/// Identifies the resolution state for an audited configuration entry.
/// </summary>
public enum ConfigAuditEntryState
{
    /// <summary>A provider supplied the value without member-level mixed provenance.</summary>
    Resolved = 0,

    /// <summary>An object value has a base source plus one or more member-level patches.</summary>
    PartiallyResolved = 1,

    /// <summary>No provider supplied a value, but the wrapper default supplied one.</summary>
    Defaulted = 2,

    /// <summary>No provider value and no default resolved for the known entry.</summary>
    Missing = 3,

    /// <summary>Resolution found a value or validation result that was invalid.</summary>
    Invalid = 4
}

/// <summary>
/// Identifies the kind of configuration source.
/// </summary>
public enum ConfigAuditSourceKind
{
    /// <summary>A generic configuration provider.</summary>
    Provider = 0,

    /// <summary>A file-based source.</summary>
    File = 1,

    /// <summary>An environment variable source.</summary>
    EnvironmentVariable = 2,

    /// <summary>A config wrapper default value.</summary>
    Default = 3,

    /// <summary>No source supplied a value.</summary>
    Missing = 4
}

/// <summary>
/// Identifies how a source contributed to the final value.
/// </summary>
public enum ConfigAuditSourceRole
{
    /// <summary>The source supplied the base value.</summary>
    Base = 0,

    /// <summary>The source overrode lower-priority sources.</summary>
    Override = 1,

    /// <summary>The source patched one or more child values.</summary>
    Patch = 2,

    /// <summary>The source supplied a fallback value.</summary>
    Fallback = 3
}

/// <summary>
/// Classifies source or value sensitivity.
/// </summary>
public enum ConfigAuditSensitivity
{
    /// <summary>Sensitivity is unknown.</summary>
    Unknown = 0,

    /// <summary>The source or value is not sensitive.</summary>
    NonSensitive = 1,

    /// <summary>The source or value should be treated as sensitive.</summary>
    Sensitive = 2
}

/// <summary>
/// Identifies diagnostic severity.
/// </summary>
public enum ConfigAuditDiagnosticSeverity
{
    /// <summary>Informational diagnostic.</summary>
    Info = 0,

    /// <summary>Warning diagnostic.</summary>
    Warning = 1,

    /// <summary>Error diagnostic.</summary>
    Error = 2
}
