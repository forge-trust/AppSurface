namespace ForgeTrust.AppSurface.Release;

/// <summary>
/// Repository path helper for release-owned files.
/// </summary>
/// <remarks>
/// Paths are rooted under <see cref="RepositoryRoot"/> and accept slash-separated repository-relative inputs.
/// <see cref="PathFor"/> rejects rooted paths and traversal that would escape the repository. Use <see cref="IsUnderPath"/>
/// when checking paths that come from command-line input, temporary files, or other untrusted sources.
/// </remarks>
internal sealed class ReleaseWorkspace
{
    /// <summary>
    /// Creates a workspace rooted at a repository directory.
    /// </summary>
    /// <param name="repositoryRoot">Absolute repository root.</param>
    internal ReleaseWorkspace(string repositoryRoot)
    {
        RepositoryRoot = Path.GetFullPath(repositoryRoot);
    }

    /// <summary>
    /// Gets the absolute repository root.
    /// </summary>
    internal string RepositoryRoot { get; }

    /// <summary>
    /// Gets the absolute changelog path.
    /// </summary>
    internal string ChangelogPath => PathFor("CHANGELOG.md");

    /// <summary>
    /// Gets the absolute unreleased note path.
    /// </summary>
    internal string UnreleasedPath => PathFor("releases/unreleased.md");

    /// <summary>
    /// Gets the absolute unreleased sidecar path.
    /// </summary>
    internal string UnreleasedSidecarPath => PathFor("releases/unreleased.md.yml");

    /// <summary>
    /// Gets the absolute package index manifest path.
    /// </summary>
    internal string PackageIndexPath => PathFor("packages/package-index.yml");

    /// <summary>
    /// Gets the absolute tagged-release template path.
    /// </summary>
    internal string TemplatePath => PathFor("releases/templates/tagged-release-template.md");

    /// <summary>
    /// Gets the absolute path for a release note file.
    /// </summary>
    /// <param name="version">Release version.</param>
    /// <returns>Absolute release note path.</returns>
    internal string ReleaseNotePath(SemVer version) => PathFor($"releases/v{version}.md");

    /// <summary>
    /// Gets the absolute path for a release note sidecar file.
    /// </summary>
    /// <param name="version">Release version.</param>
    /// <returns>Absolute sidecar path.</returns>
    internal string ReleaseSidecarPath(SemVer version) => PathFor($"releases/v{version}.md.yml");

    /// <summary>
    /// Gets the absolute path for a release manifest file.
    /// </summary>
    /// <param name="version">Release version.</param>
    /// <returns>Absolute manifest path.</returns>
    internal string ReleaseManifestPath(SemVer version) => PathFor($"releases/v{version}.release.json");

    /// <summary>
    /// Gets the absolute path for a release evidence bundle file.
    /// </summary>
    /// <param name="version">Release version.</param>
    /// <returns>Absolute release evidence bundle path.</returns>
    internal string ReleaseEvidencePath(SemVer version) => PathFor($"releases/v{version}.evidence.json");

    /// <summary>
    /// Resolves a repository-relative path and verifies that the result stays inside the repository root.
    /// </summary>
    /// <param name="relativePath">Repository-relative path using slash separators and no leading root.</param>
    /// <returns>Absolute path under <see cref="RepositoryRoot"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="relativePath"/> is rooted or traverses outside the repository.</exception>
    internal string PathFor(string relativePath)
    {
        var normalizedRelativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalizedRelativePath))
        {
            throw new ArgumentException("Release workspace paths must be repository-relative.", nameof(relativePath));
        }

        var resolved = Path.Join(RepositoryRoot, normalizedRelativePath);
        if (!IsUnderPath(RepositoryRoot, resolved))
        {
            throw new ArgumentException("Release workspace paths must resolve within the repository root.", nameof(relativePath));
        }

        return resolved;
    }

    /// <summary>
    /// Formats an absolute path as a slash-normalized repository-relative path.
    /// </summary>
    /// <param name="path">Absolute path.</param>
    /// <returns>Repository-relative path when possible.</returns>
    internal string DisplayPath(string path) => Path.GetRelativePath(RepositoryRoot, path).Replace('\\', '/');

    /// <summary>
    /// Determines whether a path is under the supplied root.
    /// </summary>
    /// <param name="root">Root path.</param>
    /// <param name="path">Candidate path.</param>
    /// <returns><c>true</c> when the path is equal to or below the root.</returns>
    internal static bool IsUnderPath(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative == "."
            || (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative));
    }
}
