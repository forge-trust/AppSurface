namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// Selects how much trust the diff should attach to the two audit reports being compared.
/// </summary>
/// <remarks>
/// Evidence mode changes diagnostics and wording only. <see cref="ConfigAuditReportDiffer"/> still compares two
/// already-built <see cref="ConfigAuditReport"/> objects and never re-resolves configuration providers.
/// </remarks>
public enum ConfigAuditDiffEvidenceMode
{
    /// <summary>The reports came from one host asking for two named environments; useful for triage but weak evidence.</summary>
    SameHostNamedEnvironment = 0,

    /// <summary>The reports were captured from the hosts or deployments they describe; stronger but still support-sensitive.</summary>
    CapturedSnapshot = 1
}

/// <summary>
/// Identifies the comparison status for one diff item.
/// </summary>
/// <remarks>
/// Values are explicit and append-only so serialized diff reports remain stable across releases.
/// </remarks>
public enum ConfigAuditDiffItemStatus
{
    /// <summary>The compared evidence matched within the limits of redacted report data.</summary>
    Unchanged = 0,

    /// <summary>The item exists only in the target report.</summary>
    Added = 1,

    /// <summary>The item exists only in the baseline report.</summary>
    Removed = 2,

    /// <summary>The item exists in both reports but its sanitized evidence changed.</summary>
    Changed = 3,

    /// <summary>The reports do not contain enough stable identity metadata to compare the item truthfully.</summary>
    Uncomparable = 4
}

/// <summary>
/// Classifies how urgently an item should be reviewed.
/// </summary>
public enum ConfigAuditDiffSignificance
{
    /// <summary>No operator action is normally needed.</summary>
    Unchanged = 0,

    /// <summary>The item provides useful context, such as provider order or source-path drift.</summary>
    Context = 1,

    /// <summary>The item may affect runtime behavior or evidence trust and should be reviewed.</summary>
    NeedsAttention = 2
}

/// <summary>
/// Identifies the kind of audit evidence represented by a diff item.
/// </summary>
public enum ConfigAuditDiffItemKind
{
    /// <summary>A known audit entry registered with AppSurface Config.</summary>
    KnownEntry = 0,

    /// <summary>An enumerable provider-discovered key.</summary>
    DiscoveredKey = 1,

    /// <summary>A provider precedence or priority entry.</summary>
    Provider = 2,

    /// <summary>The report redaction policy metadata.</summary>
    RedactionPolicy = 3,

    /// <summary>A report, entry, or discovered-key diagnostic.</summary>
    Diagnostic = 4,

    /// <summary>A dictionary child entry whose report-local label is not safe enough for normal matching.</summary>
    DictionaryEntry = 5,

    /// <summary>Source provenance attached to otherwise matching evidence.</summary>
    Source = 6
}

/// <summary>
/// Selects how source paths should be rendered by <see cref="ConfigAuditDiffTextRenderer"/>.
/// </summary>
public enum ConfigAuditDiffSourceDetail
{
    /// <summary>Render summarized source paths such as file names by default.</summary>
    Summarized = 0,

    /// <summary>Render full source details from the sanitized audit report.</summary>
    Full = 1
}

/// <summary>
/// Explains how much value-level evidence is available for one compared item.
/// </summary>
public enum ConfigAuditDiffValueEvidence
{
    /// <summary>No value evidence was present or the item does not compare values.</summary>
    None = 0,

    /// <summary>Sanitized display values can be compared directly.</summary>
    DisplayValuesComparable = 1,

    /// <summary>Both reports redacted the value, so raw equality is unknown.</summary>
    BothRedacted = 2,

    /// <summary>One report redacted the value while the other rendered a display value.</summary>
    RedactedVersusShown = 3,

    /// <summary>At least one report omitted the display value, so raw equality is unknown.</summary>
    Omitted = 4,

    /// <summary>The reports used different redaction policy metadata, so value evidence must be reviewed carefully.</summary>
    RedactionPolicyMismatch = 5,

    /// <summary>The report used a default or manual enum value that does not prove how the value was rendered.</summary>
    Unspecified = 6
}

/// <summary>
/// Configures a config audit report comparison.
/// </summary>
/// <remarks>
/// Options affect evidence wording, renderer detail, and whether unchanged items are retained. They do not cause
/// provider resolution, host startup, or command execution.
/// </remarks>
public sealed class ConfigAuditDiffOptions
{
    /// <summary>
    /// Gets or sets the evidence mode for this comparison.
    /// </summary>
    public ConfigAuditDiffEvidenceMode EvidenceMode { get; set; } = ConfigAuditDiffEvidenceMode.SameHostNamedEnvironment;

    /// <summary>
    /// Gets or sets a value indicating whether unchanged items should be included in <see cref="ConfigAuditDiffReport.Items"/>.
    /// </summary>
    public bool IncludeUnchangedItems { get; set; }

    /// <summary>
    /// Gets or sets the source-detail level used by the text renderer.
    /// </summary>
    public ConfigAuditDiffSourceDetail SourceDetail { get; set; } = ConfigAuditDiffSourceDetail.Summarized;
}

