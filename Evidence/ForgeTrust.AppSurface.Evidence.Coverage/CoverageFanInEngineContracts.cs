using System.Text.Json;
using System.Xml.Linq;

namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>Frozen facade-to-private-engine transport. JSON is a schema snapshot, never an execution instruction.</summary>
internal sealed record CoverageFanInExpected(JsonElement Policy, string Digest)
{
    internal JsonElement Source => Policy.GetProperty("source");
    internal JsonElement Run => Policy.GetProperty("run");
    internal JsonElement Gate => Policy.GetProperty("gate");
    internal JsonElement Instrumentation => Policy.GetProperty("instrumentation");
    internal JsonElement Limits => Policy.GetProperty("limits");
    internal string Configuration => Source.GetProperty("configuration").GetString()!;
    internal string[] TestProjects => Policy.GetProperty("testProjects").EnumerateArray().Select(x => x.GetString()!).ToArray();
    internal JsonElement[] Supplements => Policy.GetProperty("supplements").EnumerateArray().ToArray();
    internal CoverageFanInBounds Bounds => new(Limits.GetProperty("xmlBytes").GetInt64(), Limits.GetProperty("jsonBytes").GetInt64(),
        Limits.GetProperty("totalBytes").GetInt64(), Limits.GetProperty("depth").GetInt32(), Limits.GetProperty("lineRecordsPerReport").GetInt32(),
        Limits.GetProperty("xmlElementsPerReport").GetInt32(), Limits.GetProperty("attributesPerElement").GetInt32(),
        Limits.GetProperty("tokenCharacters").GetInt32(), Limits.GetProperty("branchRecordsPerReport").GetInt32(), Limits.GetProperty("totalRecords").GetInt32());
}
/// <summary>Internal operation request, mapped only after strict public validation.</summary>
internal sealed record CoverageFanInEngineRequest(CoverageFanInExpected Expected, string RepositoryRoot,
    string? OutputDirectory = null, string ProducerId = "primary", string? CoveragePath = null,
    string? TimingsPath = null, string? RawCoveragePath = null, string? ImportDirectory = null,
    string? OriginalSourceRoot = null, bool ReplaceOwned = false, string? PrimaryCaptureDirectory = null,
    IReadOnlyList<string>? SupplementaryCaptureDirectories = null, string? ComposedOutputDirectory = null,
    string? AcceptedOutputDirectory = null, bool HistoricalReplay = false,
    string? CollectionDirectory = null, string? SolutionPath = null, int DeadlineSeconds = 0,
    int Parallelism = 1, bool NoRestore = false, bool NoBuild = false)
{
    /// <summary>Per-call identity execution seam for deadline/failure tests; public adapters leave it null.</summary>
    internal ICoverageRunProcessRunner? IdentityRunner { get; init; }
    /// <summary>Exact native project-to-original-report mapping, with complete successful timings.</summary>
    internal IReadOnlyDictionary<string, string>? RawCoveragePaths { get; init; }
    /// <summary>Optional lowercase SHA-256 that the exact patch used by acceptance must match.</summary>
    internal string? ExpectedPatchSha256 { get; init; }
}
/// <summary>Private safe terminal diagnostic, without dependency exception data.</summary>
internal sealed record CoverageFanInEngineDiagnostic(string Code, string Stage, string Field, string Recovery);
/// <summary>Immutable exact role/hash reference published by this invocation.</summary>
internal sealed record CoverageFanInEngineArtifact(string Role, string Path, long Length, string Sha256);
/// <summary>Actual invocation result; no stale-generation success reuse.</summary>
internal sealed record CoverageFanInEngineResult(string Operation, string Outcome, string OperationId, string Digest,
    bool Created, bool Committed, bool CleanupComplete, bool Imported, string? Generation,
    IReadOnlyList<CoverageFanInEngineArtifact> Artifacts, IReadOnlyList<CoverageFanInEngineDiagnostic> Diagnostics,
    bool Accepted = false, bool Verified = false, string PatchOutcome = "NotEvaluated", string? Observation = null,
    bool HistoricalReplay = false, bool PrimaryImported = false);
/// <summary>Validated retained capture bytes. No semantic oracle is shared by composer and verifier.</summary>
internal sealed record CoverageFanInBundle(string Directory, JsonElement Manifest, string ManifestHash,
    XDocument Document, byte[] CoverageBytes, IReadOnlyDictionary<string, byte[]> AuxiliaryFiles);
