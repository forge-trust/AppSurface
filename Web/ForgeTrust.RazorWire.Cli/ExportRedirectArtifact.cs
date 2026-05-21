namespace ForgeTrust.RazorWire.Cli;

/// <summary>
/// Describes a static redirect artifact that should be written after the canonical export route is materialized.
/// </summary>
/// <param name="AliasRoute">
/// Root-relative route whose static artifact should redirect, such as <c>/docs/examples/app/README.md.html</c>.
/// </param>
/// <param name="CanonicalRoute">
/// Root-relative canonical route that must have been exported before the redirect artifact is written.
/// </param>
/// <remarks>
/// Redirect artifacts are part of the export graph so CDN validation can reject collisions and missing canonical pages
/// before files are overwritten. They are not crawler seeds and they do not imply HTTP redirect semantics on generic
/// static hosts; they materialize as tiny HTML fallback pages.
/// </remarks>
public sealed record ExportRedirectArtifact(string AliasRoute, string CanonicalRoute);
