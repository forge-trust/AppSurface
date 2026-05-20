namespace ForgeTrust.AppSurface.Docs;

/// <summary>
/// Controls package-defined default harvest exclusion groups.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DisabledGroups"/> disables selected default groups by stable group ID. <see cref="AllowGlobs"/> opts
/// paths back into selected default groups by stable group ID. Unknown group IDs fail options validation.
/// </para>
/// <para>
/// Default-exclusion allows are group-aware. If a path matches multiple enabled default groups, every matched group
/// must either be disabled or have an allow glob matching the path. Configured excludes still win after an allow.
/// </para>
/// </remarks>
public sealed class AppSurfaceDocsHarvestDefaultExclusionOptions
{
    /// <summary>
    /// Gets or sets default group IDs to disable for the containing scope.
    /// </summary>
    public string[] DisabledGroups { get; set; } = [];

    /// <summary>
    /// Gets or sets allow globs by default group ID.
    /// </summary>
    public Dictionary<string, string[]> AllowGlobs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
