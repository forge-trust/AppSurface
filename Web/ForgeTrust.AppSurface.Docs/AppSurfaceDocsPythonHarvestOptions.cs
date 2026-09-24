namespace ForgeTrust.AppSurface.Docs;

/// <summary>
/// Python source path policy, static docstring parser, and harvest-health settings.
/// </summary>
/// <remarks>
/// Python harvesting is enabled by default but requires at least one explicit <see cref="IncludeGlobs"/> entry before
/// it reads source. This keeps the package inert in existing .NET-only repositories while making a deliberate Python
/// boundary a one-setting opt-in. The harvester parses source with Tree-sitter; it never starts Python, imports a
/// module, discovers an environment, or evaluates a docstring. Global <see cref="AppSurfaceDocsHarvestOptions.Paths"/>
/// rules apply first, then these source-specific settings refine the candidate set.
/// </remarks>
public sealed class AppSurfaceDocsPythonHarvestOptions
{
    /// <summary>
    /// Gets the default maximum Python source size that the harvester will read and parse.
    /// </summary>
    public const long DefaultMaxFileSizeBytes = 262_144;

    /// <summary>
    /// Gets or sets a value indicating whether Python docstring harvesting is enabled.
    /// </summary>
    /// <remarks>
    /// The default is <see langword="true"/>, but an enabled harvester with no usable <see cref="IncludeGlobs"/> entry
    /// publishes no source-derived pages and reports actionable nonfatal guidance. Set this to <see langword="false"/>
    /// when a host never intends to document Python source.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets repository-relative Python source boundaries.
    /// </summary>
    /// <remarks>
    /// At least one nonblank, policy-valid pattern is required to read Python source. Patterns use forward-slash
    /// <c>Microsoft.Extensions.FileSystemGlobbing</c> matching. For example, use <c>src/worker/**/*.py</c> to document a
    /// sidecar package or <c>tools/automation.py</c> for one embedded script. Includes compose with global includes
    /// using AND semantics, and symlink or junction candidates are never followed.
    /// </remarks>
    public string[] IncludeGlobs { get; set; } = [];

    /// <summary>
    /// Gets or sets Python-specific repository-relative exclusion patterns.
    /// </summary>
    /// <remarks>
    /// Exclusions win over includes and default-exclusion allows. Prefer an exclusion for generated or vendored Python
    /// rather than raising <see cref="MaxFileSizeBytes"/>.
    /// </remarks>
    public string[] ExcludeGlobs { get; set; } = [];

    /// <summary>
    /// Gets or sets Python-specific default-exclusion group controls.
    /// </summary>
    public AppSurfaceDocsHarvestDefaultExclusionOptions DefaultExclusions { get; set; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether Python diagnostics should participate in aggregate strict harvest health.
    /// </summary>
    /// <remarks>
    /// Python participates automatically when <see cref="IncludeGlobs"/> contains an explicit source boundary. Set this
    /// to <see langword="true"/> to make its diagnostics strict even before a boundary is configured.
    /// </remarks>
    public bool StrictHealth { get; set; }

    /// <summary>
    /// Gets or sets the largest Python file, in bytes, that the harvester will read and parse.
    /// </summary>
    /// <remarks>
    /// Oversized files are skipped with a diagnostic before source parsing. The value must be greater than zero. This
    /// is an input budget for authored Python source, not a claim about the Tree-sitter parser's own maximum input.
    /// </remarks>
    public long MaxFileSizeBytes { get; set; } = DefaultMaxFileSizeBytes;
}
