namespace ForgeTrust.Runnable.Web.RazorWire.Cli;

/// <summary>
/// Describes one exporter-managed internal URL discovered while crawling HTML or CSS.
/// </summary>
/// <remarks>
/// <paramref name="RawValue"/> is the exact value found in markup or CSS. <paramref name="ResolvedUrl"/> is the
/// root-relative URL after resolving relative references against <paramref name="SourceRoute"/>. <paramref name="Path"/>
/// is the query-free route used for fetch de-duplication, while <paramref name="Query"/> and <paramref name="Fragment"/>
/// preserve the original URL shape for CDN validation and rewriting decisions.
/// </remarks>
internal sealed record ExportReference(
    string SourceRoute,
    ExportReferenceKind Kind,
    string RawValue,
    string ResolvedUrl,
    string Path,
    string Query,
    string Fragment)
{
    /// <summary>
    /// Gets a value indicating whether the reference points at an asset-like dependency rather than a page route.
    /// </summary>
    public bool IsAsset => Kind is ExportReferenceKind.ScriptSrc
        or ExportReferenceKind.LinkHref
        or ExportReferenceKind.ImgSrc
        or ExportReferenceKind.ImgSrcSet
        or ExportReferenceKind.CssUrl;
}
