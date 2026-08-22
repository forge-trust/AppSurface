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
/// <param name="MaximumBytes">Maximum allowed artifact length.</param>
public sealed record EvidenceArtifactSlot(string LogicalName, string RelativeRoot, string MediaType, bool Required, long MaximumBytes);

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
    EvidenceCoverageGateRequirements? CoverageGate = null);

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
    IReadOnlyList<EvidencePolicyRule> Rules);

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
    EvidencePolicy? PolicySnapshot = null);

/// <summary>
/// Captures a bounded producer result returned to the EvidenceHost.
/// </summary>
/// <param name="ProducerId">Producer that emitted the result.</param>
/// <param name="Outcome">Terminal producer outcome.</param>
/// <param name="SatisfiedAssertionIds">Assertion identifiers returned by the producer.</param>
/// <param name="Diagnostic">Secret-safe human diagnostic; never include raw logs or values.</param>
/// <param name="Artifacts">Bounded metadata for artifacts written through the declared artifact writer.</param>
/// <param name="ElapsedMilliseconds">Measured producer execution time.</param>
public sealed record EvidenceProducerResult(
    string ProducerId,
    EvidenceProducerOutcome Outcome,
    IReadOnlyList<string> SatisfiedAssertionIds,
    string? Diagnostic = null,
    IReadOnlyList<EvidenceArtifactResult>? Artifacts = null,
    long ElapsedMilliseconds = 0);

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
    string? CleanupDiagnostic = null);

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
    string ManifestDigest);

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
    EvidenceArtifactWriter? Artifacts = null);

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

/// <summary>
/// Writes only declared producer artifacts beneath a consumer-selected evidence root.
/// </summary>
public sealed class EvidenceArtifactWriter
{
    /// <summary>Maximum total artifact bytes accepted by one v1 producer declaration.</summary>
    public const long MaximumTotalArtifactBytes = 256L * 1024 * 1024;

    private readonly EvidenceProducerDeclaration _producer;
    private readonly string _rootPath;
    private readonly object _sync = new();
    private readonly Dictionary<string, EvidenceArtifactResult?> _artifacts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _destinations = new(StringComparer.Ordinal);
    private long _totalBytes;

