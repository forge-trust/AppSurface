namespace ForgeTrust.AppSurface.ReleaseContracts;

/// <summary>
/// Describes how a package documentation surface reaches the release narrative that applies to it.
/// </summary>
/// <remarks>
/// A coordinated link is deliberately an alias rather than a version lookup. At release preparation time it resolves to
/// <c>releases/current.md</c>; the checked-in pointer in each exported docs tree then names that tree's immutable tagged note.
/// Use an explicit link only when a package intentionally has a different release narrative.
/// </remarks>
public sealed record PackageReleaseLink(PackageReleaseTrack Track, string ReleaseNotesPath)
{
    /// <summary>
    /// Gets the repository-relative current pointer used by coordinated packages.
    /// </summary>
    public const string CoordinatedReleaseNotesPath = "releases/current.md";
}

/// <summary>
/// Selects the package release-link policy.
/// </summary>
public enum PackageReleaseTrack
{
    /// <summary>
    /// Resolves to the checked-in, tree-local coordinated release pointer.
    /// </summary>
    Coordinated,

    /// <summary>
    /// Uses the explicit repository-relative <c>release_notes_path</c> supplied by the package row.
    /// </summary>
    Explicit
}

/// <summary>
/// Resolves and validates package-index release-link fields shared by package and release tooling.
/// </summary>
public static class PackageReleaseLinkResolver
{
    /// <summary>
    /// Resolves a package release link from package-index YAML values.
    /// </summary>
    /// <param name="releaseTrack">Optional <c>release_track</c> YAML value.</param>
    /// <param name="releaseNotesPath">Optional <c>release_notes_path</c> YAML value.</param>
    /// <param name="link">The resolved link when validation succeeds.</param>
    /// <param name="error">A maintainer-facing validation error when validation fails.</param>
    /// <returns><see langword="true"/> when the fields describe a supported link, including a legacy absent optional link.</returns>
    /// <remarks>
    /// Rows without <c>release_track</c> retain the historical explicit-path meaning so old release manifests and archived
    /// package-index snapshots remain readable. New coordinated rows must not also carry a versioned path: that combination
    /// makes it unclear whether a reader should follow the frozen alias or the mutable package-index value.
    /// </remarks>
    public static bool TryResolve(
        string? releaseTrack,
        string? releaseNotesPath,
        out PackageReleaseLink? link,
        out string? error)
    {
        link = null;
        error = null;
        var normalizedTrack = releaseTrack?.Trim();
        var normalizedPath = releaseNotesPath?.Trim();

        if (string.IsNullOrEmpty(normalizedTrack))
        {
            if (string.IsNullOrEmpty(normalizedPath))
            {
                return true;
            }

            link = new PackageReleaseLink(PackageReleaseTrack.Explicit, normalizedPath);
            return true;
        }

        if (string.Equals(normalizedTrack, "coordinated", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrEmpty(normalizedPath))
            {
                error = "uses release_track: coordinated and must not also define release_notes_path; the coordinated target is releases/current.md";
                return false;
            }

            link = new PackageReleaseLink(PackageReleaseTrack.Coordinated, PackageReleaseLink.CoordinatedReleaseNotesPath);
            return true;
        }

        if (string.Equals(normalizedTrack, "explicit", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(normalizedPath))
            {
                error = "uses release_track: explicit and must define release_notes_path";
                return false;
            }

            link = new PackageReleaseLink(PackageReleaseTrack.Explicit, normalizedPath);
            return true;
        }

        error = $"has unsupported release_track '{normalizedTrack}'; use coordinated or explicit";
        return false;
    }
}
