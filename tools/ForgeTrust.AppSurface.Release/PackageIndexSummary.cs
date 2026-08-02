using ForgeTrust.AppSurface.ReleaseContracts;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ForgeTrust.AppSurface.Release;

internal sealed class PackageIndexSummary
{
    /// <summary>
    /// Gets public package rows whose publish decision is publish.
    /// </summary>
    internal IReadOnlyList<PackageIndexEntry> PublicPublishedPackages { get; }

    private PackageIndexSummary(IReadOnlyList<PackageIndexEntry> publicPublishedPackages)
    {
        PublicPublishedPackages = publicPublishedPackages;
    }

    /// <summary>
    /// Loads a package index summary from YAML.
    /// </summary>
    /// <param name="path">Package index path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Package index summary.</returns>
    internal static async Task<PackageIndexSummary> LoadAsync(string path, CancellationToken cancellationToken)
    {
        var content = await File.ReadAllTextAsync(path, cancellationToken);
        return Load(content);
    }

    /// <summary>
    /// Parses a package-index document into the release-owned public package summary.
    /// </summary>
    /// <param name="content">The YAML document to parse.</param>
    /// <returns>The public publish package rows and their release-link contracts.</returns>
    internal static PackageIndexSummary Load(string content)
    {
        PackageIndexManifest? manifest;
        try
        {
            manifest = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build()
                .Deserialize<PackageIndexManifest>(content);
        }
        catch (YamlException ex)
        {
            throw new ReleaseToolException(ReleaseDiagnostic.Error(
                "release-package-index-invalid",
                "The package manifest could not be parsed.",
                ex.Message,
                "Fix `packages/package-index.yml` before retrying release automation.",
                "packages/README.md"));
        }

        var packages = manifest?.Packages ?? [];
        var publicPublishedPackages = new List<PackageIndexEntry>();
        foreach (var package in packages.Where(package =>
                     string.Equals(package.Classification, "public", StringComparison.Ordinal)
                     && string.Equals(package.PublishDecision, "publish", StringComparison.Ordinal)))
        {
            if (!PackageReleaseLinkResolver.TryResolve(
                    package.ReleaseTrack,
                    package.ReleaseNotesPath,
                    out var releaseLink,
                    out var issue))
            {
                throw new ReleaseToolException(ReleaseDiagnostic.Error(
                    "release-package-link-invalid",
                    "A public package release link is invalid.",
                    $"Package '{package.Project}' {issue}.",
                    "Use release_track: coordinated with no release_notes_path, or release_track: explicit with a repository-relative release_notes_path.",
                    "releases/coordinated-release-links.md"));
            }

            publicPublishedPackages.Add(new PackageIndexEntry(
                package.Project,
                releaseLink,
                package.ReadinessBlocker));
        }

        return new PackageIndexSummary(publicPublishedPackages);
    }
}

/// <summary>
/// Package manifest root shape used by the release tool.
/// </summary>
internal sealed class PackageIndexManifest
{
    /// <summary>
    /// Gets the package rows.
    /// </summary>
    public List<PackageIndexYamlEntry> Packages { get; init; } = [];
}

/// <summary>
/// Package manifest row shape used by the release tool.
/// </summary>
internal sealed class PackageIndexYamlEntry
{
    /// <summary>
    /// Gets the project path.
    /// </summary>
    public string Project { get; init; } = string.Empty;

    /// <summary>
    /// Gets the classification string.
    /// </summary>
    public string Classification { get; init; } = string.Empty;

    /// <summary>
    /// Gets the publish decision string.
    /// </summary>
    public string? PublishDecision { get; init; }

    /// <summary>
    /// Gets the release-link policy.
    /// </summary>
    public string? ReleaseTrack { get; init; }

    /// <summary>
    /// Gets the explicit release notes path when the package uses an explicit link.
    /// </summary>
    public string? ReleaseNotesPath { get; init; }

    /// <summary>
    /// Gets the same-repository issue or pull request that blocks publication, when one remains unresolved.
    /// </summary>
    public string? ReadinessBlocker { get; init; }
}

/// <summary>
/// Package row included in a release manifest.
/// </summary>
/// <param name="Project">Repository-relative package project path. The package index supplies a non-empty path for every row.</param>
/// <param name="ReleaseLink">Resolved package release-link policy.</param>
/// <param name="ReadinessBlocker">Optional same-repository issue or pull-request reference. A non-empty value blocks publication until the package is held or the blocker is cleared.</param>
internal sealed record PackageIndexEntry(string Project, PackageReleaseLink? ReleaseLink, string? ReadinessBlocker)
{
    /// <summary>
    /// Gets the repository-relative release note path after applying the package release-link policy.
    /// </summary>
    internal string ReleaseNotesPath => ReleaseLink?.ReleaseNotesPath ?? string.Empty;
}
