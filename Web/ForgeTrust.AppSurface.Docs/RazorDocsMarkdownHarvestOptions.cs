namespace ForgeTrust.AppSurface.Docs;

/// <summary>
/// Markdown-specific harvest path policy settings.
/// </summary>
/// <remarks>
/// These options refine <see cref="RazorDocsHarvestOptions.Paths"/> for Markdown source files and the root
/// <c>LICENSE</c> candidate. Source-specific includes compose with global includes using AND semantics, and
/// source-specific excludes win over includes and default-exclusion allows.
/// </remarks>
public sealed class RazorDocsMarkdownHarvestOptions
{
    /// <summary>
    /// Gets or sets Markdown-specific include globs.
    /// </summary>
    public string[] IncludeGlobs { get; set; } = [];

    /// <summary>
    /// Gets or sets Markdown-specific exclude globs.
    /// </summary>
    public string[] ExcludeGlobs { get; set; } = [];

    /// <summary>
    /// Gets or sets Markdown-specific default-exclusion group controls.
    /// </summary>
    public RazorDocsHarvestDefaultExclusionOptions DefaultExclusions { get; set; } = new();
}
