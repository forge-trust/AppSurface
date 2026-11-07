using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;

namespace ForgeTrust.AppSurface.Evidence.Coverage;

internal static partial class CoverageFanInEngine
{
    /// <summary>Exact supplementary inventory; omission uses the complete primary inventory.</summary>
    private static string[] NativeProjects(CoverageFanInEngineRequest request)
    {
        var producer = Producer(request)!.Value;
        return producer.TryGetProperty("testProjects", out var projects) && projects.ValueKind == JsonValueKind.Array
            ? projects.EnumerateArray().Select(item => item.GetString()!).ToArray() : request.Expected.TestProjects;
    }

    private static string CollectorName(int index) => "collector-" + index.ToString("D4", CultureInfo.InvariantCulture) + ".cobertura.xml";

    /// <summary>Retains every original native report and successful project inventory, then independently checks its conservative projection.</summary>
    private static (XDocument Document, Dictionary<string, byte[]> Files) CaptureNativeSet(
        CoverageFanInEngineRequest request, CoverageFanInIO io)
    {
        var projects = NativeProjects(request);
        var paths = request.RawCoveragePaths!;
        if (projects.Length < 2 || paths.Count != projects.Length || !paths.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(projects)
            || paths.Values.Select(Full).Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Count)
            throw new InvalidDataException("Native original report inventory differs.");
        var timings = io.Read(request.TimingsPath!);
        CoverageFanInIndependentVerifier.VerifyPrimaryInventory(Parse(timings), projects, request.Expected.Configuration,
            request.Expected.Instrumentation.GetProperty("driver").GetString()!);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["timings.json"] = timings };
        var documents = new List<(XDocument Document, string SourceRoot)>();
        var root = Full(request.RepositoryRoot);
        for (var index = 0; index < projects.Length; index++)
        {
            var bytes = io.Read(paths[projects[index]], xml: true);
            files.Add(CollectorName(index), bytes);
            documents.Add((io.Xml(bytes, allowInertGeneratorDoctype: true), root));
        }
        // The multi-report native representation is composed from original collector records.
        // ReportGenerator's combined method reconstruction is not the oracle for this projection.
        var document = CoverageFanInComposer.Compose(documents);
        CoverageFanInIndependentVerifier.VerifyNativeCollection(documents, document);
        files.Add("collector-inventory.json", Serialize(new
        {
            schemaVersion = 1,
            reports = projects.Select((project, index) => new { project, file = CollectorName(index) }).ToArray()
        }));
        return (document, files);
    }

    /// <summary>Loads exact project-indexed original bytes. Transport hashes cannot replace independent coverage semantics.</summary>
    private static Dictionary<string, byte[]> LoadNativeSet(CoverageFanInEngineRequest request, CoverageFanInIO io,
        string directory, XDocument document, string sourceRoot, JsonElement files)
    {
        var projects = NativeProjects(request);
        if (projects.Length < 2) throw new InvalidDataException("Indexed native capture requires multiple projects.");
        var expected = new[] { CoverageName, "collector-inventory.json", "timings.json" }
            .Concat(Enumerable.Range(0, projects.Length).Select(CollectorName)).ToArray();
        ExactProperties(files, expected);
        var auxiliary = expected.Where(name => name != CoverageName).ToDictionary(name => name,
            name => io.Read(Path.Join(directory, name), xml: name.EndsWith(".xml", StringComparison.Ordinal)), StringComparer.Ordinal);
        var inventory = Parse(auxiliary["collector-inventory.json"]);
        ExactProperties(inventory, ["schemaVersion", "reports"]);
        if (inventory.GetProperty("schemaVersion").GetInt32() != 1
            || inventory.GetProperty("reports").ValueKind != JsonValueKind.Array
            || inventory.GetProperty("reports").GetArrayLength() != projects.Length)
            throw new InvalidDataException("Native collector inventory differs.");
        CoverageFanInIndependentVerifier.VerifyPrimaryInventory(Parse(auxiliary["timings.json"]), projects,
            request.Expected.Configuration, request.Expected.Instrumentation.GetProperty("driver").GetString()!);
        var documents = new List<(XDocument Document, string SourceRoot)>();
        var index = 0;
        foreach (var entry in inventory.GetProperty("reports").EnumerateArray())
        {
            ExactProperties(entry, ["project", "file"]);
            var name = CollectorName(index);
            if (entry.GetProperty("project").GetString() != projects[index++] || entry.GetProperty("file").GetString() != name)
                throw new InvalidDataException("Native project-to-report binding differs.");
            documents.Add((io.Xml(auxiliary[name], allowInertGeneratorDoctype: true), sourceRoot));
        }
        CoverageFanInIndependentVerifier.VerifyNativeCollection(documents, document);
        return auxiliary;
    }

    /// <summary>Finds exactly one original attachment per successful invocation without accepting linked traversal.</summary>
    internal static IReadOnlyDictionary<string, string> FindNativeReports(string collection, string[] projects,
        CoverageFanInExpected expected, CancellationToken token, Action<string>? observeTimingsHash = null,
        Action<IReadOnlyDictionary<string, string>>? observeReportHashes = null)
    {
        using var io = new CoverageFanInIO(expected.Bounds, token);
        io.Acquire([(collection, false)], requireCoordination: false);
        var bytes = io.Read(Path.Join(collection, "timings.json"));
        var timings = Parse(bytes);
        CoverageFanInIndependentVerifier.VerifyPrimaryInventory(timings, projects, expected.Configuration,
            expected.Instrumentation.GetProperty("driver").GetString()!);
        var entries = timings.GetProperty("projects").EnumerateArray().ToDictionary(
            entry => entry.GetProperty("project").GetString()!, StringComparer.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        long totalBytes = bytes.LongLength;
        var count = 0;
        foreach (var project in projects)
        {
            var invocation = entries[project].GetProperty("invocationDirectory").GetString()
                ?? throw new InvalidDataException("Collector invocation identity is missing.");
            var components = invocation.Split('/');
            if (components.Length != 4 || components[0] != "projects" || components[2] != "collector-results")
                throw new InvalidDataException("Collector invocation identity differs.");
            foreach (var component in components) CoverageRunOutputLease.ValidateArtifactComponent(component);
            var pending = new Stack<(string Path, int Depth)>();
            pending.Push((Path.Join(collection, Path.Join(components)), 0));
            var matches = new List<string>();
            while (pending.TryPop(out var current))
            {
                token.ThrowIfCancellationRequested();
                if (current.Depth > expected.Bounds.Depth)
                    throw new InvalidDataException("Collector attachment discovery exceeds depth budget.");
                using var directory = CoverageRunOutputLease.AcquireArtifactDirectory(current.Path, false, false)
                    ?? throw new IOException("Collector invocation directory unavailable.");
                foreach (var entry in directory.EnumerateArtifactEntries(10000))
                {
                    if (++count > 10000)
                        throw new InvalidDataException("Collector attachment discovery exceeds budget.");
                    if (entry.IsDirectory) pending.Push((Path.Join(current.Path, entry.Name), current.Depth + 1));
                    else if (entry.Name == CoverageName) matches.Add(Path.Join(current.Path, entry.Name));
                    if (matches.Count > 1) throw new InvalidDataException("Ambiguous collector attachment inventory.");
                }
                directory.VerifyArtifactBinding();
            }
            if (matches.Count != 1) throw new InvalidDataException("Missing original collector attachment.");
            var selected = matches[0];
            using var reportDirectory = CoverageRunOutputLease.AcquireArtifactDirectory(Path.GetDirectoryName(selected)!, false, false)
                ?? throw new IOException("Collector attachment directory unavailable.");
            using var report = reportDirectory.OpenArtifactFile(Path.GetFileName(selected), false, false);
            var maximum = Math.Min(expected.Bounds.XmlBytes, expected.Bounds.TotalBytes - totalBytes);
            if (report.Length > maximum)
                throw new InvalidDataException("Collector attachment selection exceeds byte budget.");
            hashes.Add(selected, CoverageFanInIO.HashStream(report, maximum, token));
            totalBytes = checked(totalBytes + report.Position);
            reportDirectory.VerifyArtifactBinding();
            result.Add(project, selected);
        }
        io.VerifyBindings();
        observeTimingsHash?.Invoke(CoverageFanInIO.Hash(bytes));
        observeReportHashes?.Invoke(hashes);
        return result;
    }
}
