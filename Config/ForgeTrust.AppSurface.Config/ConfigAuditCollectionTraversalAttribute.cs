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
/// attribute on the derived wrapper when it needs different traversal limits.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class ConfigAuditCollectionTraversalAttribute : Attribute
{
    /// <summary>
    /// Gets or sets the maximum nested collection depth traversed for the attributed wrapper.
    /// </summary>
    public int MaxCollectionDepth { get; set; } = ConfigAuditEntryOptions.DefaultMaxCollectionDepth;

    /// <summary>
    /// Gets or sets the maximum number of elements reported from any one traversed collection.
    /// </summary>
    public int MaxCollectionElements { get; set; } = ConfigAuditEntryOptions.DefaultMaxCollectionElements;

    /// <summary>
    /// Gets or sets the maximum number of child nodes created for the attributed entry before traversal stops.
    /// </summary>
    public int MaxReportNodes { get; set; } = ConfigAuditEntryOptions.DefaultMaxReportNodes;

    /// <summary>
    /// Gets or sets a value indicating whether non-sensitive dictionary keys may appear as element labels.
    /// </summary>
    public bool DisplayDictionaryKeys { get; set; } = true;

    internal ConfigAuditEntryOptions ToOptions() =>
        new(
            traverseCollectionElements: true,
            MaxCollectionDepth,
            MaxCollectionElements,
            MaxReportNodes,
            DisplayDictionaryKeys,
            ConfigAuditEntryOptionAssignments.All);
}
