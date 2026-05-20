namespace ForgeTrust.AppSurface.Docs;

/// <summary>
/// C# API-reference harvest path policy settings.
/// </summary>
/// <remarks>
/// These options refine <see cref="AppSurfaceDocsHarvestOptions.Paths"/> for C# source candidates. Source-specific includes
/// compose with global includes using AND semantics, and source-specific excludes win over includes and
/// default-exclusion allows. The built-in C# harvester also has a C#-only default group that excludes example
/// application source while still allowing public example README files to be harvested by Markdown.
/// </remarks>
public sealed class AppSurfaceDocsCSharpHarvestOptions
{
    /// <summary>
    /// Gets or sets C#-specific include globs.
    /// </summary>
    public string[] IncludeGlobs { get; set; } = [];

    /// <summary>
    /// Gets or sets C#-specific exclude globs.
    /// </summary>
    public string[] ExcludeGlobs { get; set; } = [];

    /// <summary>
    /// Gets or sets C#-specific default-exclusion group controls.
    /// </summary>
    public AppSurfaceDocsHarvestDefaultExclusionOptions DefaultExclusions { get; set; } = new();
}
