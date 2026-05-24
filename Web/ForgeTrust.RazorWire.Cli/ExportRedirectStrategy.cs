namespace ForgeTrust.RazorWire.Cli;

/// <summary>
/// Selects how registered redirect aliases are materialized during static export.
/// </summary>
/// <remarks>
/// <see cref="Html"/> is the default because it works on generic static hosts, including GitHub Pages, by writing tiny
/// alias HTML files that point at canonical artifacts. <see cref="Netlify"/> writes a root <c>_redirects</c> file for
/// Netlify-compatible static hosting and does not emit alias HTML files. Netlify redirect output is intended for
/// <see cref="ExportMode.Cdn"/> exports only because the rules point at publish-root static routes. Netlify rules reserve
/// the root <c>_redirects</c> file, reject aliases that serialize to their own canonical target, and reject aliases that
/// serialize to the same provider source path while pointing at different targets. New values must only be appended so
/// existing serialized, configured, or logged enum values remain stable.
/// </remarks>
public enum ExportRedirectStrategy
{
    /// <summary>
    /// Materialize each redirect alias as an HTML fallback artifact.
    /// </summary>
    Html = 0,

    /// <summary>
    /// Materialize redirect aliases as exact Netlify <c>_redirects</c> rules.
    /// </summary>
    Netlify = 1
}
