using System.Text;
using System.Text.RegularExpressions;
using ForgeTrust.AppSurface.ReleaseContracts;
using Markdig;

namespace ForgeTrust.AppSurface.Release;

internal static class ReleaseNoteBuilder
{
    private sealed record UnreleasedTemplatePlaceholder(string Text, string Section);

    private const string TakingShapePlaceholder = "- Add merged public changes here as they land.";

    private const string IncludedChangesPlaceholder = "- Add release-facing changes here as they land.";

    private const string MigrationWatchPlaceholder =
        "- Record-breaking or behavior-changing guidance here before it moves into the tagged release note.";

    private static readonly UnreleasedTemplatePlaceholder[] UnreleasedTemplatePlaceholderDefinitions =
    [
        new(TakingShapePlaceholder, "taking-shape"),
        new(IncludedChangesPlaceholder, "included"),
        new(MigrationWatchPlaceholder, "migration-watch")
    ];

    /// <summary>
    /// Gets the reset-only placeholder bullets that must not appear in a tagged release note.
    /// </summary>
    internal static IReadOnlyList<string> UnreleasedTemplatePlaceholders { get; } = Array.AsReadOnly(
        UnreleasedTemplatePlaceholderDefinitions.Select(placeholder => placeholder.Text).ToArray());

    /// <summary>
    /// Verifies the repository-owned living-note layout before generic entry composition begins.
    /// </summary>
    /// <param name="unreleasedTemplate">Raw AppSurface living-note template.</param>
    /// <exception cref="UnreleasedEntryException">Thrown when an AppSurface-required section marker is absent or repeated.</exception>
    /// <remarks>
    /// The public composer intentionally lets consumer templates define their own sections. AppSurface release preparation
    /// has a stricter contract because its reset placeholders, sidecar narrative, and release review guidance own the
    /// <c>taking-shape</c>, <c>included</c>, and <c>migration-watch</c> sections. Keep that repository policy here rather
    /// than constraining consumer projects through the public command.
    /// </remarks>
    internal static void EnsureAppSurfaceUnreleasedEntryMarkers(string unreleasedTemplate)
    {
        ArgumentNullException.ThrowIfNull(unreleasedTemplate);
        foreach (var placeholder in UnreleasedTemplatePlaceholderDefinitions)
        {
            var marker = UnreleasedEntryComposer.MarkerFor(placeholder.Section);
            var firstIndex = unreleasedTemplate.IndexOf(marker, StringComparison.Ordinal);
            if (firstIndex < 0 || unreleasedTemplate.IndexOf(marker, firstIndex + marker.Length, StringComparison.Ordinal) >= 0)
            {
                throw new UnreleasedEntryException($"The AppSurface unreleased-note template must contain exactly one '{marker}' marker.");
            }
        }
    }

    /// <summary>
    /// Removes reset-only template bullets directly before their canonical entry markers.
    /// </summary>
    /// <param name="unreleasedTemplate">The raw unreleased template, before append-only entries are composed.</param>
    /// <returns>The template with canonical reset-only bullets removed while preserving source line endings.</returns>
    /// <remarks>
    /// This must run before <c>UnreleasedEntryComposer.Compose</c>, because composition
    /// replaces the markers that identify canonical template bullets. The narrow structural match deliberately skips fenced code and
    /// HTML blocks so examples or embedded markup can use the same text without being rewritten.
    /// </remarks>
    internal static string StripResetOnlyTemplatePlaceholders(string unreleasedTemplate)
    {
        var lines = ReadLines(unreleasedTemplate);
        var removedLineIndexes = new List<int>();
        var fenceDelimiter = '\0';
        var fenceDelimiterCount = 0;
        var htmlBlockEndMarker = string.Empty;

        for (var index = 0; index < lines.Count; index++)
        {
            var line = GetLineContent(unreleasedTemplate, lines[index]);
            if (fenceDelimiter != '\0')
            {
                if (IsFencedCodeBlockClosingLine(line, fenceDelimiter, fenceDelimiterCount))
                {
                    fenceDelimiter = '\0';
                    fenceDelimiterCount = 0;
                }

                continue;
            }

            if (htmlBlockEndMarker.Length > 0)
            {
                if (line.Contains(htmlBlockEndMarker, StringComparison.OrdinalIgnoreCase))
                {
                    htmlBlockEndMarker = string.Empty;
                }

                continue;
            }

            if (TryGetFencedCodeBlockOpening(line, out fenceDelimiter, out fenceDelimiterCount))
            {
                continue;
            }

            if (TryGetHtmlBlockEndMarker(line, out var endMarker))
            {
                htmlBlockEndMarker = endMarker;
                continue;
            }

            if (IsCanonicalPlaceholderLine(unreleasedTemplate, lines, index))
            {
                removedLineIndexes.Add(index);
            }
        }

        if (removedLineIndexes.Count == 0)
        {
            return unreleasedTemplate;
        }

        var stripped = new StringBuilder(unreleasedTemplate.Length);
        var nextSourceIndex = 0;
        foreach (var lineIndex in removedLineIndexes)
        {
            var line = lines[lineIndex];
            stripped.Append(unreleasedTemplate, nextSourceIndex, line.Start - nextSourceIndex);
            nextSourceIndex = line.NextStart;
        }

        stripped.Append(unreleasedTemplate, nextSourceIndex, unreleasedTemplate.Length - nextSourceIndex);
        return stripped.ToString();
    }

