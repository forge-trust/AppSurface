using System.Text.Json.Serialization;

namespace ForgeTrust.AppSurface.Coverage;

/// <summary>Explicit gate intent. A baseline never implies patch acceptance.</summary>
public enum CoverageGateMode
{
    /// <summary>Explicit baseline; patch is not requested.</summary>
    AggregateOnly,
    /// <summary>Primary aggregate plus independently verified composed patch.</summary>
    AggregateAndPatch
}
/// <summary>Custody of the current destination.</summary>
public enum CoverageOutputMode
{
    /// <summary>Require a fresh destination.</summary>
    Fresh,
    /// <summary>Replace only compatible registered, unmodified output.</summary>
    ReplaceOwned
}
/// <summary>Original observation, independent of local import or evaluation.</summary>
public enum CoverageObservation
{
    /// <summary>Observed by the supervised collection operation.</summary>
    CollectedExecution,
    /// <summary>Successful observations explicitly supplied by a trusted caller.</summary>
    TrustedProducer
}
/// <summary>Whether retained evidence must match the caller's exact current run.</summary>
public enum CoverageEvaluationIntent
{
    /// <summary>Exact source, run and attempt expected by the trusted current caller.</summary>
    CurrentTrustedRun,
    /// <summary>Existing historical facts; cannot close a fresh obligation.</summary>
    HistoricalReplay
}
/// <summary>Terminal operation outcome; only Succeeded satisfies the requested operation.</summary>
public enum CoverageOperationOutcome
{
    /// <summary>The requested operation completed.</summary>
    Succeeded,
    /// <summary>Inputs or evidence were rejected.</summary>
    Rejected,
    /// <summary>Caller cancellation before commit.</summary>
    Cancelled,
    /// <summary>Deadline or terminal watchdog before commit.</summary>
    TimedOut,
    /// <summary>Partial effects require reconciliation.</summary>
    Incomplete
}

/// <summary>Provider-neutral trusted run identity. Matching values establish consistency, not authenticity.</summary>
public sealed record CoverageRunIdentity(string ProducerSystem, string RunScope, string RunId, int Attempt);
/// <summary>Exact clean source, selected SDK and build configuration expected by the caller.</summary>
public sealed record CoverageSourceIdentity(string Commit, string Tree, string Sdk, string Configuration);
/// <summary>Instrumentation and normalization identities shared by every producer.</summary>
public sealed record CoverageInstrumentation(string Driver = "collector", string Format = "cobertura",
    string? Include = null, string Exclude = "[*.Tests]*,[*.IntegrationTests]*",
    string Normalization = "appsurface-coverage-merge");
/// <summary>One required supplementary producer; IDs and platforms form an exact declared inventory.</summary>
public sealed record CoverageSupplement(string Id, string Platform, string Filter, IReadOnlyList<string>? TestProjects = null);
/// <summary>Explicit aggregate/patch thresholds, tolerance and patch semantics.</summary>
public sealed record CoverageGatePolicy(CoverageGateMode Mode, decimal MinLine = 95, decimal MinBranch = 85,
    decimal MinPatchLine = 95, decimal MinPatchBranch = 85, decimal Tolerance = 0.5m,
    string PatchLineMode = "codecov", string? DiffBase = null);
/// <summary>Admitted per-operation budgets. Collection additionally requires its own overall deadline.</summary>
/// <remarks>Retained patch diffs use the Git reader's fixed 20 MiB UTF-8 limit instead of JsonBytes.
/// They still count against TotalBytes during reading and publication.</remarks>
public sealed record CoverageOperationLimits(long XmlBytes = 536870912, long JsonBytes = 1048576,
    long TotalBytes = 4294967296, int Depth = 64, int LineRecordsPerReport = 1000000,
    int InputBundles = 32, int XmlElementsPerReport = 8000000, int AttributesPerElement = 32,
    int TokenCharacters = 65536, int BranchRecordsPerReport = 2000000,
    int TotalRecords = 2000000, int OperationSeconds = 300, int IdentitySeconds = 30);
