namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Describes the rule outcome that caused a harvest path decision or trace entry.
/// </summary>
internal enum AppSurfaceDocsHarvestPathDecisionCode
{
    /// <summary>
    /// The path matched the built-in candidate set without requiring configured includes or default-group allows.
    /// </summary>
    IncludedByDefaultCandidate,

    /// <summary>
    /// The path matched a global include glob from <c>AppSurfaceDocs:Harvest:Paths:IncludeGlobs</c>.
    /// </summary>
    IncludedByGlobalInclude,

    /// <summary>
    /// The path matched a source-specific include glob after satisfying any global include boundary.
    /// </summary>
    IncludedBySourceInclude,

    /// <summary>
    /// The path matched a default exclusion group and was restored by a group-specific allow glob.
    /// </summary>
    IncludedByDefaultGroupAllow,

    /// <summary>
    /// The path was rejected because it was not a safe normalized repository-relative path.
    /// </summary>
    ExcludedByInvalidPath,

    /// <summary>
    /// The path was rejected because it is not a built-in candidate for the requested source kind.
    /// </summary>
    ExcludedByBaseCandidate,

    /// <summary>
    /// A global include boundary was configured, but the path did not match it.
    /// </summary>
    ExcludedByGlobalIncludeMiss,

    /// <summary>
    /// A source-specific include boundary was configured, but the path did not match it.
    /// </summary>
    ExcludedBySourceIncludeMiss,

    /// <summary>
    /// The path matched an enabled default exclusion group without a matching group allow.
    /// </summary>
    ExcludedByDefaultGroup,

    /// <summary>
    /// The path matched a global exclude glob after include and default-group processing.
    /// </summary>
    ExcludedByGlobalExclude,

    /// <summary>
    /// The path matched a source-specific exclude glob after global policy processing.
    /// </summary>
    ExcludedBySourceExclude
}
