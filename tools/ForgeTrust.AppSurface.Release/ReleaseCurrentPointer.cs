using ForgeTrust.AppSurface.ReleaseContracts;

namespace ForgeTrust.AppSurface.Release;

/// <summary>
/// Parses and renders the frozen tree-local coordinated release pointer.
/// </summary>
/// <remarks>
/// The current pointer deliberately has a very small, byte-stable surface. Exact documentation trees copy this file, so accepting
/// free-form prose would allow a historical <c>current</c> route to silently stop identifying the release represented by its tree.
/// </remarks>
internal static class ReleaseCurrentPointer
{
    private const string MarkerPrefix = "<!-- appsurface-current-coordinated-release: ";
    private const string MarkerSuffix = " -->";

    /// <summary>Builds the initial pointer used before the repository has a reachable coordinated tag.</summary>
    internal static string BuildNone() =>
        "<!-- appsurface-current-coordinated-release: none -->\n# Current coordinated release\n\nNo coordinated AppSurface release has been tagged yet.\n";

    /// <summary>Builds the canonical pointer for a tagged coordinated release.</summary>
    internal static string Build(SemVer version) =>
        $"<!-- appsurface-current-coordinated-release: v{version} -->\n# Current coordinated release\n\nThis documentation tree represents [Release {version}](./v{version}.md).\n";