    /// <summary>
    /// Converts the living unreleased note into a tagged release note.
    /// </summary>
    /// <param name="version">Release version rendered in the heading and generated comment.</param>
    /// <param name="date">Release date rendered with invariant <c>yyyy-MM-dd</c> formatting.</param>
    /// <param name="unreleased">Unreleased Markdown content. Canonical input starts with an exact <c># Unreleased</c> heading.</param>
    /// <returns>Tagged release Markdown with a generated comment header and a trailing newline.</returns>
    /// <remarks>
    /// The method first parses Markdown to catch syntax problems, but it does not use the returned syntax tree to rewrite content.
    /// It then replaces only the exact top-level <c># Unreleased</c> heading without consuming its following blank line and two known narrative
    /// phrases using ordinal matching. Release preparation removes reset-only placeholders from the raw template before composing entries.
    /// Variants in casing or wording are left unchanged. Output is deterministic apart from the supplied version and date, uses
    /// <see cref="Environment.NewLine"/> for generated sections, and trims trailing whitespace from the source body. Callers should run
    /// release readiness checks first because duplicate headings, missing phrases, or concurrently edited Markdown are not treated as errors.
    /// </remarks>
    internal static string Build(SemVer version, DateOnly date, string unreleased)
    {
        Markdown.Parse(unreleased);
        var body = Regex.Replace(unreleased, "^#[ \\t]+Unreleased[ \\t]*\\r?$", $"# Release {version}", RegexOptions.Multiline);
        body = body.Replace("living release note for the next coordinated AppSurface version", $"release note for AppSurface {version}", StringComparison.Ordinal);
        body = body.Replace("provisional until a tag is cut", $"finalized on {date:yyyy-MM-dd}", StringComparison.Ordinal);
        var header = $"""
            <!--
            Generated by ./eng/release prepare.
            Version: {version}
            Date: {date:yyyy-MM-dd}
            -->

            """;
        return header + body.TrimEnd() + Environment.NewLine;
    }

    /// <summary>
    /// Creates the next-cycle unreleased proof artifact.
    /// </summary>
    /// <param name="previousVersion">Version that just moved into tagged release files.</param>
    /// <returns>Canonical unreleased Markdown for the next cycle, including the previous version reference and a trailing newline.</returns>
    /// <remarks>
    /// This reset intentionally discards the prior living-release body after it has been copied into a tagged release note. It preserves
    /// the expected section order for future checks: overview, shaping work, included changes, and migration watch.
    /// </remarks>
    internal static string ResetUnreleased(SemVer previousVersion)
    {
        return $"""
            # Unreleased

            This is the living release note for the next coordinated AppSurface version after `{previousVersion}`. It stays provisional until the next tag is cut.

            ## What is taking shape

            {TakingShapePlaceholder}

            <!-- appsurface:unreleased-entries section="taking-shape" -->

            ## Included in the next coordinated version

            ### Release and docs surface

            {IncludedChangesPlaceholder}

            <!-- appsurface:unreleased-entries section="included" -->

            ## Migration watch

            {MigrationWatchPlaceholder}

            <!-- appsurface:unreleased-entries section="migration-watch" -->

            """;
    }

    /// <summary>
    /// Builds the tree-local pointer used by coordinated package and documentation links.
    /// </summary>
    /// <param name="version">The immutable tagged release selected by this pointer.</param>
    /// <returns>Deterministic Markdown that links to the exact release note.</returns>
    /// <remarks>
    /// Do not replace this link with a global release lookup. Release archives copy this file into their immutable exact trees;
    /// a historical <c>current</c> route must therefore point to the release that was current when that tree was published.
    /// </remarks>
    internal static string BuildCurrentReleasePointer(SemVer version)
    {
        return ReleaseCurrentPointer.Build(version);
    }

