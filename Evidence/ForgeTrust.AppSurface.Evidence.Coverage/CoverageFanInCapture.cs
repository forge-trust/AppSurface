using System.Runtime.InteropServices;
using System.Text.Json;
using System.Xml.Linq;

namespace ForgeTrust.AppSurface.Evidence.Coverage;

internal static partial class CoverageFanInEngine
{
    private static Dictionary<string, byte[]> Capture(CoverageFanInEngineRequest r, CoverageFanInIO io, InvocationState state)
    {
        if (r.ImportDirectory is not null)
        {
            var imported = LoadBundle(r, io, r.ImportDirectory, r.ProducerId, localCustody: false);
            if (imported.Manifest.GetProperty("sourceRoot").GetString() != r.OriginalSourceRoot)
                throw new InvalidDataException("Original source-root mapping differs.");
            state.Imported = true;
            state.Observation = imported.Manifest.GetProperty("observation").GetString();
            var importedFiles = new Dictionary<string, byte[]>(imported.AuxiliaryFiles, StringComparer.Ordinal)
            {
                [CoverageName] = imported.CoverageBytes,
                [ManifestName] = io.Read(Path.Join(r.ImportDirectory, ManifestName))
            };
            return importedFiles;
        }
        XDocument document;
        Dictionary<string, byte[]> auxiliaryFiles;
        if (r.RawCoveragePaths is not null)
        {
            (document, auxiliaryFiles) = CaptureNativeSet(r, io);
        }
        else
        {
            document = io.Xml(io.Read(r.CoveragePath!, xml: true), allowInertGeneratorDoctype: true);
            var auxiliaryName = r.ProducerId == "primary" ? "timings.json" : "collector.cobertura.xml";
            var auxiliary = io.Read(r.ProducerId == "primary" ? r.TimingsPath! : r.RawCoveragePath!, xml: r.ProducerId != "primary");
            auxiliaryFiles = new(StringComparer.Ordinal) { [auxiliaryName] = auxiliary };
            if (r.ProducerId == "primary")
                CoverageFanInComposer.ValidatePrimaryTimings(Parse(auxiliary), r.Expected.TestProjects, r.Expected.Configuration,
                    r.Expected.Instrumentation.GetProperty("driver").GetString()!);
            else
            {
                if (NativeProjects(r).Length != 1) throw new InvalidDataException("Multiple native projects require indexed originals and timings.");
                var raw = io.Xml(auxiliary, allowInertGeneratorDoctype: true);
                document = CoverageFanInComposer.RetainCollectorMethods(raw, document, Full(r.RepositoryRoot));
                CoverageFanInComposer.ValidateNormalization(raw, document, Full(r.RepositoryRoot));
                CoverageFanInIndependentVerifier.VerifyNormalization(raw, document, Full(r.RepositoryRoot));
                CoverageFanInIndependentVerifier.VerifyCollectorMethods(raw, document, Full(r.RepositoryRoot));
            }
        }
        // Validate measurable points even for a capture that will not be composed in this invocation.
        _ = CoverageFanInComposer.Compose([(document, Full(r.RepositoryRoot))]);
        var coverage = io.EncodeXml(document);
        state.Observation ??= "TrustedProducer";
        var producer = Producer(r);
        if (producer is not null && producer.Value.GetProperty("platform").GetString() != Platform())
            throw new InvalidDataException("Declared native platform differs from this producer host.");
        var manifest = Serialize(new
        {
            schemaVersion = 2,
            producerId = r.ProducerId,
            scope = r.ProducerId == "primary" ? "solution" : "native",
            sourceRoot = Full(r.RepositoryRoot),
            source = r.Expected.Source,
            run = r.Expected.Run,
            policy = r.Expected.Policy,
            policyDigest = r.Expected.Digest,
            policyEncoding = "coverage-policy-c14n-1",
            platform = producer is null ? Platform() : producer.Value.GetProperty("platform").GetString(),
            filter = producer?.GetProperty("filter").GetString(),
            observation = state.Observation,
            files = auxiliaryFiles.ToDictionary(item => item.Key, item => CoverageFanInIO.Hash(item.Value), StringComparer.Ordinal)
                .Append(new KeyValuePair<string, string>(CoverageName, CoverageFanInIO.Hash(coverage)))
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)
        });
        auxiliaryFiles.Add(CoverageName, coverage);
        auxiliaryFiles.Add(ManifestName, manifest);
        return auxiliaryFiles;
    }

    private static List<CoverageFanInBundle> LoadSet(CoverageFanInEngineRequest r, CoverageFanInIO io)
    {
        var bundles = new List<CoverageFanInBundle> { LoadBundle(r, io, r.PrimaryCaptureDirectory!, "primary") };
        var remaining = r.Expected.Supplements.Select(x => x.GetProperty("id").GetString()!).ToHashSet(StringComparer.Ordinal);
        foreach (var path in r.SupplementaryCaptureDirectories!)
        {
            var manifest = Parse(io.Read(Path.Join(path, ManifestName)));
            var id = manifest.GetProperty("producerId").GetString()!;
            if (!remaining.Remove(id)) throw new InvalidDataException("Supplementary inventory differs.");
            bundles.Add(LoadBundle(r, io, path, id));
        }
        if (remaining.Count != 0) throw new InvalidDataException("Supplementary inventory incomplete.");
        return bundles.Take(1).Concat(bundles.Skip(1).OrderBy(x => x.Manifest.GetProperty("producerId").GetString(), StringComparer.Ordinal)).ToList();
    }

    private static CoverageFanInBundle LoadBundle(CoverageFanInEngineRequest r, CoverageFanInIO io, string directory,
        string producerId, bool localCustody = true)
    {
        var manifestBytes = io.Read(Path.Join(directory, ManifestName));
        var manifest = Parse(manifestBytes);
        ExactProperties(manifest, ["schemaVersion", "producerId", "scope", "sourceRoot", "source", "run", "policy",
            "policyDigest", "policyEncoding", "platform", "filter", "observation", "files"]);
        if (manifest.GetProperty("schemaVersion").GetInt32() != 2
            || manifest.GetProperty("producerId").GetString() != producerId
            || manifest.GetProperty("scope").GetString() != (producerId == "primary" ? "solution" : "native")
            || manifest.GetProperty("policyEncoding").GetString() != "coverage-policy-c14n-1"
            || manifest.GetProperty("policyDigest").GetString() != r.Expected.Digest
            || !Same(manifest.GetProperty("policy"), r.Expected.Policy)
            || !Same(manifest.GetProperty("source"), r.Expected.Source) || !Same(manifest.GetProperty("run"), r.Expected.Run)
            || manifest.GetProperty("observation").GetString() is not ("CollectedExecution" or "TrustedProducer"))
            throw new InvalidDataException("Capture identity, policy or observation differs.");
        var sourceRoot = manifest.GetProperty("sourceRoot").GetString();
        if (string.IsNullOrWhiteSpace(sourceRoot) || !(sourceRoot.StartsWith('/') || sourceRoot.StartsWith(@"\\", StringComparison.Ordinal)
            || sourceRoot.Length > 2 && char.IsAsciiLetter(sourceRoot[0]) && sourceRoot[1] == ':' && sourceRoot[2] is '\\' or '/'))
            throw new InvalidDataException("Capture source-root is not absolute.");
        var supplement = producerId == "primary" ? (JsonElement?)null : r.Expected.Supplements.Single(x => x.GetProperty("id").GetString() == producerId);
        if (supplement is not null && (manifest.GetProperty("platform").GetString() != supplement.Value.GetProperty("platform").GetString()
            || manifest.GetProperty("filter").GetString() != supplement.Value.GetProperty("filter").GetString())
            || supplement is null && manifest.GetProperty("filter").ValueKind != JsonValueKind.Null)
            throw new InvalidDataException("Capture platform or instrumentation differs.");
        var coverage = io.Read(Path.Join(directory, CoverageName), xml: true);
        var files = manifest.GetProperty("files");
        var document = io.Xml(coverage);
        Dictionary<string, byte[]> auxiliaryFiles;
        var bundleRequest = r with { ProducerId = producerId };
        if (producerId != "primary" && files.TryGetProperty("collector-inventory.json", out _))
            auxiliaryFiles = LoadNativeSet(bundleRequest, io, directory, document, sourceRoot, files);
        else
        {
            var auxiliaryName = producerId == "primary" ? "timings.json" : "collector.cobertura.xml";
            ExactProperties(files, [CoverageName, auxiliaryName]);
            if (producerId != "primary" && NativeProjects(bundleRequest).Length != 1)
                throw new InvalidDataException("Single-report capture cannot satisfy multiple native projects.");
            auxiliaryFiles = new(StringComparer.Ordinal)
            { [auxiliaryName] = io.Read(Path.Join(directory, auxiliaryName), xml: producerId != "primary") };
        }
        if (files.GetProperty(CoverageName).GetString() != CoverageFanInIO.Hash(coverage)
            || auxiliaryFiles.Any(item => files.GetProperty(item.Key).GetString() != CoverageFanInIO.Hash(item.Value)))
            throw new InvalidDataException("Capture integrity differs.");
        if (producerId == "primary")
        {
            CoverageFanInIndependentVerifier.VerifyPrimaryInventory(Parse(auxiliaryFiles["timings.json"]),
                r.Expected.TestProjects, r.Expected.Configuration, r.Expected.Instrumentation.GetProperty("driver").GetString()!);
            // Portable capture has the same measurable-point admission as a fresh producer.
            // Matching transport hashes cannot make malformed coverage a successful capture.
            _ = CoverageFanInComposer.Compose([(document, sourceRoot)]);
        }
        else if (!auxiliaryFiles.ContainsKey("collector-inventory.json"))
        {
            var raw = io.Xml(auxiliaryFiles["collector.cobertura.xml"], allowInertGeneratorDoctype: true);
            CoverageFanInIndependentVerifier.VerifyNormalization(raw, document, sourceRoot);
            CoverageFanInIndependentVerifier.VerifyCollectorMethods(raw, document, sourceRoot);
        }
        var payloadNames = auxiliaryFiles.Keys.Append(CoverageName).Append(ManifestName).ToArray();
        if (localCustody) VerifyGeneration(r, io, directory, "capture", payloadNames);
        else
        {
            // Explicit import grants no deletion or adoption rights over the transport tree.
            // Uploads may retain local completion metadata, which never grants receiving-host custody.
            var names = payloadNames.ToList();
            if (File.Exists(Path.Join(directory, CoverageFanInIO.CompletionName))) names.Add(CoverageFanInIO.CompletionName);
            io.ExactInventory(directory, names);
        }
        return new(Full(directory), manifest, CoverageFanInIO.Hash(manifestBytes), document, coverage, auxiliaryFiles);
    }
    private static void ExactProperties(JsonElement value, IReadOnlyList<string> expected)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.EnumerateObject().Select(x => x.Name).ToHashSet(StringComparer.Ordinal).SetEquals(expected))
            throw new InvalidDataException("Evidence schema fields differ.");
    }
    private static string Platform() => OperatingSystem.IsMacOS() ? "Darwin" : OperatingSystem.IsWindows() ? "Windows" :
        OperatingSystem.IsLinux() ? "Linux" : throw new PlatformNotSupportedException("Coverage native platform unsupported.");
}