    /// <summary>
    /// Validates canonical pointer bytes and returns its optional referenced tag version.
    /// </summary>
    internal static bool TryParse(string content, out SemVer? version)
    {
        version = null;
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (string.Equals(normalized, BuildNone(), StringComparison.Ordinal))
        {
            return true;
        }

        if (!normalized.StartsWith(MarkerPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var markerEnd = normalized.IndexOf(MarkerSuffix, MarkerPrefix.Length, StringComparison.Ordinal);
        if (markerEnd < MarkerPrefix.Length)
        {
            return false;
        }

        var tag = normalized[MarkerPrefix.Length..markerEnd];
        if (!tag.StartsWith('v') || !SemVer.TryParse(tag[1..], out var parsed))
        {
            return false;
        }

        if (!string.Equals(normalized, Build(parsed), StringComparison.Ordinal))
        {
            return false;
        }

        version = parsed;
        return true;
    }

    internal static ReleaseDiagnostic InvalidBodyDiagnostic() => ReleaseDiagnostic.Error(
        "release-current-page-body-invalid",
        $"'{PackageReleaseLink.CoordinatedReleaseNotesPath}' is not one of the canonical coordinated release pointer templates.",
        "The frozen pointer's marker, body, or release link was manually changed.",
        "Restore the canonical pointer for the latest reachable tag, or use the initial `none` template before the first tag.",
        "releases/coordinated-release-links.md");
}

/// <summary>
/// Finds annotated, reachable coordinated release tags and enforces the current-pointer advancement rule.
/// </summary>
internal sealed class ReleaseCurrentPointerGate
{
    private readonly ReleaseWorkspace _workspace;
    private readonly ICommandRunner _commandRunner;

    internal ReleaseCurrentPointerGate(ReleaseWorkspace workspace, ICommandRunner commandRunner)
    {
        _workspace = workspace;
        _commandRunner = commandRunner;
    }

    internal async Task<IReadOnlyList<ReleaseDiagnostic>> ValidateAsync(
        SemVer target,
        string currentPointer,
        string preparationBaseCommit,
        CancellationToken cancellationToken)
    {
        var diagnostics = new List<ReleaseDiagnostic>();
        if (!ReleaseCurrentPointer.TryParse(currentPointer, out var marker))
        {
            diagnostics.Add(ReleaseCurrentPointer.InvalidBodyDiagnostic());
            return diagnostics;
        }

        var tags = await DiscoverTagsAsync(preparationBaseCommit, diagnostics, cancellationToken);
        var targetTag = await _commandRunner.RunAsync(
            new CommandInvocation("git", ["rev-parse", "--verify", "--quiet", $"refs/tags/{target.TagName}"], _workspace.RepositoryRoot),
            cancellationToken);
        if (targetTag.ExitCode == 0 || tags.Any(item => string.Equals(item.Tag, target.TagName, StringComparison.Ordinal)))
        {
            diagnostics.Add(ReleaseDiagnostic.Error(
                "release-current-page-target-tag-exists",
                $"Target tag '{target.TagName}' already exists.",
                "Release preparation must create new versioned artifacts before the annotated tag is created.",
                "Choose a new target version and prepare it before creating its tag.",
                "releases/coordinated-release-links.md"));
        }
        else if (targetTag.ExitCode != 1)
        {
            diagnostics.Add(ReleaseDiagnostic.Error(
                "release-current-page-tag-discovery-failed",
                $"Release preparation could determine whether target tag '{target.TagName}' exists.",
                string.IsNullOrWhiteSpace(targetTag.StandardError) ? "git rev-parse returned a nonzero exit code." : targetTag.StandardError.Trim(),
                "Repair the Git worktree and rerun release preparation.",
                "releases/coordinated-release-links.md"));
        }

        var ordered = tags.OrderBy(item => item.Version).ToArray();
        for (var index = 1; index < ordered.Length; index++)
        {
            if (ordered[index - 1].Version.CompareTo(ordered[index].Version) == 0)
            {
                diagnostics.Add(ReleaseDiagnostic.Error(
                    "release-current-page-tag-ambiguous",
                    "Two reachable annotated tags have equal SemVer precedence.",
                    $"'{ordered[index - 1].Tag}' and '{ordered[index].Tag}' both describe version '{ordered[index].Version}'.",
                    "Remove or rename the ambiguous tag before preparing another release.",
                    "releases/coordinated-release-links.md"));
            }
        }

        var latest = ordered.LastOrDefault();
        if (marker is null)
        {
            if (latest is not null)
            {
                diagnostics.Add(ReleaseDiagnostic.Error(
                    "release-current-page-stale",
                    "The current release pointer still uses the initial none marker despite a reachable coordinated tag.",
                    $"The latest reachable tag is '{latest.Tag}'.",
                    "Restore releases/current.md from the latest prepared release before preparing the next version.",
                    "releases/coordinated-release-links.md"));
            }
        }
        else
        {
            if (latest is null || marker.CompareTo(latest.Version) != 0)
            {
                diagnostics.Add(ReleaseDiagnostic.Error(
                    "release-current-page-stale",
                    "The current release pointer does not identify the latest reachable coordinated tag.",
                    $"Pointer marker is '{marker.TagName}', while the latest reachable tag is '{latest?.Tag ?? "none"}'.",
                    "Restore releases/current.md from the latest prepared release before preparing the next version.",
                    "releases/coordinated-release-links.md"));
            }

            if (target.CompareTo(marker) <= 0)
            {
                diagnostics.Add(ReleaseDiagnostic.Error(
                    "release-current-page-version-not-newer",
                    $"Target version '{target}' is not newer than the current pointer marker '{marker}'.",
                    "Release preparation may only advance the coordinated release pointer.",
                    "Choose a SemVer version with higher precedence than the current marker.",
                    "releases/coordinated-release-links.md"));
            }
        }

        return diagnostics;
    }

    private async Task<IReadOnlyList<ReachableReleaseTag>> DiscoverTagsAsync(
        string preparationBaseCommit,
        List<ReleaseDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var list = await _commandRunner.RunAsync(
            new CommandInvocation("git", ["for-each-ref", "--format=%(refname:short)", "refs/tags/v*"], _workspace.RepositoryRoot),
            cancellationToken);
        if (list.ExitCode != 0)
        {
            diagnostics.Add(ReleaseDiagnostic.Error(
                "release-current-page-tag-discovery-failed",
                "Release preparation could not enumerate coordinated release tags.",
                string.IsNullOrWhiteSpace(list.StandardError) ? "git for-each-ref returned a nonzero exit code." : list.StandardError.Trim(),
                "Repair the Git worktree and rerun release preparation; do not treat a tag-discovery failure as an empty history.",
                "releases/coordinated-release-links.md"));
            return [];
        }

        var results = new List<ReachableReleaseTag>();
        foreach (var tag in list.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!tag.StartsWith('v') || !SemVer.TryParse(tag[1..], out var version))
            {
                continue;
            }

            var annotated = await _commandRunner.RunAsync(
                new CommandInvocation("git", ["cat-file", "-t", $"refs/tags/{tag}"], _workspace.RepositoryRoot),
                cancellationToken);
            if (annotated.ExitCode != 0)
            {
                diagnostics.Add(ReleaseDiagnostic.Error(
                    "release-current-page-tag-discovery-failed",
                    $"Release preparation could inspect tag '{tag}'.",
                    string.IsNullOrWhiteSpace(annotated.StandardError) ? "git cat-file returned a nonzero exit code." : annotated.StandardError.Trim(),
                    "Repair the tag reference before preparing a release.",
                    "releases/coordinated-release-links.md"));
                continue;
            }

            if (!string.Equals(annotated.StandardOutput.Trim(), "tag", StringComparison.Ordinal))
            {
                continue;
            }

            var peeled = await _commandRunner.RunAsync(
                new CommandInvocation("git", ["rev-parse", $"refs/tags/{tag}^{{commit}}"], _workspace.RepositoryRoot),
                cancellationToken);
            var commit = peeled.StandardOutput.Trim();
            if (peeled.ExitCode != 0 || string.IsNullOrWhiteSpace(commit))
            {
                diagnostics.Add(ReleaseDiagnostic.Error(
                    "release-current-page-tag-unreadable",
                    $"Annotated release tag '{tag}' could not be resolved to a commit.",
                    peeled.StandardError.Trim(),
                    "Repair or remove the broken tag before preparing a release.",
                    "releases/coordinated-release-links.md"));
                continue;
            }

            var reachable = await _commandRunner.RunAsync(
                new CommandInvocation("git", ["merge-base", "--is-ancestor", commit, preparationBaseCommit], _workspace.RepositoryRoot),
                cancellationToken);
            if (reachable.ExitCode == 0)
            {
                results.Add(new ReachableReleaseTag(tag, version));
            }
            else if (reachable.ExitCode != 1)
            {
                diagnostics.Add(ReleaseDiagnostic.Error(
                    "release-current-page-tag-discovery-failed",
                    $"Release preparation could determine whether tag '{tag}' is reachable from the preparation base.",
                    string.IsNullOrWhiteSpace(reachable.StandardError) ? "git merge-base returned a nonzero exit code." : reachable.StandardError.Trim(),
                    "Repair the Git worktree and rerun release preparation.",
                    "releases/coordinated-release-links.md"));
            }
        }

        return results;
    }

    private sealed record ReachableReleaseTag(string Tag, SemVer Version);
}
