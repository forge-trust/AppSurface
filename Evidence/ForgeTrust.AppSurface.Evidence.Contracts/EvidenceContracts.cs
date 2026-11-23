using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForgeTrust.AppSurface.Evidence.Contracts;

/// <summary>
/// Defines the scope selected by a versioned evidence policy.
/// </summary>
public enum EvidenceProfileScope
{
    /// <summary>Evidence is proportional to a selected pull-request risk profile.</summary>
    Targeted,

    /// <summary>Evidence is the policy-designated release profile.</summary>
    Release,
}

/// <summary>
/// Describes whether a resolved evidence execution satisfied its declared contract.
/// </summary>
public enum EvidenceExecutionVerdict
{
    /// <summary>The selected resources, producers, obligations, artifacts, and cleanup completed.</summary>
    Passed,

    /// <summary>Required evidence did not complete, but the contract itself remained valid.</summary>
    Incomplete,

    /// <summary>The policy, plan, artifact, producer result, or trusted input was invalid.</summary>
    Invalid,
}

/// <summary>
/// Defines the machine-readable claim that a manifest may make.
/// </summary>
public enum EvidenceClaimKind
{
    /// <summary>No downstream gate may consume the result as an evidence claim.</summary>
    None,

    /// <summary>A targeted profile closed every selected obligation.</summary>
    TargetedComplete,

    /// <summary>A release profile closed every selected obligation and met release eligibility.</summary>
    ReleaseComplete,

    /// <summary>An explicit policy rule selected no obligations or producers.</summary>
    NoEvidenceRequired,

    /// <summary>The result is informative only and cannot satisfy a gate.</summary>
    ObservationOnly,

    /// <summary>A complete local run reused one or more verified prior results; it is informational only.</summary>
    LocalReusedComplete,
}

/// <summary>
/// Defines the allowed downstream consumers of an evidence claim.
/// </summary>
public enum EvidenceClaimEligibility
{
    /// <summary>The claim cannot be consumed by a gate.</summary>
    None,

    /// <summary>The claim may satisfy a pull-request gate.</summary>
    PullRequestGate,

    /// <summary>The claim may satisfy a release gate.</summary>
    ReleaseGate,

    /// <summary>The claim is useful to people and automation but is not gate eligible.</summary>
    Informational,
}

/// <summary>
/// Describes the constrained status of the CI execution envelope bound to an evidence run.
/// </summary>
public enum EvidenceEnvelopeStatus
{
    /// <summary>The selected targeted profile did not require a trusted envelope.</summary>
    NotRequired,

    /// <summary>A registered verifier accepted structural CI inputs but no independent attestation exists.</summary>
    ValidatedNotAttested,

    /// <summary>The required trusted envelope was unavailable.</summary>
    Unavailable,

    /// <summary>The supplied envelope was rejected by the registered verifier.</summary>
    Invalid,
}

/// <summary>
/// Defines a terminal producer outcome.
/// </summary>
public enum EvidenceProducerOutcome
{
    /// <summary>The producer completed and returned validated assertions and artifacts.</summary>
    Passed,

    /// <summary>The producer observed a product or test assertion failure.</summary>
    Failed,

    /// <summary>The producer observed nondeterministic execution that cannot be promoted to success.</summary>
    Flaky,

    /// <summary>The producer exceeded its declared deadline.</summary>
    TimedOut,

    /// <summary>A required dependency or capability was unavailable.</summary>
    Unavailable,

    /// <summary>The caller or CI cancelled the producer.</summary>
    Cancelled,

    /// <summary>The producer violated a declaration, artifact, or assertion contract.</summary>
    Invalid,

    /// <summary>The producer was not selected by the resolved profile.</summary>
    SkippedNotRequired,

    /// <summary>The result was restored from a locally verified producer receipt.</summary>
    Reused,
}

/// <summary>
/// Defines the terminal readiness outcome for one declared evidence resource.
/// </summary>
public enum EvidenceResourceOutcome
{
    /// <summary>The declared resource reached the readiness condition required by its dependent producers.</summary>
    Ready,

    /// <summary>The resource was unavailable before the declared readiness deadline.</summary>
    Unavailable,

    /// <summary>The resource did not become ready before its declared deadline.</summary>
    TimedOut,

    /// <summary>The caller cancelled the EvidenceHost before the resource became ready.</summary>
    Cancelled,

    /// <summary>The readiness result violated the declared evidence contract.</summary>
    Invalid,

    /// <summary>No fresh producer required this resource to execute.</summary>
    NotRequiredForExecution,
}

/// <summary>
/// Describes a normalized changed path used for deterministic policy resolution.
/// </summary>
/// <param name="Path">A repository-relative path with forward-slash separators.</param>
/// <param name="Kind">The source-control change kind, such as added, modified, deleted, or renamed.</param>
/// <param name="PreviousPath">The previous normalized path for a rename; otherwise <see langword="null"/>.</param>
public sealed record NormalizedDiffPath(string Path, string Kind = "modified", string? PreviousPath = null);

/// <summary>
/// Declares one artifact slot that a producer is allowed to return.
/// </summary>
/// <param name="LogicalName">Stable logical artifact name.</param>
/// <param name="RelativeRoot">Normalized artifact-root-relative directory permitted for this slot.</param>
/// <param name="MediaType">Expected media type.</param>
/// <param name="Required">Whether a missing artifact invalidates the producer result.</param>
/// <param name="MaximumBytes">Maximum aggregate byte length across all files in this slot.</param>
public sealed record EvidenceArtifactSlot(string LogicalName, string RelativeRoot, string MediaType, bool Required, long MaximumBytes)
{
    /// <summary>Gets the optional maximum number of artifacts in this slot; omission preserves the one-file limit.</summary>
    /// <remarks>Valid values are 1 through 128. MaximumBytes bounds the aggregate slot bytes; the producer-wide 256 MiB limit also applies. Explicit values require contract 1.1.</remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaximumFiles { get; init; }
}

/// <summary>Declares the repository-relative files that completely influence a reusable producer result.</summary>
/// <param name="Include">Nonempty repository-relative include patterns.</param>
/// <param name="Exclude">Repository-relative patterns removed from the included set; use an empty list for none.</param>
/// <remarks>Use forward slashes, * within a segment, and ** as a whole segment. Planning validates grammar without filesystem access;
/// preparation rejects an empty matched file set. This declaration is the producer owner's completeness promise, not a sandbox.</remarks>
public sealed record EvidenceProducerInputs(IReadOnlyList<string> Include, IReadOnlyList<string> Exclude);

/// <summary>
/// Captures bounded metadata for one declared artifact without serializing its raw content.
/// </summary>
/// <param name="LogicalName">Declared artifact slot identifier.</param>
/// <param name="RelativePath">Normalized path beneath the evidence artifact root.</param>
/// <param name="MediaType">Declared media type.</param>
/// <param name="LengthBytes">Written artifact length.</param>
/// <param name="Sha256">Lower-case SHA-256 digest of the written bytes.</param>
public sealed record EvidenceArtifactResult(
    string LogicalName,
    string RelativePath,
    string MediaType,
    long LengthBytes,
    string Sha256);

/// <summary>
/// Binds a coverage producer's declared assertion to the existing AppSurface coverage gate thresholds.
/// </summary>
/// <param name="MinLinePercent">Minimum overall line coverage percentage.</param>
/// <param name="MinBranchPercent">Minimum overall branch coverage percentage.</param>
/// <param name="MinPatchLinePercent">Optional minimum changed-line coverage percentage.</param>
/// <param name="MinPatchBranchPercent">Optional minimum changed-branch coverage percentage.</param>
/// <param name="PatchLineMode">Changed-line calculation mode: <c>measurable</c> or <c>codecov</c>.</param>
/// <param name="TolerancePercent">Configured coverage gate tolerance percentage.</param>
public sealed record EvidenceCoverageGateRequirements(
    decimal MinLinePercent,
    decimal MinBranchPercent,
    decimal? MinPatchLinePercent = null,
    decimal? MinPatchBranchPercent = null,
    string PatchLineMode = "measurable",
    decimal TolerancePercent = 0.5m);

