namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// Describes the resolved configuration state for an AppSurface environment.
/// </summary>
/// <remarks>
/// Reports are immutable snapshots from the caller's perspective: provider order, entries, diagnostics, and redaction
/// policy describe one audit run and should not be treated as live configuration. Use <see cref="Entries"/> for
/// machine inspection and <see cref="DiscoveredKeys"/> for effective provider-discovered configuration keys that are
/// not necessarily represented by known entries. Use <see cref="ConfigAuditTextRenderer"/> when operators need a
/// deterministic text dump.
/// </remarks>
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
    /// Gets effective configuration keys discovered directly from enumerable providers.
    /// </summary>
    /// <remarks>
    /// Discovered keys describe the effective merged configuration visible to AppSurface providers that implement
    /// audit enumeration. They are not a complete raw inventory: providers that cannot enumerate keys are omitted,
    /// shadowed lower-priority file keys are not included, and environment variables or secret providers are not
    /// enumerated by the built-in v1 surface. <see cref="ConfigAuditDiscoveredKey.DisplayValue"/> is redacted or
    /// omitted before it enters the public report, but source metadata such as file paths, provider names, and config
    /// paths may still be support-sensitive.
    /// </remarks>
    public IReadOnlyList<ConfigAuditDiscoveredKey> DiscoveredKeys { get; init; } = [];

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
/// Describes one effective provider-discovered configuration key in an audit report.
/// </summary>
/// <remarks>
/// Classifications are relative to the AppSurface audit registry, not to all application code. An
/// <see cref="ConfigAuditDiscoveredKeyClassification.Unknown"/> key can be a typo, stale setting, or a value consumed
/// outside AppSurface's known-entry registry; it is not proof that no code uses the key. Use
/// <see cref="ValueDisplayState"/> to distinguish shown, redacted, complex-omitted, and inventory-omitted values. The
/// display state describes this report's rendering decision only; it is not a sensitivity or secrecy classification.
/// </remarks>
public sealed class ConfigAuditDiscoveredKey
{
    /// <summary>
    /// Gets the discovered configuration key path.
    /// </summary>
    public required string Key { get; init; }

    /// <summary>
    /// Gets the key's relationship to the AppSurface audit registry.
    /// </summary>
    public required ConfigAuditDiscoveredKeyClassification Classification { get; init; }

    /// <summary>
    /// Gets the display-safe value, or <see langword="null"/> when a non-sensitive value is omitted.
    /// </summary>
    /// <remarks>
    /// Exact registered scalar keys can include a display value. Non-sensitive object and array parent values are
    /// omitted instead of serialized, and non-sensitive provider-discovered inventory values that are not exact audit
    /// entries are omitted by default. Use <see cref="ValueDisplayState"/> instead of testing this property for
    /// <see langword="null"/> when consuming structured reports.
    /// </remarks>
    public string? DisplayValue { get; init; }

    /// <summary>
    /// Gets a value indicating whether <see cref="DisplayValue"/> was replaced by the redaction placeholder.
    /// </summary>
    public bool IsRedacted { get; init; }

    /// <summary>
    /// Gets the display decision applied to <see cref="DisplayValue"/> for this discovered key.
    /// </summary>
    /// <remarks>
    /// This state explains how the public report rendered the discovered value. It is not a sensitivity label and does
    /// not prove whether a value is secret or safe outside the current report context. The default
    /// <see cref="ConfigAuditDiscoveredValueDisplayState.Unspecified"/> preserves compatibility for reports manually
    /// constructed before this property existed; reporter-produced instances always set an explicit state.
    /// </remarks>
    public ConfigAuditDiscoveredValueDisplayState ValueDisplayState { get; init; }

    /// <summary>
    /// Gets source records associated with this effective discovered key.
    /// </summary>
    public IReadOnlyList<ConfigAuditSourceRecord> Sources { get; init; } = [];

