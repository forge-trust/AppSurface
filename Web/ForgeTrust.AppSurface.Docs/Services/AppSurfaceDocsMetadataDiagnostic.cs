using ForgeTrust.AppSurface.Docs.Models;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Describes one non-fatal AppSurface Docs metadata authoring problem discovered while parsing or normalizing metadata.
/// </summary>
/// <param name="Code">Stable diagnostic code suitable for tests, logs, and documentation.</param>
/// <param name="FieldPath">Metadata field path associated with the warning, such as <c>featured_page_groups[0].pages</c>.</param>
/// <param name="Problem">Reader-facing summary of what is wrong.</param>
/// <param name="Cause">Explanation of why AppSurface Docs cannot safely use the authored value as-is.</param>
/// <param name="Fix">Suggested author action that resolves the warning.</param>
internal sealed record AppSurfaceDocsMetadataDiagnostic(
    string Code,
    string FieldPath,
    string Problem,
    string Cause,
    string Fix);

/// <summary>
/// Carries normalized metadata together with non-fatal diagnostics from a Markdown metadata parse.
/// </summary>
/// <param name="Metadata">The parsed metadata, or <c>null</c> when no usable metadata document was present.</param>
/// <param name="Diagnostics">Warnings produced while parsing or normalizing metadata fields.</param>
internal sealed record MarkdownMetadataParseResult(
    DocMetadata? Metadata,
    IReadOnlyList<AppSurfaceDocsMetadataDiagnostic> Diagnostics)
{
    /// <summary>
    /// Gets the strict inline opt-in result for protected Markdown download.
    /// </summary>
    /// <remarks>
    /// This remains internal parsing state. It lets the Markdown harvester reuse the same front-matter split and YAML
    /// validation already required for rendered metadata instead of reparsing the document solely for raw-source eligibility.
    /// Sidecar parsing leaves the default <see cref="MarkdownDownloadEligibility.NotDeclared"/> value because sidecars
    /// cannot grant download access.
    /// </remarks>
    public MarkdownDownloadEligibility DownloadEligibility { get; init; } = MarkdownDownloadEligibility.NotDeclared;
}
