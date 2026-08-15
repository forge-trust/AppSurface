using ForgeTrust.AppSurface.ReleaseContracts;

namespace ForgeTrust.AppSurface.Release;

/// <summary>
/// A single entry from <c>git diff --name-status</c>.
/// </summary>
/// <param name="Status">The Git status code, such as <c>A</c>, <c>M</c>, <c>D</c>, or <c>R100</c>.</param>
/// <param name="Path">The current path reported by Git.</param>
/// <param name="OriginalPath">The original path for a rename, when Git reported one.</param>
internal sealed record ReleasePreparationChange(string Status, string Path, string? OriginalPath = null);

/// <summary>
/// Result of validating the exact release-preparation change set.
/// </summary>
internal sealed record ReleasePreparationChangePolicyResult(IReadOnlyList<string> Errors)
{
    /// <summary>
    /// Gets whether the diff is exactly the generated release artifacts and next-cycle rollover files for the requested version.
    /// </summary>
    internal bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Enforces the narrow change contract for a release-preparation pull request.
/// </summary>
/// <remarks>
/// The policy applies to the complete Git diff between the pull request base and head.
/// It intentionally excludes <c>releases/current.md.yml</c>: that sidecar is permanent,
/// version-independent metadata and must never be regenerated or changed by release preparation.
/// </remarks>
internal static class ReleasePreparationChangePolicy
{
    /// <summary>
    /// Validates that the diff contains the versioned release artifacts, frozen current pointer, changelog, and next-cycle files for <paramref name="version"/>.
    /// </summary>
    /// <param name="version">Release version without a leading <c>v</c>.</param>
    /// <param name="changes">Complete Git name-status diff.</param>
    /// <param name="consumedUnreleasedEntryPaths">The exact entry paths the versioned release manifest declares as composed and archived.</param>
    /// <returns>Validation errors, or an empty result when the diff is valid.</returns>
    internal static ReleasePreparationChangePolicyResult Validate(
        string version,
        IEnumerable<ReleasePreparationChange> changes,
        IEnumerable<string>? consumedUnreleasedEntryPaths = null)
    {
        ArgumentNullException.ThrowIfNull(changes);

        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(version))
        {
            errors.Add("A requested release version is required.");
            return new ReleasePreparationChangePolicyResult(errors);
        }

        var requiredPaths = new HashSet<string>(StringComparer.Ordinal)
        {
            $"releases/v{version}.md",
            $"releases/v{version}.md.yml",
            $"releases/v{version}.release.json",
            $"releases/v{version}.evidence.json",
            "releases/current.md",
            "CHANGELOG.md",
            "releases/unreleased.md"
        };
        // Resetting an already-canonical unreleased sidecar produces no Git diff. If it is present, it must remain an
        // ordinary modification so release preparation cannot add, delete, or rename the next-cycle metadata.
        var optionalModifiedPaths = new HashSet<string>(StringComparer.Ordinal)
        {
            "releases/unreleased.md.yml"
        };
        var expectedPaths = new HashSet<string>(requiredPaths, StringComparer.Ordinal);
        expectedPaths.UnionWith(optionalModifiedPaths);
        var actualChanges = changes.ToArray();
        var declaredEntryPaths = (consumedUnreleasedEntryPaths ?? []).ToArray();
        var declaredEntryPathSet = new HashSet<string>(declaredEntryPaths, StringComparer.Ordinal);
        foreach (var entryPath in declaredEntryPaths)
        {
            if (string.IsNullOrWhiteSpace(entryPath) || !UnreleasedEntryComposer.IsEntryPath(entryPath))
            {
                errors.Add($"Release manifest declares an invalid consumed unreleased entry path: {entryPath}.");
            }
        }

        if (declaredEntryPathSet.Count != declaredEntryPaths.Length)
        {
            errors.Add("Release manifest declares the same consumed unreleased entry path more than once.");
        }

