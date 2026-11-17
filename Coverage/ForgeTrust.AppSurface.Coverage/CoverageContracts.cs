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
    /// <summary>Require an existing compatible registered, unmodified generation; never create a missing destination.</summary>
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
/// <remarks>
/// Contract version 2 is the default and retains the existing positive patch-line behavior. Version 3 requires the
/// closed <c>coverlet-collector-cobertura-v1</c> report profile. The profile describes admitted report semantics; it
/// does not claim that the collector reported every executable source point. For a v3 aggregate-and-patch policy,
/// <see cref="CoverageGatePolicy.DiffBase"/> must be a full lowercase commit ID matching the source repository's
/// object format. The public positional constructor and its optional defaults are unchanged.
/// </remarks>
[JsonConverter(typeof(CoverageFanInPolicyJsonConverter))]
public sealed record CoverageFanInPolicy(CoverageSourceIdentity Source, CoverageRunIdentity Run,
    IReadOnlyList<string> TestProjects, IReadOnlyList<CoverageSupplement> Supplements, CoverageGatePolicy Gate,
    CoverageInstrumentation? Instrumentation = null, CoverageOperationLimits? Limits = null)
{
    /// <summary>Fan-in contract version; defaults to the legacy version 2 contract.</summary>
    /// <remarks>Version 2 canonical policy bytes omit this property to preserve their frozen encoding.</remarks>
    public int ContractVersion { get; init; } = 2;

    /// <summary>Closed report-admission profile required by contract version 3; null for version 2.</summary>
    /// <remarks>The initial supported value is <c>coverlet-collector-cobertura-v1</c>.</remarks>
    public string? ReportProfileId { get; init; }
}

/// <summary>Applicability state for one changed-line or changed-branch metric.</summary>
/// <remarks>
/// Its JSON wire strings are exactly <c>measured</c>, <c>notApplicable</c>, and <c>unverified</c>. Measured and
/// not-applicable metrics use reason <c>reported-points</c> and <c>no-reported-patch-points</c>, respectively;
/// unverified metrics use <c>invalid-evidence</c>. Not-applicable describes admitted report measurements only and
/// does not establish that changed source contains no executable code.
/// </remarks>
public enum CoveragePatchApplicabilityOutcome
{
    /// <summary>At least one changed point was reported and its metric was recomputed.</summary>
    Measured,
    /// <summary>Admitted reports contain no changed points for this metric; this is not a source-completeness claim.</summary>
    NotApplicable,
    /// <summary>Evidence prevented a trustworthy metric calculation.</summary>
    Unverified
}

/// <summary>Recomputed applicability and counts for one patch metric.</summary>
/// <remarks>
/// All six fields are required on the wire, including explicit nulls. Count fields are nullable nonnegative Int32
/// values and percent is a nullable decimal from 0 through 100. Measured results require measurable greater than zero,
/// covered no greater than measurable, and a percentage rounded to four decimal places with AwayFromZero applied to
/// <c>100 * covered / measurable</c>. Not-applicable results require measurable and covered both zero and a null percentage.
/// Unverified results require null changed-lines, measurable, covered, and percent. ChangedLines is the number of
/// diff-added lines shared by the line and branch results; branch measurement counts can exceed it.
/// </remarks>
public sealed record CoveragePatchMetricResult(CoveragePatchApplicabilityOutcome Outcome, string Reason,
    int? ChangedLines, int? Measurable, int? Covered, decimal? Percent);

/// <summary>Required line and branch applicability results for one report stage.</summary>
/// <remarks>Both result properties are required in the v3 wire object.</remarks>
public sealed record CoveragePatchMetricSet(CoveragePatchMetricResult Line, CoveragePatchMetricResult Branch);

/// <summary>Primary and composed patch applicability results from a completed v3 metric evaluation.</summary>
/// <remarks>
/// Both result sets are required in the v3 wire object. A pre-evaluation failure has no applicability object; an
/// unverified result cannot establish acceptance.
/// </remarks>
public sealed record CoveragePatchApplicability(CoveragePatchMetricSet Primary, CoveragePatchMetricSet Composed);

/// <summary>Capture successful trusted producer artifacts, native collector facts, or portable v2 evidence into local custody.</summary>
/// <remarks>
/// Supply exactly one input form: a normalized primary report and its successful timings; a single-project native
/// capture with <see cref="RawCoveragePath"/>, optional <see cref="CoveragePath"/>, and no timings; an indexed native
/// capture with <see cref="RawCoveragePaths"/> and <see cref="TimingsPath"/>, but no <see cref="CoveragePath"/>; or
/// a portable-v2 import with no report or timing selectors. Native measurements are derived from original collector
/// class and method facts. A supplied <see cref="CoveragePath"/> on a single-project native capture is bounded
/// compatibility input, not the measurement source. Raw collector root summaries remain in the byte-exact originals,
/// but canonical native root totals derive from serialized class-line points; method points are retained for patch measurement. The primary aggregate gate remains
/// based on the exact original normalized primary root counters. Every native capture is independently checked
/// against its retained original collector report or reports before publication.
/// </remarks>
/// <param name="Policy">Trusted complete policy.</param>
/// <param name="RepositoryRoot">Matching clean local Git source.</param>
/// <param name="OutputDirectory">Fresh owned capture destination, unless replacement is explicitly requested.</param>
/// <param name="ProducerId">primary, or an exact supplementary producer ID.</param>
/// <param name="CoveragePath">Required normalized Cobertura report for a primary capture; optional bounded-XML compatibility input only for single-project native capture. Absent for indexed native capture and import. Never a native measurement source.</param>
/// <param name="TimingsPath">Required successful timings for primary capture and complete successful inventory for indexed native capture. Absent for single-project native capture and import.</param>
/// <param name="RawCoveragePath">Required original collector bytes for a native producer declaring exactly one test project in the single-report form. Absent for primary, indexed native capture, and import.</param>
/// <param name="ImportDirectory">Explicit portable capture to validate/materialize. Mutually exclusive with <paramref name="CoveragePath"/>, <paramref name="TimingsPath"/>, <paramref name="RawCoveragePath"/>, and <paramref name="RawCoveragePaths"/>.</param>
/// <param name="OriginalSourceRoot">Exact original root mapping for imported evidence.</param>
/// <param name="OutputMode">Fresh-only default.</param>
/// <param name="RawCoveragePaths">Indexed native original collector reports keyed by the exact declared project identities. Requires a complete <paramref name="TimingsPath"/>, supports one or more projects, and excludes <paramref name="CoveragePath"/> and <paramref name="RawCoveragePath"/>. Collection always uses this indexed form, including for one project.</param>
public sealed record CoverageCaptureRequest(CoverageFanInPolicy Policy, string RepositoryRoot,
    string OutputDirectory, string ProducerId, string? CoveragePath = null, string? TimingsPath = null,
    string? RawCoveragePath = null, string? ImportDirectory = null, string? OriginalSourceRoot = null,
    CoverageOutputMode OutputMode = CoverageOutputMode.Fresh,
    IReadOnlyDictionary<string, string>? RawCoveragePaths = null);
