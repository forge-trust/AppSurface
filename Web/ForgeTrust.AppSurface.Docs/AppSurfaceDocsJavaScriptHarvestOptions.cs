namespace ForgeTrust.AppSurface.Docs;

/// <summary>
/// JavaScript public API harvest path policy, parser, and strict-health settings.
/// </summary>
/// <remarks>
/// JavaScript harvesting is enabled by default and scans policy-approved <c>.js</c> files for explicit
/// <c>@public</c> browser-contract doclets. Unannotated JavaScript is ignored. Global
/// <see cref="AppSurfaceDocsHarvestOptions.Paths"/> rules apply first, then these source-specific include, exclude, and
/// default-exclusion settings refine the JavaScript candidate set. <see cref="IncludeGlobs"/> is an optional narrowing
/// boundary, not an enable switch. Tags such as <c>@internal</c>, <c>@private</c>, and <c>@ignore</c> always exclude a
/// doclet from the generated public API surface.
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
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets JavaScript-specific repository-relative include globs.
    /// </summary>
    /// <remarks>
    /// Patterns use forward-slash <c>Microsoft.Extensions.FileSystemGlobbing</c> matching. Examples include
    /// <c>Web/ForgeTrust.RazorWire/assets/contracts/razorwire-public-contracts.js</c> for a single file or
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
    /// <remarks>
    /// Broad default discovery always requires <c>@public</c>, even when this setting is <see langword="false" />. The
    /// compatibility escape applies only when <see cref="IncludeGlobs"/> contains at least one explicit JavaScript source
    /// boundary.
    /// </remarks>
    public bool RequirePublicTag { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether default JavaScript discovery should participate in strict aggregate harvest health.
    /// </summary>
    /// <remarks>
    /// The default is <see langword="false" />, which makes broad JavaScript discovery best-effort: empty results, parse
    /// failures, read failures, and timeouts produce diagnostics but do not mask or cause strict Markdown/C# harvest
    /// outcomes. JavaScript always participates in strict health when <see cref="IncludeGlobs"/> is nonempty.
    /// </remarks>
    public bool StrictHealth { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether incomplete public JavaScript event doclets should fail harvest health.
    /// </summary>
    /// <remarks>
    /// The default is <see langword="false" />, which preserves best-effort event documentation: incomplete public
    /// events render and emit <see cref="Models.DocHarvestDiagnosticCodes.JavaScriptIncompletePublicDoclet"/> warnings.
    /// Set this to <see langword="true" /> for CI and release verification when public browser events must document
    /// <c>@target</c>, <c>@firesWhen</c>, and either at least one valid <c>@property detail.*</c> field or
    /// <c>@detail none</c>. This option applies only to incomplete public event doclets and does not make parse,
    /// oversized-file, unsupported-shape, or malformed-doclet diagnostics strict; those remain controlled by
    /// <see cref="StrictHealth"/> or explicit <see cref="IncludeGlobs"/>.
    /// </remarks>
    public bool RequireCompleteEventDoclets { get; set; }

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