/// <summary>Complete explicit consumer policy. Operations validate and freeze collections before side effects.</summary>
public sealed record CoverageFanInPolicy(CoverageSourceIdentity Source, CoverageRunIdentity Run,
    IReadOnlyList<string> TestProjects, IReadOnlyList<CoverageSupplement> Supplements, CoverageGatePolicy Gate,
    CoverageInstrumentation? Instrumentation = null, CoverageOperationLimits? Limits = null);

/// <summary>Capture a successful trusted producer, or explicitly materialize portable v2 evidence into fresh custody.</summary>
/// <param name="Policy">Trusted complete policy.</param>
/// <param name="RepositoryRoot">Matching clean local Git source.</param>
/// <param name="OutputDirectory">Fresh owned capture destination, unless replacement is explicitly requested.</param>
/// <param name="ProducerId">primary, or an exact supplementary producer ID.</param>
/// <param name="CoveragePath">Successful normalized Cobertura report; absent for import or indexed native capture.</param>
/// <param name="TimingsPath">Complete successful primary or indexed native inventory; absent for single-report native/import.</param>
/// <param name="RawCoveragePath">Original collector bytes for a single-project native producer; absent for primary/import/indexed native.</param>
/// <param name="ImportDirectory">Explicit portable capture to validate/materialize; mutually exclusive with report paths.</param>
/// <param name="OriginalSourceRoot">Exact original root mapping for imported evidence.</param>
/// <param name="OutputMode">Fresh-only default.</param>
/// <param name="RawCoveragePaths">For a native inventory with multiple projects, exact project-to-original-collector paths instead of RawCoveragePath. Requires complete TimingsPath.</param>
public sealed record CoverageCaptureRequest(CoverageFanInPolicy Policy, string RepositoryRoot,
    string OutputDirectory, string ProducerId, string? CoveragePath = null, string? TimingsPath = null,
    string? RawCoveragePath = null, string? ImportDirectory = null, string? OriginalSourceRoot = null,
    CoverageOutputMode OutputMode = CoverageOutputMode.Fresh,
    IReadOnlyDictionary<string, string>? RawCoveragePaths = null);
/// <summary>Collect the complete explicit inventory or declared native filter through the existing supervised engine.</summary>
/// <remarks>Observed collection rebuilds before testing. <c>NoBuild</c> must be false.
/// Declared test projects must be regular tracked files in the pinned Git source, including pinned submodules.
/// <c>NoRestore</c> may be true when restore has already completed; this does not skip the rebuild.</remarks>
public sealed record CoverageCollectRequest(CoverageFanInPolicy Policy, string RepositoryRoot,
    string OutputDirectory, string CollectionDirectory, string ProducerId, int DeadlineSeconds,
    string? SolutionPath = null, int Parallelism = 1, bool NoRestore = false, bool NoBuild = false);
/// <summary>Conservatively compose exactly one primary plus the policy's exact supplementary set.</summary>
public sealed record CoverageComposeRequest(CoverageFanInPolicy Policy, string RepositoryRoot,
    string PrimaryCaptureDirectory, IReadOnlyList<string> SupplementaryCaptureDirectories, string OutputDirectory,
    CoverageOutputMode OutputMode = CoverageOutputMode.Fresh,
    CoverageEvaluationIntent EvaluationIntent = CoverageEvaluationIntent.CurrentTrustedRun);
/// <summary>Require primary aggregate, explicit patch policy, and independent verification before acceptance.</summary>
public sealed record CoverageAcceptRequest(CoverageFanInPolicy Policy, string RepositoryRoot,
    string PrimaryCaptureDirectory, IReadOnlyList<string> SupplementaryCaptureDirectories,
    string ComposedOutputDirectory, string OutputDirectory, CoverageOutputMode OutputMode = CoverageOutputMode.Fresh,
    CoverageEvaluationIntent EvaluationIntent = CoverageEvaluationIntent.CurrentTrustedRun)
{
    /// <summary>Optional SHA-256 of the exact unified Git patch to require before running either gate.</summary>
    /// <remarks>Valid only for patch acceptance. A mismatch never publishes an accepted generation.</remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExpectedPatchSha256 { get; init; }
}
/// <summary>Read-only validation of an existing accepted generation; creates no coordination or reports.</summary>
public sealed record CoverageVerifyRequest(CoverageFanInPolicy Policy, string RepositoryRoot,
    string PrimaryCaptureDirectory, IReadOnlyList<string> SupplementaryCaptureDirectories,
    string ComposedOutputDirectory, string AcceptedOutputDirectory,
    CoverageEvaluationIntent EvaluationIntent = CoverageEvaluationIntent.CurrentTrustedRun);