    /// <summary>
    /// Gets diagnostics specific to this discovered key.
    /// </summary>
    public IReadOnlyList<ConfigAuditDiagnostic> Diagnostics { get; init; } = [];
}

/// <summary>
/// Describes one provider in the audit report precedence list.
/// </summary>
/// <remarks>
/// <see cref="Precedence"/> is the display order used by audit reports. Environment providers are marked as
/// overrides because they are checked before normal priority-ordered providers.
/// </remarks>
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
/// <remarks>
/// <see cref="State"/> summarizes the entry as a whole. Object entries can contain <see cref="Children"/> with more
/// specific provenance, including nested <see cref="ConfigAuditEntryState.PartiallyResolved"/> states when descendants
/// are patched. <see cref="DisplayValue"/> is already redacted and can be <see langword="null"/> for complex values,
/// including collections whose elements are not dumped into parent display strings. Callers should inspect source
/// records and available children instead of assuming a full object dump is available.
/// </remarks>
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
    /// Gets the display-safe value. Sensitive values are already redacted, and collection parent values may be omitted
    /// so nested element data cannot leak through a serialized dump.
    /// </summary>
    public string? DisplayValue { get; init; }

    /// <summary>
    /// Gets a value indicating whether <see cref="DisplayValue"/> was redacted.
    /// </summary>
    public bool IsRedacted { get; init; }

    /// <summary>
    /// Gets collection element identity when this entry represents an array/list item or dictionary item.
    /// </summary>
    /// <remarks>
    /// Element labels are already display-safe. Sensitive dictionary keys are replaced before the report object is
    /// created, so callers must not expect <see cref="ConfigAuditElementIdentity.KeyLabel"/> to be reversible.
    /// </remarks>
    public ConfigAuditElementIdentity? Element { get; init; }

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
/// Describes a source coordinate inside a configuration file.
/// </summary>
/// <remarks>
/// Both values are one-based. <see cref="ByteColumnNumber"/> counts UTF-8 bytes from the start of the physical line,
/// so it can differ from an editor's character column when a line contains non-ASCII characters before the source token.
/// </remarks>
public sealed class ConfigAuditSourceLocation
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigAuditSourceLocation"/> class.
    /// </summary>
    /// <param name="lineNumber">The one-based physical line number containing the source token.</param>
    /// <param name="byteColumnNumber">The one-based UTF-8 byte column containing the source token.</param>
    public ConfigAuditSourceLocation(int lineNumber, int byteColumnNumber)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(lineNumber, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(byteColumnNumber, 1);

        LineNumber = lineNumber;
        ByteColumnNumber = byteColumnNumber;
    }

    /// <summary>
    /// Gets the one-based physical line number containing the source token.
    /// </summary>
    public int LineNumber { get; }

    /// <summary>
    /// Gets the one-based UTF-8 byte column containing the source token.
    /// </summary>
    /// <remarks>
    /// This is a byte coordinate over the UTF-8 file content, not a Unicode scalar, text element, or editor display
    /// column. A non-ASCII character earlier on the same line can increase this value by more than one.
    /// </remarks>
    public int ByteColumnNumber { get; }
}

/// <summary>
/// Describes the collection element represented by a child audit entry.
/// </summary>
/// <remarks>
/// Array and list entries use zero-based <see cref="Index"/> values. Dictionary entries use <see cref="KeyLabel"/>,
/// which is either the non-sensitive key label, a display-suppressed placeholder, or an in-report redaction label such
/// as <c>[redacted-key-1]</c>. Labels are intended for display and comparison within one report only. When configured,
/// <see cref="KeyCorrelationId"/> is the separate environment-scoped opaque value for comparing dictionary keys inside
/// one named environment's report history. Use <see cref="ComparisonKeyCorrelationId"/> for explicit cross-environment
/// config diff matching.
/// </remarks>
public sealed class ConfigAuditElementIdentity
{
    /// <summary>
    /// Gets the collection element kind.
    /// </summary>
    public required ConfigAuditElementKind Kind { get; init; }

