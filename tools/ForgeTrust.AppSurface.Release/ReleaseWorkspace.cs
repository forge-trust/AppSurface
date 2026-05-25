using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Console;
using ForgeTrust.AppSurface.Core;
using Markdig;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ForgeTrust.AppSurface.Release;

/// <summary>
/// Repository path helper for release-owned files.
/// </summary>
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
    /// Resolves a repository-relative path.
    /// </summary>
    /// <param name="relativePath">Repository-relative path using slash separators.</param>
    /// <returns>Absolute path.</returns>
    internal string PathFor(string relativePath) => Path.Combine(RepositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

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
