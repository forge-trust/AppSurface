namespace ForgeTrust.AppSurface.Docs;

/// <summary>
/// Global repository-relative harvest path policy settings shared by every built-in RazorDocs harvester.
/// </summary>
/// <remarks>
/// Patterns use normalized repository-relative paths with <c>/</c> separators. Empty include globs mean "use each
/// harvester's normal candidate set." Nonempty include globs define the global public-docs territory that
/// source-specific include globs can refine but cannot bypass. Exclude globs are final denials and win over includes
/// and default-exclusion allows.
/// </remarks>
public sealed class RazorDocsHarvestPathOptions
{
    /// <summary>
    /// Gets or sets optional repository-wide include globs.
    /// </summary>
    public string[] IncludeGlobs { get; set; } = [];

    /// <summary>
    /// Gets or sets repository-wide exclude globs that win over includes and default-exclusion allows.
    /// </summary>
    public string[] ExcludeGlobs { get; set; } = [];

    /// <summary>
    /// Gets or sets controls for package-defined default exclusion groups.
    /// </summary>
    public RazorDocsHarvestDefaultExclusionOptions DefaultExclusions { get; set; } = new();
}