/// <summary>
/// Declares a producer selected by an evidence profile.
/// </summary>
/// <param name="Id">Stable producer identifier.</param>
/// <param name="Kind">Registered producer kind, such as <c>coverage</c> or <c>browser-e2e</c>.</param>
/// <param name="Version">Producer implementation semantic version.</param>
/// <param name="RequiredResources">Resource identifiers that must be ready before execution.</param>
/// <param name="AssertionIds">Assertion identifiers that this producer may close.</param>
/// <param name="ArtifactSlots">Closed artifact declarations for this producer.</param>
/// <param name="TimeoutSeconds">Positive producer deadline in seconds.</param>
/// <param name="CoverageGate">Explicit coverage gate requirements when this producer closes a coverage assertion.</param>
public sealed record EvidenceProducerDeclaration(
    string Id,
    string Kind,
    string Version,
    IReadOnlyList<string> RequiredResources,
    IReadOnlyList<string> AssertionIds,
    IReadOnlyList<EvidenceArtifactSlot> ArtifactSlots,
    int TimeoutSeconds,
    EvidenceCoverageGateRequirements? CoverageGate = null)
{
    /// <summary>Gets the optional built-in coverage execution envelope, defaulting to Debug, no managed test results, and serial execution.</summary>
    /// <remarks>Only coverage producers interpret this value. It does not change coverage filters, gates, or source scope.</remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EvidenceCoverageExecutionOptions? CoverageExecution { get; init; }

    /// <summary>Gets the optional coverage artifact retention mode, managed by default or full to retain raw duplicate XML.</summary>
    /// <remarks>
    /// Only coverage producers interpret this value. Exactly <c>managed</c> and <c>full</c> are supported;
    /// neither changes collection or coverage gates. The existing positional constructor remains unchanged.
    /// See the <c>coverageArtifactRetention</c> reference in the coverage storage guide.
    /// </remarks>
    public string CoverageArtifactRetention { get; init; } = "managed";

    /// <summary>Gets the optional complete file-input declaration required for local result reuse.</summary>
    /// <remarks>Omission leaves the producer ineligible for reuse. Use only when every file affecting execution is declared;
    /// an explicit invocation opt-in is also required. See <see cref="EvidenceProducerInputs"/> for pattern constraints.</remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EvidenceProducerInputs? Inputs { get; init; }
}

/// <summary>
/// Declares an evidence resource required by a profile.
/// </summary>
/// <param name="Id">Stable resource identifier.</param>
/// <param name="Readiness">Declared readiness mode, such as <c>aspire_health</c> or <c>completion</c>.</param>
/// <param name="DeadlineSeconds">Positive readiness deadline in seconds.</param>
/// <param name="Requires">Resource identifiers that must be ready first.</param>
public sealed record EvidenceResourceDeclaration(
    string Id,
    string Readiness,
    int DeadlineSeconds,
    IReadOnlyList<string> Requires);

/// <summary>
/// Captures bounded terminal readiness metadata for one declared resource.
/// </summary>
/// <param name="ResourceId">Declared resource identifier.</param>
/// <param name="Outcome">Terminal readiness outcome.</param>
/// <param name="ElapsedMilliseconds">Time spent waiting for the resource.</param>
/// <param name="Diagnostic">Secret-safe readiness diagnostic.</param>
public sealed record EvidenceResourceResult(
    string ResourceId,
    EvidenceResourceOutcome Outcome,
    long ElapsedMilliseconds,
    string? Diagnostic = null);

/// <summary>
/// Defines a changed-risk requirement that must be closed before a claim is complete.
/// </summary>
/// <param name="Id">Stable obligation identifier.</param>
/// <param name="RiskClass">Consumer-defined risk class.</param>
/// <param name="Rationale">Human explanation for why the obligation was selected.</param>
/// <param name="RequiredProducerIds">Every producer that must pass for closure.</param>
/// <param name="RequiredAssertionId">The assertion that confirms closure.</param>
public sealed record EvidenceObligation(
    string Id,
    string RiskClass,
    string Rationale,
    IReadOnlyList<string> RequiredProducerIds,
    string RequiredAssertionId);

/// <summary>
/// Defines a closed profile of resources, producers, and obligations.
/// </summary>
/// <param name="Id">Stable profile identifier.</param>
/// <param name="Scope">Targeted or release evidence breadth.</param>
/// <param name="Resources">Resources required by the profile.</param>
/// <param name="Producers">Registered producers selected by the profile.</param>
/// <param name="Obligations">Risk obligations selected by the profile.</param>
public sealed record EvidenceProfile(
    string Id,
    EvidenceProfileScope Scope,
    IReadOnlyList<EvidenceResourceDeclaration> Resources,
    IReadOnlyList<EvidenceProducerDeclaration> Producers,
    IReadOnlyList<EvidenceObligation> Obligations);

/// <summary>
/// Defines the shared v1 limits for one EvidenceHost profile declaration.
/// </summary>
public static class EvidenceProfileLimits
{
    /// <summary>Maximum resources a v1 profile may declare.</summary>
    public const int MaximumResources = 16;

    /// <summary>Maximum producers a v1 profile may declare.</summary>
    public const int MaximumProducers = 32;

    /// <summary>Maximum obligations a v1 profile may declare.</summary>
    public const int MaximumObligations = 128;
}

/// <summary>
/// Maps exact paths or segment globs to a single named evidence profile.
/// </summary>
/// <param name="Id">Stable rule identifier.</param>
/// <param name="Pattern">Exact repository path or segment glob.</param>
/// <param name="ProfileId">Profile selected when the rule matches.</param>
/// <param name="Precedence">Explicit tie breaker for equal-specificity patterns.</param>
public sealed record EvidencePolicyRule(string Id, string Pattern, string ProfileId, int Precedence = 0);

/// <summary>
/// Represents a versioned, checked-in evidence policy.
/// </summary>
/// <param name="Id">Stable policy identifier.</param>
/// <param name="Version">Consumer-controlled policy version.</param>
/// <param name="ConservativeProfileId">Profile selected when a changed path has no direct match.</param>
/// <param name="Profiles">Closed set of selectable profiles.</param>
/// <param name="Rules">Path-selection rules.</param>
public sealed record EvidencePolicy(
    string Id,
    string Version,
    string ConservativeProfileId,
    IReadOnlyList<EvidenceProfile> Profiles,
    IReadOnlyList<EvidencePolicyRule> Rules)
{
    /// <summary>Combines distinct profiles selected by matched paths into one closed profile.</summary>
    /// <remarks>Defaults to <see langword="false"/>, preserving the conservative fallback for multi-profile selections and omitting the property from canonical JSON.</remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool CombineProfiles { get; init; }
}

/// <summary>
/// Captures the immutable result of policy resolution before resource or producer execution.
/// </summary>
/// <param name="ContractVersion">Evidence contract version.</param>
/// <param name="PolicyId">Resolved policy identifier.</param>
/// <param name="PolicyDigest">SHA-256 digest of canonical policy bytes.</param>
/// <param name="DiffDigest">SHA-256 digest of canonical normalized diff bytes.</param>
/// <param name="Profile">Selected closed profile.</param>
/// <param name="ChangedPaths">Normalized paths used during selection.</param>
/// <param name="MatchedRuleIds">Rules that explain selection.</param>
/// <param name="PlanDigest">SHA-256 digest of canonical plan bytes excluding this digest field.</param>
/// <param name="PolicySnapshot">Canonical checked-in policy snapshot used for resolution and later verification.</param>
public sealed record EvidencePlan(
    string ContractVersion,
    string PolicyId,
    string PolicyDigest,
    string DiffDigest,
    EvidenceProfile Profile,
    IReadOnlyList<NormalizedDiffPath> ChangedPaths,
    IReadOnlyList<string> MatchedRuleIds,
    string PlanDigest,
    EvidencePolicy? PolicySnapshot = null)
{
    /// <summary>Lowercase SHA-256 of the exact unified patch bytes captured at planning, when available.</summary>
    /// <remarks><see cref="DiffDigest"/> still identifies the normalized path list; a path-only plan cannot bind patch acceptance.</remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PatchSha256 { get; init; }
}