    private static bool IsCanonicalPlaceholderLine(string source, IReadOnlyList<MarkdownLine> lines, int index)
    {
        if (index + 2 >= lines.Count || GetLineContent(source, lines[index + 1]).Length != 0)
        {
            return false;
        }

        var line = GetLineContent(source, lines[index]);
        var marker = GetLineContent(source, lines[index + 2]);
        return UnreleasedTemplatePlaceholderDefinitions.Any(placeholder =>
            string.Equals(line, placeholder.Text, StringComparison.Ordinal)
            && string.Equals(marker, UnreleasedEntryComposer.MarkerFor(placeholder.Section), StringComparison.Ordinal));
    }

    private static List<MarkdownLine> ReadLines(string markdown)
    {
        var lines = new List<MarkdownLine>();
        var start = 0;
        while (start < markdown.Length)
        {
            var lineFeed = markdown.IndexOf('\n', start);
            if (lineFeed < 0)
            {
                lines.Add(new MarkdownLine(start, markdown.Length - start, markdown.Length));
                break;
            }

            var contentLength = lineFeed - start;
            if (contentLength > 0 && markdown[lineFeed - 1] == '\r')
            {
                contentLength--;
            }

            lines.Add(new MarkdownLine(start, contentLength, lineFeed + 1));
            start = lineFeed + 1;
        }

        return lines;
    }

    private static string GetLineContent(string source, MarkdownLine line) => source.Substring(line.Start, line.ContentLength);

    private static bool TryGetFencedCodeBlockOpening(string line, out char delimiter, out int delimiterCount)
    {
        var start = 0;
        while (start < line.Length && start < 4 && line[start] == ' ')
        {
            start++;
        }

        var candidateDelimiter = start < line.Length ? line[start] : '\0';
        delimiter = '\0';
        delimiterCount = 0;
        if (start > 3 || (candidateDelimiter != '`' && candidateDelimiter != '~'))
        {
            return false;
        }

        while (start + delimiterCount < line.Length && line[start + delimiterCount] == candidateDelimiter)
        {
            delimiterCount++;
        }

        if (delimiterCount < 3)
        {
            delimiterCount = 0;
            return false;
        }

        delimiter = candidateDelimiter;
        return true;
    }

    private static bool IsFencedCodeBlockClosingLine(string line, char delimiter, int delimiterCount)
    {
        var start = 0;
        while (start < line.Length && start < 4 && line[start] == ' ')
        {
            start++;
        }

        if (start > 3)
        {
            return false;
        }

        var count = 0;
        while (start + count < line.Length && line[start + count] == delimiter)
        {
            count++;
        }

        return count >= delimiterCount && line[(start + count)..].Trim().Length == 0;
    }

    private static bool TryGetHtmlBlockEndMarker(string line, out string endMarker)
    {
        var trimmed = line.TrimStart(' ', '\t');
        if (trimmed.StartsWith("<!--", StringComparison.Ordinal))
        {
            endMarker = "-->";
            return !trimmed.Contains(endMarker, StringComparison.Ordinal);
        }

        if (trimmed.Length < 3 || trimmed[0] != '<' || trimmed[1] is '/' or '!' or '?')
        {
            endMarker = string.Empty;
            return false;
        }

        var tagLength = 0;
        while (1 + tagLength < trimmed.Length && char.IsLetterOrDigit(trimmed[1 + tagLength]))
        {
            tagLength++;
        }

        var tag = trimmed.Substring(1, tagLength);
        if (tag.Length == 0 || !HtmlBlockTags.Contains(tag))
        {
            endMarker = string.Empty;
            return false;
        }

        endMarker = $"</{tag}";
        return !trimmed.Contains(endMarker, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly HashSet<string> HtmlBlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "address", "article", "aside", "blockquote", "body", "caption", "center", "details", "dialog", "div", "dl", "fieldset",
        "figcaption", "figure", "footer", "form", "head", "header", "html", "iframe", "main", "menu", "nav", "ol", "pre", "script",
        "section", "style", "summary", "table", "tbody", "td", "textarea", "tfoot", "th", "thead", "title", "tr", "ul"
    };

    private readonly record struct MarkdownLine(int Start, int ContentLength, int NextStart);
}
