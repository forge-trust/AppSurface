namespace ForgeTrust.RazorWire.Cli;

/// <summary>
/// Describes one exporter-managed internal URL discovered while crawling HTML or CSS.
/// </summary>
/// <remarks>
/// <paramref name="RawValue"/> is the exact value found in markup or CSS after outer attribute/token whitespace is trimmed.
/// <paramref name="ResolvedUrl"/> is the root-relative URL after resolving relative references against
/// <paramref name="SourceRoute"/>. <paramref name="Path"/> is the query-free route used for fetch de-duplication, while
/// <paramref name="Query"/> and <paramref name="Fragment"/> preserve the original URL shape for CDN validation and rewriting
/// decisions. <paramref name="Provenance"/> records the HTML element/attribute or CSS token that produced the reference so
/// validation diagnostics can explain what to fix.
/// </remarks>
internal sealed record ExportReference(
    string SourceRoute,
    ExportReferenceKind Kind,
    ExportReferenceRole Role,
    string RawValue,
    string ResolvedUrl,
    string Path,
    string Query,
    string Fragment,
    ExportReferenceProvenance? Provenance = null,
    ExportReferenceLinkMetadata? LinkMetadata = null)
{
    /// <summary>
    /// Gets a value indicating whether the reference points at a browser-delivered static asset.
    /// </summary>
    public bool IsAsset => Role == ExportReferenceRole.StaticAsset;

    /// <summary>
    /// Determines whether the selected export mode requires this reference to materialize as an exported static asset.
    /// </summary>
    /// <param name="mode">The active export mode.</param>
    /// <returns><see langword="true"/> when missing materialization should fail validation.</returns>
    public bool RequiresStaticMaterialization(ExportMode mode)
        => (mode is ExportMode.Cdn or ExportMode.Hybrid) && Role == ExportReferenceRole.StaticAsset;
}