    /// <summary>
    /// Gets the zero-based array or list index, when applicable.
    /// </summary>
    public int? Index { get; init; }

    /// <summary>
    /// Gets the display-safe dictionary key label, when applicable.
    /// </summary>
    public string? KeyLabel { get; init; }

    /// <summary>
    /// Gets a value indicating whether the original dictionary key was redacted or intentionally hidden.
    /// </summary>
    public bool IsKeyRedacted { get; init; }

    /// <summary>
    /// Gets the opt-in opaque identifier for correlating the same dictionary key across reports.
    /// </summary>
    /// <remarks>
    /// This value is populated only when entry options enable dictionary key correlation and global correlation key
    /// material is valid. It includes the report environment in its derivation, is not reversible, is not part of the
    /// display path, and should still be treated as sensitive support metadata because it reveals equality and churn
    /// across reports from the same environment.
    /// </remarks>
    public string? KeyCorrelationId { get; init; }

    /// <summary>
    /// Gets the opt-in opaque identifier for matching the same dictionary key across compared environments.
    /// </summary>
    /// <remarks>
    /// This value is populated only when entry options enable dictionary key correlation and global correlation key
    /// material is valid. Unlike <see cref="KeyCorrelationId"/>, the environment name is deliberately omitted from the
    /// derivation so <see cref="ConfigAuditReportDiffer"/> can match captured staging and production reports without
    /// trusting report-local redacted labels. It is not reversible and should still be treated as support-sensitive
    /// equality metadata.
    /// </remarks>
    public string? ComparisonKeyCorrelationId { get; init; }
}

/// <summary>
/// Describes one source that contributed to a configuration entry.
/// </summary>
/// <remarks>
/// Source records identify where a value came from and how it was applied. File paths, environment variable names, and
/// config paths are optional because not every provider exposes the same provenance. File sources can also include
/// <see cref="Location"/> when the provider can truthfully map the parsed value back to an exact file coordinate. The
/// source role is especially important for mixed values: a base source can be combined with patch sources from
/// higher-priority providers.
/// </remarks>
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
    /// Gets the exact file coordinate for this source when the provider can prove one.
    /// </summary>
    /// <remarks>
    /// A <see langword="null"/> value means the source is still known but no truthful coordinate is available, such as
    /// for non-file sources, ambiguous case-insensitive file paths, unsupported paths, parser mismatches, or collection
    /// element descendants.
    /// </remarks>
    public ConfigAuditSourceLocation? Location { get; init; }

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
/// <remarks>
/// Diagnostic messages are intended to be display-safe and stable enough for operators. Use <see cref="Code"/> for
/// programmatic handling, and use <see cref="Source"/> when a diagnostic can be tied to one provider, file, or
/// environment variable.
/// </remarks>
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
/// <remarks>
/// The built-in policy is always enabled and uses fragment matching before values are exposed through
/// <see cref="ConfigAuditEntry.DisplayValue"/>. <see cref="MatchedFragments"/> is a snapshot for explanation, not a
/// mutable policy hook. Dictionary key correlation metadata describes the configured report policy without exposing
/// the secret key used for scoped HMAC derivation.
/// </remarks>
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

    /// <summary>
    /// Gets the configured dictionary key correlation mode for the report.
    /// </summary>
    public ConfigAuditDictionaryKeyCorrelationMode DictionaryKeyCorrelationMode { get; init; } = ConfigAuditDictionaryKeyCorrelationMode.None;

    /// <summary>
    /// Gets the display-safe correlation key id when configured.
    /// </summary>
    public string? DictionaryKeyCorrelationKeyId { get; init; }

    /// <summary>
    /// Gets the configured application or product scope when configured.
    /// </summary>
    public string? DictionaryKeyCorrelationApplicationScope { get; init; }
}

