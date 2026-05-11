namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Normalizes harvested documentation source paths into the canonical browser-facing routes used by RazorDocs.
/// </summary>
internal static class DocRoutePath
{
    /// <summary>
    /// Constructs a canonical browser-facing path for a harvested documentation source path.
    /// </summary>
    /// <param name="sourcePath">The harvested source path, optionally including a fragment.</param>
    /// <returns>
    /// The canonical docs route path, including the <c>.html</c> suffix RazorDocs serves to browsers and any original
    /// fragment identifier.
    /// </returns>
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