/// <summary>
/// Captures a bounded producer result returned to the EvidenceHost.
/// </summary>
/// <param name="ProducerId">Producer that emitted the result.</param>
/// <param name="Outcome">Terminal producer outcome.</param>
/// <param name="SatisfiedAssertionIds">Assertion identifiers returned by the producer.</param>
/// <param name="Diagnostic">Secret-safe human diagnostic; never include raw logs or values.</param>
/// <param name="Artifacts">Bounded metadata for artifacts written through the declared artifact writer.</param>
/// <param name="ElapsedMilliseconds">Measured fresh execution time; must be zero for Reused. Historical duration belongs in the origin receipt.</param>
public sealed record EvidenceProducerResult(
    string ProducerId,
    EvidenceProducerOutcome Outcome,
    IReadOnlyList<string> SatisfiedAssertionIds,
    string? Diagnostic = null,
    IReadOnlyList<EvidenceArtifactResult>? Artifacts = null,
    long ElapsedMilliseconds = 0)
{
    /// <summary>Gets the lower-case SHA-256 producer key authorizing local restoration; absent for fresh execution.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProducerKey { get; init; }

    /// <summary>Gets the lower-case digest of the original manifest containing this result.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OriginManifestDigest { get; init; }

    /// <summary>Gets the nondefault original completion time of the reused result; absent for fresh execution.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? OriginCompletedAt { get; init; }
}

/// <summary>
/// Captures bounded, secret-free lifecycle timing and cleanup status for one evidence execution.
/// </summary>
/// <param name="PlanningMilliseconds">Time spent resolving the policy and plan.</param>
/// <param name="ResourceReadinessMilliseconds">Cumulative time spent awaiting declared resources.</param>
/// <param name="ProducerMilliseconds">Cumulative producer execution time.</param>
/// <param name="CleanupMilliseconds">Time spent disposing evidence-owned registrations.</param>
/// <param name="TotalMilliseconds">Total measured execution duration.</param>
/// <param name="CleanupCompleted">Whether owned cleanup completed without a terminal failure.</param>
/// <param name="CleanupDiagnostic">Secret-safe cleanup diagnostic when cleanup did not complete.</param>
public sealed record EvidenceExecutionMetrics(
    long PlanningMilliseconds = 0,
    long ResourceReadinessMilliseconds = 0,
    long ProducerMilliseconds = 0,
    long CleanupMilliseconds = 0,
    long TotalMilliseconds = 0,
    bool CleanupCompleted = true,
    string? CleanupDiagnostic = null)
{
    /// <summary>Gets elapsed preparation time for this invocation, in milliseconds; zero is omitted from JSON.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long PreparationMilliseconds { get; init; }

    /// <summary>Gets time spent waiting for local receipts in this invocation, in milliseconds; zero is omitted from JSON.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long ReceiptWaitMilliseconds { get; init; }

    /// <summary>Gets time spent restoring artifacts in this invocation, in milliseconds; zero is omitted from JSON.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long ArtifactRestoreMilliseconds { get; init; }
}

/// <summary>
/// Captures the immutable claim and execution result of an evidence run.
/// </summary>
/// <param name="ContractVersion">Evidence contract version.</param>
/// <param name="PlanDigest">Digest of the resolved plan.</param>
/// <param name="ExecutionVerdict">Whether declared execution requirements completed.</param>
/// <param name="ClaimKind">Claim emitted by this run.</param>
/// <param name="Eligibility">Downstream consumers permitted to use the claim.</param>
/// <param name="EnvelopeStatus">Constrained CI-envelope status; it is not an implied runtime sandbox attestation.</param>
/// <param name="ResourceResults">Bounded readiness outcomes for selected resources.</param>
/// <param name="SelectedObligationIds">Obligations selected by the plan.</param>
/// <param name="ClosedObligationIds">Obligations closed by returned assertions.</param>
/// <param name="UnmediatedObligationIds">Selected obligations that remain open.</param>
/// <param name="ProducerResults">Bounded producer terminal results.</param>
/// <param name="Metrics">Secret-free lifecycle timing and cleanup state.</param>
/// <param name="ManifestDigest">SHA-256 digest of canonical manifest bytes excluding this digest field.</param>
public sealed record EvidenceManifest(
    string ContractVersion,
    string PlanDigest,
    EvidenceExecutionVerdict ExecutionVerdict,
    EvidenceClaimKind ClaimKind,
    EvidenceClaimEligibility Eligibility,
    EvidenceEnvelopeStatus EnvelopeStatus,
    IReadOnlyList<EvidenceResourceResult> ResourceResults,
    IReadOnlyList<string> SelectedObligationIds,
    IReadOnlyList<string> ClosedObligationIds,
    IReadOnlyList<string> UnmediatedObligationIds,
    IReadOnlyList<EvidenceProducerResult> ProducerResults,
    EvidenceExecutionMetrics Metrics,
    string ManifestDigest)
{
    /// <summary>Gets the number of restored producer results, derived from <see cref="ProducerResults"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ReuseCount => ProducerResults.Count(static result => result.Outcome == EvidenceProducerOutcome.Reused);
}

/// <summary>
/// Supplies the immutable execution context exposed to a registered evidence producer.
/// </summary>
/// <param name="Plan">Resolved evidence plan.</param>
/// <param name="Producer">The producer declaration being executed.</param>
/// <param name="TimeProvider">Clock seam used for deadlines and deterministic tests.</param>
/// <param name="Artifacts">Bounded writer for declared artifact slots. It exposes no raw host path.</param>
public sealed record EvidenceProducerContext(
    EvidencePlan Plan,
    EvidenceProducerDeclaration Producer,
    TimeProvider TimeProvider,
    EvidenceArtifactWriter? Artifacts = null)
{
    /// <summary>Gets the current invocation ownership ID when executing through a prepared host.</summary>
    /// <remarks>This opaque ID is not key material or evidence provenance and is never serialized with the context.</remarks>
    [JsonIgnore]
    public string? ExecutionId { get; init; }

    /// <summary>Gets the invocation-private cooperative child channel to propagate to owned child processes.</summary>
    /// <remarks>
    /// Pass this capability to EvidenceOwnedChild's explicit-environment overload or copy it into an owned child
    /// process environment. Never mutate the process-global environment or persist this opaque channel in artifacts,
    /// key material or logs. Null means this producer has no cooperative channel. Key-material callbacks receive no
    /// execution capability. Version 1 supplies this channel only to eligible built-in coverage workloads; custom
    /// producers receive null and must own and join their children themselves. This is cooperative enrollment,
    /// not OS containment of unregistered children.
    /// </remarks>
    [JsonIgnore]
    public IReadOnlyDictionary<string, string>? OwnedChildEnvironment { get; init; }
}

/// <summary>
/// Represents an explicitly registered typed evidence producer.
/// </summary>
public interface IEvidenceProducer
{
    /// <summary>
    /// Gets the stable declaration identifier handled by this producer.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Produces bounded assertions and diagnostics for one resolved declaration.
    /// </summary>
    /// <param name="context">Immutable plan and producer context.</param>
    /// <param name="cancellationToken">Cancellation requested by the EvidenceHost lifecycle.</param>
    /// <returns>A terminal producer result.</returns>
    ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken);
}

/// <summary>Supplies additional local execution identity before any evidence resource is started.</summary>
/// <remarks>Implement only for execution inputs that cannot be represented by declared files. Callbacks must be local,
/// side-effect-free, cancellation-aware, and must not start resources. The host owns and joins the actual callback task;
/// a timeout cannot forcibly terminate in-process code or authorize concurrent fresh work.</remarks>
public interface IEvidenceProducerKeyMaterial
{
    /// <summary>Gets bounded key material for the declared producer.</summary>
    /// <param name="context">Immutable plan, producer declaration, and clock; it exposes no artifacts or ready resources.</param>
    /// <param name="cancellationToken">Cancellation requested by preparation.</param>
    /// <returns>Ordinal key/value material used only to derive the producer key: at most 64 entries, keys up to 128 characters,
    /// values up to 4 KiB UTF-8 each, and 256 KiB total. Do not return secrets.</returns>
    ValueTask<IReadOnlyDictionary<string, string>> GetKeyMaterialAsync(EvidenceProducerKeyMaterialContext context, CancellationToken cancellationToken);
}

/// <summary>Provides the restricted context for producer key-material callbacks.</summary>
/// <param name="Plan">Frozen resolved plan.</param>
/// <param name="Producer">Frozen producer declaration.</param>
/// <param name="TimeProvider">Clock seam used by the caller.</param>
public sealed record EvidenceProducerKeyMaterialContext(EvidencePlan Plan, EvidenceProducerDeclaration Producer, TimeProvider TimeProvider);

