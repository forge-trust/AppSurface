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
            return new(StringComparer.Ordinal)
            {
                [CoverageName] = imported.CoverageBytes,
                [imported.AuxiliaryName] = imported.AuxiliaryBytes,
                [ManifestName] = io.Read(Path.Join(r.ImportDirectory, ManifestName))
            };
        }
        var rawCoverage = io.Read(r.CoveragePath!, xml: true);
        var document = io.Xml(rawCoverage, allowInertGeneratorDoctype: true);
        var auxiliaryName = r.ProducerId == "primary" ? "timings.json" : "collector.cobertura.xml";
        var auxiliary = io.Read(r.ProducerId == "primary" ? r.TimingsPath! : r.RawCoveragePath!, xml: r.ProducerId != "primary");
        if (r.ProducerId == "primary")
            CoverageFanInComposer.ValidatePrimaryTimings(Parse(auxiliary), r.Expected.TestProjects, r.Expected.Configuration,
                r.Expected.Instrumentation.GetProperty("driver").GetString()!);
        else
        {
            var raw = io.Xml(auxiliary, allowInertGeneratorDoctype: true);
            document = CoverageFanInComposer.RetainCollectorMethods(raw, document, Full(r.RepositoryRoot));
            CoverageFanInComposer.ValidateNormalization(raw, document, Full(r.RepositoryRoot));
            CoverageFanInIndependentVerifier.VerifyNormalization(raw, document, Full(r.RepositoryRoot));
            CoverageFanInIndependentVerifier.VerifyCollectorMethods(raw, document, Full(r.RepositoryRoot));
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
            files = new Dictionary<string, string>(StringComparer.Ordinal)
            { [CoverageName] = CoverageFanInIO.Hash(coverage), [auxiliaryName] = CoverageFanInIO.Hash(auxiliary) }
        });
        return new(StringComparer.Ordinal) { [CoverageName] = coverage, [auxiliaryName] = auxiliary, [ManifestName] = manifest };
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
        var auxiliaryName = producerId == "primary" ? "timings.json" : "collector.cobertura.xml";
        var coverage = io.Read(Path.Join(directory, CoverageName), xml: true);
        var auxiliary = io.Read(Path.Join(directory, auxiliaryName), xml: producerId != "primary");
        var files = manifest.GetProperty("files");
        ExactProperties(files, [CoverageName, auxiliaryName]);
        if (files.GetProperty(CoverageName).GetString() != CoverageFanInIO.Hash(coverage)
            || files.GetProperty(auxiliaryName).GetString() != CoverageFanInIO.Hash(auxiliary))
            throw new InvalidDataException("Capture integrity differs.");
        var document = io.Xml(coverage);
        if (producerId == "primary")
        {
            CoverageFanInIndependentVerifier.VerifyPrimaryInventory(Parse(auxiliary),
                r.Expected.TestProjects, r.Expected.Configuration, r.Expected.Instrumentation.GetProperty("driver").GetString()!);
            // Portable capture has the same measurable-point admission as a fresh producer.
            // Matching transport hashes cannot make malformed coverage a successful capture.
            _ = CoverageFanInComposer.Compose([(document, sourceRoot)]);
        }
        else
        {
            var raw = io.Xml(auxiliary, allowInertGeneratorDoctype: true);
            CoverageFanInIndependentVerifier.VerifyNormalization(raw, document, sourceRoot);
            CoverageFanInIndependentVerifier.VerifyCollectorMethods(raw, document, sourceRoot);
        }
        if (localCustody) VerifyGeneration(r, io, directory, "capture", [CoverageName, auxiliaryName, ManifestName]);
        else
        {
            // Explicit import grants no deletion or adoption rights over the transport tree.
            // Uploads may retain local completion metadata, which never grants receiving-host custody.
            var names = new List<string> { CoverageName, auxiliaryName, ManifestName };
            if (File.Exists(Path.Join(directory, CoverageFanInIO.CompletionName))) names.Add(CoverageFanInIO.CompletionName);
            io.ExactInventory(directory, names);
        }
        return new(Full(directory), manifest, CoverageFanInIO.Hash(manifestBytes), document, coverage, auxiliary, auxiliaryName);
    }
    private static void ExactProperties(JsonElement value, IReadOnlyList<string> expected)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.EnumerateObject().Select(x => x.Name).ToHashSet(StringComparer.Ordinal).SetEquals(expected))
            throw new InvalidDataException("Evidence schema fields differ.");
    }
    private static string Platform() => OperatingSystem.IsMacOS() ? "Darwin" : OperatingSystem.IsWindows() ? "Windows" :
        OperatingSystem.IsLinux() ? "Linux" : throw new PlatformNotSupportedException("Coverage native platform unsupported.");
}
