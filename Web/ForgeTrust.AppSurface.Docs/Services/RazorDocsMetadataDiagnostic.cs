using ForgeTrust.AppSurface.Docs.Models;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Describes one non-fatal RazorDocs metadata authoring problem discovered while parsing or normalizing metadata.
/// </summary>
/// <param name="Code">Stable diagnostic code suitable for tests, logs, and documentation.</param>
/// <param name="FieldPath">Metadata field path associated with the warning, such as <c>featured_page_groups[0].pages</c>.</param>
/// <param name="Problem">Reader-facing summary of what is wrong.</param>
/// <param name="Cause">Explanation of why RazorDocs cannot safely use the authored value as-is.</param>
/// <param name="Fix">Suggested author action that resolves the warning.</param>
internal sealed record RazorDocsMetadataDiagnostic(
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
    IReadOnlyList<RazorDocsMetadataDiagnostic> Diagnostics);