/// <summary>
/// Writes only declared producer artifacts beneath a consumer-selected evidence root.
/// </summary>
public sealed class EvidenceArtifactWriter
{
    /// <summary>Maximum total artifact bytes accepted by one v1 producer declaration.</summary>
    public const long MaximumTotalArtifactBytes = 256L * 1024 * 1024;

    private readonly EvidenceProducerDeclaration _producer;
    private readonly string _rootPath;
    private readonly bool _requireUnlinkedPaths;
    private readonly object _sync = new();
    private readonly Dictionary<string, EvidenceArtifactResult?> _artifacts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _destinations;
    private readonly Dictionary<string, long> _slotBytes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _slotCounts = new(StringComparer.Ordinal);
    private long _totalBytes;

    /// <summary>
    /// Initializes a bounded artifact writer for one producer declaration.
    /// </summary>
    /// <param name="producer">Closed producer declaration that owns allowed artifact slots.</param>
    /// <param name="rootPath">Controlled evidence artifact root.</param>
    public EvidenceArtifactWriter(EvidenceProducerDeclaration producer, string rootPath)
        : this(producer, rootPath, requireUnlinkedPaths: false) { }

    /// <summary>Creates a writer that can enforce unlinked receipt custody even for single-file slots.</summary>
    /// <param name="producer">Closed declaration that owns the slots.</param>
    /// <param name="rootPath">Controlled evidence artifact root.</param>
    /// <param name="requireUnlinkedPaths">Whether every slot requires the unlinked path preflight.</param>
    /// <remarks>Use this for reuse custody. The caller still owns the root for the entire write and verification.</remarks>
    internal EvidenceArtifactWriter(EvidenceProducerDeclaration producer, string rootPath, bool requireUnlinkedPaths)
    {
        _producer = producer ?? throw new ArgumentNullException(nameof(producer));
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        _requireUnlinkedPaths = requireUnlinkedPaths;
        _destinations = new(producer.ArtifactSlots.Any(static slot => slot.MaximumFiles is not null)
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    }

    /// <summary>Gets completed artifact metadata emitted through this writer in ordinal logical-name order.</summary>
    public IReadOnlyList<EvidenceArtifactResult> WrittenArtifacts
    {
        get
        {
            lock (_sync)
            {
                return _artifacts.Values
                    .OfType<EvidenceArtifactResult>()
                    .OrderBy(static artifact => artifact.LogicalName, StringComparer.Ordinal)
                    .ThenBy(static artifact => artifact.RelativePath, StringComparer.Ordinal)
                    .ToArray();
            }
        }
    }

    /// <summary>Gets the controlled root for first-party adoption of already delivered, verified reports.</summary>
    /// <remarks>This private capability does not grant filesystem or receipt authority.</remarks>
    internal string RootPath => _rootPath;

    /// <summary>Records one already delivered artifact after the first-party adapter verifies its exact bytes.</summary>
    /// <param name="artifact">Complete metadata bound to the same controlled root as this writer.</param>
    /// <remarks>The caller must validate the complete file set through no-follow readers before recording it.
    /// This method enforces the same destination, count and byte reservations as writing, changes no files,
    /// and does not waive later writer verification, required-slot validation or receipt admission.</remarks>
    /// <exception cref="ArgumentException">The metadata does not match a declared slot or canonical path.</exception>
    /// <exception cref="InvalidOperationException">The declaration's existing reservation limits are exceeded.</exception>
    internal void RecordVerifiedExistingArtifact(EvidenceArtifactResult artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var slot = _producer.ArtifactSlots.SingleOrDefault(slot => slot.LogicalName == artifact.LogicalName)
            ?? throw new ArgumentException("The verified artifact slot is not declared.", nameof(artifact));
        var normalized = EvidenceArtifactValidation.NormalizeRelativePath(artifact.RelativePath);
        EvidenceArtifactValidation.ValidatePathForSlot(slot, artifact.RelativePath);
        if (normalized != artifact.RelativePath || artifact.MediaType != slot.MediaType || artifact.LengthBytes < 0
            || artifact.Sha256.Length != 64 || artifact.Sha256.Any(static value => !char.IsAsciiHexDigitLower(value)))
            throw new ArgumentException("The verified artifact metadata is invalid.", nameof(artifact));
        ReserveArtifact(artifact.LogicalName, normalized, artifact.LengthBytes, slot.MaximumBytes);
        lock (_sync) _artifacts[normalized] = artifact;
    }

    /// <summary>
    /// Writes one declared artifact and returns only its bounded metadata.
    /// </summary>
    /// <param name="logicalName">Declared artifact slot identifier.</param>
    /// <param name="relativePath">Normalized artifact-root-relative destination path.</param>
    /// <param name="contents">Artifact bytes to write.</param>
    /// <param name="cancellationToken">Cancellation requested by the EvidenceHost lifecycle.</param>
    /// <returns>Hash and metadata for the written artifact.</returns>
    public async ValueTask<EvidenceArtifactResult> WriteAsync(
        string logicalName,
        string relativePath,
        ReadOnlyMemory<byte> contents,
        CancellationToken cancellationToken = default)
    {
        if (_producer.ArtifactSlots.FirstOrDefault(slot => string.Equals(slot.LogicalName, logicalName, StringComparison.Ordinal)) is not { } slot)
        {
            throw new InvalidOperationException($"Artifact '{logicalName}' is not declared by producer '{_producer.Id}'.");
        }

        var normalizedPath = EvidenceArtifactValidation.NormalizeRelativePath(relativePath);
        EvidenceArtifactValidation.ValidatePathForSlot(slot, relativePath);
        var destination = EvidenceArtifactValidation.GetContainedPath(_rootPath, normalizedPath,
            _requireUnlinkedPaths || slot.MaximumFiles is not null);
        ReserveArtifact(logicalName, normalizedPath, contents.Length, slot.MaximumBytes);
        var created = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (slot.MaximumFiles is null)
            {
                // Preserve the original byte-overload behavior for legacy single-file slots.
                await File.WriteAllBytesAsync(destination, contents, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await using var output = new FileStream(destination, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    Options = FileOptions.Asynchronous,
                });
                created = true;
                await output.WriteAsync(contents, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            var artifact = new EvidenceArtifactResult(logicalName, normalizedPath, slot.MediaType, contents.Length,
                EvidenceDigest.Sha256(contents.Span));
            lock (_sync)
            {
                _artifacts[normalizedPath] = artifact;
            }

            return artifact;
        }
        catch
        {
            if (created) TryDeletePartial(destination);
            ReleaseArtifact(logicalName, normalizedPath, contents.Length);
            throw;
        }
    }

    /// <summary>
    /// Streams one declared artifact to disk while hashing and enforcing its slot and aggregate limits.
    /// </summary>
    /// <param name="logicalName">Declared artifact slot identifier.</param>
    /// <param name="relativePath">Normalized artifact-root-relative destination path.</param>
    /// <param name="contents">Artifact stream. The stream is read until end of stream and is not disposed by this method.</param>
    /// <param name="cancellationToken">Cancellation requested by the EvidenceHost lifecycle.</param>
    /// <returns>Hash and metadata for the written artifact.</returns>
    public async ValueTask<EvidenceArtifactResult> WriteAsync(
        string logicalName,
        string relativePath,
        Stream contents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contents);
        if (_producer.ArtifactSlots.FirstOrDefault(slot => string.Equals(slot.LogicalName, logicalName, StringComparison.Ordinal)) is not { } slot)
        {
            throw new InvalidOperationException($"Artifact '{logicalName}' is not declared by producer '{_producer.Id}'.");
        }

        var normalizedPath = EvidenceArtifactValidation.NormalizeRelativePath(relativePath);
        EvidenceArtifactValidation.ValidatePathForSlot(slot, relativePath);
        var destination = EvidenceArtifactValidation.GetContainedPath(_rootPath, normalizedPath,
            _requireUnlinkedPaths || slot.MaximumFiles is not null);
        ReserveArtifact(logicalName, normalizedPath, 0, slot.MaximumBytes);

        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        long lengthBytes = 0;
        var created = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var output = new FileStream(destination, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = buffer.Length,
                Options = FileOptions.Asynchronous,
            });
            created = true;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            while (true)
            {
                var read = await contents.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                AddArtifactBytes(logicalName, read, slot.MaximumBytes);
                lengthBytes += read;
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            var artifact = new EvidenceArtifactResult(
                logicalName,
                normalizedPath,
                slot.MediaType,
                lengthBytes,
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
            lock (_sync)
            {
                _artifacts[normalizedPath] = artifact;
            }

            return artifact;
        }
        catch
        {
            if (created) TryDeletePartial(destination);
            ReleaseArtifact(logicalName, normalizedPath, lengthBytes);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Revalidates the final on-disk bytes for every artifact written through this writer.
    /// </summary>
    /// <param name="cancellationToken">Cancellation requested by the EvidenceHost lifecycle.</param>
    /// <returns><see langword="true"/> when every artifact still has its declared length and digest.</returns>
    public async Task<bool> VerifyWrittenArtifactsAsync(CancellationToken cancellationToken = default)
    {
        foreach (var artifact in WrittenArtifacts)
        {
            string path;
            try
            {
                var slot = _producer.ArtifactSlots.First(slot => slot.LogicalName == artifact.LogicalName);
                path = EvidenceArtifactValidation.GetContainedPath(_rootPath, artifact.RelativePath,
                    _requireUnlinkedPaths || slot.MaximumFiles is not null);
                await using var input = new FileStream(path, new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read,
                    BufferSize = 64 * 1024,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                });
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
                long lengthBytes = 0;
                try
                {
                    while (true)
                    {
                        var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }

                        lengthBytes += read;
                        if (lengthBytes > artifact.LengthBytes) return false;
                        hash.AppendData(buffer, 0, read);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                if (lengthBytes != artifact.LengthBytes
                    || !string.Equals(Convert.ToHexString(hash.GetHashAndReset()), artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return false;
            }
        }

        return true;
    }

    private void ReserveArtifact(string logicalName, string normalizedPath, long lengthBytes, long maximumBytes)
    {
        lock (_sync)
        {
            if (!_destinations.Add(normalizedPath))
            {
                throw new InvalidOperationException($"Artifact destination '{normalizedPath}' was already written by producer '{_producer.Id}'.");
            }

            var slot = _producer.ArtifactSlots.Single(candidate => string.Equals(candidate.LogicalName, logicalName, StringComparison.Ordinal));
            var maximumFiles = slot.MaximumFiles ?? 1;
            var slotCount = _slotCounts.GetValueOrDefault(logicalName);
            var slotBytes = _slotBytes.GetValueOrDefault(logicalName);
            if (maximumFiles is < 1 or > 128 || slot.MaximumBytes < 0
                || slotCount >= maximumFiles || lengthBytes > slot.MaximumBytes - slotBytes)
            {
                _destinations.Remove(normalizedPath);
                throw new InvalidOperationException($"Artifact slot '{logicalName}' exceeds its declared file or byte limit.");
            }

            _artifacts.Add(normalizedPath, null);

            if (lengthBytes > maximumBytes
                || lengthBytes > MaximumTotalArtifactBytes - _totalBytes)
            {
                _destinations.Remove(normalizedPath);
                _artifacts.Remove(normalizedPath);
                throw new InvalidOperationException($"Artifact '{logicalName}' exceeds its evidence size limit.");
            }

            _totalBytes += lengthBytes;
            _slotBytes[logicalName] = slotBytes + lengthBytes;
            _slotCounts[logicalName] = slotCount + 1;
        }
    }

    private void AddArtifactBytes(string logicalName, long additionalBytes, long maximumBytes)
    {
        lock (_sync)
        {
            var slotBytes = _slotBytes[logicalName];
            if (slotBytes > maximumBytes - additionalBytes
                || additionalBytes > MaximumTotalArtifactBytes - _totalBytes)
            {
                throw new InvalidOperationException($"Artifact '{logicalName}' exceeds its evidence size limit.");
            }

            _totalBytes += additionalBytes;
            _slotBytes[logicalName] = slotBytes + additionalBytes;
        }
    }

    private void ReleaseArtifact(string logicalName, string normalizedPath, long lengthBytes)
    {
        lock (_sync)
        {
            _artifacts.Remove(normalizedPath);
            _destinations.Remove(normalizedPath);
            _totalBytes -= lengthBytes;
            _slotBytes[logicalName] -= lengthBytes;
            _slotCounts[logicalName]--;
        }
    }

    private static void TryDeletePartial(string destination)
    {
        try
        {
            File.Delete(destination);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// Validates declared evidence artifact metadata and containment without reading raw artifact contents into manifests.
/// </summary>
public static class EvidenceArtifactValidation
{
    /// <summary>
    /// Validates producer artifact metadata against its closed declaration set.
    /// </summary>
    /// <param name="producer">Producer declaration that owns the slots.</param>
    /// <param name="artifacts">Producer-returned artifact metadata.</param>
    /// <returns><see langword="true"/> when every artifact is declared, bounded, and valid.</returns>
    public static bool AreValid(EvidenceProducerDeclaration producer, IReadOnlyList<EvidenceArtifactResult>? artifacts)
    {
        ArgumentNullException.ThrowIfNull(producer);
        artifacts ??= [];
        if (producer.ArtifactSlots.GroupBy(static slot => slot.LogicalName, StringComparer.Ordinal).Any(static group => group.Count() > 1)
            || producer.ArtifactSlots.Any(static slot => slot.MaximumBytes < 0 || slot.MaximumFiles is <= 0 or > 128)
            || artifacts.GroupBy(static artifact => artifact.RelativePath, StringComparer.Ordinal).Any(static group => group.Count() > 1))
        {
            return false;
        }

        var slots = producer.ArtifactSlots.ToDictionary(static slot => slot.LogicalName, StringComparer.Ordinal);
        long totalBytes = 0;
        var destinations = new HashSet<string>(producer.ArtifactSlots.Any(static slot => slot.MaximumFiles is not null)
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var artifact in artifacts)
        {
            if (!slots.TryGetValue(artifact.LogicalName, out var slot)
                || artifact.LengthBytes < 0
                || artifact.LengthBytes > slot.MaximumBytes
                || !string.Equals(artifact.MediaType, slot.MediaType, StringComparison.Ordinal)
                || !IsSha256(artifact.Sha256))
            {
                return false;
            }

            if (artifact.LengthBytes > EvidenceArtifactWriter.MaximumTotalArtifactBytes - totalBytes)
            {
                return false;
            }

            totalBytes += artifact.LengthBytes;

            try
            {
                var normalizedPath = NormalizeRelativePath(artifact.RelativePath);
                if ((slot.MaximumFiles is not null && !string.Equals(normalizedPath, artifact.RelativePath, StringComparison.Ordinal))
                    || !destinations.Add(normalizedPath))
                {
                    return false;
                }

                ValidatePathForSlot(slot, normalizedPath);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        foreach (var slot in producer.ArtifactSlots)
        {
            var slotArtifacts = artifacts.Where(artifact => string.Equals(artifact.LogicalName, slot.LogicalName, StringComparison.Ordinal)).ToArray();
            if (slotArtifacts.Length > (slot.MaximumFiles ?? 1)
                || slotArtifacts.Sum(static artifact => artifact.LengthBytes) > slot.MaximumBytes)
            {
                return false;
            }
        }

        return producer.ArtifactSlots.Where(static slot => slot.Required)
            .All(slot => artifacts.Any(artifact => string.Equals(artifact.LogicalName, slot.LogicalName, StringComparison.Ordinal)));
    }

    /// <summary>
    /// Normalizes an artifact-root-relative path or throws when it escapes the artifact boundary.
    /// </summary>
    /// <param name="relativePath">Artifact-root-relative candidate path.</param>
    /// <returns>Normalized forward-slash path.</returns>
    public static string NormalizeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("Evidence artifact paths cannot be empty.", nameof(relativePath));
        }

        var normalized = relativePath.Trim().Replace('\\', '/');
        if (Path.IsPathRooted(normalized)
            || normalized.Contains("//", StringComparison.Ordinal)
            || normalized.EndsWith("/", StringComparison.Ordinal)
            || normalized.Split('/').Any(static segment => segment is "." or ".."))
        {
            throw new ArgumentException("Evidence artifact paths must be normalized and root-relative.", nameof(relativePath));
        }

        return normalized;
    }

    /// <summary>Validates an already canonical portable path for new multi-file slots and private receipt custody.</summary>
    /// <param name="relativePath">Unmodified artifact-root-relative path.</param>
    /// <returns>The unchanged safe portable path.</returns>
    /// <remarks>Legacy single-file normalization retains its original character rules. Receipt admission always uses this stricter boundary.</remarks>
    internal static string NormalizePortableRelativePath(string relativePath)
    {
        var normalized = NormalizeRelativePath(relativePath);
        if (!string.Equals(normalized, relativePath, StringComparison.Ordinal)
            || normalized.Any(char.IsControl)
            || normalized.IndexOfAny([':', '<', '>', '"', '|', '?', '*']) >= 0
            || normalized.Split('/').Any(static segment => IsReservedSegment(segment)))
            throw new ArgumentException("Evidence receipt and multi-file paths must be canonical and portable.", nameof(relativePath));
        return normalized;
    }

    /// <summary>
    /// Validates that an artifact path remains beneath the declared slot root.
    /// </summary>
    /// <param name="slot">Declared artifact slot.</param>
    /// <param name="relativePath">Artifact-root-relative candidate path.</param>
    public static void ValidatePathForSlot(EvidenceArtifactSlot slot, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(slot);
        var normalizedRoot = slot.MaximumFiles is null ? NormalizeRelativePath(slot.RelativeRoot) : NormalizePortableRelativePath(slot.RelativeRoot);
        var normalizedPath = slot.MaximumFiles is null ? NormalizeRelativePath(relativePath) : NormalizePortableRelativePath(relativePath);
        if (!string.Equals(normalizedPath, normalizedRoot, StringComparison.Ordinal)
            && !normalizedPath.StartsWith(normalizedRoot + "/", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Artifact path '{relativePath}' is outside declared root '{slot.RelativeRoot}'.", nameof(relativePath));
        }
    }

    /// <summary>
    /// Resolves a root-relative path and checks lexical containment beneath the controlled artifact root.
    /// </summary>
    /// <param name="rootPath">Controlled artifact root.</param>
    /// <param name="relativePath">Validated root-relative artifact path.</param>
    /// <returns>Contained absolute destination path.</returns>
    public static string GetContainedPath(string rootPath, string relativePath)
        => GetContainedPath(rootPath, relativePath, requireUnlinkedPaths: false);

    /// <summary>Resolves a contained path and optionally rejects existing links at and below the root.</summary>
    /// <param name="rootPath">Controlled artifact root.</param>
    /// <param name="relativePath">Validated root-relative artifact path.</param>
    /// <param name="requireUnlinkedPaths">Whether the root and descendant paths must be unlinked.</param>
    /// <returns>Contained absolute destination path.</returns>
    /// <remarks>The link check is a preflight. The caller must retain exclusive root custody.</remarks>
    internal static string GetContainedPath(string rootPath, string relativePath, bool requireUnlinkedPaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        var root = Path.GetFullPath(rootPath);
        var normalizedPath = NormalizeRelativePath(relativePath);
        var destination = Path.GetFullPath(Path.Join(root, normalizedPath));
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(rootWithSeparator, StringComparison.Ordinal))
        {
            throw new ArgumentException("Evidence artifact path escapes the controlled artifact root.", nameof(relativePath));
        }

        if (!requireUnlinkedPaths) return destination;
        var current = root;
        RejectLinkedPath(current);
        foreach (var segment in normalizedPath.Split('/'))
        {
            current = Path.Join(current, segment);
            RejectLinkedPath(current);
        }

        return destination;
    }

    private static void RejectLinkedPath(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new ArgumentException("Evidence artifact paths cannot contain linked entries.", nameof(path));
            }
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private static bool IsReservedSegment(string segment)
    {
        if (segment.EndsWith('.') || segment.EndsWith(' ')) return true;
        var stem = segment.Split('.')[0];
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || stem.Length == 4 && stem[3] is (>= '1' and <= '9') or '¹' or '²' or '³'
                && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(static character => char.IsAsciiHexDigit(character));
}

/// <summary>
/// Serializes contract objects deterministically for evidence identity and verification.
/// </summary>
public static class EvidenceCanonicalJson
{
    /// <summary>
    /// Serializes a value as canonical UTF-8 JSON with ordinal object-property order.
    /// </summary>
    /// <typeparam name="TValue">Value type to serialize.</typeparam>
    /// <param name="value">Value to serialize.</param>
    /// <returns>Canonical UTF-8 JSON bytes.</returns>
    /// <remarks>Enum serialization preserves the generic <see cref="JsonStringEnumConverter"/> behavior for contract and consumer-defined enums.</remarks>
    public static byte[] Serialize<TValue>(TValue value)
    {
        ArgumentNullException.ThrowIfNull(value);

        using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(value, SerializerOptions));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteElement(document.RootElement, writer);
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Deserializes a JSON document using the compatible contract serializer behavior.
    /// </summary>
    /// <typeparam name="TValue">Value type to deserialize.</typeparam>
    /// <param name="utf8Json">JSON bytes.</param>
    /// <returns>The deserialized value.</returns>
    /// <remarks>
    /// Generic values, including evidence policies, preserve <see cref="JsonStringEnumConverter"/> behavior.
    /// Reads of <see cref="EvidencePlan"/> and <see cref="EvidenceManifest"/> require defined named enum tokens;
    /// numeric enum tokens and numeric strings are rejected. Those contracts must use version 1.0 or 1.1,
    /// and an explicit manifest reuse count must match its producer results.
    /// </remarks>
    public static TValue Deserialize<TValue>(ReadOnlySpan<byte> utf8Json)
    {
        var serializerOptions = typeof(TValue) == typeof(EvidencePlan) || typeof(TValue) == typeof(EvidenceManifest)
            ? StrictContractReaderOptions
            : SerializerOptions;
        var value = JsonSerializer.Deserialize<TValue>(utf8Json, serializerOptions);
        if (value is EvidencePlan plan && plan.ContractVersion is not ("1.0" or "1.1")
            || value is EvidenceManifest manifest && manifest.ContractVersion is not ("1.0" or "1.1"))
        {
            throw new JsonException("Unsupported evidence contract version.");
        }

        if (value is EvidenceManifest result)
        {
            using var document = JsonDocument.Parse(utf8Json.ToArray());
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, nameof(EvidenceManifest.ReuseCount), StringComparison.OrdinalIgnoreCase)
                    && (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var count) || count != result.ReuseCount))
                {
                    throw new JsonException("ReuseCount does not match the producer results.");
                }
            }
        }

        return value ?? throw new InvalidOperationException($"Evidence JSON did not contain a {typeof(TValue).Name} value.");
    }

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions(strictContractEnums: false);

    private static readonly JsonSerializerOptions StrictContractReaderOptions = CreateSerializerOptions(strictContractEnums: true);

    private static JsonSerializerOptions CreateSerializerOptions(bool strictContractEnums)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = false,
        };

        if (strictContractEnums)
        {
            options.Converters.Add(new NamedEnumConverter<EvidenceProfileScope>());
            options.Converters.Add(new NamedEnumConverter<EvidenceExecutionVerdict>());
            options.Converters.Add(new NamedEnumConverter<EvidenceClaimKind>());
            options.Converters.Add(new NamedEnumConverter<EvidenceClaimEligibility>());
            options.Converters.Add(new NamedEnumConverter<EvidenceEnvelopeStatus>());
            options.Converters.Add(new NamedEnumConverter<EvidenceProducerOutcome>());
            options.Converters.Add(new NamedEnumConverter<EvidenceResourceOutcome>());
            options.Converters.Add(new NamedEnumConverter<EvidenceCoverageTestResultFormat>());
        }

        // Preserve the original generic enum behavior for serialization, policy reads, and other consumers.
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    // Used only by the EvidencePlan and EvidenceManifest reader options. The generic fallback remains
    // JsonStringEnumConverter so consumer enums and legacy policy input keep their existing semantics.
    private sealed class NamedEnumConverter<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
    {
        public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var text = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            if (text is not null
                && Enum.GetNames<TEnum>().FirstOrDefault(candidate => string.Equals(candidate, text, StringComparison.OrdinalIgnoreCase)) is { } name)
            {
                return Enum.Parse<TEnum>(name);
            }

            throw new JsonException($"Expected a named {typeof(TEnum).Name} value.");
        }

        public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
            => throw new NotSupportedException("The strict contract reader converter does not support serialization.");
    }

    private static void WriteElement(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(static property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteElement(property.Value, writer);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteElement(item, writer);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: true);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new InvalidOperationException($"Unsupported JSON token {element.ValueKind}.");
        }
    }
}

/// <summary>
/// Builds and verifies manifest claims from a resolved plan and bounded producer results.
/// </summary>
public static class EvidenceManifestBuilder
{
    /// <summary>
    /// Produces a manifest and closes obligations when every required producer passed or has structurally valid reuse provenance,
    /// and returned the required assertion. Reused completion is informational and never gate eligible.
    /// </summary>
    /// <param name="plan">Resolved plan.</param>
    /// <param name="producerResults">Terminal results returned by selected producers.</param>
    /// <param name="observationOnly">Whether the caller intentionally requested a non-gate observation.</param>
    /// <param name="envelopeStatus">Constrained CI-envelope status bound to the manifest.</param>
    /// <param name="resourceResults">Terminal readiness results for selected resources.</param>
    /// <param name="metrics">Secret-free lifecycle timing and cleanup state.</param>
    /// <returns>A digest-bound manifest.</returns>
    public static EvidenceManifest Build(
        EvidencePlan plan,
        IReadOnlyList<EvidenceProducerResult> producerResults,
        bool observationOnly = false,
        EvidenceEnvelopeStatus envelopeStatus = EvidenceEnvelopeStatus.NotRequired,
        IReadOnlyList<EvidenceResourceResult>? resourceResults = null,
        EvidenceExecutionMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(producerResults);
        resourceResults ??= [];
        metrics ??= new EvidenceExecutionMetrics();

        var duplicateDeclarations = plan.Profile.Producers
            .GroupBy(static producer => producer.Id, StringComparer.Ordinal)
            .Any(static group => group.Count() > 1);
        var producerDeclarations = plan.Profile.Producers
            .GroupBy(static producer => producer.Id, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        var duplicateResults = producerResults
            .GroupBy(static result => result.ProducerId, StringComparer.Ordinal)
            .Any(static group => group.Count() > 1);
        var results = producerResults
            .GroupBy(static result => result.ProducerId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        var duplicateResourceResults = resourceResults
            .GroupBy(static result => result.ResourceId, StringComparer.Ordinal)
            .Any(static group => group.Count() > 1);
        var resources = resourceResults
            .GroupBy(static result => result.ResourceId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        var declaredResources = plan.Profile.Resources
            .GroupBy(static resource => resource.Id, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        var hasReuseData = producerResults.Any(static result => result.Outcome == EvidenceProducerOutcome.Reused
            || result.ProducerKey is not null || result.OriginManifestDigest is not null || result.OriginCompletedAt is not null);
        var policyDeclarations = (plan.PolicySnapshot?.Profiles ?? []).Append(plan.Profile);
        var declarationsRequireV11 = policyDeclarations.Any(static profile => profile.Producers.Any(static producer => producer.Inputs is not null
            || producer.CoverageExecution is { CooperativeChildLifetime: true }
            || producer.ArtifactSlots.Any(static slot => slot.MaximumFiles is not null)));
        var invalid = plan.ContractVersion is not ("1.0" or "1.1")
            || !Enum.IsDefined(plan.Profile.Scope)
            || !Enum.IsDefined(envelopeStatus)
            || duplicateDeclarations
            || duplicateResults
            || duplicateResourceResults
            || plan.Profile.Resources.Count != declaredResources.Count
            || resourceResults.Any(static result => result.Outcome == EvidenceResourceOutcome.NotRequiredForExecution)
                && plan.ContractVersion != "1.1"
            || producerResults.Any(static result => !Enum.IsDefined(result.Outcome))
            || plan.ContractVersion == "1.1" && producerResults.Any(static result => result.Outcome == EvidenceProducerOutcome.Invalid)
            || resourceResults.Any(static result => !Enum.IsDefined(result.Outcome))
            || metrics.PreparationMilliseconds < 0 || metrics.ReceiptWaitMilliseconds < 0 || metrics.ArtifactRestoreMilliseconds < 0
            || (metrics.PreparationMilliseconds != 0 || metrics.ReceiptWaitMilliseconds != 0 || metrics.ArtifactRestoreMilliseconds != 0)
                && plan.ContractVersion != "1.1"
            || hasReuseData && !string.Equals(plan.ContractVersion, "1.1", StringComparison.Ordinal)
            || declarationsRequireV11 && !string.Equals(plan.ContractVersion, "1.1", StringComparison.Ordinal)
            || results.Keys.Any(id => !producerDeclarations.ContainsKey(id))
            || resources.Keys.Any(id => !declaredResources.ContainsKey(id))
            || results.Any(pair => !producerDeclarations.TryGetValue(pair.Key, out var declaration)
                || pair.Value.SatisfiedAssertionIds.Any(assertion =>
                    !declaration.AssertionIds.Contains(assertion, StringComparer.Ordinal))
                || pair.Value.Outcome == EvidenceProducerOutcome.Reused && !IsValidReuseProvenance(pair.Value)
                || pair.Value.Outcome != EvidenceProducerOutcome.Reused
                    && (pair.Value.ProducerKey is not null || pair.Value.OriginManifestDigest is not null || pair.Value.OriginCompletedAt is not null)
                || !EvidenceArtifactValidation.AreValid(
                    plan.ContractVersion == "1.1" && pair.Value.Outcome is not (EvidenceProducerOutcome.Passed or EvidenceProducerOutcome.Reused)
                        ? declaration with { ArtifactSlots = declaration.ArtifactSlots.Select(static slot => slot with { Required = false }).ToArray() }
                        : declaration,
                    pair.Value.Artifacts));
        var closed = new List<string>();
        var unmediated = new List<string>();

        foreach (var obligation in plan.Profile.Obligations)
        {
            var isClosed = obligation.RequiredProducerIds.All(producerId =>
                results.TryGetValue(producerId, out var result)
                && (result.Outcome == EvidenceProducerOutcome.Passed
                    || result.Outcome == EvidenceProducerOutcome.Reused && IsValidReuseProvenance(result))
                && result.SatisfiedAssertionIds.Contains(obligation.RequiredAssertionId, StringComparer.Ordinal));
            (isClosed ? closed : unmediated).Add(obligation.Id);
        }

        var everyProducerPassed = plan.Profile.Producers.All(producer =>
            results.TryGetValue(producer.Id, out var result)
            && (result.Outcome == EvidenceProducerOutcome.Passed
                || result.Outcome == EvidenceProducerOutcome.Reused && IsValidReuseProvenance(result)));
        var requiredByFresh = GetFreshResourceClosure(plan.Profile, results);
        var everyResourceReady = plan.Profile.Resources.All(resource =>
            resources.TryGetValue(resource.Id, out var result)
            && (result.Outcome == EvidenceResourceOutcome.Ready
                || result.Outcome == EvidenceResourceOutcome.NotRequiredForExecution && !requiredByFresh.Contains(resource.Id)));
        var hasReusedResult = producerResults.Any(static result => result.Outcome == EvidenceProducerOutcome.Reused);
        var noEvidence = plan.Profile.Resources.Count == 0 && plan.Profile.Producers.Count == 0 && plan.Profile.Obligations.Count == 0;
        var releaseEnvelopeAccepted = plan.Profile.Scope != EvidenceProfileScope.Release
            || envelopeStatus == EvidenceEnvelopeStatus.ValidatedNotAttested;
        var verdict = invalid
            ? EvidenceExecutionVerdict.Invalid
            : everyResourceReady && everyProducerPassed && unmediated.Count == 0 && releaseEnvelopeAccepted && metrics.CleanupCompleted
                ? EvidenceExecutionVerdict.Passed
                : EvidenceExecutionVerdict.Incomplete;
        var claim = verdict == EvidenceExecutionVerdict.Invalid
            ? EvidenceClaimKind.None
            : observationOnly
                ? EvidenceClaimKind.ObservationOnly
                : hasReusedResult && verdict == EvidenceExecutionVerdict.Passed
                    ? EvidenceClaimKind.LocalReusedComplete
                : verdict != EvidenceExecutionVerdict.Passed
                ? EvidenceClaimKind.None
                : noEvidence
                    ? EvidenceClaimKind.NoEvidenceRequired
                    : plan.Profile.Scope == EvidenceProfileScope.Release
                        ? EvidenceClaimKind.ReleaseComplete
                        : EvidenceClaimKind.TargetedComplete;
        var eligibility = claim switch
        {
            EvidenceClaimKind.TargetedComplete => EvidenceClaimEligibility.PullRequestGate,
            EvidenceClaimKind.ReleaseComplete => EvidenceClaimEligibility.ReleaseGate,
            EvidenceClaimKind.NoEvidenceRequired => EvidenceClaimEligibility.PullRequestGate,
            EvidenceClaimKind.ObservationOnly => EvidenceClaimEligibility.Informational,
            EvidenceClaimKind.LocalReusedComplete => EvidenceClaimEligibility.Informational,
            _ => EvidenceClaimEligibility.None,
        };
        var draft = new EvidenceManifest(
            ContractVersion: plan.ContractVersion,
            PlanDigest: plan.PlanDigest,
            ExecutionVerdict: verdict,
            ClaimKind: claim,
            Eligibility: eligibility,
            EnvelopeStatus: envelopeStatus,
            ResourceResults: resourceResults.OrderBy(static result => result.ResourceId, StringComparer.Ordinal).ToArray(),
            SelectedObligationIds: plan.Profile.Obligations.Select(static obligation => obligation.Id).OrderBy(static id => id, StringComparer.Ordinal).ToArray(),
            ClosedObligationIds: closed.OrderBy(static id => id, StringComparer.Ordinal).ToArray(),
            UnmediatedObligationIds: unmediated.OrderBy(static id => id, StringComparer.Ordinal).ToArray(),
            ProducerResults: producerResults.OrderBy(static result => result.ProducerId, StringComparer.Ordinal)
                .Select(result => plan.ContractVersion == "1.1" && result.Artifacts is not null
                    ? result with
                    {
                        Artifacts = result.Artifacts.OrderBy(static artifact => artifact.LogicalName, StringComparer.Ordinal)
                        .ThenBy(static artifact => artifact.RelativePath, StringComparer.Ordinal).ToArray()
                    }
                    : result).ToArray(),
            Metrics: metrics,
            ManifestDigest: string.Empty);

        return draft with { ManifestDigest = EvidenceDigest.CanonicalSha256(draft) };
    }

    /// <summary>
    /// Validates that a manifest still binds to a supplied plan and canonical manifest content.
    /// This detects inconsistent or edited claim fields, but does not authenticate the origin of a plan or manifest.
    /// Gates must obtain both values through a trusted CI channel.
    /// </summary>
    /// <param name="plan">Plan expected by the verifier.</param>
    /// <param name="manifest">Manifest to verify.</param>
    /// <returns><see langword="true"/> when the binding and digest are valid.</returns>
    public static bool Verify(EvidencePlan plan, EvidenceManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(manifest);

        if (plan.ContractVersion is not ("1.0" or "1.1")
            || manifest.ContractVersion is not ("1.0" or "1.1")
            || !string.Equals(plan.ContractVersion, manifest.ContractVersion, StringComparison.Ordinal)
            || plan.ContractVersion == "1.0" && (
                (plan.PolicySnapshot?.Profiles ?? [plan.Profile]).Append(plan.Profile).Any(static profile => profile.Producers.Any(static producer =>
                    producer.Inputs is not null || producer.CoverageExecution is { CooperativeChildLifetime: true }
                    || producer.ArtifactSlots.Any(static slot => slot.MaximumFiles is not null)))
                || manifest.Metrics.PreparationMilliseconds != 0 || manifest.Metrics.ReceiptWaitMilliseconds != 0 || manifest.Metrics.ArtifactRestoreMilliseconds != 0
                || manifest.ReuseCount != 0
                || manifest.ProducerResults.Any(static result => result.ProducerKey is not null || result.OriginManifestDigest is not null || result.OriginCompletedAt is not null)
                || manifest.ResourceResults.Any(static result => result.Outcome == EvidenceResourceOutcome.NotRequiredForExecution)
                || manifest.ClaimKind == EvidenceClaimKind.LocalReusedComplete))
        {
            return false;
        }

        var planWithoutDigest = plan with { PlanDigest = string.Empty };
        if (plan.PolicySnapshot is null
            || plan.PatchSha256 is { } patch && (patch.Length != 64 || !patch.All(char.IsAsciiHexDigitLower))
            || !string.Equals(plan.PolicyDigest, EvidenceDigest.CanonicalSha256(plan.PolicySnapshot), StringComparison.Ordinal)
            || !string.Equals(plan.DiffDigest, EvidenceDigest.CanonicalSha256(plan.ChangedPaths), StringComparison.Ordinal)
            || !string.Equals(plan.PlanDigest, EvidenceDigest.CanonicalSha256(planWithoutDigest), StringComparison.Ordinal)
            || !string.Equals(plan.PlanDigest, manifest.PlanDigest, StringComparison.Ordinal))
        {
            return false;
        }

        var withoutDigest = manifest with { ManifestDigest = string.Empty };
        if (!string.Equals(manifest.ManifestDigest, EvidenceDigest.CanonicalSha256(withoutDigest), StringComparison.Ordinal))
        {
            return false;
        }

        var expected = Build(
            plan,
            manifest.ProducerResults,
            manifest.ClaimKind == EvidenceClaimKind.ObservationOnly,
            manifest.EnvelopeStatus,
            manifest.ResourceResults,
            manifest.Metrics);
        return string.Equals(manifest.ManifestDigest, expected.ManifestDigest, StringComparison.Ordinal);
    }

    /// <summary>Verifies integrity and confirms that a manifest is admissible as a downstream gate claim.</summary>
    /// <param name="plan">Plan expected by the gate.</param>
    /// <param name="manifest">Manifest to verify.</param>
    /// <returns><see langword="true"/> only for a valid, gate-eligible non-reuse claim.</returns>
    public static bool VerifyForGate(EvidencePlan plan, EvidenceManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(manifest);
        return plan.ContractVersion is "1.0" or "1.1"
            && string.Equals(plan.ContractVersion, manifest.ContractVersion, StringComparison.Ordinal)
            && manifest.ClaimKind is EvidenceClaimKind.TargetedComplete or EvidenceClaimKind.ReleaseComplete or EvidenceClaimKind.NoEvidenceRequired
            && manifest.Eligibility is EvidenceClaimEligibility.PullRequestGate or EvidenceClaimEligibility.ReleaseGate
            && !manifest.ProducerResults.Any(static result => result.Outcome == EvidenceProducerOutcome.Reused)
            && Verify(plan, manifest);
    }

    private static bool IsValidReuseProvenance(EvidenceProducerResult result)
        => result.ProducerKey is { Length: 64 } key && key.All(char.IsAsciiHexDigitLower)
            && result.ElapsedMilliseconds == 0
            && result.OriginManifestDigest is { Length: 64 } digest && digest.All(char.IsAsciiHexDigitLower)
            && result.OriginCompletedAt is { } completedAt && completedAt != default;

    private static HashSet<string> GetFreshResourceClosure(EvidenceProfile profile, IReadOnlyDictionary<string, EvidenceProducerResult> results)
    {
        var resources = profile.Resources.GroupBy(static resource => resource.Id, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        var required = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(profile.Producers
            .Where(producer => !results.TryGetValue(producer.Id, out var result) || result.Outcome != EvidenceProducerOutcome.Reused || !IsValidReuseProvenance(result))
            .SelectMany(static producer => producer.RequiredResources));
        while (pending.TryPop(out var id))
        {
            if (!required.Add(id) || !resources.TryGetValue(id, out var resource)) continue;
            foreach (var dependency in resource.Requires) pending.Push(dependency);
        }
        return required;
    }
}

/// <summary>
/// Computes SHA-256 evidence identities from canonical bytes.
/// </summary>
public static class EvidenceDigest
{
    /// <summary>
    /// Computes a lower-case hexadecimal SHA-256 digest.
    /// </summary>
    /// <param name="bytes">Bytes to digest.</param>
    /// <returns>The digest.</returns>
    public static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>
    /// Computes a canonical-object digest.
    /// </summary>
    /// <typeparam name="TValue">Value type to digest.</typeparam>
    /// <param name="value">Value to serialize and digest.</param>
    /// <returns>The canonical SHA-256 digest.</returns>
    public static string CanonicalSha256<TValue>(TValue value) => Sha256(EvidenceCanonicalJson.Serialize(value));
}
