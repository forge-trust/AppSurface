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
        return new PackageIndexSummary(packages
            .Where(package => string.Equals(package.Classification, "public", StringComparison.Ordinal)
                && string.Equals(package.PublishDecision, "publish", StringComparison.Ordinal))
            .Select(package => new PackageIndexEntry(
                package.Project,
                package.ReleaseNotesPath ?? string.Empty,
                package.ReadinessBlocker))
            .ToArray());
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
    /// Gets the release notes path.
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
internal sealed record PackageIndexEntry(string Project, string ReleaseNotesPath, string? ReadinessBlocker);
