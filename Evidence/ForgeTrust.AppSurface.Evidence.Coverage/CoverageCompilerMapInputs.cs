using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>Preserves distinct C# state-machine maps at the standard reporter input boundary.</summary>
/// <remarks>
/// Compiler selections can give one source method different generated class names. ReportGenerator
/// 5.5.10 folds those classes within a single Cobertura document and overwrites overlapping line
/// observations in document order. Separate input documents retain each original raw class map.
/// This adapter partitions XML structure only: it never combines counters, identifies branches across
/// builds, removes an unhit condition, or changes a source filename. The original report is untouched.
/// Explicit reports need absolute class filenames or a single absolute source anchor for canonical
/// relative filenames. Other inputs retain the reporter's existing path resolution. This is report
/// data handling, not execution authority.
/// </remarks>
internal static partial class CoverageCompilerMapInputs
{
    /// <summary>Returns the original inputs or an exactly-once partition of colliding compiler maps.</summary>
    /// <param name="coverageFiles">Explicit report paths; reporter glob expressions pass through unchanged.</param>
    /// <param name="outputDirectory">Owned merge output beneath which fresh partition files are retained.</param>
    /// <param name="token">The existing merge operation token, also checked during reads and writes.</param>
    /// <returns>Reporter inputs containing every original class map exactly once.</returns>
    /// <exception cref="InvalidDataException">An explicit XML input cannot be parsed safely.</exception>
    /// <remarks>
    /// Partitioning does not establish that different maps describe equivalent branches. Their condition
    /// IDs and counts remain local to their original maps, including every untaken arm. Non-colliding
    /// classes remain together in one base document. The unsplit report is not also selected.
    /// </remarks>
    internal static async Task<IReadOnlyList<string>> PrepareAsync(IReadOnlyList<string> coverageFiles,
        string outputDirectory, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(coverageFiles);
        ArgumentException.ThrowIfNullOrEmpty(outputDirectory);
        token.ThrowIfCancellationRequested();
        var inputs = new List<string>();
        string? staging = null;
        for (var reportIndex = 0; reportIndex < coverageFiles.Count; reportIndex++)
        {
            token.ThrowIfCancellationRequested();
            var path = coverageFiles[reportIndex];
            if (!File.Exists(path))
            {
                inputs.Add(path);
                continue;
            }

            XDocument document;
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 81920, useAsync: true);
                using var reader = XmlReader.Create(stream, new XmlReaderSettings
                {
                    Async = true,
                    DtdProcessing = DtdProcessing.Ignore,
                    XmlResolver = null,
                });
                document = await XDocument.LoadAsync(reader, LoadOptions.PreserveWhitespace, token).ConfigureAwait(false);
            }
            catch (XmlException)
            {
                throw new InvalidDataException("The compiler-generated coverage map input was rejected.");
            }

            var maps = Classes(document).ToArray();
            var anchors = document.Element("coverage")?.Element("sources")?.Elements("source")
                .Select(source => source.Value).ToArray() ?? [];
            var absoluteAnchor = anchors.Length == 1 && Path.IsPathFullyQualified(anchors[0]);
            if (maps.Length == 0 || maps.Any(map => !HasStableSourcePath((string?)map.Attribute("filename"), absoluteAnchor)))
            {
                inputs.Add(path);
                continue;
            }

            var candidates = maps.Select((map, index) => (Map: map, Index: index,
                    Match: StateMachineName().Match((string?)map.Attribute("name") ?? string.Empty)))
                .Where(item => item.Match.Success)
                .GroupBy(item => (Package: (string?)item.Map.Ancestors("package").First().Attribute("name"),
                    File: (string)item.Map.Attribute("filename")!, Owner: item.Match.Groups["owner"].Value,
                    Method: item.Match.Groups["method"].Value));
            var partitioned = new HashSet<int>();
            foreach (var group in candidates)
            {
                token.ThrowIfCancellationRequested();
                var family = group.ToArray();
                if (family.Select(item => (string?)item.Map.Attribute("name")).Distinct(StringComparer.Ordinal).Count() < 2)
                    continue;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var overlap = false;
                foreach (var item in family)
                {
                    // A class-level row and its method projection are the same map observation.
                    foreach (var number in item.Map.Descendants("line").Select(line => (string?)line.Attribute("number"))
                        .Where(number => number is not null).Distinct(StringComparer.Ordinal))
                        overlap |= !seen.Add(number!);
                }
                if (overlap) partitioned.UnionWith(family.Select(item => item.Index));
            }
            if (partitioned.Count == 0)
            {
                inputs.Add(path);
                continue;
            }

            staging ??= Path.Join(Path.GetFullPath(outputDirectory), "compiler-map-inputs-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            if (partitioned.Count < maps.Length)
                inputs.Add(await SavePartitionAsync(document, staging, reportIndex, "base",
                    index => !partitioned.Contains(index), token).ConfigureAwait(false));
            foreach (var index in partitioned.Order())
            {
                token.ThrowIfCancellationRequested();
                inputs.Add(await SavePartitionAsync(document, staging, reportIndex,
                    index.ToString("D6", CultureInfo.InvariantCulture), candidate => candidate == index, token).ConfigureAwait(false));
            }
        }
        return inputs.AsReadOnly();
    }

    private static IEnumerable<XElement> Classes(XDocument document) =>
        document.Element("coverage")?.Element("packages")?.Elements("package")
            .Elements("classes").Elements("class") ?? [];

    private static bool HasStableSourcePath(string? filename, bool absoluteAnchor) =>
        !string.IsNullOrEmpty(filename) && (Path.IsPathFullyQualified(filename)
            || absoluteAnchor && !filename.Contains('\\') && !filename.Contains(':')
                && filename.Split('/').All(part => part.Length > 0 && part is not ("." or "..")));

    private static async Task<string> SavePartitionAsync(XDocument original, string staging, int reportIndex,
        string suffix, Func<int, bool> include, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var copy = new XDocument(original);
        var maps = Classes(copy).ToArray();
        for (var index = 0; index < maps.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            if (!include(index)) maps[index].Remove();
        }
        foreach (var package in copy.Root!.Element("packages")!.Elements("package").ToArray())
            if (!package.Elements("classes").Elements("class").Any()) package.Remove();
        var path = Path.Join(staging, reportIndex.ToString("D6", CultureInfo.InvariantCulture) + "-" + suffix + ".xml");
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 81920, useAsync: true);
        await using var writer = XmlWriter.Create(output, new XmlWriterSettings
        {
            Async = true,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            CloseOutput = false,
        });
        await copy.SaveAsync(writer, token).ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return path;
    }

    [GeneratedRegex(@"^(?<owner>.+)/<(?<method>[^/>]+)>d__[0-9]+(?:`[0-9]+)?$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex StateMachineName();
}