        if (!declaredEntryPaths.SequenceEqual(declaredEntryPaths.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            errors.Add("Release manifest consumed unreleased entry paths must be ordinally sorted.");
        }

        if (actualChanges.Length == 0)
        {
            errors.Add($"The release-preparation diff is empty; expected exactly: {string.Join(", ", expectedPaths.Order(StringComparer.Ordinal))}.");
            return new ReleasePreparationChangePolicyResult(errors);
        }

        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        var seenEntryPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in actualChanges)
        {
            if (string.Equals(change.Path, PackageReleaseLink.CoordinatedReleaseSidecarPath, StringComparison.Ordinal)
                || string.Equals(change.OriginalPath, PackageReleaseLink.CoordinatedReleaseSidecarPath, StringComparison.Ordinal))
            {
                errors.Add($"{PackageReleaseLink.CoordinatedReleaseSidecarPath} is permanent metadata and must not change.");
                continue;
            }

            if (change.Status.StartsWith('R'))
            {
                errors.Add($"Renames are not allowed in release preparation: {change.OriginalPath ?? change.Path} -> {change.Path}.");
                continue;
            }

            if (change.Status.StartsWith('D'))
            {
                if (declaredEntryPathSet.Contains(change.Path))
                {
                    if (!seenEntryPaths.Add(change.Path))
                    {
                        errors.Add($"Unreleased entry path appears more than once: {change.Path}.");
                    }

                    continue;
                }

                errors.Add($"Release preparation may delete only an unreleased entry recorded by the versioned release manifest: {change.Path}.");
                continue;
            }

            if (!string.Equals(change.Status, "A", StringComparison.Ordinal)
                && !string.Equals(change.Status, "M", StringComparison.Ordinal))
            {
                errors.Add($"Unsupported Git change status '{change.Status}' for {change.Path}.");
                continue;
            }

            if (!expectedPaths.Contains(change.Path))
            {
                errors.Add($"Unexpected release-preparation path: {change.Path}.");
                continue;
            }

            if (optionalModifiedPaths.Contains(change.Path)
                && !string.Equals(change.Status, "M", StringComparison.Ordinal))
            {
                errors.Add($"Optional release-preparation path must be M when present: {change.Path}.");
            }

            if (!seenPaths.Add(change.Path))
            {
                errors.Add($"Release-preparation path appears more than once: {change.Path}.");
            }
        }

        foreach (var expectedPath in requiredPaths)
        {
            if (!seenPaths.Contains(expectedPath))
            {
                errors.Add($"Required release-preparation path is missing: {expectedPath}.");
            }
        }

        foreach (var declaredEntryPath in declaredEntryPaths)
        {
            if (!seenEntryPaths.Contains(declaredEntryPath))
            {
                errors.Add($"Release manifest records a consumed unreleased entry that the release-preparation diff does not delete: {declaredEntryPath}.");
            }
        }

        return new ReleasePreparationChangePolicyResult(errors);
    }

    /// <summary>
    /// Parses tab-delimited output from <c>git diff --name-status --find-renames</c>.
    /// </summary>
    /// <param name="nameStatusOutput">Git name-status output.</param>
    /// <returns>Parsed change entries.</returns>
    internal static IReadOnlyList<ReleasePreparationChange> ParseNameStatus(string nameStatusOutput)
    {
        ArgumentNullException.ThrowIfNull(nameStatusOutput);

        var changes = new List<ReleasePreparationChange>();
        foreach (var line in nameStatusOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.TrimEnd('\r').Split('\t');
            if (fields.Length >= 3 && fields[0].StartsWith('R'))
            {
                changes.Add(new ReleasePreparationChange(fields[0], fields[2], fields[1]));
            }
            else if (fields.Length >= 2)
            {
                changes.Add(new ReleasePreparationChange(fields[0], fields[1]));
            }
        }

        return changes;
    }
}
