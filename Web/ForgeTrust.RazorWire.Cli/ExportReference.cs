namespace ForgeTrust.RazorWire.Cli;

/// <summary>
/// Describes one exporter-managed internal URL discovered while crawling HTML or CSS.
/// </summary>
/// <remarks>
/// <paramref name="RawValue"/> is the exact value found in markup or CSS after outer attribute/token whitespace is trimmed.
/// <paramref name="ResolvedUrl"/> is the root-relative URL after resolving relative references against
/// <paramref name="SourceRoute"/>. <paramref name="Path"/> is the query-free route used for fetch de-duplication, while
/// <paramref name="Query"/> and <paramref name="Fragment"/> preserve the original URL shape for CDN validation and rewriting
/// decisions. <paramref name="Role"/> records whether the reference must be exported as a static asset, can remain a live
/// route, or should be validated as a page route. <paramref name="Provenance"/> records the HTML element/attribute or CSS
/// token that produced the reference so validation diagnostics can explain what to fix.
/// </remarks>
/// <param name="SourceRoute">The route whose exported content contained the reference.</param>
/// <param name="Kind">The HTML attribute or CSS token shape that produced the reference.</param>
/// <param name="Role">The export behavior assigned to the reference after classification.</param>
/// <param name="RawValue">The discovered URL value after outer whitespace is removed.</param>
/// <param name="ResolvedUrl">The root-relative URL resolved from <paramref name="RawValue"/>.</param>
/// <param name="Path">The query-free path used for fetch and validation lookups.</param>
/// <param name="Query">The query component retained for diagnostics and rewrite decisions.</param>
/// <param name="Fragment">The fragment component retained for diagnostics and rewrite decisions.</param>
/// <param name="Provenance">Optional source metadata for diagnostics that point back to the originating token.</param>
/// <param name="LinkMetadata">Optional <c>link</c> attributes used to explain preload, modulepreload, and stylesheet classification.</param>
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
    /// <remarks>
    /// Page and live-route references can still be valid internal URLs, but they are not copied into the static asset
    /// output set and should not be treated as required files during hybrid validation.
    /// </remarks>
    public bool IsAsset => Role == ExportReferenceRole.StaticAsset;

    /// <summary>
    /// Determines whether the selected export mode requires this reference to materialize as an exported static asset.
    /// </summary>
    /// <remarks>
    /// CDN and hybrid exports both rewrite static assets to materialized files, but page routes and live routes are
    /// validated through their route outcomes instead. This method intentionally keys off <see cref="Role"/> so supported
    /// <c>link</c> hints that resolve to page routes do not become false missing-asset diagnostics.
    /// </remarks>
    /// <param name="mode">The active export mode.</param>
    /// <returns><see langword="true"/> when missing materialization should fail validation.</returns>
    public bool RequiresStaticMaterialization(ExportMode mode)
        => (mode is ExportMode.Cdn or ExportMode.Hybrid) && Role == ExportReferenceRole.StaticAsset;
}
