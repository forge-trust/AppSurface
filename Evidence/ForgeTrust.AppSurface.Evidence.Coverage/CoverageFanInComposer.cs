using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>
/// Composes normalized Cobertura observations using the producer's conservative fan-in rules.
/// </summary>
/// <remarks>
/// Record occurrences remain distinct because the downstream patch gate consumes class and method line
/// collections. Branch observations are never summed: an observation can promote a point only when it proves
/// full coverage with the denominator already retained for that point.
/// </remarks>
internal static class CoverageFanInComposer
{
    private const string InvalidCoverageDataMessage = "Coverage report data is invalid.";
    private const string InvalidCoveragePathMessage = "Coverage path is invalid.";
    private const string InvalidPrimaryTimingsMessage = "Primary coverage timings are invalid.";
    private const string OutputVersion = "appsurface-verified-fanin-v1";

    private static readonly Regex BranchCoveragePattern = new(
        @"\A[0-9.]+% \((\d+)/(\d+)\)\z",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// Composes the supplied primary and supplementary reports into a conservative Cobertura document.
    /// </summary>
    /// <param name="inputs">Reports paired with the source root against which their filenames are resolved.</param>
    /// <returns>A new document containing every retained class, method, and measurable line occurrence.</returns>
    /// <exception cref="InvalidDataException">An input has invalid identity, path, line, or branch data.</exception>
    internal static XDocument Compose(IReadOnlyList<(XDocument Document, string SourceRoot)> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count == 0)
        {
            throw InvalidCoverageData();
        }

        var classes = new Dictionary<ClassIdentity, ClassAccumulator>();

        foreach (var (document, sourceRoot) in inputs)
        {
            if (document is null)
            {
                throw InvalidCoverageData();
            }

            var portableRoot = ParseSourceRoot(sourceRoot);
            var classOccurrences = new Dictionary<ClassBaseIdentity, int>();
            long documentClassLineCount = 0;

            foreach (var package in GetPackages(document))
            {
                var packageName = package.Attribute("name")?.Value;
                if (packageName is null)
                {
                    throw InvalidCoverageData();
                }

                foreach (var sourceClass in package.Elements("classes").SelectMany(element => element.Elements("class")))
                {
                    var className = sourceClass.Attribute("name")?.Value;
                    var filename = sourceClass.Attribute("filename")?.Value;
                    if (className is null || filename is null)
                    {
                        throw InvalidCoverageData();
                    }

                    var relativeFilename = ResolveFilename(document, filename, portableRoot);
                    var baseIdentity = new ClassBaseIdentity(packageName, className, relativeFilename);
                    var occurrence = classOccurrences.TryGetValue(baseIdentity, out var previousOccurrence)
                        ? previousOccurrence
                        : 0;
                    classOccurrences[baseIdentity] = occurrence + 1;

                    var identity = new ClassIdentity(packageName, className, relativeFilename, occurrence);
                    if (!classes.TryGetValue(identity, out var accumulator))
                    {
                        accumulator = new ClassAccumulator();
                        classes.Add(identity, accumulator);
                    }

                    documentClassLineCount += ObserveLines(sourceClass.Element("lines"), accumulator.Lines);

                    var methodOccurrences = new Dictionary<MethodBaseIdentity, int>();
                    foreach (var method in sourceClass.Elements("methods").SelectMany(element => element.Elements("method")))
                    {
                        var methodName = method.Attribute("name")?.Value ?? string.Empty;
                        var signature = method.Attribute("signature")?.Value ?? string.Empty;
                        var methodBaseIdentity = new MethodBaseIdentity(methodName, signature);
                        var methodOccurrence = methodOccurrences.TryGetValue(methodBaseIdentity, out var previousMethodOccurrence)
                            ? previousMethodOccurrence
                            : 0;
                        methodOccurrences[methodBaseIdentity] = methodOccurrence + 1;

                        var methodIdentity = new MethodIdentity(methodName, signature, methodOccurrence);
                        if (!accumulator.Methods.TryGetValue(methodIdentity, out var methodLines))
                        {
                            methodLines = [];
                            accumulator.Methods.Add(methodIdentity, methodLines);
                        }

                        _ = ObserveLines(method.Element("lines"), methodLines);
                    }
                }
            }

            if (documentClassLineCount == 0)
            {
                throw InvalidCoverageData();
            }
        }

        return WriteComposition(classes);
    }