/// <summary>
/// Describes the result of comparing two sanitized config audit reports.
/// </summary>
/// <remarks>
/// The report contains only evidence already present in the input audit reports plus comparison diagnostics. It is safe
/// to serialize as a support artifact subject to the same sensitivity rules as <see cref="ConfigAuditReport"/>: source
/// paths, provider names, environment names, and correlation identifiers may still be support-sensitive.
/// </remarks>
public sealed class ConfigAuditDiffReport
{
    /// <summary>Gets the baseline report environment.</summary>
    public required string BaselineEnvironment { get; init; }

    /// <summary>Gets the target report environment.</summary>
    public required string TargetEnvironment { get; init; }

    /// <summary>Gets the time when the diff report was generated.</summary>
    public required DateTimeOffset GeneratedAt { get; init; }

    /// <summary>Gets the evidence mode used for this comparison.</summary>
    public required ConfigAuditDiffEvidenceMode EvidenceMode { get; init; }

    /// <summary>Gets the source-detail level requested for renderer output.</summary>
    public required ConfigAuditDiffSourceDetail SourceDetail { get; init; }

    /// <summary>Gets aggregate item counts for this diff.</summary>
    public required ConfigAuditDiffSummary Summary { get; init; }

    /// <summary>Gets comparison diagnostics that apply to the whole diff.</summary>
    public IReadOnlyList<ConfigAuditComparisonDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>Gets deterministic diff items retained for rendering or programmatic inspection.</summary>
    public IReadOnlyList<ConfigAuditDiffItem> Items { get; init; } = [];
}

/// <summary>
/// Summarizes item counts for a config audit diff.
/// </summary>
public sealed class ConfigAuditDiffSummary
{
    /// <summary>Gets the number of changed items.</summary>
    public required int Changed { get; init; }

    /// <summary>Gets the number of added items.</summary>
    public required int Added { get; init; }

    /// <summary>Gets the number of removed items.</summary>
    public required int Removed { get; init; }

    /// <summary>Gets the number of unchanged items, even when unchanged items are not retained.</summary>
    public required int Unchanged { get; init; }

    /// <summary>Gets the number of uncomparable items.</summary>
    public required int Uncomparable { get; init; }

    /// <summary>Gets the number of comparison diagnostics.</summary>
    public required int Diagnostics { get; init; }
}

/// <summary>
/// Describes one display-safe config audit comparison diagnostic.
/// </summary>
/// <remarks>
/// Diagnostics explain uncertainty, duplicate evidence, default/manual enum values, and evidence-mode warnings. They
/// are intended for rendering and support triage, not for leaking raw provider exception messages.
/// </remarks>
public sealed class ConfigAuditComparisonDiagnostic
{
    /// <summary>Gets the diagnostic severity.</summary>
    public required ConfigAuditDiagnosticSeverity Severity { get; init; }

    /// <summary>Gets the stable diagnostic code.</summary>
    public required string Code { get; init; }

    /// <summary>Gets the display-safe diagnostic message.</summary>
    public required string Message { get; init; }

    /// <summary>Gets the affected item path or key, when available.</summary>
    public string? Key { get; init; }
}

/// <summary>
/// Describes one item in a config audit diff.
/// </summary>
/// <remarks>
/// Items keep baseline and target evidence separate so renderers can explain uncertainty without implying raw equality.
/// Empty source and diagnostic lists mean the original report did not provide that evidence for the item.
/// </remarks>
public sealed class ConfigAuditDiffItem
{
    /// <summary>Gets the kind of audit evidence represented by this item.</summary>
    public required ConfigAuditDiffItemKind Kind { get; init; }

    /// <summary>Gets the comparison status.</summary>
    public required ConfigAuditDiffItemStatus Status { get; init; }

    /// <summary>Gets the review significance.</summary>
    public required ConfigAuditDiffSignificance Significance { get; init; }

    /// <summary>Gets the deterministic item key or path.</summary>
    public required string Key { get; init; }

    /// <summary>Gets an operator-facing display-safe description.</summary>
    public required string Description { get; init; }

    /// <summary>Gets the baseline display value, when the audit report rendered one.</summary>
    public string? BaselineDisplayValue { get; init; }

    /// <summary>Gets the target display value, when the audit report rendered one.</summary>
    public string? TargetDisplayValue { get; init; }

    /// <summary>Gets the value-evidence classification.</summary>
    public ConfigAuditDiffValueEvidence ValueEvidence { get; init; } = ConfigAuditDiffValueEvidence.None;

    /// <summary>Gets baseline source records attached to this item.</summary>
    public IReadOnlyList<ConfigAuditSourceRecord> BaselineSources { get; init; } = [];

    /// <summary>Gets target source records attached to this item.</summary>
    public IReadOnlyList<ConfigAuditSourceRecord> TargetSources { get; init; } = [];

    /// <summary>Gets item-level comparison diagnostics.</summary>
    public IReadOnlyList<ConfigAuditComparisonDiagnostic> Diagnostics { get; init; } = [];
}