/// <summary>A bounded safe diagnostic. Arbitrary source/dependency exception text is never copied here.</summary>
public sealed record CoverageDiagnostic(string Code, string Stage, string Field, string Recovery,
    string Documentation = "https://appsurface.dev/guides/coverage-fan-in#diagnostics");
/// <summary>Actual output effects, including failures after publication or delivery.</summary>
public sealed record CoverageOperationEffects(bool OutputCreated, bool GenerationCommitted, bool CleanupComplete,
    bool Imported, string? Generation);
/// <summary>Retained artifact role and integrity reference, without execution/freshness claims.</summary>
public sealed record CoverageArtifactReference(string Role, string Path, long Length, string Sha256);
/// <summary>Bounded result of one operation. Capture/compose success never implies accepted coverage.</summary>
public sealed class CoverageFanInResult
{
    internal CoverageFanInResult(string operation, CoverageOperationOutcome outcome, string operationId,
        string? policyDigest, CoverageOperationEffects effects, IReadOnlyList<CoverageArtifactReference> artifacts,
        IReadOnlyList<CoverageDiagnostic> diagnostics, bool accepted = false, bool verified = false,
        string patchOutcome = "NotEvaluated", CoverageObservation? observation = null,
        CoverageEvaluationIntent intent = CoverageEvaluationIntent.CurrentTrustedRun, bool primaryImported = false)
    {
        Operation = operation; Outcome = outcome; OperationId = operationId; PolicyDigest = policyDigest;
        Effects = effects; Artifacts = artifacts; Diagnostics = diagnostics; Accepted = accepted;
        Verified = verified; PatchOutcome = patchOutcome; Observation = observation; EvaluationIntent = intent;
        PrimaryImported = primaryImported;
    }
    /// <summary>Requested operation.</summary>
    public string Operation { get; }
    /// <summary>Terminal outcome.</summary>
    public CoverageOperationOutcome Outcome { get; }
    /// <summary>Unique current invocation identity.</summary>
    public string OperationId { get; }
    /// <summary>coverage-policy-c14n-1 digest of the frozen expected policy.</summary>
    public string? PolicyDigest { get; }
    /// <summary>Actual partial or committed effects.</summary>
    public CoverageOperationEffects Effects { get; }
    /// <summary>Retained artifacts, which do not independently establish acceptance.</summary>
    public IReadOnlyList<CoverageArtifactReference> Artifacts { get; }
    /// <summary>Bounded safe reason codes and precise recovery actions.</summary>
    public IReadOnlyList<CoverageDiagnostic> Diagnostics { get; }
    /// <summary>True only for completed independently verified acceptance.</summary>
    public bool Accepted { get; }
    /// <summary>Existing evidence consistency, never proof of arbitrary execution or authenticity.</summary>
    public bool Verified { get; }
    /// <summary>PatchAccepted, PatchNotRequested, or NotEvaluated.</summary>
    public string PatchOutcome { get; }
    /// <summary>Original producer observation, preserved by import.</summary>
    public CoverageObservation? Observation { get; }
    /// <summary>Whether the primary was materialized from transport rather than captured locally.</summary>
    /// <remarks>Applies to operations that consume a primary capture: compose, accept, and verify.</remarks>
    public bool PrimaryImported { get; }
    /// <summary>Current trusted run versus historical replay; import does not change it.</summary>
    public CoverageEvaluationIntent EvaluationIntent { get; }
}