    /// <summary>
    /// Initializes a bounded artifact writer for one producer declaration.
    /// </summary>
    /// <param name="producer">Closed producer declaration that owns allowed artifact slots.</param>
    /// <param name="rootPath">Controlled evidence artifact root.</param>
    public EvidenceArtifactWriter(EvidenceProducerDeclaration producer, string rootPath)
    {
        _producer = producer ?? throw new ArgumentNullException(nameof(producer));
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
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
                    .ToArray();
            }
        }
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
        EvidenceArtifactValidation.ValidatePathForSlot(slot, normalizedPath);
        ReserveArtifact(logicalName, normalizedPath, contents.Length, slot.MaximumBytes);

        try
        {
            var destination = EvidenceArtifactValidation.GetContainedPath(_rootPath, normalizedPath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await File.WriteAllBytesAsync(destination, contents, cancellationToken).ConfigureAwait(false);
            var artifact = new EvidenceArtifactResult(
                logicalName,
                normalizedPath,
                slot.MediaType,
                contents.Length,
                EvidenceDigest.Sha256(contents.Span));
            lock (_sync)
            {
                _artifacts[logicalName] = artifact;
            }

            return artifact;
        }
        catch
        {
            ReleaseArtifact(logicalName, normalizedPath, contents.Length);
            throw;
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
                path = EvidenceArtifactValidation.GetContainedPath(_rootPath, artifact.RelativePath);
                var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                if (bytes.LongLength != artifact.LengthBytes
                    || !string.Equals(EvidenceDigest.Sha256(bytes), artifact.Sha256, StringComparison.Ordinal))
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

    private void ReserveArtifact(string logicalName, string normalizedPath, int lengthBytes, long maximumBytes)
    {
        lock (_sync)
        {
            if (!_artifacts.TryAdd(logicalName, null))
            {
                throw new InvalidOperationException($"Artifact '{logicalName}' was already written by producer '{_producer.Id}'.");
            }

            if (!_destinations.Add(normalizedPath))
            {
                _artifacts.Remove(logicalName);
                throw new InvalidOperationException($"Artifact destination '{normalizedPath}' was already written by producer '{_producer.Id}'.");
            }

            if (lengthBytes > maximumBytes
                || lengthBytes > MaximumTotalArtifactBytes - _totalBytes)
            {
                _destinations.Remove(normalizedPath);
                _artifacts.Remove(logicalName);
                throw new InvalidOperationException($"Artifact '{logicalName}' exceeds its evidence size limit.");
            }

            _totalBytes += lengthBytes;
        }
    }

    private void ReleaseArtifact(string logicalName, string normalizedPath, int lengthBytes)
    {
        lock (_sync)
        {
            _artifacts.Remove(logicalName);
            _destinations.Remove(normalizedPath);
            _totalBytes -= lengthBytes;
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
            || artifacts.GroupBy(static artifact => artifact.LogicalName, StringComparer.Ordinal).Any(static group => group.Count() > 1)
            || artifacts.GroupBy(static artifact => artifact.RelativePath, StringComparer.Ordinal).Any(static group => group.Count() > 1))
        {
            return false;
        }

        var slots = producer.ArtifactSlots.ToDictionary(static slot => slot.LogicalName, StringComparer.Ordinal);
        if (artifacts.Count > slots.Count)
        {
            return false;
        }

        long totalBytes = 0;
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
                ValidatePathForSlot(slot, artifact.RelativePath);
            }
            catch (ArgumentException)
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

    /// <summary>
    /// Validates that an artifact path remains beneath the declared slot root.
    /// </summary>
    /// <param name="slot">Declared artifact slot.</param>
    /// <param name="relativePath">Artifact-root-relative candidate path.</param>
    public static void ValidatePathForSlot(EvidenceArtifactSlot slot, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(slot);
        var normalizedRoot = NormalizeRelativePath(slot.RelativeRoot);
        var normalizedPath = NormalizeRelativePath(relativePath);
        if (!string.Equals(normalizedPath, normalizedRoot, StringComparison.Ordinal)
            && !normalizedPath.StartsWith(normalizedRoot + "/", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Artifact path '{relativePath}' is outside declared root '{slot.RelativeRoot}'.", nameof(relativePath));
        }
    }

    /// <summary>
    /// Returns an absolute path contained beneath a controlled artifact root.
    /// </summary>
    /// <param name="rootPath">Controlled artifact root.</param>
    /// <param name="relativePath">Validated root-relative artifact path.</param>
    /// <returns>Contained absolute destination path.</returns>
    public static string GetContainedPath(string rootPath, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        var root = Path.GetFullPath(rootPath);
        var destination = Path.GetFullPath(Path.Join(root, NormalizeRelativePath(relativePath)));
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(rootWithSeparator, StringComparison.Ordinal))
        {
            throw new ArgumentException("Evidence artifact path escapes the controlled artifact root.", nameof(relativePath));
        }

        return destination;
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(static character => char.IsAsciiHexDigit(character));
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
    /// Deserializes a JSON document using the contract serializer options.
    /// </summary>
    /// <typeparam name="TValue">Value type to deserialize.</typeparam>
    /// <param name="utf8Json">JSON bytes.</param>
    /// <returns>The deserialized value.</returns>
    public static TValue Deserialize<TValue>(ReadOnlySpan<byte> utf8Json)
    {
        var value = JsonSerializer.Deserialize<TValue>(utf8Json, SerializerOptions);
        return value ?? throw new InvalidOperationException($"Evidence JSON did not contain a {typeof(TValue).Name} value.");
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

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
    /// Produces a manifest and closes obligations only when every declared producer and assertion requirement passed.
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
        var invalid = duplicateDeclarations
            || duplicateResults
            || duplicateResourceResults
            || results.Keys.Any(id => !producerDeclarations.ContainsKey(id))
            || resources.Keys.Any(id => !declaredResources.ContainsKey(id))
            || results.Any(pair => !producerDeclarations.TryGetValue(pair.Key, out var declaration)
                || pair.Value.SatisfiedAssertionIds.Any(assertion =>
                    !declaration.AssertionIds.Contains(assertion, StringComparer.Ordinal))
                || !EvidenceArtifactValidation.AreValid(declaration, pair.Value.Artifacts));
        var closed = new List<string>();
        var unmediated = new List<string>();

        foreach (var obligation in plan.Profile.Obligations)
        {
            var isClosed = obligation.RequiredProducerIds.All(producerId =>
                results.TryGetValue(producerId, out var result)
                && result.Outcome == EvidenceProducerOutcome.Passed
                && result.SatisfiedAssertionIds.Contains(obligation.RequiredAssertionId, StringComparer.Ordinal));
            (isClosed ? closed : unmediated).Add(obligation.Id);
        }

        var everyProducerPassed = plan.Profile.Producers.All(producer =>
            results.TryGetValue(producer.Id, out var result) && result.Outcome == EvidenceProducerOutcome.Passed);
        var everyResourceReady = plan.Profile.Resources.All(resource =>
            resources.TryGetValue(resource.Id, out var result) && result.Outcome == EvidenceResourceOutcome.Ready);
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
            ProducerResults: producerResults.OrderBy(static result => result.ProducerId, StringComparer.Ordinal).ToArray(),
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

        var planWithoutDigest = plan with { PlanDigest = string.Empty };
        if (plan.PolicySnapshot is null
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
