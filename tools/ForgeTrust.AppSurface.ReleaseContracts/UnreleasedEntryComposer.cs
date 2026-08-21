using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;

namespace ForgeTrust.AppSurface.ReleaseContracts;

/// <summary>
/// Loads append-only release-note entries and inserts them into a stable living-note template.
/// </summary>
/// <remarks>
/// Entries are individual Markdown files in a flat, filename-sorted directory. This avoids concurrent edits to the
/// shared living-note template when independent work streams describe their release-facing changes. Each entry begins
/// with an exact section directive and may add nested headings or ordinary Markdown, but cannot introduce a new
/// top-level section. The supported section identifiers are declared by the template's composition markers, so consumer
/// projects can use their own release-note structure without a code change. Callers use the composed content for
/// rendering or release preparation; the checked-in template remains stable until its owning release workflow resets it.
/// </remarks>
internal static class UnreleasedEntryComposer
{
    internal const string EntriesDirectoryName = "unreleased.entries";

    private const string EntriesDirectoryPrefix = "releases/" + EntriesDirectoryName + "/";
    private const string EntryDirectivePrefix = "<!-- appsurface:unreleased-entry section=\"";
    private const string EntryDirectiveSuffix = "\" -->";
    private static readonly Regex SectionIdentifierPattern = new(
        "^[a-z0-9]+(?:-[a-z0-9]+)*$",
        RegexOptions.CultureInvariant);
    private static readonly Regex TemplateMarkerPattern = new(
        "^ {0,3}<!-- appsurface:unreleased-entries section=\"(?<section>[a-z0-9]+(?:-[a-z0-9]+)*)\" -->[ \\t]*\\r?$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);
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
    /// <param name="template">Living-note template that contains one marker for each template-declared section.</param>
    /// <param name="entries">Validated entries to insert.</param>
    /// <param name="destinationPath">Absolute path of the composed living or versioned release note.</param>
    /// <returns>The deterministic composed release note.</returns>
    /// <exception cref="UnreleasedEntryException">Thrown when the template does not have the expected marker shape.</exception>
    internal static string Compose(string template, IEnumerable<UnreleasedEntry> entries, string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        EnsureTerminalSafeText(template, "The living-note template");

        var markers = GetTemplateMarkers(template);
        var sections = markers.Select(marker => marker.Section).ToArray();
        var sectionSet = new HashSet<string>(sections, StringComparer.Ordinal);
        var entryList = entries.ToArray();
        foreach (var entry in entryList)
        {
            EnsureTerminalSafeText(entry.Markdown, $"Unreleased entry '{Path.GetFileName(entry.Path)}'");
            if (!sectionSet.Contains(entry.Section))
            {
                throw new UnreleasedEntryException(
                    $"Unreleased entry '{Path.GetFileName(entry.Path)}' uses section '{entry.Section}', which is not declared by the living-note template. Supported sections: {string.Join(", ", sections)}.");
            }
        }

        var entriesBySection = entryList
            .GroupBy(entry => entry.Section, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(entry => Path.GetFileName(entry.Path), StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);
        var composed = new StringBuilder(template.Length);
        var sourcePosition = 0;
        foreach (var marker in markers)
        {
            var section = marker.Section;
            var sectionContent = entriesBySection.TryGetValue(section, out var sectionEntries)
                ? string.Join("\n\n", sectionEntries.Select(entry => RebaseRelativeLinkDestinations(entry.Markdown, entry.Path, destinationPath)))
                : string.Empty;
            composed.Append(template, sourcePosition, marker.Index - sourcePosition);
            composed.Append(sectionContent);
            sourcePosition = marker.Index + marker.Length;
        }

        composed.Append(template, sourcePosition, template.Length - sourcePosition);
        var composedText = composed.ToString();

        if (composedText.Contains("<!-- appsurface:unreleased-entries", StringComparison.Ordinal))
        {
            throw new UnreleasedEntryException("The living-note template contains an unsupported or malformed append-only entry marker.");
        }

        return composedText;
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
    /// excludes inline, fenced, and indented code so examples retain their original bytes.
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
        var codeBlockRanges = FindCodeBlockRanges(markdown);
        var lineStart = 0;
        while (lineStart < markdown.Length)
        {
            var lineEnd = markdown.IndexOf('\n', lineStart);
            var contentEnd = lineEnd < 0 ? markdown.Length : lineEnd;
            var line = markdown.Substring(lineStart, contentEnd - lineStart);
            if (IntersectsMarkdownRange(lineStart, contentEnd, codeBlockRanges))
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

    /// <summary>
    /// Collects source-coordinate ranges for code blocks parsed from entry Markdown.
    /// </summary>
    /// <param name="markdown">Entry Markdown whose code blocks are excluded from rewriting.</param>
    /// <returns>Half-open source ranges for every fenced or indented code block, including nested containers.</returns>
    private static IReadOnlyList<MarkdownRange> FindCodeBlockRanges(string markdown)
    {
        var ranges = new List<MarkdownRange>();
        AddCodeBlockRanges(Markdown.Parse(markdown), ranges);
        return ranges;
    }

    /// <summary>
    /// Recursively adds code-block spans from a Markdown block container.
    /// </summary>
    /// <param name="container">Container whose descendants may include code blocks.</param>
    /// <param name="ranges">Collection receiving half-open source ranges.</param>
    private static void AddCodeBlockRanges(ContainerBlock container, List<MarkdownRange> ranges)
    {
        foreach (var block in container)
        {
            if (block is CodeBlock)
            {
                ranges.Add(new MarkdownRange(block.Span.Start, block.Span.End + 1));
            }

            if (block is ContainerBlock childContainer)
            {
                AddCodeBlockRanges(childContainer, ranges);
            }
        }
    }

    /// <summary>
    /// Collects source-coordinate ranges delimited by inline-code backticks.
    /// </summary>
    /// <param name="markdown">Entry Markdown whose inline-code spans are excluded from rewriting.</param>
    /// <returns>Half-open source ranges for matched inline-code delimiters and their contents.</returns>
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

    /// <summary>
    /// Rewrites eligible link destinations from an immutable source line.
    /// </summary>
    /// <param name="markdown">One source line outside a Markdown code block.</param>
    /// <param name="entryDirectory">Directory that resolves entry-relative destinations.</param>
    /// <param name="destinationDirectory">Directory that must resolve the composed destinations.</param>
    /// <param name="sourceOffset">Absolute offset of the source line in the entry Markdown.</param>
    /// <param name="inlineCodeRanges">Inline-code ranges in the original entry Markdown.</param>
    /// <returns>The line with eligible destination spans rebased without moving source coordinates.</returns>
    private static string RewriteLinkDestinations(
        string markdown,
        string entryDirectory,
        string destinationDirectory,
        int sourceOffset,
        IReadOnlyList<MarkdownRange> inlineCodeRanges)
    {
        var rewritten = new StringBuilder(markdown.Length);
        var sourcePosition = 0;
        var destinations = RelativeLinkDestinationPatterns
            .SelectMany(pattern => pattern.Matches(markdown).Cast<Match>())
            .OrderBy(match => match.Index)
            .ThenByDescending(match => match.Length)
            .Select(match => match.Groups["destination"]);
        foreach (var destination in destinations)
        {
            rewritten.Append(markdown, sourcePosition, destination.Index - sourcePosition);
            var rebasedDestination = IsInInlineCodeRange(destination.Index + sourceOffset, inlineCodeRanges)
                ? destination.Value
                : RebaseRelativeDestination(destination.Value, entryDirectory, destinationDirectory);
            rewritten.Append(rebasedDestination);
            sourcePosition = destination.Index + destination.Length;
        }

        rewritten.Append(markdown, sourcePosition, markdown.Length - sourcePosition);
        return rewritten.ToString();
    }

    /// <summary>
    /// Determines whether a source position belongs to an inline-code span.
    /// </summary>
    /// <param name="position">Zero-based source position.</param>
    /// <param name="inlineCodeRanges">Inline-code ranges in the source Markdown.</param>
    /// <returns><see langword="true"/> when the position is preserved as inline code.</returns>
    private static bool IsInInlineCodeRange(int position, IReadOnlyList<MarkdownRange> inlineCodeRanges)
    {
        return IsInMarkdownRange(position, inlineCodeRanges);
    }

    /// <summary>
    /// Determines whether a source position lies within any half-open Markdown range.
    /// </summary>
    /// <param name="position">Zero-based source position.</param>
    /// <param name="ranges">Half-open ranges to inspect.</param>
    /// <returns><see langword="true"/> when the position is contained by a range.</returns>
    private static bool IsInMarkdownRange(int position, IReadOnlyList<MarkdownRange> ranges)
    {
        return ranges.Any(range => position >= range.Start && position < range.End);
    }

    /// <summary>
    /// Determines whether a source line overlaps any half-open Markdown range.
    /// </summary>
    /// <param name="start">Inclusive line-start source position.</param>
    /// <param name="end">Exclusive line-end source position.</param>
    /// <param name="ranges">Half-open ranges to inspect.</param>
    /// <returns><see langword="true"/> when the line and a range share source content.</returns>
    private static bool IntersectsMarkdownRange(int start, int end, IReadOnlyList<MarkdownRange> ranges)
    {
        return ranges.Any(range => range.Start < end && start < range.End);
    }

    private static string RebaseRelativeDestination(string destination, string entryDirectory, string destinationDirectory)
    {
        var suffixStart = destination.IndexOfAny(['?', '#']);
        var path = suffixStart < 0 ? destination : destination[..suffixStart];
        var suffix = suffixStart < 0 ? string.Empty : destination[suffixStart..];
        var normalizedPath = path.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalizedPath) || path.StartsWith('\\'))
        {
            return destination;
        }

        var sourcePath = Path.GetFullPath(Path.Join(entryDirectory, normalizedPath));
        var relativePath = Path.GetRelativePath(destinationDirectory, sourcePath).Replace(Path.DirectorySeparatorChar, '/');
        if (path.EndsWith("/", StringComparison.Ordinal) && !relativePath.EndsWith("/", StringComparison.Ordinal))
        {
            relativePath += "/";
        }

        return relativePath + suffix;
    }

    /// <summary>
    /// Counts consecutive occurrences of a character from a source position.
    /// </summary>
    /// <param name="value">Source text to inspect.</param>
    /// <param name="start">Zero-based position where the candidate run begins.</param>
    /// <param name="character">Character expected in the run.</param>
    /// <returns>Number of consecutive matching characters.</returns>
    private static int CountRun(string value, int start, char character)
    {
        var end = start;
        while (end < value.Length && value[end] == character)
        {
            end++;
        }

        return end - start;
    }

    /// <summary>
    /// Half-open source-coordinate range in an entry Markdown document.
    /// </summary>
    /// <param name="Start">Inclusive zero-based source position.</param>
    /// <param name="End">Exclusive zero-based source position.</param>
    private readonly record struct MarkdownRange(int Start, int End);

    private readonly record struct TemplateMarker(int Index, int Length, string Section);

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
    /// <param name="section">Valid section identifier.</param>
    /// <returns>Exact HTML comment marker.</returns>
    internal static string MarkerFor(string section)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(section);
        if (!SectionIdentifierPattern.IsMatch(section))
        {
            throw new ArgumentException("The section identifier must use lowercase letters, digits, and single hyphens.", nameof(section));
        }

        return $"<!-- appsurface:unreleased-entries section=\"{section}\" -->";
    }

    /// <summary>
    /// Reads the unique composition markers declared by the living-note template.
    /// </summary>
    /// <param name="template">Living-note template containing composition markers.</param>
    /// <returns>Composition markers in their template order.</returns>
    /// <exception cref="UnreleasedEntryException">Thrown when the template has no markers or repeats a section.</exception>
    private static IReadOnlyList<TemplateMarker> GetTemplateMarkers(string template)
    {
        var codeBlockRanges = FindCodeBlockRanges(template);
        var markers = new List<TemplateMarker>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in TemplateMarkerPattern.Matches(template))
        {
            if (IntersectsMarkdownRange(match.Index, match.Index + match.Length, codeBlockRanges))
            {
                continue;
            }

            var section = match.Groups["section"].Value;
            if (!seen.Add(section))
            {
                throw new UnreleasedEntryException($"The living-note template must contain exactly one '{MarkerFor(section)}' marker.");
            }

            markers.Add(new TemplateMarker(match.Index, match.Length, section));
        }

        if (markers.Count == 0)
        {
            throw new UnreleasedEntryException("The living-note template must contain at least one append-only entry marker.");
        }

        return markers;
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
        if (!SectionIdentifierPattern.IsMatch(section))
        {
            throw new UnreleasedEntryException($"Unreleased entry '{Path.GetFileName(path)}' uses invalid section '{section}'. Section identifiers must use lowercase letters, digits, and single hyphens.");
        }

        var markdown = (firstLineEnd >= 0 ? content[(firstLineEnd + 1)..] : string.Empty)
            .TrimStart('\r', '\n')
            .TrimEnd('\r', '\n');
        if (string.IsNullOrWhiteSpace(markdown))
        {
            throw new UnreleasedEntryException($"Unreleased entry '{Path.GetFileName(path)}' must contain Markdown after its section directive.");
        }

        EnsureTerminalSafeText(markdown, $"Unreleased entry '{Path.GetFileName(path)}'");

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

    private static void EnsureTerminalSafeText(string text, string description)
    {
        if (text.Any(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t'))
        {
            throw new UnreleasedEntryException($"{description} must not contain terminal control characters.");
        }
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
