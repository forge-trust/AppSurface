using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ForgeTrust.AppSurface.ReleaseContracts;

/// <summary>
/// Loads append-only unreleased-note entries and inserts them into the stable living-note template.
/// </summary>
/// <remarks>
/// Entries are individual Markdown files in <c>releases/unreleased.entries</c>. The flat, filename-sorted directory
/// avoids concurrent edits to the living-note template. Each entry begins with an exact section directive and may add
/// nested headings or ordinary Markdown, but cannot introduce a new top-level section. Callers use the composed content
/// for rendering or release preparation; the checked-in template remains stable until release preparation resets it.
/// </remarks>
internal static class UnreleasedEntryComposer
{
    internal const string EntriesDirectoryName = "unreleased.entries";

    private const string EntriesDirectoryPrefix = "releases/" + EntriesDirectoryName + "/";
    private const string EntryDirectivePrefix = "<!-- appsurface:unreleased-entry section=\"";
    private const string EntryDirectiveSuffix = "\" -->";
    private static readonly string[] Sections = ["taking-shape", "included", "migration-watch"];
    private static readonly Regex EntryFileNamePattern = new(
        "^[0-9]{4}-[0-9]{2}-[0-9]{2}-[a-z0-9]+(?:-[a-z0-9]+)*\\.md$",
        RegexOptions.CultureInvariant);
    private static readonly Regex TopLevelHeadingPattern = new(
        "^(?: {0,3}#{1,2}(?!#)[ \\t]| {0,3}\\S.*\\r?\\n {0,3}(?:=+|-+)[ \\t]*\\r?$)",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);
    private static readonly Regex[] RelativeLinkDestinationPatterns =
    [
        new Regex(
            @"(?<!\\)!?\[[^\]\r\n]*\]\([ \t]*(?<destination>(?!(?:[A-Za-z][A-Za-z0-9+.-]*:|/|#|\?))(?:[^()\s<>\r\n]+|\((?<destinationParenthesis>)|\)(?<-destinationParenthesis>))+(?(destinationParenthesis)(?!)))(?=[ \t]*(?:(?:(?:""[^""\r\n]*""))|(?:'[^'\r\n]*')|\([^\)\r\n]*\))?[ \t]*\))",
            RegexOptions.CultureInvariant),
        new Regex(
            @"(?<!\\)!?\[[^\]\r\n]*\]\([ \t]*<(?<destination>(?!(?:[A-Za-z][A-Za-z0-9+.-]*:|/|#|\?))[^>\r\n]+)>",
            RegexOptions.CultureInvariant),
        new Regex(
            @"^ {0,3}\[[^\]\r\n]+\]:[ \t]*(?<destination>(?!(?:[A-Za-z][A-Za-z0-9+.-]*:|/|#|\?))(?:[^()\s<>\r\n]+|\((?<destinationParenthesis>)|\)(?<-destinationParenthesis>))+(?(destinationParenthesis)(?!)))",
            RegexOptions.Multiline | RegexOptions.CultureInvariant),
        new Regex(
            @"^ {0,3}\[[^\]\r\n]+\]:[ \t]*<(?<destination>(?!(?:[A-Za-z][A-Za-z0-9+.-]*:|/|#|\?))[^>\r\n]+)>",
            RegexOptions.Multiline | RegexOptions.CultureInvariant)
    ];

    /// <summary>
    /// Loads and validates every entry file in a flat entries directory.
    /// </summary>
    /// <param name="entriesDirectory">Absolute entries-directory path.</param>
    /// <param name="cancellationToken">Token observed between file reads.</param>
    /// <returns>Validated entries and their absolute source paths, ordered by filename.</returns>
    /// <exception cref="UnreleasedEntryException">Thrown when an entry path or its Markdown directive is invalid.</exception>
    internal static async Task<UnreleasedEntrySet> LoadAsync(string entriesDirectory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entriesDirectory);

