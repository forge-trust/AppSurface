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
    /// Gets the default maximum C# source size that the harvester will read and parse.
    /// </summary>
    public const long DefaultMaxFileSizeBytes = 1_048_576;

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

    /// <summary>
    /// Gets or sets the largest C# file, in bytes, that the harvester will read and parse.
    /// </summary>
    /// <remarks>
    /// Oversized files are skipped with a harvest diagnostic before Roslyn parsing so generated or adversarial source
    /// cannot dominate harvest memory. Prefer excluding generated source with <see cref="ExcludeGlobs"/> instead of
    /// raising this limit. The value must be greater than zero; zero and negative values are invalid and are rejected
    /// during options validation. The <see cref="DefaultMaxFileSizeBytes"/> default is sized for authored API source,
    /// not as a Roslyn parser safety threshold.
    /// </remarks>
    public long MaxFileSizeBytes { get; set; } = DefaultMaxFileSizeBytes;
}