/// <summary>Collect the complete explicit inventory or declared native filter through the existing supervised engine.</summary>
/// <remarks>Observed collection rebuilds before testing. <c>NoBuild</c> must be false.
/// Declared test projects must be regular tracked files in the pinned Git source, including pinned submodules.
/// Ignored files are rejected except conventional <c>bin/</c> and <c>obj/</c> directories beside tracked .NET projects with no ancestor project directory.
/// <c>NoRestore</c> may be true when trusted restore has already completed; this does not skip the rebuild.</remarks>
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
/// <remarks>
/// When returned by an operation, context and trace collections are copied into frozen snapshots before publication.
/// These properties default to null for legacy results. Expected failures have no trace. Unexpected traces contain
/// only sanitized eligible frames, never paths, arguments, messages, or arbitrary exception text.
/// </remarks>
public sealed record CoverageDiagnostic(string Code, string Stage, string Field, string Recovery,
    string Documentation = "https://appsurface.dev/guides/coverage-fan-in#diagnostics")
{
    /// <summary>Optional stable cause from the closed v3 diagnostic vocabulary.</summary>
    /// <remarks>
    /// Supported causes are unsupported-version, unsupported-profile, invalid-policy, missing-diff, malformed-diff,
    /// truncated-diff, unsupported-diff, merge-base-unavailable, identity-mismatch, report-invalid,
    /// reported-record-loss, applicability-mismatch, limit-exceeded, threshold-failed, cancelled,
    /// deadline-exceeded, unexpected-failure, delivery-failed, and unsupported-git-version. Other strings are not
    /// part of the v3 contract.
    /// </remarks>
    public string? Cause { get; init; }

    /// <summary>Optional bounded diagnostic context, frozen when the operation result is published.</summary>
    /// <remarks>Keys are limited to contractVersion, reportProfileId, diffPolicyId, baseCommit, headCommit,
    /// exceptionType, gitExitCode, and gitVersion. Values must not expose arbitrary input, paths, environment data,
    /// stderr, or exception messages.</remarks>
    public IReadOnlyDictionary<string, string>? Context { get; init; }

    /// <summary>Optional sanitized trace for an unexpected failure, frozen when the operation result is published.</summary>
    /// <remarks>
    /// Expected failures use null. A trace has at most 8 entries, each at most 256 characters. Throw frames are
    /// limited to the <c>ForgeTrust.AppSurface.Coverage</c>, <c>ForgeTrust.AppSurface.Evidence.Coverage</c>, and
    /// <c>ForgeTrust.AppSurface.Cli</c> namespace families, matched at namespace-component boundaries. If no eligible
    /// throw frame remains, one fixed labeled handled-at entry may identify the handler; the exception type is in
    /// context and the operation ID remains on <see cref="CoverageFanInResult.OperationId"/>. Paths, arguments, and
    /// messages are removed from every entry.
    /// </remarks>
    public IReadOnlyList<string>? Trace { get; init; }
}
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
        CoverageEvaluationIntent intent = CoverageEvaluationIntent.CurrentTrustedRun, bool primaryImported = false,
        CoveragePatchApplicability? patchApplicability = null, CoverageFanInAccounting? accounting = null)
    {
        Operation = operation; Outcome = outcome; OperationId = operationId; PolicyDigest = policyDigest;
        Effects = effects; Artifacts = artifacts; Diagnostics = diagnostics; Accepted = accepted;
        Verified = verified; PatchOutcome = patchOutcome; Observation = observation; EvaluationIntent = intent;
        PrimaryImported = primaryImported; PatchApplicability = patchApplicability; Accounting = accounting;
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
    /// <summary>V3 primary and composed patch metrics, or null when no applicability evaluation was produced.</summary>
    /// <remarks>Null for v2, capture/compose, aggregate-only operations, and failures before metric evaluation.</remarks>
    public CoveragePatchApplicability? PatchApplicability { get; }
    /// <summary>Internal current-invocation logical counters for installed proof, never a legacy wire field or public API.</summary>
    internal CoverageFanInAccounting? Accounting { get; }
}

/// <summary>Internal v3 logical-admission snapshot; it does not measure process memory or decoded heap.</summary>
internal sealed record CoverageFanInAccounting(long UsedBytes, long LimitBytes, long XmlBytes = 0, long JsonBytes = 0,
    long LineRecords = 0, long BranchRecords = 0, long StagedBytes = 0, long StagedFiles = 0);
