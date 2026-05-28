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

internal static class ChangelogEditor
{
    /// <summary>
    /// Inserts a tagged changelog section immediately after the current Unreleased heading.
    /// </summary>
    /// <param name="changelog">Existing changelog content. Canonical input contains a single <c>## Unreleased</c> heading.</param>
    /// <param name="version">Release version inserted as <c>## {version} - yyyy-MM-dd</c>.</param>
    /// <param name="date">Release date rendered with invariant <c>yyyy-MM-dd</c> formatting.</param>
    /// <param name="releasePath">Repository-relative release note path linked from the new section.</param>
    /// <returns>Updated changelog content with the tagged section inserted or appended.</returns>
    /// <remarks>
    /// The algorithm is intentionally text-based to preserve surrounding Markdown. If <c>## Unreleased</c> is missing, the release section
    /// is appended. If the first-release placeholder follows <c>## Unreleased</c>, that placeholder block is replaced. Otherwise the
    /// section is inserted before the next <c>## </c> heading. Duplicate release sections are not de-duplicated; callers should run
    /// readiness checks before calling this method. Malformed heading hierarchies and concurrent changelog edits can therefore produce
    /// surprising placement, so this helper should only be used on the repository's canonical changelog shape.
    /// </remarks>
    internal static string RollForward(string changelog, SemVer version, DateOnly date, string releasePath)
    {
        var heading = $"## {version} - {date:yyyy-MM-dd}";
        var insert = $"""

            {heading}

            - Narrative release note: [v{version}](./{releasePath})
            - Release manifest: [v{version}.release.json](./releases/v{version}.release.json)

            """;

        var marker = "## Unreleased";
        var markerIndex = changelog.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return changelog.TrimEnd() + Environment.NewLine + insert;
        }

        const string firstReleasePlaceholder = "## No tagged releases yet";
        var nextHeading = changelog.IndexOf("\n## ", markerIndex + marker.Length, StringComparison.Ordinal);
        if (nextHeading < 0)
        {
            nextHeading = changelog.IndexOf(firstReleasePlaceholder, StringComparison.Ordinal);
        }

        if (nextHeading < 0)
        {
            return changelog.TrimEnd() + insert + Environment.NewLine;
        }

        var placeholderHeading = changelog.IndexOf(firstReleasePlaceholder, nextHeading, StringComparison.Ordinal);
        if (placeholderHeading == nextHeading || placeholderHeading == nextHeading + 1)
        {
            var followingHeading = changelog.IndexOf("\n## ", placeholderHeading + firstReleasePlaceholder.Length, StringComparison.Ordinal);
            if (followingHeading < 0)
            {
                return changelog[..nextHeading] + insert;
            }

            return changelog[..nextHeading] + insert + changelog[followingHeading..];
        }

        return changelog.Insert(nextHeading, insert);
    }
}