        if (!Directory.Exists(entriesDirectory))
        {
            return new UnreleasedEntrySet([], []);
        }

        var directoryAttributes = File.GetAttributes(entriesDirectory);
        if ((directoryAttributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnreleasedEntryException($"Unreleased entries directory '{entriesDirectory}' must not be a symlink, junction, or other reparse point.");
        }

        var entryPaths = new List<string>();
        foreach (var path in Directory.EnumerateFileSystemEntries(entriesDirectory).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(path))
            {
                throw new UnreleasedEntryException($"Unreleased entries directory '{entriesDirectory}' must be flat; nested directory '{Path.GetFileName(path)}' is not allowed.");
            }

            if (!IsEntryFileName(Path.GetFileName(path)))
            {
                throw new UnreleasedEntryException($"Unreleased entry '{Path.GetFileName(path)}' must use the YYYY-MM-DD-topic.md filename shape.");
            }

            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnreleasedEntryException($"Unreleased entry '{Path.GetFileName(path)}' must not be a symlink, junction, or other reparse point.");
            }

            entryPaths.Add(path);
        }

        var entries = new List<UnreleasedEntry>(entryPaths.Count);
        var snapshots = new List<UnreleasedEntrySnapshot>(entryPaths.Count);
        foreach (var path in entryPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            var content = Encoding.UTF8.GetString(bytes);
            if (content.Length > 0 && content[0] == '\uFEFF')
            {
                content = content[1..];
            }

            entries.Add(Parse(path, content));
            snapshots.Add(new UnreleasedEntrySnapshot(path, ComputeSha256(bytes)));
        }

        return new UnreleasedEntrySet(entries, snapshots);
    }

    /// <summary>
    /// Inserts validated entries at the end of their designated top-level template sections.
    /// </summary>
    /// <param name="template">Living-note template that contains one marker for each supported section.</param>
    /// <param name="entries">Validated entries to insert.</param>
    /// <param name="destinationPath">Absolute path of the composed living or versioned release note.</param>
    /// <returns>The deterministic composed release note.</returns>
    /// <exception cref="UnreleasedEntryException">Thrown when the template does not have the expected marker shape.</exception>
    internal static string Compose(string template, IEnumerable<UnreleasedEntry> entries, string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var entryList = entries.ToArray();
        var entriesBySection = entryList
            .GroupBy(entry => entry.Section, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(entry => Path.GetFileName(entry.Path), StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);
        var composed = template;
        foreach (var section in Sections)
        {
            var marker = MarkerFor(section);
            var markerIndex = composed.IndexOf(marker, StringComparison.Ordinal);
            if (markerIndex < 0 || composed.IndexOf(marker, markerIndex + marker.Length, StringComparison.Ordinal) >= 0)
            {
                throw new UnreleasedEntryException($"The unreleased-note template must contain exactly one '{marker}' marker.");
            }

            var sectionContent = entriesBySection.TryGetValue(section, out var sectionEntries)
                ? string.Join("\n\n", sectionEntries.Select(entry => RebaseRelativeLinkDestinations(entry.Markdown, entry.Path, destinationPath)))
                : string.Empty;
            composed = composed.Replace(marker, sectionContent, StringComparison.Ordinal);
        }

        if (composed.Contains("<!-- appsurface:unreleased-entries", StringComparison.Ordinal))
        {
            throw new UnreleasedEntryException("The unreleased-note template must contain exactly one append-only entry marker for every supported section and no unsupported entry markers.");
        }

        return composed;
    }

    /// <summary>
    /// Rebases relative Markdown link destinations from an entry source into its composed release-note destination.
    /// </summary>
    /// <param name="markdown">Validated entry Markdown.</param>
    /// <param name="entryPath">Absolute entry source path.</param>
    /// <param name="destinationPath">Absolute composed document path.</param>
    /// <returns>Entry Markdown whose relative inline and reference link destinations resolve from the composed document.</returns>
    /// <remarks>
    /// Entry files live one directory below both <c>releases/unreleased.md</c> and versioned release notes. The composer
    /// therefore preserves each destination's target while recalculating its relative path from the composed document.
    /// External, rooted, query-only, and fragment-only destinations remain unchanged. The transformation deliberately
    /// excludes inline and fenced code so examples retain their original bytes.
    /// </remarks>
    private static string RebaseRelativeLinkDestinations(string markdown, string entryPath, string destinationPath)
    {
        var entryDirectory = Path.GetDirectoryName(entryPath);
        var destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrWhiteSpace(entryDirectory) || string.IsNullOrWhiteSpace(destinationDirectory))
        {
            return markdown;
        }

        var rebased = new StringBuilder(markdown.Length);
        var inlineCodeRanges = FindInlineCodeRanges(markdown);
        var inFencedCodeBlock = false;
        var fenceDelimiter = '\0';
        var fenceDelimiterCount = 0;
        var lineStart = 0;
        while (lineStart < markdown.Length)
        {
            var lineEnd = markdown.IndexOf('\n', lineStart);
            var contentEnd = lineEnd < 0 ? markdown.Length : lineEnd;
            var line = markdown.Substring(lineStart, contentEnd - lineStart);
            if (TryGetFenceDelimiter(line, out var candidateFenceDelimiter, out var candidateFenceDelimiterCount))
            {
                if (!inFencedCodeBlock)
                {
                    inFencedCodeBlock = true;
                    fenceDelimiter = candidateFenceDelimiter;
                    fenceDelimiterCount = candidateFenceDelimiterCount;
                }
                else if (candidateFenceDelimiter == fenceDelimiter && candidateFenceDelimiterCount >= fenceDelimiterCount)
                {
                    inFencedCodeBlock = false;
                    fenceDelimiter = '\0';
                    fenceDelimiterCount = 0;
                }

                rebased.Append(line);
            }
            else if (inFencedCodeBlock)
            {
                rebased.Append(line);
            }
            else
            {
                rebased.Append(RewriteLinkDestinations(line, entryDirectory, destinationDirectory, lineStart, inlineCodeRanges));
            }

            if (lineEnd >= 0)
            {
                rebased.Append('\n');
                lineStart = lineEnd + 1;
            }
            else
            {
                break;
            }
        }

        return rebased.ToString();
    }

    private static IReadOnlyList<MarkdownRange> FindInlineCodeRanges(string markdown)
    {
        var ranges = new List<MarkdownRange>();
        var position = 0;
        while (position < markdown.Length)
        {
            var openingDelimiter = markdown.IndexOf('`', position);
            if (openingDelimiter < 0)
            {
                break;
            }

            var openingDelimiterCount = CountRun(markdown, openingDelimiter, '`');
            var delimiter = new string('`', openingDelimiterCount);
            var closingDelimiter = markdown.IndexOf(delimiter, openingDelimiter + openingDelimiterCount, StringComparison.Ordinal);
            if (closingDelimiter < 0)
            {
                position = openingDelimiter + openingDelimiterCount;
                continue;
            }

            ranges.Add(new MarkdownRange(openingDelimiter, closingDelimiter + openingDelimiterCount));
            position = closingDelimiter + openingDelimiterCount;
        }

        return ranges;
    }

    private static string RewriteLinkDestinations(
        string markdown,
        string entryDirectory,
        string destinationDirectory,
        int sourceOffset,
        IReadOnlyList<MarkdownRange> inlineCodeRanges)
    {
        var rewritten = markdown;
        foreach (var pattern in RelativeLinkDestinationPatterns)
        {
            rewritten = pattern.Replace(
                rewritten,
                match =>
                {
                    var destination = match.Groups["destination"];
                    if (IsInInlineCodeRange(destination.Index + sourceOffset, inlineCodeRanges))
                    {
                        return match.Value;
                    }

                    var rebasedDestination = RebaseRelativeDestination(destination.Value, entryDirectory, destinationDirectory);
                    if (string.Equals(destination.Value, rebasedDestination, StringComparison.Ordinal))
                    {
                        return match.Value;
                    }

                    var relativeStart = destination.Index - match.Index;
                    return match.Value[..relativeStart]
                           + rebasedDestination
                           + match.Value[(relativeStart + destination.Length)..];
                });
        }

        return rewritten;
    }

    private static bool IsInInlineCodeRange(int position, IReadOnlyList<MarkdownRange> inlineCodeRanges)
    {
        return inlineCodeRanges.Any(range => position >= range.Start && position < range.End);
    }

    private static string RebaseRelativeDestination(string destination, string entryDirectory, string destinationDirectory)
    {
        var suffixStart = destination.IndexOfAny(['?', '#']);
        var path = suffixStart < 0 ? destination : destination[..suffixStart];
        var suffix = suffixStart < 0 ? string.Empty : destination[suffixStart..];
        var sourcePath = Path.GetFullPath(Path.Combine(entryDirectory, path.Replace('/', Path.DirectorySeparatorChar)));
        var relativePath = Path.GetRelativePath(destinationDirectory, sourcePath).Replace(Path.DirectorySeparatorChar, '/');
        if (path.EndsWith("/", StringComparison.Ordinal) && !relativePath.EndsWith("/", StringComparison.Ordinal))
        {
            relativePath += "/";
        }

        return relativePath + suffix;
    }

    private static bool TryGetFenceDelimiter(string line, out char delimiter, out int delimiterCount)
    {
        var position = 0;
        while (position < line.Length && position < 3 && line[position] == ' ')
        {
            position++;
        }

        delimiter = position < line.Length ? line[position] : '\0';
        delimiterCount = delimiter is '`' or '~' ? CountRun(line, position, delimiter) : 0;
        return delimiterCount >= 3;
    }

    private static int CountRun(string value, int start, char character)
    {
        var end = start;
        while (end < value.Length && value[end] == character)
        {
            end++;
        }

        return end - start;
    }

    private readonly record struct MarkdownRange(int Start, int End);

    /// <summary>
    /// Gets whether a repository-relative path can be an append-only unreleased entry.
    /// </summary>
    /// <param name="repositoryRelativePath">Slash-separated repository-relative path.</param>
    /// <returns><see langword="true"/> when the path is a direct valid entry file.</returns>
    internal static bool IsEntryPath(string repositoryRelativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRelativePath);

        var normalizedPath = repositoryRelativePath.Replace('\\', '/');
        if (!normalizedPath.StartsWith(EntriesDirectoryPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var fileName = normalizedPath[EntriesDirectoryPrefix.Length..];
        return !fileName.Contains('/') && IsEntryFileName(fileName);
    }

    /// <summary>
    /// Gets the exact marker used by the living-note template for a section.
    /// </summary>
    /// <param name="section">Supported section identifier.</param>
    /// <returns>Exact HTML comment marker.</returns>
    internal static string MarkerFor(string section)
    {
        if (!Sections.Contains(section, StringComparer.Ordinal))
        {
            throw new ArgumentOutOfRangeException(nameof(section), section, "The section is not a supported unreleased-entry destination.");
        }

        return $"<!-- appsurface:unreleased-entries section=\"{section}\" -->";
    }

    private static UnreleasedEntry Parse(string path, string content)
    {
        var firstLineEnd = content.IndexOf('\n');
        var directive = (firstLineEnd >= 0 ? content[..firstLineEnd] : content).TrimEnd('\r');
        if (directive.Length < EntryDirectivePrefix.Length + EntryDirectiveSuffix.Length
            || !directive.StartsWith(EntryDirectivePrefix, StringComparison.Ordinal)
            || !directive.EndsWith(EntryDirectiveSuffix, StringComparison.Ordinal))
        {
            throw new UnreleasedEntryException($"Unreleased entry '{Path.GetFileName(path)}' must begin with '{EntryDirectivePrefix}<section>{EntryDirectiveSuffix}'.");
        }

        var sectionLength = directive.Length - EntryDirectivePrefix.Length - EntryDirectiveSuffix.Length;
        var section = directive.Substring(EntryDirectivePrefix.Length, sectionLength);
        if (!Sections.Contains(section, StringComparer.Ordinal))
        {
            throw new UnreleasedEntryException($"Unreleased entry '{Path.GetFileName(path)}' uses unsupported section '{section}'. Supported sections: {string.Join(", ", Sections)}.");
        }

        var markdown = (firstLineEnd >= 0 ? content[(firstLineEnd + 1)..] : string.Empty)
            .TrimStart('\r', '\n')
            .TrimEnd('\r', '\n');
        if (string.IsNullOrWhiteSpace(markdown))
        {
            throw new UnreleasedEntryException($"Unreleased entry '{Path.GetFileName(path)}' must contain Markdown after its section directive.");
        }

        if (markdown.Contains("<!-- appsurface:unreleased-entries", StringComparison.Ordinal)
            || markdown.Contains("<!-- appsurface:unreleased-entry", StringComparison.Ordinal))
        {
            throw new UnreleasedEntryException($"Unreleased entry '{Path.GetFileName(path)}' must not contain an AppSurface unreleased-entry composition marker.");
        }

        if (TopLevelHeadingPattern.IsMatch(markdown))
        {
            throw new UnreleasedEntryException($"Unreleased entry '{Path.GetFileName(path)}' must not introduce a top-level '#' or '##' section; use the declared destination or a nested '###' heading.");
        }

        return new UnreleasedEntry(path, section, markdown);
    }

    private static bool IsEntryFileName(string fileName)
    {
        return EntryFileNamePattern.IsMatch(fileName)
               && DateOnly.TryParseExact(
                   fileName[..10],
                   "yyyy-MM-dd",
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.None,
                   out _);
    }

    private static string ComputeSha256(byte[] content)
    {
        var hash = SHA256.HashData(content);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

/// <summary>
/// One validated append-only Markdown entry and its destination section.
/// </summary>
/// <param name="Path">Absolute source-file path.</param>
/// <param name="Section">Stable section identifier selected by the file directive.</param>
/// <param name="Markdown">Validated Markdown inserted at the section bottom.</param>
internal sealed record UnreleasedEntry(string Path, string Section, string Markdown);

/// <summary>
/// Validated entries together with the source files release preparation removes after archival.
/// </summary>
/// <param name="Entries">Entries to compose.</param>
/// <param name="Snapshots">Absolute source-file paths and content digests in deterministic filename order.</param>
internal sealed record UnreleasedEntrySet(IReadOnlyList<UnreleasedEntry> Entries, IReadOnlyList<UnreleasedEntrySnapshot> Snapshots)
{
    /// <summary>
    /// Gets absolute source-file paths in deterministic filename order.
    /// </summary>
    internal IReadOnlyList<string> Paths => Snapshots.Select(snapshot => snapshot.Path).ToArray();
}

/// <summary>
/// Captures one append-only entry source file and the bytes release preparation must revalidate before deletion.
/// </summary>
/// <param name="Path">Absolute source-file path.</param>
/// <param name="Sha256">Lowercase SHA-256 digest of the source bytes.</param>
internal sealed record UnreleasedEntrySnapshot(string Path, string Sha256);

/// <summary>
/// Indicates an invalid append-only unreleased entry or living-note template marker shape.
/// </summary>
internal sealed class UnreleasedEntryException : Exception
{
    /// <summary>
    /// Creates an exception with a reader-actionable validation message.
    /// </summary>
    /// <param name="message">Validation failure description.</param>
    internal UnreleasedEntryException(string message)
        : base(message)
    {
    }
}
