namespace ForgeTrust.RazorWire.Cli;

/// <summary>
/// Describes a redirect alias registration that should be materialized after the canonical export route is proven.
/// </summary>
/// <param name="AliasRoute">
/// Root-relative alias route that should redirect, such as <c>/docs/examples/app/README.md.html</c>.
/// </param>
/// <param name="CanonicalRoute">
/// Root-relative canonical route that must have been exported before the redirect registration is materialized.
/// </param>
/// <remarks>
/// Redirect registrations are part of the export graph so validation can reject collisions, missing canonical pages, and
/// provider-specific output conflicts before files are overwritten. They are not crawler seeds. The selected
/// <see cref="ExportRedirectStrategy"/> decides whether registrations become generic HTML fallback files or provider
/// redirect rules such as Netlify's root <c>_redirects</c> file.
/// </remarks>
public sealed record ExportRedirectArtifact(string AliasRoute, string CanonicalRoute);
