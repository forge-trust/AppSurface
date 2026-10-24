using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace ForgeTrust.AppSurface.Evidence.Aspire;

/// <summary>Trusted, caller-supplied inputs for native coverage fan-in verification.</summary>
public sealed record EvidenceCoverageFanInRequest(
    string PrimaryCaptureDirectory,
    IReadOnlyList<string> NativeCaptureDirectories,
    string ComposedOutputDirectory,
    string RepositoryRoot,
    string SolutionFile,
    ISet<string> ExpectedTestProjects,
    string Commit,
    string Tree,
    string Sdk,
    string Configuration,
    IReadOnlyDictionary<string, string?> Run,
    string PrimaryGateJson,
    string ComposedGateJson,
    string ExpectedDiffBase);

/// <summary>Verified fan-in counters and the identities of the four accepted inputs.</summary>
public sealed record EvidenceCoverageFanInResult(
    bool Verified,
    IReadOnlyList<string> InputDirectories,
    int InputClassLines,
    int InputMethodLines,
    int RetainedClassLines,
    int RetainedMethodLines,
    int LinesCovered,
    int BranchesCovered,
    int BranchesValid,
    IReadOnlyDictionary<string, string> InputManifestHashes);

/// <summary>Verifies the existing coverage fan-in receipt, its inputs, and gate outputs.</summary>
public static class EvidenceCoverageFanIn
{
    private const int Schema = 1;
    private const long MaxXml = 512L * 1024 * 1024;
    private const long MaxJson = 1024L * 1024;
    private const string NativeFilter = "FullyQualifiedName~CoverageRunArtifactReaderTests|"
        + "FullyQualifiedName~CoverageRunOutputGuardSecurityTests|FullyQualifiedName~CoverageGateTests|"
        + "FullyQualifiedName~CoverageArtifact|FullyQualifiedName~CoverageStorageCommand";
    private static readonly string[] Platforms = ["Linux", "Windows", "Darwin"];
    private static readonly JsonElement ExpectedPolicy = JsonSerializer.SerializeToElement(new
    {
        driver = "collector",
        format = "cobertura",
        exclude = "[*.Tests]*,[*.IntegrationTests]*",
        include = (string?)null,
        normalization = "appsurface-coverage-merge",
    });

