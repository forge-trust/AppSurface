namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// Enables safe collection element traversal for a discovered configuration wrapper in audit reports.
/// </summary>
/// <remarks>
/// Apply this attribute to a <see cref="Config{T}"/> or <see cref="ConfigStruct{T}"/> wrapper when operators need
/// element-level visibility for that key. Attribute presence enables traversal; there is no separate enabled flag.
/// The limit properties mirror <see cref="ConfigAuditEntryOptions"/> and are validated when reports are built.
/// Invalid limits emit a <c>config-audit-options-invalid</c> diagnostic and fall back to safe bounded defaults.
/// Non-sensitive dictionary keys may be displayed by default, but sensitive-looking keys are always redacted before
/// structured reports or text output are returned. The attribute is inherited by derived wrappers; place a new
/// attribute on the derived wrapper when it needs different traversal limits. Dictionary key correlation stays disabled
/// unless <see cref="DictionaryKeyCorrelationMode"/> is set and global correlation key material is configured.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class ConfigAuditCollectionTraversalAttribute : Attribute
{
    /// <summary>
    /// Gets or sets the maximum nested collection depth traversed for the attributed wrapper.
    /// </summary>
    /// <remarks>
    /// The default is <see cref="ConfigAuditEntryOptions.DefaultMaxCollectionDepth"/>. Values must be greater than
    /// or equal to <c>0</c>; invalid values are reported by audit diagnostics and replaced with the safe default for
    /// traversal.
    /// </remarks>
    public int MaxCollectionDepth { get; set; } = ConfigAuditEntryOptions.DefaultMaxCollectionDepth;

    /// <summary>
    /// Gets or sets the maximum number of elements reported from any one traversed collection.
    /// </summary>
    /// <remarks>
    /// The default is <see cref="ConfigAuditEntryOptions.DefaultMaxCollectionElements"/>. Values must be greater than
    /// or equal to <c>0</c>; invalid values are reported by audit diagnostics and replaced with the safe default for
    /// traversal.
    /// </remarks>
    public int MaxCollectionElements { get; set; } = ConfigAuditEntryOptions.DefaultMaxCollectionElements;

    /// <summary>
    /// Gets or sets the maximum number of child nodes created for the attributed entry before traversal stops.
    /// </summary>
    /// <remarks>
    /// The default is <see cref="ConfigAuditEntryOptions.DefaultMaxReportNodes"/>. Values must be greater than or
    /// equal to <c>1</c>; invalid values are reported by audit diagnostics and replaced with the safe default for
    /// traversal.
    /// </remarks>
    public int MaxReportNodes { get; set; } = ConfigAuditEntryOptions.DefaultMaxReportNodes;

    /// <summary>
    /// Gets or sets a value indicating whether non-sensitive dictionary keys may appear as element labels.
    /// </summary>
    /// <remarks>
    /// The default is <see langword="true"/>. Sensitive-looking dictionary keys are redacted even when this property
    /// allows key labels to be displayed.
    /// </remarks>
    public bool DisplayDictionaryKeys { get; set; } = true;

    /// <summary>
    /// Gets or sets the opt-in dictionary key correlation mode for the attributed wrapper.
    /// </summary>
    /// <remarks>
    /// The default is <see cref="ConfigAuditDictionaryKeyCorrelationMode.None"/>, which keeps redacted dictionary key
    /// labels report-local. <see cref="ConfigAuditDictionaryKeyCorrelationMode.ScopedHmac"/> adds opaque correlation
    /// ids when <see cref="ConfigAuditDictionaryKeyCorrelationOptions"/> supplies valid key material.
    /// </remarks>
    public ConfigAuditDictionaryKeyCorrelationMode DictionaryKeyCorrelationMode { get; set; } = ConfigAuditDictionaryKeyCorrelationMode.None;

    /// <summary>
    /// Converts the attribute values into immutable audit entry options.
    /// </summary>
    /// <remarks>
    /// The returned options enable collection traversal, copy attribute values as-is, and mark every option as
    /// assigned with <see cref="ConfigAuditEntryOptionAssignments.All"/>. Marking every option assigned lets the
    /// wrapper attribute provide a complete wrapper-level policy while still allowing explicitly assigned manual
    /// registration options, including default-valued assignments, to override individual properties later.
    /// </remarks>
    internal ConfigAuditEntryOptions ToOptions() =>
        new(
            traverseCollectionElements: true,
            MaxCollectionDepth,
            MaxCollectionElements,
            MaxReportNodes,
            DisplayDictionaryKeys,
            DictionaryKeyCorrelationMode,
            ConfigAuditEntryOptionAssignments.All);
}
