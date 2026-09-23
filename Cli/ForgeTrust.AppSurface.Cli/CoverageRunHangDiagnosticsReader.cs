using System.Xml;
using ArtifactReader = ForgeTrust.AppSurface.Evidence.Coverage.CoverageRunArtifactReader;

#if EVIDENCE_COVERAGE_CORE
namespace ForgeTrust.AppSurface.Evidence.Coverage;
#else
namespace ForgeTrust.AppSurface.Cli;
#endif

/// <summary>A safe, relative observation from one VSTest sequence file.</summary>
/// <param name="RelativePath">Path beneath the AppSurface output root, with forward separators.</param>
/// <param name="LastStartedTest">Allowlisted last-started name, or null when absent or unsafe.</param>
internal sealed record CoverageRunHangSequenceObservation(string RelativePath, string? LastStartedTest);

/// <summary>Bounded reader for VSTest sequence files beneath an owned coverage results directory.</summary>
internal static class CoverageRunHangDiagnosticsReader
{
    internal const int MaximumDepth = 4;
    internal const int MaximumEntriesPerDirectory = 256;
    internal const int MaximumEntries = 1024;
    internal const int MaximumDirectories = 128;
    internal const int MaximumSequences = 8;
    internal const long MaximumFileBytes = 1024 * 1024;
    internal const long MaximumTotalBytes = 8 * 1024 * 1024;
    internal const int MaximumTests = 10_000;
    internal const int MaximumXmlDepth = 8;
    internal const int MaximumNameLength = 256;
    private static readonly TimeSpan BestEffortBudget = TimeSpan.FromSeconds(2);

    /// <summary>A bounded inspection status and zero or more scoped sequence observations.</summary>
    /// <param name="Status">Stable diagnostic status; never changes the primary coverage outcome.</param>
    /// <param name="Sequences">Safe relative paths and allowlisted last-started names.</param>
    internal record InspectionResult(
        string Status,
        IReadOnlyList<CoverageRunHangSequenceObservation> Sequences);

