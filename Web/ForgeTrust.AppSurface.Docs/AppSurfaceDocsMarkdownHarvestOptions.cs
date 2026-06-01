namespace ForgeTrust.AppSurface.Docs;

/// <summary>
/// Markdown-specific harvest path policy and resource-limit settings.
/// </summary>
/// <remarks>
/// These options refine <see cref="AppSurfaceDocsHarvestOptions.Paths"/> for Markdown source files and the root
/// <c>LICENSE</c> candidate. Source-specific includes compose with global includes using AND semantics, and
/// source-specific excludes win over includes and default-exclusion allows. The byte limits are pre-read guards for
/// Markdown body files and paired metadata sidecars; they do not limit Markdig parser complexity, AST depth, execution
/// time, or cancellation behavior after a file has been admitted for parsing.
/// </remarks>
public sealed class AppSurfaceDocsMarkdownHarvestOptions
{
    /// <summary>
    /// Gets the default maximum Markdown body size that the harvester will read and parse.
    /// </summary>
    public const long DefaultMaxFileSizeBytes = 1_048_576;

    /// <summary>
    /// Gets the default maximum paired Markdown metadata sidecar size that the harvester will read and parse.
    /// </summary>
    public const long DefaultMaxMetadataFileSizeBytes = 65_536;

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
    public AppSurfaceDocsHarvestDefaultExclusionOptions DefaultExclusions { get; set; } = new();

    /// <summary>
    /// Gets or sets the largest Markdown body file, in bytes, that the harvester will read and parse.
    /// </summary>
    /// <remarks>
    /// The default is <see cref="DefaultMaxFileSizeBytes"/> bytes. Files that exceed this limit are skipped before the
    /// file is read, before inline front matter is parsed, and before Markdig receives the Markdown body. Inline front
    /// matter is part of the Markdown body file and is therefore governed by this limit. Use path excludes for generated
    /// or accidental large files, and raise this limit only for intentional authored Markdown pages.
    /// </remarks>
    public long MaxFileSizeBytes { get; set; } = DefaultMaxFileSizeBytes;

    /// <summary>
    /// Gets or sets the largest paired Markdown metadata sidecar, in bytes, that the harvester will read and parse.
    /// </summary>
    /// <remarks>
    /// The default is <see cref="DefaultMaxMetadataFileSizeBytes"/> bytes. Paired <c>.md.yml</c> and <c>.md.yaml</c>
    /// metadata sidecars that exceed this limit are ignored before YAML parsing, while the Markdown body continues to
    /// publish when it is within <see cref="MaxFileSizeBytes"/>. This setting does not apply to inline front matter.
    /// </remarks>
    public long MaxMetadataFileSizeBytes { get; set; } = DefaultMaxMetadataFileSizeBytes;
}