    /// <summary>
    /// Rewrites every Cobertura class filename as a source-root-relative portable path and returns the document.
    /// </summary>
    /// <param name="document">Cobertura document to normalize in place.</param>
    /// <param name="sourceRoot">Explicit absolute source root, in either Windows or POSIX spelling.</param>
    /// <exception cref="InvalidDataException">A filename is an alias, escapes the source root, or has an invalid spelling.</exception>
    internal static XDocument NormalizePaths(XDocument document, string sourceRoot)
    {
        ArgumentNullException.ThrowIfNull(document);
        var portableRoot = ParseSourceRoot(sourceRoot);
        var changes = new List<(XAttribute Attribute, string RelativePath)>();

        foreach (var sourceClass in GetClasses(document))
        {
            var filename = sourceClass.Attribute("filename");
            if (filename is null)
            {
                throw InvalidCoveragePath();
            }

            changes.Add((filename, ResolveFilename(document, filename.Value, portableRoot)));
        }

        foreach (var (attribute, relativePath) in changes)
        {
            attribute.Value = relativePath;
        }

        document.Root?.Element("sources")?.ReplaceWith(
            new XElement("sources", new XElement("source", portableRoot.Prefix)));

        return document;
    }

    /// <summary>
    /// Verifies that normalization retains every raw source point and observed hit without shrinking branches.
    /// </summary>
    /// <param name="raw">Original collector report.</param>
    /// <param name="normalized">Report produced by the configured normalizer.</param>
    /// <param name="sourceRoot">Explicit absolute source root for both reports.</param>
    /// <exception cref="InvalidDataException">Normalization removed a point or observed hit, or reduced branch points.</exception>
    internal static void ValidateNormalization(XDocument raw, XDocument normalized, string sourceRoot)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(normalized);
        var portableRoot = ParseSourceRoot(sourceRoot);
        var rawObservations = ReadLineObservations(raw, portableRoot);
        var normalizedObservations = ReadLineObservations(normalized, portableRoot);