    /// <summary>Inspects sequence candidates under a verified invocation directory using output root for containment.</summary>
    /// <param name="ownedResultsDirectory">The invocation-specific owned directory to inspect.</param>
    /// <param name="outputDirectory">Verified output root containing the owned invocation directory.</param>
    /// <param name="clock">Optional clock used for the best-effort inspection budget.</param>
    internal static InspectionResult Inspect(
        string ownedResultsDirectory,
        string outputDirectory,
        TimeProvider? clock = null)
    {
        var root = Path.GetFullPath(ownedResultsDirectory);
        var output = Path.GetFullPath(outputDirectory);
        if (!IsWithin(output, root)) return Result("escaping");
        if (!Directory.Exists(root)) return Result("missing");
        try
        {
            for (var path = root; IsWithin(output, path); path = Path.GetDirectoryName(path) ?? string.Empty)
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return Result("escaping");
                if (string.Equals(path, output, PathComparison)) break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result("unreadable");
        }

        clock ??= TimeProvider.System;
        var started = clock.GetTimestamp();
        bool BudgetExpired() => clock.GetElapsedTime(started) >= BestEffortBudget;
        var pending = new Queue<(string Path, int Depth)>();
        pending.Enqueue((root, 0));
        var observations = new List<CoverageRunHangSequenceObservation>();
        var seenCandidates = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var visitedEntries = 0;
        var visitedDirectories = 0;
        long totalBytes = 0;
        var testEntries = 0;
        var sequenceCandidates = 0;
        var invalidStatus = (string?)null;

        while (pending.Count > 0)
        {
            if (BudgetExpired() || visitedDirectories >= MaximumDirectories) return Result("inspection-limited", observations);
            var (directory, depth) = pending.Dequeue();
            visitedDirectories++;
            IEnumerable<FileSystemInfo> children;
            try { children = new DirectoryInfo(directory).EnumerateFileSystemInfos(); }
            catch (UnauthorizedAccessException) { invalidStatus ??= "unreadable"; continue; }
            catch (IOException) { invalidStatus ??= "unreadable"; continue; }

            var localEntries = 0;
            foreach (var child in children)
            {
                if (BudgetExpired() || visitedEntries >= MaximumEntries || localEntries >= MaximumEntriesPerDirectory)
                    return Result("inspection-limited", observations);
                visitedEntries++;
                localEntries++;
                var isSequence = IsSequenceFileName(child.Name);
                var relative = Relative(output, child.FullName);
                if (child.Name.Length > MaximumNameLength)
                {
                    if (isSequence) invalidStatus ??= "escaping";
                    continue;
                }

                FileAttributes attributes;
                try { attributes = child.Attributes; }
                catch (UnauthorizedAccessException) { if (isSequence) invalidStatus ??= "unreadable"; continue; }
                catch (IOException) { if (isSequence) invalidStatus ??= "unreadable"; continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    invalidStatus ??= "escaping";
                    continue;
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (depth >= MaximumDepth) { invalidStatus ??= "inspection-limited"; continue; }
                    pending.Enqueue((child.FullName, depth + 1));
                    continue;
                }
                if (!isSequence) continue;
                if (!seenCandidates.Add(Path.GetFullPath(child.FullName))) { invalidStatus ??= "duplicate"; continue; }
                if (sequenceCandidates >= MaximumSequences) return Result("inspection-limited", observations);
                sequenceCandidates++;
                if (!IsWithin(root, child.FullName)) { invalidStatus ??= "escaping"; continue; }

                try
                {
                    using var stream = ArtifactReader.OpenRegularFile(output, root, child.FullName);
                    if (stream.Length > MaximumFileBytes || totalBytes + stream.Length > MaximumTotalBytes)
                    { invalidStatus ??= "oversized"; continue; }
                    totalBytes += stream.Length;
                    using var reader = XmlReader.Create(stream, new XmlReaderSettings
                    {
                        DtdProcessing = DtdProcessing.Prohibit,
                        XmlResolver = null,
                        MaxCharactersInDocument = MaximumFileBytes,
                        IgnoreComments = true,
                        IgnoreProcessingInstructions = true
                    });
                    string? lastStartedTest = null;
                    var elementStack = new List<string>();
                    while (reader.Read())
                    {
                        if (BudgetExpired()) return Result("inspection-limited", observations);
                        if (reader.Depth > MaximumXmlDepth) throw new XmlException("XML nesting exceeds the allowed depth.");
                        if (reader.NodeType == XmlNodeType.Element && reader.Depth == 0
                            && (reader.LocalName != "TestSequence" || reader.NamespaceURI.Length != 0))
                            throw new XmlException("Unexpected sequence document root.");
                        if (reader.NodeType == XmlNodeType.EndElement)
                        {
                            if (elementStack.Count > 0) elementStack.RemoveAt(elementStack.Count - 1);
                            continue;
                        }
                        if (reader.NodeType != XmlNodeType.Element) continue;
                        if (reader.LocalName == "Test" && reader.NamespaceURI.Length == 0
                            && elementStack.Count > 0 && elementStack[^1] == "TestSequence")
                        {
                            if (++testEntries > MaximumTests) return Result("inspection-limited", observations);
                            var name = reader.GetAttribute("Name");
                            lastStartedTest = IsSafeName(name) ? name : null;
                        }
                        if (!reader.IsEmptyElement) elementStack.Add(reader.LocalName);
                    }
                    observations.Add(new(relative, lastStartedTest));
                }
                catch (XmlException) { invalidStatus ??= "malformed"; }
                catch (UnauthorizedAccessException) { invalidStatus ??= "unreadable"; }
                catch (IOException ex)
                {
                    invalidStatus ??= ex.Message.Contains("escaped", StringComparison.OrdinalIgnoreCase) ? "escaping" : "unreadable";
                }
            }
        }

        return Result(invalidStatus ?? (observations.Count == 0
            ? "missing"
            : observations.Any(observation => observation.LastStartedTest is null) ? "name-omitted" : "found"), observations);
    }

    private static InspectionResult Result(string status, IReadOnlyList<CoverageRunHangSequenceObservation>? observations = null)
        => new(status, observations ?? Array.Empty<CoverageRunHangSequenceObservation>());

    private static bool IsSafeName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaximumNameLength || !(char.IsAsciiLetter(name[0]) || name[0] == '_')) return false;
        return name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '+');
    }

    private static bool IsSequenceFileName(string name)
    {
        if (name.Equals("Sequence.xml", StringComparison.OrdinalIgnoreCase)) return true;
        if (!name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) return false;

        const string prefix = "Sequence_";
        const string suffix = "_Sequence.xml";
        return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && Guid.TryParse(name[prefix.Length..^4], out _)
            || name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                && Guid.TryParse(name[..^suffix.Length], out _);
    }

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static bool IsWithin(string root, string path)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        path = Path.GetFullPath(path);
        return path.Equals(root, PathComparison) || path.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
