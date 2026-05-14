namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Normalizes harvested non-Markdown source paths into legacy browser-facing routes used by RazorDocs.
/// </summary>
/// <remarks>
/// A harvested source path is the repository-relative or generated path assigned to a <c>DocNode</c> before RazorDocs
/// publishes it. This helper is intentionally narrow: it exists for generated or imported non-Markdown sources such as
/// XML API reference pages, generated JSON or YAML API specs, and imported HTML fragments that still use the historical
/// <c>.html</c> route shape. Markdown documents should use <see cref="DocRouteIdentityCatalog"/> instead, because that
/// catalog owns clean public routes, redirect aliases, collisions, and reserved-route diagnostics.
/// </remarks>
internal static class DocRoutePath
{
    /// <summary>
    /// Constructs a legacy browser-facing path for a harvested non-Markdown documentation source path.
    /// </summary>
    /// <param name="sourcePath">The harvested source path, optionally including a fragment.</param>
    /// <returns>
    /// The legacy docs route path, including the <c>.html</c> suffix used by generated API docs and any original
    /// fragment identifier. Markdown public routes are owned by <see cref="DocRouteIdentityCatalog"/>.
    /// </returns>
    /// <remarks>
    /// Use this method only when a source must keep the generated-docs compatibility contract where
    /// <c>Namespaces/Foo.Bar</c> becomes <c>Namespaces/Foo.Bar.html</c>. It trims leading and trailing separators,
    /// preserves fragments, normalizes backslashes to slashes, and appends <c>.html</c> unless the final file name already
    /// has that suffix. The word "canonical" in the method name refers to this legacy generated-doc route
    /// canonicalization; it does not mean the clean Markdown route contract. For authored Markdown pages, callers should
    /// query <see cref="DocRouteIdentityCatalog"/> so explicit <c>canonical_slug</c>, redirect aliases, public-route
    /// collisions, and reserved-route checks all remain centralized.
    /// </remarks>
    internal static string BuildCanonicalPath(string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);

        var hashIndex = sourcePath.IndexOf('#');
        var fragment = hashIndex >= 0 ? sourcePath[hashIndex..] : string.Empty;
        var trimmed = NormalizeLookupPath(sourcePath);
        if (string.IsNullOrEmpty(trimmed))
        {
            return "index.html" + fragment;
        }

        var directory = Path.GetDirectoryName(trimmed);
        if (!string.IsNullOrEmpty(directory))
        {
            directory = directory.Replace('\\', '/');
        }

        var fileName = Path.GetFileName(trimmed);
        var safeFileName = fileName.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
            ? fileName
            : fileName + ".html";
        return (string.IsNullOrEmpty(directory) ? safeFileName : $"{directory}/{safeFileName}") + fragment;
    }

    private static string NormalizeLookupPath(string path)
    {
        var sanitized = path.Trim().Replace('\\', '/').Trim('/');
        var hashIndex = sanitized.IndexOf('#');
        if (hashIndex >= 0)
        {
            sanitized = sanitized[..hashIndex];
        }

        return sanitized;
    }
}
