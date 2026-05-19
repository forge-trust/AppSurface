namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Enumerates package-defined default exclusion groups used by harvest path policy evaluation.
/// </summary>
/// <remarks>
/// Member names are stable internal configuration IDs consumed by <c>DisabledGroups</c> and <c>AllowGlobs</c>.
/// Do not rename existing members without a migration plan; append new members for new groups.
/// </remarks>
internal enum AppSurfaceDocsHarvestDefaultExclusionGroup
{
    /// <summary>
    /// Excludes generated build output paths such as <c>bin</c>, <c>obj</c>, and dependency output trees.
    /// </summary>
    BuildOutput,

    /// <summary>
    /// Excludes hidden-directory paths whose directory segments begin with <c>.</c>.
    /// </summary>
    HiddenDirectories,

    /// <summary>
    /// Excludes test-project and test-fixture source paths.
    /// </summary>
    TestProjects,

    /// <summary>
    /// Excludes C# example application source while leaving Markdown examples harvestable.
    /// </summary>
    CSharpExampleSource
}
