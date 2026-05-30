namespace ForgeTrust.AppSurface.Release;

internal static class ChangelogEditor
{
    /// <summary>
    /// Resets the compact Unreleased ledger and inserts a tagged changelog section after it.
    /// </summary>
    /// <param name="changelog">Existing changelog content. Canonical input contains a single <c>## Unreleased</c> heading.</param>
    /// <param name="version">Release version inserted as <c>## {version} - yyyy-MM-dd</c>.</param>
    /// <param name="date">Release date rendered with invariant <c>yyyy-MM-dd</c> formatting.</param>
    /// <param name="releasePath">Repository-relative release note path linked from the new section.</param>
    /// <returns>Updated changelog content with a reset Unreleased section and the tagged section inserted or appended.</returns>
    /// <remarks>
    /// The algorithm is intentionally text-based to preserve surrounding Markdown. The detailed release narrative lives in
    /// <c>releases/unreleased.md</c> before preparation and <c>releases/v{version}.md</c> after preparation; the changelog keeps only the
    /// durable compact ledger. If <c>## Unreleased</c> is missing, the canonical compact section is appended before the new tagged section.
    /// If the first-release placeholder follows <c>## Unreleased</c>, that placeholder block is replaced. Duplicate release sections are
    /// not de-duplicated; callers should run readiness checks before calling this method. Malformed heading hierarchies and concurrent
    /// changelog edits can therefore produce surprising placement, so this helper should only be used on the repository's canonical
    /// changelog shape.
    /// </remarks>
    internal static string RollForward(string changelog, SemVer version, DateOnly date, string releasePath)
    {
        const string unreleasedSection = """
            ## Unreleased

            - Narrative release note: [Upcoming release note](./releases/unreleased.md)
            - Upgrade policy: [Pre-1.0 upgrade policy](./releases/upgrade-policy.md)
            - Authoring workflow: [Release authoring checklist](./releases/release-authoring-checklist.md)
            """;
        var heading = $"## {version} - {date:yyyy-MM-dd}";
        var insert = $"""

            {heading}

            - Narrative release note: [v{version}](./{releasePath})
            - Release manifest: `releases/v{version}.release.json`

            """;

        var marker = "## Unreleased";
        var markerIndex = changelog.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return changelog.TrimEnd() + Environment.NewLine + Environment.NewLine + unreleasedSection + insert;
        }

        const string firstReleasePlaceholder = "## No tagged releases yet";
        var nextHeading = changelog.IndexOf("\n## ", markerIndex + marker.Length, StringComparison.Ordinal);
        var suffix = string.Empty;
        if (nextHeading >= 0)
        {
            var headingStart = nextHeading + 1;
            if (changelog.AsSpan(headingStart).StartsWith(firstReleasePlaceholder, StringComparison.Ordinal))
            {
                var followingHeading = changelog.IndexOf("\n## ", headingStart + firstReleasePlaceholder.Length, StringComparison.Ordinal);
                suffix = followingHeading < 0 ? string.Empty : changelog[followingHeading..];
            }
            else
            {
                suffix = changelog[nextHeading..];
            }
        }

        return changelog[..markerIndex].TrimEnd() + Environment.NewLine + Environment.NewLine + unreleasedSection + insert + suffix;
    }
}
