namespace ForgeTrust.AppSurface.Docs;

/// <summary>
/// Global repository-relative harvest path policy settings shared by every built-in AppSurface Docs harvester.
/// </summary>
/// <remarks>
/// Patterns use normalized repository-relative paths with <c>/</c> separators. Empty include globs mean "use each
/// harvester's normal candidate set." Nonempty include globs define the global public-docs territory that
/// source-specific include globs can refine but cannot bypass. Exclude globs are final denials and win over includes
/// and default-exclusion allows.
/// </remarks>
public sealed class AppSurfaceDocsHarvestPathOptions
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
    public AppSurfaceDocsHarvestDefaultExclusionOptions DefaultExclusions { get; set; } = new();

    /// <summary>
    /// Gets or sets repository-owned VCS ignore file integration for source harvesting.
    /// </summary>
    /// <remarks>
    /// AppSurface Docs reads Git <c>.gitignore</c> files under the resolved repository root by default so legacy
    /// generated, vendored, and package-manager-owned trees do not become published documentation accidentally.
    /// Use <see cref="AppSurfaceDocsHarvestVcsIgnoreOptions.AllowGlobs"/> for selected public documentation that
    /// intentionally lives under an ignored path, or set <see cref="AppSurfaceDocsHarvestVcsIgnoreOptions.Enabled"/>
    /// to <see langword="false" /> when a host wants to ignore repository VCS policy entirely.
    /// </remarks>
    public AppSurfaceDocsHarvestVcsIgnoreOptions VcsIgnore { get; set; } = new();
}

/// <summary>
/// Controls how AppSurface Docs applies repository-owned Git ignore files during source harvesting.
/// </summary>
/// <remarks>
/// This option is intentionally scoped to Git <c>.gitignore</c> files that live under the resolved source root.
/// AppSurface Docs does not read machine-local Git excludes, global Git excludes, or <c>.git/info/exclude</c> in v1 so
/// harvest behavior stays reproducible across local development, CI, static export, and package consumers.
/// </remarks>
public sealed class AppSurfaceDocsHarvestVcsIgnoreOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether Git <c>.gitignore</c> files should participate in harvest filtering.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets AppSurface repository-relative globs that restore selected paths excluded only by VCS ignore rules.
    /// </summary>
    /// <remarks>
    /// These patterns use the same safe AppSurface glob syntax as <see cref="AppSurfaceDocsHarvestPathOptions.IncludeGlobs"/>
    /// and <see cref="AppSurfaceDocsHarvestPathOptions.ExcludeGlobs"/>. They are not Git-ignore patterns. An allow glob
    /// can neutralize a VCS-ignore exclusion, but it cannot override AppSurface default exclusions or configured
    /// AppSurface exclude globs.
    /// </remarks>
    public string[] AllowGlobs { get; set; } = [];
}
