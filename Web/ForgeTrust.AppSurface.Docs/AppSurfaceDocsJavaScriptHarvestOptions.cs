namespace ForgeTrust.AppSurface.Docs;

/// <summary>
/// JavaScript public API harvest path policy and parser settings.
/// </summary>
/// <remarks>
/// JavaScript harvesting is disabled by default and requires at least one source-specific
/// <see cref="IncludeGlobs"/> entry when enabled. That explicit boundary prevents AppSurface Docs from crawling every
/// authored browser asset in a repository. Global <see cref="AppSurfaceDocsHarvestOptions.Paths"/> rules apply first,
/// then these source-specific include, exclude, and default-exclusion settings refine the JavaScript candidate set.
/// The parser requires explicit <c>@public</c> doclets by default, and tags such as <c>@internal</c>,
/// <c>@private</c>, and <c>@ignore</c> always exclude a doclet from the generated public API surface.
/// </remarks>
public sealed class AppSurfaceDocsJavaScriptHarvestOptions
{
    /// <summary>
    /// Gets the default maximum JavaScript source size that the harvester will parse.
    /// </summary>
    public const long DefaultMaxFileSizeBytes = 262_144;

    /// <summary>
    /// Gets the default JavaScript-specific exclude globs.
    /// </summary>
    public static readonly string[] DefaultExcludeGlobs = ["**/*.min.js"];

    /// <summary>
    /// Gets or sets a value indicating whether JavaScript public API harvesting is enabled.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets JavaScript-specific repository-relative include globs.
    /// </summary>
    /// <remarks>
    /// Patterns use forward-slash <c>Microsoft.Extensions.FileSystemGlobbing</c> matching. Examples include
    /// <c>Web/ForgeTrust.RazorWire/wwwroot/razorwire/razorwire.js</c> for a single file or
    /// <c>src/widgets/**/*.js</c> for a bounded source tree. Include globs compose with global includes using AND
    /// semantics. Blank entries are ignored during options normalization.
    /// </remarks>
    public string[] IncludeGlobs { get; set; } = [];

    /// <summary>
    /// Gets or sets JavaScript-specific repository-relative exclude globs.
    /// </summary>
    /// <remarks>
    /// Excludes win over includes and default-exclusion allows. The default skips minified bundles so generated vendor
    /// assets do not dominate parse time or create noisy public API diagnostics. Build output, hidden directories, and
    /// test directories are controlled through <see cref="DefaultExclusions"/>.
    /// </remarks>
    public string[] ExcludeGlobs { get; set; } = [.. DefaultExcludeGlobs];

    /// <summary>
    /// Gets or sets JavaScript-specific default-exclusion group controls.
    /// </summary>
    public AppSurfaceDocsHarvestDefaultExclusionOptions DefaultExclusions { get; set; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether supported doclets must include <c>@public</c>.
    /// </summary>
    public bool RequirePublicTag { get; set; } = true;

    /// <summary>
    /// Gets or sets the largest JavaScript file, in bytes, that the harvester will parse.
    /// </summary>
    /// <remarks>
    /// Oversized files are skipped with a harvest diagnostic so generated bundles do not dominate snapshot time or make
    /// parser failures noisy. The default comes from the parser decision spike and covers the current RazorWire and
    /// AppSurface Docs authored browser assets with headroom.
    /// </remarks>
    public long MaxFileSizeBytes { get; set; } = DefaultMaxFileSizeBytes;
}