/// <summary>
/// Identifies how dictionary keys should receive cross-report correlation identifiers.
/// </summary>
/// <remarks>
/// Values are explicit and append-only so serialized reports remain stable across releases.
/// </remarks>
public enum ConfigAuditDictionaryKeyCorrelationMode
{
    /// <summary>Dictionary keys use display labels only; redacted labels are report-local.</summary>
    None = 0,

    /// <summary>Dictionary keys receive scoped HMAC identifiers when global correlation key material is configured.</summary>
    ScopedHmac = 1
}

/// <summary>
/// Identifies the resolution state for an audited configuration entry.
/// </summary>
/// <remarks>
/// Values are explicit and append-only so serialized reports remain stable across releases.
/// </remarks>
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
/// Identifies how a provider-discovered key relates to the AppSurface audit registry.
/// </summary>
/// <remarks>
/// Values are explicit and append-only so serialized reports remain stable across releases. These classifications are
/// registry-relative: <see cref="Unknown"/> does not mean globally unused, and <see cref="KnownDescendant"/> is based
/// on dotted-path segment matching rather than schema validation of a wrapper type.
/// </remarks>
public enum ConfigAuditDiscoveredKeyClassification
{
    /// <summary>The discovered key exactly matches a known audit entry.</summary>
    Known = 0,

    /// <summary>The discovered key is under a known entry path using dotted-path segment matching.</summary>
    KnownDescendant = 1,

    /// <summary>The discovered key is not known to the AppSurface audit registry.</summary>
    Unknown = 2
}

/// <summary>
/// Identifies how a provider-discovered value appears in the public audit report.
/// </summary>
/// <remarks>
/// Values are explicit and append-only so serialized reports remain stable across releases. This enum describes report
/// display state only: it is not a sensitivity or secrecy classification, and source metadata may remain
/// support-sensitive even when a value is omitted.
/// </remarks>
public enum ConfigAuditDiscoveredValueDisplayState
{
    /// <summary>The display state was not specified, typically on a manually constructed legacy report object.</summary>
    Unspecified = 0,

    /// <summary>The discovered scalar value is shown in <see cref="ConfigAuditDiscoveredKey.DisplayValue"/>.</summary>
    Shown = 1,

    /// <summary>The value was replaced by the audit redaction placeholder.</summary>
    Redacted = 2,

    /// <summary>The value is an object or array parent omitted instead of serialized as a scalar display value.</summary>
    OmittedComplex = 3,

    /// <summary>The scalar value was omitted because the discovered key is inventory, not an exact audit entry.</summary>
    OmittedInventory = 4
}

/// <summary>
/// Identifies the kind of collection element represented by an audit child entry.
/// </summary>
/// <remarks>
/// Values are explicit and append-only so serialized reports remain stable across releases.
/// </remarks>
public enum ConfigAuditElementKind
{
    /// <summary>An item from a one-dimensional array.</summary>
    ArrayItem = 0,

    /// <summary>An item from a list-like collection.</summary>
    ListItem = 1,

    /// <summary>An item from a dictionary-like collection.</summary>
    DictionaryItem = 2
}

/// <summary>
/// Identifies the kind of configuration source.
/// </summary>
/// <remarks>
/// Values are explicit and append-only so serialized reports remain stable across releases.
/// </remarks>
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
/// <remarks>
/// Values are explicit and append-only so serialized reports remain stable across releases.
/// </remarks>
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
/// <remarks>
/// Values are explicit and append-only so serialized reports remain stable across releases. For entry options,
/// <see cref="NonSensitive"/> is a classification hint, not a redaction bypass; sensitive fragments, provider source
/// sensitivity, and another registration's <see cref="Sensitive"/> classification still win.
/// </remarks>
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
/// <remarks>
/// Values are explicit and append-only so serialized reports remain stable across releases.
/// </remarks>
public enum ConfigAuditDiagnosticSeverity
{
    /// <summary>Informational diagnostic.</summary>
    Info = 0,

    /// <summary>Warning diagnostic.</summary>
    Warning = 1,

    /// <summary>Error diagnostic.</summary>
    Error = 2
}