    /// <summary>Validates all fan-in evidence and returns counters only after every comparison succeeds.</summary>
    public static async ValueTask<EvidenceCoverageFanInResult> VerifyAsync(EvidenceCoverageFanInRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            return await VerifyCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            throw Reject("Malformed coverage fan-in evidence.", exception);
        }
    }

    private static async ValueTask<EvidenceCoverageFanInResult> VerifyCoreAsync(EvidenceCoverageFanInRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        if (request.NativeCaptureDirectories.Count != 3) throw Reject("Exactly three native evidence directories are required.");
        var primary = await LoadAsync(request, request.PrimaryCaptureDirectory, "solution", cancellationToken).ConfigureAwait(false);
        var natives = new List<Loaded>(3);
        foreach (var directory in request.NativeCaptureDirectories)
            natives.Add(await LoadAsync(request, directory, "native", cancellationToken).ConfigureAwait(false));
        if (natives.Select(x => x.Manifest.GetProperty("platform").GetString()).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(Platforms.OrderBy(x => x, StringComparer.Ordinal), StringComparer.Ordinal) == false)
            throw Reject("Exactly one Linux, Windows and Darwin native input is required.");
        if (!JsonEquals(primary.Manifest.GetProperty("run"), request.Run) || natives.Any(x => !JsonEquals(x.Manifest.GetProperty("run"), request.Run))) throw Reject("Workflow provenance differs.");
        if (request.Run.Count == 0 || request.Run.Any(x => string.IsNullOrWhiteSpace(x.Value))) throw Reject("Workflow provenance is required.");

        var receipt = await ReadJsonAsync(Path.Combine(request.ComposedOutputDirectory, "verification.json"), cancellationToken).ConfigureAwait(false);
        var all = OrderInputsFromReceipt(receipt, primary, natives);
        VerifySolution(primary.Timings!.Value, request);
        var composed = Compose(all);
        var output = await ReadXmlAsync(Path.Combine(request.ComposedOutputDirectory, "coverage.cobertura.xml"), cancellationToken).ConfigureAwait(false);
        CompareXml(composed.Document, output, "composed output");
        var primaryPaths = await ReadXmlAsync(Path.Combine(request.ComposedOutputDirectory, "primary-paths.cobertura.xml"), cancellationToken).ConfigureAwait(false);
        VerifyPrimaryPaths(primary.Document, primaryPaths, primary.Manifest.GetProperty("sourceRoot").GetString()!);
        if (!Directory.Exists(request.ComposedOutputDirectory)
            || new DirectoryInfo(request.ComposedOutputDirectory).Attributes.HasFlag(FileAttributes.ReparsePoint)
            || !ExactFiles(request.ComposedOutputDirectory, ["coverage.cobertura.xml", "primary-paths.cobertura.xml", "verification.json"]))
            throw Reject("Composed output inventory mismatch.");
        await VerifyReceiptAsync(receipt, request, all, request.ComposedOutputDirectory, composed, cancellationToken).ConfigureAwait(false);
        var primaryGate = await ReadJsonAsync(request.PrimaryGateJson, cancellationToken).ConfigureAwait(false);
        var composedGate = await ReadJsonAsync(request.ComposedGateJson, cancellationToken).ConfigureAwait(false);
        VerifyGate(primaryGate, request.ExpectedDiffBase, requirePatch: false, "primary-paths.cobertura.xml", primaryPaths);
        VerifyGate(composedGate, request.ExpectedDiffBase, requirePatch: true, "coverage.cobertura.xml", output);
        if (!JsonElementsEqual(primaryGate.GetProperty("patchDiffSource"), composedGate.GetProperty("patchDiffSource"))) throw Reject("Coverage gate diff source snapshots differ.");
        VerifyMeasurableUnion(primaryGate, composedGate);
        return new EvidenceCoverageFanInResult(
            true, all.Select(x => x.Directory).ToArray(), composed.InputClassLines, composed.InputMethodLines,
            composed.ClassLines, composed.MethodLines, composed.LinesCovered, composed.BranchesCovered, composed.BranchesValid,
            all.ToDictionary(x => x.Directory, x => x.ManifestHash, StringComparer.Ordinal));
    }

    private static void ValidateRequest(EvidenceCoverageFanInRequest r)
    {
        foreach (var value in new[]
        {
            r.PrimaryCaptureDirectory, r.ComposedOutputDirectory, r.RepositoryRoot, r.SolutionFile,
            r.Commit, r.Tree, r.Sdk, r.Configuration, r.PrimaryGateJson, r.ComposedGateJson, r.ExpectedDiffBase,
        })
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Fan-in verification inputs must be explicit.");
        if (!string.Equals(r.Configuration, "Debug", StringComparison.Ordinal) && !string.Equals(r.Configuration, "Release", StringComparison.Ordinal)) throw new ArgumentException("Configuration must be Debug or Release.");
        if (r.ExpectedTestProjects.Count == 0) throw new ArgumentException("The trusted solution test-project inventory is required.");
        var root = Path.GetFullPath(r.RepositoryRoot);
        var solution = Path.GetFullPath(r.SolutionFile);
        if (!solution.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(solution)) throw new ArgumentException("The explicit solution file must be inside the repository root.");
        var actualProjects = XDocument.Load(solution).Descendants("Project")
            .Select(x => x.Attribute("Path")?.Value)
            .Where(x => !string.IsNullOrWhiteSpace(x) && (x.EndsWith(".Tests.csproj", StringComparison.Ordinal) || x.EndsWith(".IntegrationTests.csproj", StringComparison.Ordinal)))
            .Select(x => x!.Replace('\\', '/'))
            .ToHashSet(StringComparer.Ordinal);
        if (!actualProjects.SetEquals(r.ExpectedTestProjects)) throw new ArgumentException("The trusted solution test-project inventory differs from the actual .slnx inventory.");
    }

    private sealed record Loaded(string Directory, JsonElement Manifest, string ManifestHash, XDocument Document, JsonElement? Timings);
    private sealed record Composition(XDocument Document, int InputClassLines, int InputMethodLines, int ClassLines, int MethodLines, int LinesCovered, int BranchesCovered, int BranchesValid);

    private static async ValueTask<Loaded> LoadAsync(EvidenceCoverageFanInRequest r, string directory, string scope, CancellationToken ct)
    {
        var manifestPath = Path.Combine(directory, "manifest.json");
        var manifestBytes = await ReadBytesAsync(manifestPath, MaxJson, ct).ConfigureAwait(false);
        var manifest = ParseJson(manifestBytes, manifestPath);
        var expectedFiles = scope == "solution" ? new[] { "coverage.cobertura.xml", "timings.json" } : new[] { "coverage.cobertura.xml", "collector.cobertura.xml" };
        if (!Directory.Exists(directory) || new DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint)
            || !ExactFiles(directory, expectedFiles.Append("manifest.json")))
            throw Reject("Evidence directory inventory mismatch.");
        var sourceRoot = manifest.GetProperty("sourceRoot").GetString();
        if (string.IsNullOrWhiteSpace(sourceRoot) || !IsAbsoluteSourceRoot(sourceRoot)
            || manifest.GetProperty("schemaVersion").GetInt32() != Schema || manifest.GetProperty("scope").GetString() != scope
            || manifest.GetProperty("configuration").GetString() != r.Configuration || manifest.GetProperty("commit").GetString() != r.Commit
            || manifest.GetProperty("tree").GetString() != r.Tree || manifest.GetProperty("sdk").GetString() != r.Sdk
            || !JsonElementsEqual(manifest.GetProperty("policy"), ExpectedPolicy))
            throw Reject("Manifest identity or policy mismatch.");
        if (scope == "native" && manifest.GetProperty("filter").GetString() != NativeFilter) throw Reject("Manifest filter mismatch.");
        if (scope == "solution" && manifest.GetProperty("filter").ValueKind != JsonValueKind.Null) throw Reject("Manifest filter mismatch.");
        var files = manifest.GetProperty("files");
        if (files.EnumerateObject().Select(x => x.Name).OrderBy(x => x).SequenceEqual(expectedFiles.OrderBy(x => x)) == false) throw Reject("Manifest file inventory mismatch.");
        foreach (var name in expectedFiles)
        {
            var path = Path.Combine(directory, name);
            var hash = await Sha256Async(path, name.EndsWith(".xml", StringComparison.Ordinal) ? MaxXml : MaxJson, ct).ConfigureAwait(false);
            if (!string.Equals(hash, files.GetProperty(name).GetString(), StringComparison.OrdinalIgnoreCase)) throw Reject("Evidence checksum mismatch.");
        }
        var document = await ReadXmlAsync(Path.Combine(directory, "coverage.cobertura.xml"), ct).ConfigureAwait(false);
        if (scope == "native")
        {
            var raw = await ReadXmlAsync(Path.Combine(directory, "collector.cobertura.xml"), ct).ConfigureAwait(false);
            VerifyNormalization(raw, document, manifest.GetProperty("sourceRoot").GetString()!);
        }
        JsonElement? timings = scope == "solution" ? ParseJson(await ReadBytesAsync(Path.Combine(directory, "timings.json"), MaxJson, ct).ConfigureAwait(false), "timings.json") : null;
        return new Loaded(directory, manifest, Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant(), document, timings);
    }

    private static Loaded[] OrderInputsFromReceipt(JsonElement receipt, Loaded primary, IReadOnlyList<Loaded> natives)
    {
        var known = new[] { primary }.Concat(natives).ToArray();
        var hashes = known.Select(x => x.ManifestHash).ToHashSet(StringComparer.Ordinal);
        if (hashes.Count != known.Length) throw Reject("Input manifests are missing or duplicated.");
        var listed = receipt.GetProperty("inputs").EnumerateArray().ToArray();
        if (listed.Length != known.Length) throw Reject("Receipt input inventory mismatch.");
        var ordered = new List<Loaded>(known.Length);
        foreach (var item in listed)
        {
            var hash = item.GetProperty("manifestSha256").GetString();
            if (hash is null || !hashes.Contains(hash) || ordered.Any(x => x.ManifestHash == hash)) throw Reject("Receipt input manifests are missing or duplicated.");
            ordered.Add(known.Single(x => x.ManifestHash == hash));
        }
        if (!string.Equals(ordered[0].ManifestHash, primary.ManifestHash, StringComparison.Ordinal)) throw Reject("The primary input must be first in the receipt.");
        return ordered.ToArray();
    }

    private static async ValueTask<JsonElement> ReadJsonAsync(string path, CancellationToken ct) => ParseJson(await ReadBytesAsync(path, MaxJson, ct).ConfigureAwait(false), path);
    private static JsonElement ParseJson(byte[] bytes, string path)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            RejectDuplicateJsonProperties(document.RootElement);
            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw Reject($"Invalid JSON: {path}", exception);
        }
    }
    private static void RejectDuplicateJsonProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Reject("Duplicate JSON evidence properties are forbidden.");
                RejectDuplicateJsonProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) RejectDuplicateJsonProperties(item);
        }
    }
    private static async ValueTask<byte[]> ReadBytesAsync(string path, long limit, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length <= 0 || info.Length > limit) throw Reject($"Invalid or oversized evidence: {path}");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
        using var memory = new MemoryStream((int)Math.Min(info.Length, int.MaxValue));
        await new EvidenceBoundedReadStream(stream, limit, ct).CopyToAsync(memory, ct).ConfigureAwait(false);
        return memory.ToArray();
    }
    private static bool ExactFiles(string directory, IEnumerable<string> expected)
    {
        var actual = Directory.EnumerateFileSystemEntries(directory).Take(9).Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
        return actual.Count <= 8 && actual.SetEquals(expected);
    }
    private static async ValueTask<string> Sha256Async(string path, long limit, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length <= 0 || info.Length > limit)
            throw Reject($"Invalid evidence: {path}");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        var total = 0L;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > limit) throw Reject("Evidence exceeds its size limit.");
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
    private static async ValueTask<XDocument> ReadXmlAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var input = OpenBoundedXmlStream(path, MaxXml, ct);
            using var reader = XmlReader.Create(input, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Parse,
                XmlResolver = null,
                MaxCharactersInDocument = MaxXml,
                MaxCharactersFromEntities = 1,
            });
            while (reader.Read() && reader.NodeType != XmlNodeType.Element)
            {
                if (reader.NodeType == XmlNodeType.DocumentType && !string.IsNullOrEmpty(reader.Value))
                    throw Reject("Coverage internal DTD subsets are forbidden.");
            }

            var document = XDocument.Load(reader, LoadOptions.None);
            if (document.Root?.Name.LocalName != "coverage") throw Reject("Expected Cobertura coverage root.");
            ValidateCoberturaShape(document.Root);
            foreach (var name in new[] { "lines-valid", "lines-covered", "branches-valid", "branches-covered" }) _ = Attr(document.Root, name);
            if (Attr(document.Root, "lines-valid") <= 0 || Attr(document.Root, "lines-covered") < 0
                || Attr(document.Root, "lines-covered") > Attr(document.Root, "lines-valid")
                || Attr(document.Root, "branches-covered") < 0
                || Attr(document.Root, "branches-covered") > Attr(document.Root, "branches-valid"))
                throw Reject("Coverage counters are empty or out of range.");
            return document;
        }
        catch (XmlException e) { throw Reject($"Invalid coverage XML: {path}", e); }
    }
    private static Stream OpenBoundedXmlStream(string path, long limit, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length <= 0 || info.Length > limit)
            throw Reject($"Invalid or oversized coverage report: {path}");
        return new EvidenceBoundedReadStream(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous), limit, cancellationToken);
    }
    private static void ValidateCoberturaShape(XElement root)
    {
        EnsureChildren(root, "sources", "packages");
        foreach (var source in root.Element("sources")?.Elements() ?? [])
        {
            if (source.Name.LocalName != "source" || source.Elements().Any())
                throw Reject("Unexpected Cobertura source structure.");
        }
        var packages = root.Element("packages") ?? throw Reject("Cobertura packages are missing.");
        foreach (var package in packages.Elements())
        {
            if (package.Name.LocalName != "package") throw Reject("Unexpected Cobertura package structure.");
            var classes = package.Element("classes") ?? throw Reject("Cobertura classes are missing.");
            if (classes.Elements().Any(x => x.Name.LocalName != "class")) throw Reject("Unexpected Cobertura class structure.");
            foreach (var cls in classes.Elements("class"))
            {
                EnsureChildren(cls, "methods", "lines");
                if (cls.Element("methods") is { } methods)
                {
                    foreach (var method in methods.Elements())
                    {
                        if (method.Name.LocalName != "method") throw Reject("Unexpected Cobertura method structure.");
                        EnsureChildren(method, "lines");
                    }
                }
                foreach (var lines in cls.Elements("lines").Concat(cls.Elements("methods").Elements("method").Elements("lines")))
                {
                    foreach (var line in lines.Elements())
                    {
                        if (line.Name.LocalName != "line") throw Reject("Unexpected Cobertura line structure.");
                        if (line.Elements().Any(x => x.Name.LocalName != "conditions")
                            || line.Elements("conditions").Any(x => x.Elements().Any(y => y.Name.LocalName != "condition" || y.Elements().Any())))
                            throw Reject("Unexpected Cobertura condition structure.");
                    }
                }
            }
        }
    }
    private static void EnsureChildren(XElement element, params string[] allowed)
    {
        if (element.Elements().Any(child => !allowed.Contains(child.Name.LocalName, StringComparer.Ordinal)))
            throw Reject("Unexpected Cobertura XML structure.");
    }
    private static Exception Reject(string message, Exception? inner = null) => new InvalidDataException(message, inner);

    private static void VerifySolution(JsonElement timings, EvidenceCoverageFanInRequest r)
    {
        if (timings.GetProperty("configuration").GetString() != r.Configuration
            || timings.GetProperty("coverageDriver").GetString() != "collector"
            || timings.GetProperty("merge").GetProperty("exitCode").GetInt32() != 0
            || timings.GetProperty("artifacts").GetProperty("coverageFiles").GetInt32() != r.ExpectedTestProjects.Count)
            throw Reject("Primary solution timing summary failed.");
        var projects = timings.GetProperty("projects").EnumerateArray().ToArray();
        if (projects.Length != r.ExpectedTestProjects.Count) throw Reject("Primary solution project inventory is incomplete.");
        var actual = projects.Select(x => x.GetProperty("project").GetString()!).ToArray();
        if (actual.Distinct(StringComparer.Ordinal).Count() != actual.Length || !actual.ToHashSet(StringComparer.Ordinal).SetEquals(r.ExpectedTestProjects))
            throw Reject("Primary solution project inventory differs.");
        if (projects.Any(x => x.GetProperty("exitCode").GetInt32() != 0
            || x.GetProperty("executionStatus").GetString() != "completed"
            || x.GetProperty("coverageArtifactStatus").GetString() != "produced"))
            throw Reject("Primary solution project did not complete successfully.");
    }

    private static void VerifyNormalization(XDocument raw, XDocument normalized, string root)
    {
        var before = Observations(raw, root);
        var after = Observations(normalized, root);
        if (before.Count == 0 || before.Any(x => !after.TryGetValue(x.Key, out var value) || x.Value && !value))
            throw Reject("Native normalization removed source points or hits.");
        if (Attr(normalized.Root!, "branches-valid") < Attr(raw.Root!, "branches-valid")) throw Reject("Native normalization reduced branch denominator.");
    }
    private static Dictionary<(string File, int Line), bool> Observations(XDocument doc, string root)
    {
        var result = new Dictionary<(string, int), bool>();
        foreach (var cls in doc.Descendants("class"))
        {
            var file = Relative(cls.Attribute("filename")?.Value ?? "", root);
            foreach (var line in cls.Element("lines")?.Elements("line") ?? [])
            {
                var number = Int(line, "number");
                var hits = Int(line, "hits");
                if (number <= 0 || hits < 0) throw Reject("Invalid coverage line.");
                result[(file, number)] = result.GetValueOrDefault((file, number)) || hits > 0;
            }
        }
        return result;
    }
    private static string Relative(string value, string root)
    {
        value = value.Replace('\\', '/');
        var prefix = root.Replace('\\', '/').TrimEnd('/') + "/";
        if (value.StartsWith(prefix, StringComparison.Ordinal)) value = value[prefix.Length..];
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0') || value.StartsWith('/') || value.Contains(':') || value.Split('/').Contains(".."))
            throw Reject("Coverage path escapes its source root.");
        // Match PurePosixPath in the original producer, including redundant separators and dot segments.
        return string.Join('/', value.Split('/').Where(part => part.Length > 0 && part != "."));
    }
    private static bool IsAbsoluteSourceRoot(string value) => Path.IsPathRooted(value) || value.Length >= 3 && char.IsLetter(value[0]) && value[1] == ':' && (value[2] == '\\' || value[2] == '/');

    private static Composition Compose(IReadOnlyList<Loaded> inputs)
    {
        var classes = new SortedDictionary<(string Package, string Name, string File, int Occurrence), ClassData>(ClassKeyComparer.Instance);
        int inputLines = 0, inputMethods = 0;
        foreach (var input in inputs)
        {
            var inputClassLines = 0;
            var root = input.Manifest.GetProperty("sourceRoot").GetString()!;
            var occurrences = new Dictionary<(string, string, string), int>();
            foreach (var cls in input.Document.Descendants("class"))
            {
                var package = cls.Parent?.Parent?.Attribute("name")?.Value ?? "";
                var name = cls.Attribute("name")?.Value ?? "";
                var file = Relative(cls.Attribute("filename")?.Value ?? "", root);
                var identity = (package, name, file);
                var occurrence = occurrences.GetValueOrDefault(identity);
                occurrences[identity] = occurrence + 1;
                var key = (package, name, file, occurrence);
                if (!classes.TryGetValue(key, out var data))
                {
                    data = new ClassData();
                    classes[key] = data;
                }
                var classCount = Observe(data.Lines, cls.Element("lines"));
                inputClassLines += classCount;
                inputLines += classCount;
                var methodOccurrences = new Dictionary<(string, string), int>();
                foreach (var method in cls.Element("methods")?.Elements("method") ?? [])
                {
                    var methodKey = (method.Attribute("name")?.Value ?? "", method.Attribute("signature")?.Value ?? "");
                    var methodOccurrence = methodOccurrences.GetValueOrDefault(methodKey);
                    methodOccurrences[methodKey] = methodOccurrence + 1;
                    var methodKeyWithOccurrence = (methodKey.Item1, methodKey.Item2, methodOccurrence);
                    if (!data.Methods.TryGetValue(methodKeyWithOccurrence, out var lines))
                    {
                        lines = [];
                        data.Methods[methodKeyWithOccurrence] = lines;
                    }
                    inputMethods += Observe(lines, method.Element("lines"));
                }
            }
            if (inputClassLines == 0) throw Reject("Evidence contains no measurable class lines.");
        }
        var output = new XElement("coverage", new XAttribute("version", "appsurface-verified-fanin-v1"), new XElement("packages"));
        int covered = 0, valid = 0, branchesCovered = 0, branchesValid = 0, methodLines = 0;
        foreach (var group in classes)
        {
            var package = new XElement("package", new XAttribute("name", group.Key.Package));
            var cls = new XElement("class", new XAttribute("name", group.Key.Name), new XAttribute("filename", group.Key.File));
            package.Add(new XElement("classes", cls));
            if (group.Value.Methods.Count > 0)
            {
                var methods = new XElement("methods");
                foreach (var methodRecord in group.Value.Methods.OrderBy(x => x.Key.Item1, StringComparer.Ordinal)
                    .ThenBy(x => x.Key.Item2, StringComparer.Ordinal).ThenBy(x => x.Key.Item3))
                {
                    var method = new XElement("method", new XAttribute("name", methodRecord.Key.Item1), new XAttribute("signature", methodRecord.Key.Item2));
                    method.Add(Lines(methodRecord.Value));
                    methods.Add(method);
                    methodLines += methodRecord.Value.Count;
                }
                cls.Add(methods);
            }
            cls.Add(Lines(group.Value.Lines));
            output.Element("packages")!.Add(package);
            foreach (var point in group.Value.Lines.Values)
            {
                valid++;
                covered += point.Hits > 0 ? 1 : 0;
                branchesCovered += point.BranchCovered;
                branchesValid += point.BranchValid;
            }
        }
        output.SetAttributeValue("lines-covered", covered);
        output.SetAttributeValue("lines-valid", valid);
        output.SetAttributeValue("branches-covered", branchesCovered);
        output.SetAttributeValue("branches-valid", branchesValid);
        output.SetAttributeValue("line-rate", valid == 0 ? "0" : ((double)covered / valid).ToString(CultureInfo.InvariantCulture));
        output.SetAttributeValue("branch-rate", branchesValid == 0 ? "1" : ((double)branchesCovered / branchesValid).ToString(CultureInfo.InvariantCulture));
        return new Composition(new XDocument(output), inputLines, inputMethods, valid, methodLines, covered, branchesCovered, branchesValid);
    }
    private sealed class ClassData
    {
        public Dictionary<(int, int), Point> Lines { get; } = [];
        public Dictionary<(string, string, int), Dictionary<(int, int), Point>> Methods { get; } = [];
    }
    private sealed class ClassKeyComparer : IComparer<(string Package, string Name, string File, int Occurrence)>
    {
        public static ClassKeyComparer Instance { get; } = new();
        public int Compare((string Package, string Name, string File, int Occurrence) x, (string Package, string Name, string File, int Occurrence) y)
        {
            var result = StringComparer.Ordinal.Compare(x.Package, y.Package);
            if (result != 0) return result;
            result = StringComparer.Ordinal.Compare(x.Name, y.Name);
            if (result != 0) return result;
            result = StringComparer.Ordinal.Compare(x.File, y.File);
            return result != 0 ? result : x.Occurrence.CompareTo(y.Occurrence);
        }
    }
    private sealed record Point(int Hits, int BranchCovered, int BranchValid);
    private static int Observe(Dictionary<(int, int), Point> target, XElement? lines)
    {
        if (lines is null) return 0;
        var occurrence = new Dictionary<int, int>();
        var count = 0;
        foreach (var line in lines.Elements("line"))
        {
            var number = Int(line, "number");
            var hits = Int(line, "hits");
            if (number <= 0 || hits < 0) throw Reject("Invalid coverage line.");
            var key = (number, occurrence.GetValueOrDefault(number));
            occurrence[number] = key.Item2 + 1;
            var branch = Branches(line);
            if (target.TryGetValue(key, out var previous))
            {
                var promote = branch.Valid == previous.BranchValid && branch.Valid > 0 && branch.Covered == branch.Valid;
                target[key] = new(Math.Max(previous.Hits, hits), promote ? branch.Covered : previous.BranchCovered, previous.BranchValid);
            }
            else
            {
                target[key] = new(hits, branch.Covered, branch.Valid);
            }
            count++;
        }
        return count;
    }
    private static XElement Lines(Dictionary<(int, int), Point> lines)
    {
        var element = new XElement("lines");
        foreach (var item in lines.OrderBy(x => x.Key))
        {
            var line = new XElement("line", new XAttribute("number", item.Key.Item1), new XAttribute("hits", item.Value.Hits),
                new XAttribute("branch", (item.Value.BranchValid > 0).ToString().ToLowerInvariant()));
            if (item.Value.BranchValid > 0)
            {
                // Python's :g uses six significant digits; the receipt preserves that exact spelling.
                var percent = (item.Value.BranchCovered * 100d / item.Value.BranchValid).ToString("G6", CultureInfo.InvariantCulture);
                line.SetAttributeValue("condition-coverage", $"{percent}% ({item.Value.BranchCovered}/{item.Value.BranchValid})");
            }
            element.Add(line);
        }
        return element;
    }

    private static (int Covered, int Valid) Branches(XElement line)
    {
        var value = line.Attribute("condition-coverage")?.Value;
        if (value is null)
        {
            if (string.Equals(line.Attribute("branch")?.Value, "true", StringComparison.OrdinalIgnoreCase))
                throw Reject("Branch counters are missing.");
            return (0, 0);
        }
        var match = Regex.Match(value, @"\A[0-9.]+% \(([0-9]+)/([0-9]+)\)\z", RegexOptions.CultureInvariant);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var covered)
            || !int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var valid)
            || valid <= 0 || covered > valid)
            throw Reject("Invalid branch counters.");
        return (covered, valid);
    }

    private static void CompareXml(XDocument expected, XDocument actual, string label)
    {
        if (!Equivalent(expected.Root!, actual.Root!)) throw Reject($"{label} does not match verified composition.");
    }
    private static bool Equivalent(XElement expected, XElement actual)
    {
        if (expected.Name != actual.Name || !EquivalentAttributes(expected, actual)) return false;
        var expectedChildren = expected.Elements().ToArray();
        var actualChildren = actual.Elements().ToArray();
        if (expectedChildren.Length != actualChildren.Length) return false;
        for (var i = 0; i < expectedChildren.Length; i++)
        {
            if (!Equivalent(expectedChildren[i], actualChildren[i])) return false;
        }
        return string.Equals(expected.Value, actual.Value, StringComparison.Ordinal) || expectedChildren.Length > 0;
    }
    private static bool EquivalentAttributes(XElement expected, XElement actual)
    {
        var left = expected.Attributes().ToDictionary(x => x.Name.LocalName, x => x.Value, StringComparer.Ordinal);
        var right = actual.Attributes().ToDictionary(x => x.Name.LocalName, x => x.Value, StringComparer.Ordinal);
        if (!left.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(right.Keys)) return false;
        foreach (var pair in left)
        {
            if (pair.Key is "line-rate" or "branch-rate"
                ? !double.TryParse(pair.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
                    || !double.TryParse(right[pair.Key], NumberStyles.Float, CultureInfo.InvariantCulture, out var b) || Math.Abs(a - b) > 1e-9
                : !string.Equals(pair.Value, right[pair.Key], StringComparison.Ordinal))
                return false;
        }
        return true;
    }
    private static void VerifyPrimaryPaths(XDocument primary, XDocument paths, string root)
    {
        var expected = new XDocument(primary);
        foreach (var cls in expected.Descendants("class"))
            cls.SetAttributeValue("filename", Relative(cls.Attribute("filename")?.Value ?? "", root));
        CompareXml(expected, paths, "primary paths output");
    }
    private static async ValueTask VerifyReceiptAsync(JsonElement receipt, EvidenceCoverageFanInRequest r, IReadOnlyList<Loaded> inputs, string outputDirectory, Composition composition, CancellationToken ct)
    {
        if (receipt.GetProperty("commit").GetString() != r.Commit || receipt.GetProperty("tree").GetString() != r.Tree
            || receipt.GetProperty("sdk").GetString() != r.Sdk || receipt.GetProperty("configuration").GetString() != r.Configuration
            || !JsonElementsEqual(receipt.GetProperty("policy"), ExpectedPolicy) || !JsonEquals(receipt.GetProperty("run"), r.Run))
            throw Reject("Verification receipt identity mismatch.");
        var outputHash = await Sha256Async(Path.Combine(outputDirectory, "coverage.cobertura.xml"), MaxXml, ct).ConfigureAwait(false);
        var primaryHash = await Sha256Async(Path.Combine(outputDirectory, "primary-paths.cobertura.xml"), MaxXml, ct).ConfigureAwait(false);
        if (receipt.GetProperty("outputSha256").GetString() != outputHash || receipt.GetProperty("primaryPathsSha256").GetString() != primaryHash)
            throw Reject("Composed output checksum mismatch.");
        var inventory = receipt.GetProperty("inventory");
        var classCounts = inputs.Select(x => x.Document.Descendants("class").Elements("lines").Elements("line").Count());
        var methodCounts = inputs.Select(x => x.Document.Descendants("method").Elements("lines").Elements("line").Count());
        if (!inventory.GetProperty("inputClassLines").EnumerateArray().Select(x => x.GetInt32()).SequenceEqual(classCounts)
            || !inventory.GetProperty("inputMethodLines").EnumerateArray().Select(x => x.GetInt32()).SequenceEqual(methodCounts)
            || inventory.GetProperty("retainedClassLines").GetInt32() != composition.ClassLines
            || inventory.GetProperty("retainedMethodLines").GetInt32() != composition.MethodLines
            || inventory.GetProperty("linesCovered").GetInt32() != composition.LinesCovered
            || inventory.GetProperty("branchesCovered").GetInt32() != composition.BranchesCovered
            || inventory.GetProperty("branchesValid").GetInt32() != composition.BranchesValid)
            throw Reject("Composition inventory mismatch.");
    }
    private static void VerifyGate(JsonElement gate, string expectedDiffBase, bool requirePatch, string expectedCoverageFile, XDocument coverage)
    {
        var thresholds = gate.GetProperty("thresholds");
        var lineThreshold = thresholds.GetProperty("line").GetDouble();
        var branchThreshold = thresholds.GetProperty("branch").GetDouble();
        if (gate.GetProperty("passed").ValueKind != JsonValueKind.True || gate.GetProperty("tolerancePercent").GetDouble() != .5
            || gate.GetProperty("patchLineMode").GetString() != "codecov")
            throw Reject("Coverage gate policy or result mismatch.");
        if (lineThreshold != (requirePatch ? 0 : 95) || branchThreshold != (requirePatch ? 0 : 85)) throw Reject("Coverage gate overall thresholds changed.");
        if (Path.GetFileName(gate.GetProperty("coverage").GetString()) != expectedCoverageFile) throw Reject("Coverage gate report role mismatch.");
        var diff = gate.GetProperty("patchDiffSource");
        if (diff.GetProperty("diffBase").GetString() != expectedDiffBase) throw Reject("Coverage gate diff base mismatch.");
        if (requirePatch && (thresholds.GetProperty("patchLine").GetDouble() != 95 || thresholds.GetProperty("patchBranch").GetDouble() != 85)) throw Reject("Coverage gate patch thresholds changed.");
        var effective = gate.GetProperty("effectiveThresholds");
        CheckMetric(gate.GetProperty("line"), effective.GetProperty("line").GetDouble());
        CheckMetric(gate.GetProperty("branch"), effective.GetProperty("branch").GetDouble());
        if (gate.GetProperty("line").GetProperty("covered").GetInt32() != Attr(coverage.Root!, "lines-covered")
            || gate.GetProperty("line").GetProperty("valid").GetInt32() != Attr(coverage.Root!, "lines-valid")
            || gate.GetProperty("branch").GetProperty("covered").GetInt32() != Attr(coverage.Root!, "branches-covered")
            || gate.GetProperty("branch").GetProperty("valid").GetInt32() != Attr(coverage.Root!, "branches-valid"))
            throw Reject("Coverage gate aggregate counts do not match its XML report.");
        RequireEffective(effective.GetProperty("line").GetDouble(), lineThreshold);
        RequireEffective(effective.GetProperty("branch").GetDouble(), branchThreshold);
        if (requirePatch)
        {
            CheckMetric(gate.GetProperty("patchLine"), effective.GetProperty("patchLine").GetDouble(), useMeasurable: true);
            CheckMetric(gate.GetProperty("patchBranch"), effective.GetProperty("patchBranch").GetDouble(), useMeasurable: true);
            RequireEffective(effective.GetProperty("patchLine").GetDouble(), 95);
            RequireEffective(effective.GetProperty("patchBranch").GetDouble(), 85);
        }
    }
    private static void RequireEffective(double actual, double configured)
    {
        if (actual != Math.Max(0, configured - .5)) throw Reject("Coverage gate effective threshold was forged.");
    }
    private static void CheckMetric(JsonElement metric, double threshold, bool useMeasurable = false)
    {
        var covered = metric.GetProperty("covered").GetDouble();
        var valid = useMeasurable ? metric.GetProperty("measurable").GetDouble() : metric.GetProperty("valid").GetDouble();
        var percent = metric.GetProperty("percent").GetDouble();
        if (valid <= 0 || covered < 0 || covered > valid || double.IsNaN(percent)
            || Math.Abs(percent - covered * 100 / valid) > .01 || percent < threshold)
            throw Reject("Coverage gate metric does not satisfy its numerical threshold.");
    }
    private static void VerifyMeasurableUnion(JsonElement primary, JsonElement composed)
    {
        foreach (var name in new[] { "patchLine", "patchBranch" })
        {
            if (composed.GetProperty(name).GetProperty("measurable").GetInt32() < primary.GetProperty(name).GetProperty("measurable").GetInt32())
                throw Reject("Fan-in removed measurable coverage points.");
        }
        if (composed.GetProperty("patchLine").GetProperty("measurable").GetInt32() == 0
            || composed.GetProperty("patchBranch").GetProperty("measurable").GetInt32() == 0)
            throw Reject("Measurable patch coverage is empty.");
    }
    private static int Attr(XElement e, string name) => int.Parse(e.Attribute(name)?.Value ?? throw Reject($"Missing coverage counter: {name}"), CultureInfo.InvariantCulture);
    private static int Int(XElement e, string name) => int.Parse(e.Attribute(name)?.Value ?? throw Reject($"Missing coverage value: {name}"), CultureInfo.InvariantCulture);
    private static bool JsonEquals(JsonElement element, IReadOnlyDictionary<string, string?> expected) =>
        element.ValueKind == JsonValueKind.Object && element.EnumerateObject().Count() == expected.Count
        && expected.All(x => element.TryGetProperty(x.Key, out var value)
            && value.ValueKind == (x.Value is null ? JsonValueKind.Null : JsonValueKind.String) && value.GetString() == x.Value);
    private static bool JsonElementsEqual(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind) return false;
        return left.ValueKind switch
        {
            JsonValueKind.Object => left.EnumerateObject().Count() == right.EnumerateObject().Count() && left.EnumerateObject().All(x => right.TryGetProperty(x.Name, out var value) && JsonElementsEqual(x.Value, value)),
            JsonValueKind.Array => left.GetArrayLength() == right.GetArrayLength() && left.EnumerateArray().Zip(right.EnumerateArray()).All(x => JsonElementsEqual(x.First, x.Second)),
            JsonValueKind.String => left.GetString() == right.GetString(),
            JsonValueKind.Number => left.GetRawText() == right.GetRawText() || left.GetDecimal() == right.GetDecimal(),
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
            _ => false,
        };
    }
}