        if (rawObservations.Count == 0
            || rawObservations.Any(observation =>
                !normalizedObservations.TryGetValue(observation.Key, out var normalizedHit)
                || observation.Value && !normalizedHit)
            || ReadRootCounter(raw, "branches-valid") > ReadRootCounter(normalized, "branches-valid"))
        {
            throw InvalidCoverageData();
        }
        var rawMethods = ReadMethodObservations(raw, portableRoot);
        var normalizedMethods = ReadMethodObservations(normalized, portableRoot);
        static Dictionary<(string File, string Name, string Signature, string Points), int> Inventory(
            Dictionary<NormalizationMethodIdentity, Dictionary<LineIdentity, LineFact>> methods) => methods
            .GroupBy(method => (method.Key.File, method.Key.Name, method.Key.Signature,
                string.Join(';', method.Value.OrderBy(point => point.Key.Number).ThenBy(point => point.Key.Occurrence)
                    .Select(point => FormattableString.Invariant($"{point.Key.Number},{point.Key.Occurrence},{(point.Value.Hits > 0 ? 1 : 0)},{point.Value.BranchesCovered},{point.Value.BranchesValid}")))))
            .ToDictionary(group => group.Key, group => group.Count());
        var expectedMethods = Inventory(rawMethods);
        var observedMethods = Inventory(normalizedMethods);
        if (expectedMethods.Count != observedMethods.Count || expectedMethods.Any(method =>
            !observedMethods.TryGetValue(method.Key, out var count) || count != method.Value))
            throw InvalidCoverageData();
    }

    /// <summary>Retains original method records when ReportGenerator folds generated classes or expands method ranges.</summary>
    /// <remarks>The collector remains the method oracle. Normalized class points are checked separately; this
    /// projection never invents method hits or uses ReportGenerator's reconstructed method ranges.</remarks>
    internal static XDocument RetainCollectorMethods(XDocument raw, XDocument normalized, string sourceRoot)
    {
        var portableRoot = ParseSourceRoot(sourceRoot);
        var result = new XDocument(normalized);
        var destinations = GetClasses(result).GroupBy(c => (
            Package: c.Ancestors("package").First().Attribute("name")?.Value ?? string.Empty,
            Class: c.Attribute("name")?.Value ?? string.Empty,
            File: ResolveFilename(result, c.Attribute("filename")?.Value ?? throw InvalidCoveragePath(), portableRoot)))
            .ToDictionary(group => group.Key, group => new Queue<XElement>(group));
        var files = destinations.Keys.Select(key => key.File).ToHashSet(StringComparer.Ordinal);
        foreach (var sourceClass in GetClasses(result)) sourceClass.Elements("methods").Remove();
        foreach (var sourceClass in GetClasses(raw))
        {
            var methods = sourceClass.Elements("methods").Elements("method").ToArray();
            if (methods.Length == 0) continue;
            var file = ResolveFilename(raw, sourceClass.Attribute("filename")?.Value ?? throw InvalidCoveragePath(), portableRoot);
            if (!files.Contains(file)) throw InvalidCoverageData();
            var packageName = sourceClass.Ancestors("package").First().Attribute("name")?.Value ?? string.Empty;
            var className = sourceClass.Attribute("name")?.Value ?? string.Empty;
            XElement destination;
            if (destinations.TryGetValue((packageName, className, file), out var owners) && owners.Count > 0)
                destination = owners.Dequeue();
            else
            {
                // Keep original ownership even when the normalizer folds this class. This
                // method-only record adds no aggregate class points or branch denominator.
                destination = new XElement("class", new XAttribute("name", className), new XAttribute("filename", file),
                    new XElement("lines"));
                result.Root!.Element("packages")!.Add(new XElement("package", new XAttribute("name", packageName),
                    new XElement("classes", destination)));
            }
            var container = destination.Element("methods");
            if (container is null) { container = new XElement("methods"); destination.AddFirst(container); }
            container.Add(methods.Select(method => new XElement(method)));
        }
        return result;
    }

    /// <summary>Retains overloads, repeated methods and every method point across permitted class folding.</summary>
    private static Dictionary<NormalizationMethodIdentity, Dictionary<LineIdentity, LineFact>> ReadMethodObservations(
        XDocument document, PortableRoot sourceRoot)
    {
        var methods = new Dictionary<NormalizationMethodIdentity, Dictionary<LineIdentity, LineFact>>();
        var occurrences = new Dictionary<(string File, string Name, string Signature), int>();
        foreach (var sourceClass in GetClasses(document))
        {
            var file = ResolveFilename(document, sourceClass.Attribute("filename")?.Value ?? throw InvalidCoveragePath(), sourceRoot);
            foreach (var method in sourceClass.Elements("methods").Elements("method"))
            {
                var name = method.Attribute("name")?.Value ?? string.Empty;
                var signature = method.Attribute("signature")?.Value ?? string.Empty;
                var key = (file, name, signature);
                var occurrence = occurrences.GetValueOrDefault(key);
                occurrences[key] = checked(occurrence + 1);
                var lines = new Dictionary<LineIdentity, LineFact>();
                _ = ObserveLines(method.Element("lines"), lines);
                methods.Add(new(file, name, signature, occurrence), lines);
            }
        }
        return methods;
    }

    /// <summary>
    /// Requires timings to prove successful execution of the exact declared primary test inventory.
    /// </summary>
    /// <param name="timings">Parsed primary coverage timings document.</param>
    /// <param name="expectedProjects">Complete explicit project inventory expected from the primary run.</param>
    /// <param name="configuration">Expected build configuration.</param>
    /// <param name="driver">Expected coverage driver.</param>
    /// <exception cref="InvalidDataException">The timings do not attest a complete successful run.</exception>
    internal static void ValidatePrimaryTimings(
        JsonElement timings,
        IReadOnlyList<string> expectedProjects,
        string configuration,
        string driver)
    {
        if (timings.ValueKind != JsonValueKind.Object
            || expectedProjects is null
            || expectedProjects.Count == 0
            || string.IsNullOrEmpty(configuration)
            || string.IsNullOrEmpty(driver))
        {
            throw InvalidPrimaryTimings();
        }

        var expected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in expectedProjects)
        {
            if (string.IsNullOrEmpty(project) || !expected.Add(project))
            {
                throw InvalidPrimaryTimings();
            }
        }

        if (!TryGetUniqueProperty(timings, "configuration", out var configurationValue)
            || configurationValue.ValueKind != JsonValueKind.String
            || !string.Equals(configurationValue.GetString(), configuration, StringComparison.Ordinal)
            || !TryGetUniqueProperty(timings, "coverageDriver", out var driverValue)
            || driverValue.ValueKind != JsonValueKind.String
            || !string.Equals(driverValue.GetString(), driver, StringComparison.Ordinal)
            || !TryGetUniqueProperty(timings, "merge", out var merge)
            || merge.ValueKind != JsonValueKind.Object
            || !TryGetUniqueProperty(merge, "exitCode", out var mergeExitCode)
            || mergeExitCode.ValueKind != JsonValueKind.Number
            || !mergeExitCode.TryGetInt32(out var mergeExitCodeValue)
            || mergeExitCodeValue != 0
            || !TryGetUniqueProperty(timings, "artifacts", out var artifacts)
            || artifacts.ValueKind != JsonValueKind.Object
            || !TryGetUniqueProperty(artifacts, "coverageFiles", out var coverageFiles)
            || coverageFiles.ValueKind != JsonValueKind.Number
            || !coverageFiles.TryGetInt32(out var coverageFileCount)
            || coverageFileCount != expected.Count
            || !TryGetUniqueProperty(timings, "projects", out var projects)
            || projects.ValueKind != JsonValueKind.Array)
        {
            throw InvalidPrimaryTimings();
        }

        var actual = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in projects.EnumerateArray())
        {
            if (project.ValueKind != JsonValueKind.Object
                || !TryGetUniqueProperty(project, "project", out var projectPath)
                || projectPath.ValueKind != JsonValueKind.String
                || string.IsNullOrEmpty(projectPath.GetString())
                || !TryGetUniqueProperty(project, "exitCode", out var projectExitCode)
                || projectExitCode.ValueKind != JsonValueKind.Number
                || !projectExitCode.TryGetInt32(out var projectExitCodeValue)
                || projectExitCodeValue != 0
                || !TryGetUniqueProperty(project, "executionStatus", out var executionStatus)
                || executionStatus.ValueKind != JsonValueKind.String
                || !string.Equals(executionStatus.GetString(), "completed", StringComparison.Ordinal)
                || !TryGetUniqueProperty(project, "coverageArtifactStatus", out var artifactStatus)
                || artifactStatus.ValueKind != JsonValueKind.String
                || !string.Equals(artifactStatus.GetString(), "produced", StringComparison.Ordinal))
            {
                throw InvalidPrimaryTimings();
            }

            var name = projectPath.GetString()!;
            if (!expected.Contains(name) || !actual.Add(name))
            {
                throw InvalidPrimaryTimings();
            }
        }

        if (actual.Count != expected.Count)
        {
            throw InvalidPrimaryTimings();
        }
    }

    private static XDocument WriteComposition(Dictionary<ClassIdentity, ClassAccumulator> classes)
    {
        var root = new XElement("coverage",
            new XAttribute("version", OutputVersion),
            new XElement("packages"));
        var packageContainer = root.Element("packages")!;
        long linesCovered = 0;
        long linesValid = 0;
        long branchesCovered = 0;
        long branchesValid = 0;

        foreach (var (identity, accumulator) in classes
                     .OrderBy(item => item.Key.Package, StringComparer.Ordinal)
                     .ThenBy(item => item.Key.Class, StringComparer.Ordinal)
                     .ThenBy(item => item.Key.Filename, StringComparer.Ordinal)
                     .ThenBy(item => item.Key.Occurrence))
        {
            var classElement = new XElement("class",
                new XAttribute("name", identity.Class),
                new XAttribute("filename", identity.Filename));
            packageContainer.Add(new XElement("package",
                new XAttribute("name", identity.Package),
                new XElement("classes", classElement)));

            if (accumulator.Methods.Count > 0)
            {
                var methods = new XElement("methods");
                foreach (var (methodIdentity, lines) in accumulator.Methods
                             .OrderBy(item => item.Key.Name, StringComparer.Ordinal)
                             .ThenBy(item => item.Key.Signature, StringComparer.Ordinal)
                             .ThenBy(item => item.Key.Occurrence))
                {
                    var method = new XElement("method",
                        new XAttribute("name", methodIdentity.Name),
                        new XAttribute("signature", methodIdentity.Signature));
                    WriteLines(method, lines);
                    methods.Add(method);
                }

                classElement.Add(methods);
            }

            WriteLines(classElement, accumulator.Lines);
            foreach (var fact in accumulator.Lines.Values)
            {
                try
                {
                    linesValid = checked(linesValid + 1);
                    if (fact.Hits > 0)
                    {
                        linesCovered = checked(linesCovered + 1);
                    }

                    branchesCovered = checked(branchesCovered + fact.BranchesCovered);
                    branchesValid = checked(branchesValid + fact.BranchesValid);
                }
                catch (OverflowException)
                {
                    throw InvalidCoverageData();
                }
            }
        }

        if (linesValid == 0)
        {
            throw InvalidCoverageData();
        }

        root.SetAttributeValue("lines-covered", linesCovered.ToString(CultureInfo.InvariantCulture));
        root.SetAttributeValue("lines-valid", linesValid.ToString(CultureInfo.InvariantCulture));
        root.SetAttributeValue("branches-covered", branchesCovered.ToString(CultureInfo.InvariantCulture));
        root.SetAttributeValue("branches-valid", branchesValid.ToString(CultureInfo.InvariantCulture));
        root.SetAttributeValue("line-rate", ((double)linesCovered / linesValid).ToString("R", CultureInfo.InvariantCulture));
        root.SetAttributeValue("branch-rate", (branchesValid == 0 ? 1d : (double)branchesCovered / branchesValid)
            .ToString("R", CultureInfo.InvariantCulture));
        return new XDocument(root);
    }

    private static void WriteLines(XElement parent, Dictionary<LineIdentity, LineFact> lines)
    {
        var linesElement = new XElement("lines");
        foreach (var (identity, fact) in lines
                     .OrderBy(item => item.Key.Number)
                     .ThenBy(item => item.Key.Occurrence))
        {
            var line = new XElement("line",
                new XAttribute("number", identity.Number.ToString(CultureInfo.InvariantCulture)),
                new XAttribute("hits", fact.Hits.ToString(CultureInfo.InvariantCulture)),
                new XAttribute("branch", (fact.BranchesValid > 0).ToString().ToLowerInvariant()));
            if (fact.BranchesValid > 0)
            {
                var percentage = ((double)fact.BranchesCovered * 100 / fact.BranchesValid)
                    .ToString("G6", CultureInfo.InvariantCulture);
                line.SetAttributeValue("condition-coverage",
                    $"{percentage}% ({fact.BranchesCovered.ToString(CultureInfo.InvariantCulture)}/{fact.BranchesValid.ToString(CultureInfo.InvariantCulture)})");
            }

            linesElement.Add(line);
        }

        parent.Add(linesElement);
    }

    private static long ObserveLines(XElement? sourceLines, Dictionary<LineIdentity, LineFact> target)
    {
        if (sourceLines is null)
        {
            return 0;
        }

        var occurrences = new Dictionary<long, int>();
        long count = 0;
        foreach (var line in sourceLines.Elements("line"))
        {
            var number = ReadLineInteger(line, "number");
            var hits = ReadLineInteger(line, "hits");
            if (number <= 0 || hits < 0)
            {
                throw InvalidCoverageData();
            }

            var occurrence = occurrences.TryGetValue(number, out var previousOccurrence) ? previousOccurrence : 0;
            occurrences[number] = occurrence + 1;
            var identity = new LineIdentity(number, occurrence);
            var (branchesCovered, branchesValid) = ReadBranchCounts(line);
            var observation = new LineFact(hits, branchesCovered, branchesValid);

            if (target.TryGetValue(identity, out var previous))
            {
                var retainedBranchesCovered = previous.BranchesCovered;
                if (branchesValid == previous.BranchesValid && branchesCovered == branchesValid)
                {
                    retainedBranchesCovered = branchesCovered;
                }

                observation = new LineFact(
                    Math.Max(previous.Hits, hits),
                    retainedBranchesCovered,
                    previous.BranchesValid);
            }

            target[identity] = observation;
            count++;
        }

        return count;
    }

    private static (long Covered, long Valid) ReadBranchCounts(XElement line)
    {
        var conditionCoverage = line.Attribute("condition-coverage")?.Value;
        if (conditionCoverage is null)
        {
            if (string.Equals(line.Attribute("branch")?.Value, "true", StringComparison.OrdinalIgnoreCase))
            {
                throw InvalidCoverageData();
            }

            return (0, 0);
        }

        var match = BranchCoveragePattern.Match(conditionCoverage);
        if (!match.Success
            || !long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var covered)
            || !long.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var valid)
            || valid <= 0
            || covered > valid)
        {
            throw InvalidCoverageData();
        }

        return (covered, valid);
    }

    private static Dictionary<LinePointIdentity, bool> ReadLineObservations(XDocument document, PortableRoot sourceRoot)
    {
        var observations = new Dictionary<LinePointIdentity, bool>();
        foreach (var sourceClass in GetClasses(document))
        {
            var filename = sourceClass.Attribute("filename")?.Value;
            if (filename is null)
            {
                throw InvalidCoveragePath();
            }

            var relativeFilename = ResolveFilename(document, filename, sourceRoot);
            var lines = sourceClass.Element("lines");
            if (lines is null)
            {
                continue;
            }

            foreach (var line in lines.Elements("line"))
            {
                var number = ReadLineInteger(line, "number");
                var hits = ReadLineInteger(line, "hits");
                if (number <= 0 || hits < 0)
                {
                    throw InvalidCoverageData();
                }

                var key = new LinePointIdentity(relativeFilename, number);
                observations[key] = (observations.TryGetValue(key, out var previous) && previous) || hits > 0;
            }
        }

        return observations;
    }

    private static long ReadRootCounter(XDocument document, string name)
    {
        if (document.Root is null
            || !long.TryParse(document.Root.Attribute(name)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            || value < 0)
        {
            throw InvalidCoverageData();
        }

        return value;
    }

    private static long ReadLineInteger(XElement line, string name)
    {
        if (!long.TryParse(line.Attribute(name)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            throw InvalidCoverageData();
        }

        return value;
    }

    private static IEnumerable<XElement> GetPackages(XDocument document)
    {
        if (document.Root is null || document.Root.Name != "coverage")
        {
            throw InvalidCoverageData();
        }

        return document.Root.Elements("packages").SelectMany(element => element.Elements("package"));
    }

    private static IEnumerable<XElement> GetClasses(XDocument document)
    {
        return GetPackages(document)
            .SelectMany(package => package.Elements("classes"))
            .SelectMany(classes => classes.Elements("class"));
    }

    private static PortableRoot ParseSourceRoot(string sourceRoot)
    {
        if (string.IsNullOrEmpty(sourceRoot) || sourceRoot.Contains('\0'))
        {
            throw InvalidCoveragePath();
        }

        var normalized = sourceRoot.Replace('\\', '/');
        var kind = GetRootKind(normalized);
        if (kind == RootKind.Relative)
        {
            throw InvalidCoveragePath();
        }

        var rootLength = kind switch
        {
            RootKind.Posix => 1,
            RootKind.Drive => 3,
            RootKind.Unc => 2,
            _ => throw InvalidCoveragePath(),
        };
        var remainder = normalized[rootLength..];

        if (remainder.EndsWith('/'))
        {
            remainder = remainder[..^1];
        }

        var segments = ParseSegments(remainder);
        if (kind == RootKind.Unc && segments.Length < 2)
        {
            throw InvalidCoveragePath();
        }

        var canonical = kind switch
        {
            RootKind.Posix => "/" + string.Join('/', segments),
            RootKind.Drive => normalized[..3] + string.Join('/', segments),
            RootKind.Unc => "//" + string.Join('/', segments),
            _ => throw InvalidCoveragePath(),
        };
        var prefix = canonical.EndsWith('/') ? canonical : canonical + "/";
        return new PortableRoot(prefix, kind);
    }

    /// <summary>Resolves a Cobertura filename through its declared source bases and the trusted repository root.</summary>
    /// <remarks>Collector filenames can be relative to a filesystem root rather than the repository.
    /// Every accepted interpretation must identify one repository-relative file; no host filesystem probing is used.</remarks>
    private static string ResolveFilename(XDocument document, string filename, PortableRoot sourceRoot)
    {
        if (GetRootKind(filename.Replace('\\', '/')) != RootKind.Relative)
            return NormalizeFilename(filename, sourceRoot);
        var sources = document.Root?.Element("sources")?.Elements("source").Select(x => x.Value).ToArray() ?? [];
        if (sources.Length == 0) return NormalizeFilename(filename, sourceRoot);
        var matches = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            var spelling = source.Replace('\\', '/');
            var basis = GetRootKind(spelling) == RootKind.Relative
                ? sourceRoot.Prefix + spelling.TrimEnd('/') : spelling.TrimEnd('/');
            try { matches.Add(NormalizeFilename(basis + "/" + filename.Replace('\\', '/'), sourceRoot)); }
            catch (InvalidDataException) { /* A source base outside this repository cannot resolve this point. */ }
        }
        if (matches.Count != 1) throw InvalidCoveragePath();
        return matches.Single();
    }

    private readonly record struct NormalizationMethodIdentity(string File, string Name, string Signature, int Occurrence);

    private static string NormalizeFilename(string filename, PortableRoot sourceRoot)
    {
        if (string.IsNullOrEmpty(filename) || filename.Contains('\0'))
        {
            throw InvalidCoveragePath();
        }

        var normalized = filename.Replace('\\', '/');
        var kind = GetRootKind(normalized);
        string relative;
        if (kind == RootKind.Relative)
        {
            relative = normalized;
        }
        else
        {
            var absolutePath = NormalizeAbsolutePath(normalized, kind);
            if (kind != sourceRoot.Kind || !absolutePath.StartsWith(sourceRoot.Prefix, StringComparison.Ordinal))
            {
                throw InvalidCoveragePath();
            }

            relative = absolutePath[sourceRoot.Prefix.Length..];
        }

        var segments = ParseSegments(relative);
        return string.Join('/', segments);
    }

    private static string NormalizeAbsolutePath(string path, RootKind kind)
    {
        var prefixLength = kind switch
        {
            RootKind.Posix => 1,
            RootKind.Drive => 3,
            RootKind.Unc => 2,
            _ => throw InvalidCoveragePath(),
        };
        var remainder = path[prefixLength..];
        var segments = ParseSegments(remainder);
        if (kind == RootKind.Unc && segments.Length < 2)
        {
            throw InvalidCoveragePath();
        }

        return kind switch
        {
            RootKind.Posix => "/" + string.Join('/', segments),
            RootKind.Drive => path[..3] + string.Join('/', segments),
            RootKind.Unc => "//" + string.Join('/', segments),
            _ => throw InvalidCoveragePath(),
        };
    }

    private static string[] ParseSegments(string path)
    {
        if (path.Length == 0)
        {
            return [];
        }

        var segments = path.Split('/');
        if (segments.Any(segment =>
                segment.Length == 0
                || segment is "." or ".."
                || segment.Contains(':')))
        {
            throw InvalidCoveragePath();
        }

        return segments;
    }

    private static RootKind GetRootKind(string path)
    {
        if (path.StartsWith("//", StringComparison.Ordinal))
        {
            return RootKind.Unc;
        }

        if (path.Length >= 3 && IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '/')
        {
            return RootKind.Drive;
        }

        return path.StartsWith('/') ? RootKind.Posix : RootKind.Relative;
    }

    private static bool IsAsciiLetter(char value)
    {
        return value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
    }

    private static bool TryGetUniqueProperty(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        var found = false;
        foreach (var property in element.EnumerateObject())
        {
            if (!property.NameEquals(name))
            {
                continue;
            }

            if (found)
            {
                return false;
            }

            found = true;
            value = property.Value;
        }

        return found;
    }

    private static InvalidDataException InvalidCoverageData()
    {
        return new InvalidDataException(InvalidCoverageDataMessage);
    }

    private static InvalidDataException InvalidCoveragePath()
    {
        return new InvalidDataException(InvalidCoveragePathMessage);
    }

    private static InvalidDataException InvalidPrimaryTimings()
    {
        return new InvalidDataException(InvalidPrimaryTimingsMessage);
    }

    private enum RootKind
    {
        Relative,
        Posix,
        Drive,
        Unc,
    }

    private readonly record struct PortableRoot(string Prefix, RootKind Kind);

    private readonly record struct ClassBaseIdentity(string Package, string Class, string Filename);

    private readonly record struct ClassIdentity(string Package, string Class, string Filename, int Occurrence);

    private readonly record struct MethodBaseIdentity(string Name, string Signature);

    private readonly record struct MethodIdentity(string Name, string Signature, int Occurrence);

    private readonly record struct LineIdentity(long Number, int Occurrence);

    private readonly record struct LinePointIdentity(string Filename, long Number);

    private readonly record struct LineFact(long Hits, long BranchesCovered, long BranchesValid);

    private sealed class ClassAccumulator
    {
        internal Dictionary<LineIdentity, LineFact> Lines { get; } = [];

        internal Dictionary<MethodIdentity, Dictionary<LineIdentity, LineFact>> Methods { get; } = [];
    }
}
