using System.Text.Json;
using System.Text.Json.Serialization;
using ForgeTrust.AppSurface.Coverage;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Aspire;

/// <summary>Adapts an explicitly versioned coverage acceptance to an EvidenceHost obligation.</summary>
/// <remarks>Supplied captures are trusted-producer observations. Only independently accepted current-run evidence
/// with completed cleanup and successful required result delivery may satisfy the declared assertion.
/// Patch acceptance must match the exact patch SHA-256 recorded on the resolved plan. A v3 patch obligation also
/// requires complete primary and composed line/branch applicability; <c>PatchAccepted</c> alone is insufficient.
/// Historical replay and aggregate-only baselines cannot satisfy a patch-required obligation.</remarks>
public sealed class EvidenceCoverageFanInProducer : IEvidenceProducer
{
    private readonly CoverageAcceptRequest _request;
    private readonly string _assertion;
    private readonly string _artifactSlot;
    private readonly bool _requirePatch;
    private readonly bool _requireObservedPrimary;

    /// <summary>Creates an acceptance adapter with explicit delivery and assurance requirements.</summary>
    /// <param name="id">Producer declaration ID.</param>
    /// <param name="request">The exact current-run acceptance request; not a caller-supplied result.</param>
    /// <param name="assertionId">Declared assertion satisfied only after complete acceptance and delivery.</param>
    /// <param name="artifactSlot">Declared Evidence artifact slot for the bounded terminal result.</param>
    /// <param name="requirePatch">Whether aggregate-only results are insufficient. Defaults to true.</param>
    /// <param name="requireObservedPrimary">Whether the primary must come from supervised local collection rather than import. Defaults to false.</param>
    public EvidenceCoverageFanInProducer(string id, CoverageAcceptRequest request, string assertionId,
        string artifactSlot = "result", bool requirePatch = true, bool requireObservedPrimary = false)
    {
        Id = string.IsNullOrWhiteSpace(id) ? throw new ArgumentException("Explicit producer ID required.", nameof(id)) : id;
        ArgumentNullException.ThrowIfNull(request);
        _request = Snapshot(request);
        _assertion = string.IsNullOrWhiteSpace(assertionId) ? throw new ArgumentException("Explicit assertion required.", nameof(assertionId)) : assertionId;
        _artifactSlot = string.IsNullOrWhiteSpace(artifactSlot) ? throw new ArgumentException("Explicit result slot required.", nameof(artifactSlot)) : artifactSlot;
        _requirePatch = requirePatch; _requireObservedPrimary = requireObservedPrimary;
    }
    /// <summary>The explicit Evidence producer declaration ID.</summary>
    public string Id { get; }

    /// <summary>Accepts through the supported facade and delivers the current result before returning any assertion.</summary>
    public async ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var patch = _request.Policy.Gate?.Mode == CoverageGateMode.AggregateAndPatch;
        var expectedPatch = patch ? context.Plan.PatchSha256 : null;
        if (context.Producer.Id != Id || !context.Producer.AssertionIds.Contains(_assertion, StringComparer.Ordinal)
            || !context.Producer.ArtifactSlots.Any(slot => slot.LogicalName == _artifactSlot)
            || !context.Plan.Profile.Producers.Any(producer => producer.Id == Id
                && MatchesGate(producer.CoverageGate))
            || !MatchesGate(context.Producer.CoverageGate)
            || context.Artifacts is null || _request.EvaluationIntent != CoverageEvaluationIntent.CurrentTrustedRun
            || _requirePatch && !patch
            || patch && (expectedPatch is null || expectedPatch.Length != 64 || !expectedPatch.All(char.IsAsciiHexDigit)
                || _request.ExpectedPatchSha256 is { } requested && !string.Equals(requested, expectedPatch, StringComparison.OrdinalIgnoreCase)))
            return new(Id, EvidenceProducerOutcome.Invalid, [], "ASCOV310 Correct the declared current-run coverage obligation and result slot.");
        CoverageFanInResult result;
        try
        {
            result = await CoverageFanIn.AcceptAsync(patch ? _request with { ExpectedPatchSha256 = expectedPatch } : _request,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        { return new(Id, cancellationToken.IsCancellationRequested ? EvidenceProducerOutcome.Cancelled : EvidenceProducerOutcome.TimedOut, [], "ASCOV306 Coverage acceptance was cancelled or timed out before completion."); }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        { return new(Id, EvidenceProducerOutcome.Invalid, [], "ASCOV301 Correct the explicit fan-in request before retrying."); }
        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(ResultProjection(result, _request.Policy), new JsonSerializerOptions
            { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } });
            await context.Artifacts.WriteAsync(_artifactSlot, "coverage/result.json", json, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or NotSupportedException)
        { return new(Id, EvidenceProducerOutcome.Failed, [], "ASCOV309 Result delivery failed; reconcile the exact committed generation before retrying."); }
        catch (OperationCanceledException)
        { return new(Id, cancellationToken.IsCancellationRequested ? EvidenceProducerOutcome.Cancelled : EvidenceProducerOutcome.TimedOut, [], "ASCOV306 Result delivery was cancelled or timed out; reconcile the accepted generation before retrying."); }
        if (result.Outcome != CoverageOperationOutcome.Succeeded || !result.Accepted || !result.Verified
            || !result.Effects.GenerationCommitted || !result.Effects.CleanupComplete
            || result.EvaluationIntent != CoverageEvaluationIntent.CurrentTrustedRun
            || !SatisfiesPatchAssurance(_request.Policy.ContractVersion, _requirePatch, patch, result)
            || _requireObservedPrimary && (result.PrimaryImported || result.Observation != CoverageObservation.CollectedExecution))
            return new(Id, result.Outcome switch
            {
                CoverageOperationOutcome.TimedOut => EvidenceProducerOutcome.TimedOut,
                CoverageOperationOutcome.Cancelled => EvidenceProducerOutcome.Cancelled,
                _ => EvidenceProducerOutcome.Failed
            },
                [], "ASCOV310 Coverage acceptance did not satisfy the declared assurance and delivery requirements.");
        return new(Id, EvidenceProducerOutcome.Passed, [_assertion]);
    }

    private bool MatchesGate(EvidenceCoverageGateRequirements? declared)
    {
        var gate = _request.Policy.Gate;
        return declared is not null && gate is not null && gate.MinLine == declared.MinLinePercent
            && gate.MinBranch == declared.MinBranchPercent && gate.Tolerance == declared.TolerancePercent
            && string.Equals(gate.PatchLineMode, declared.PatchLineMode, StringComparison.OrdinalIgnoreCase)
            && (gate.Mode == CoverageGateMode.AggregateAndPatch
                ? gate.MinPatchLine == declared.MinPatchLinePercent && gate.MinPatchBranch == declared.MinPatchBranchPercent
                : declared.MinPatchLinePercent is null && declared.MinPatchBranchPercent is null);
    }

    private static CoverageAcceptRequest Snapshot(CoverageAcceptRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Policy);
        var policy = request.Policy;
        var supplements = policy.Supplements?.Select(supplement => supplement with
        {
            TestProjects = supplement.TestProjects is null
                ? null
                : Array.AsReadOnly(supplement.TestProjects.ToArray())
        }).ToArray() ?? throw new ArgumentException("Coverage supplements are required.", nameof(request));
        var frozenPolicy = policy with
        {
            TestProjects = Array.AsReadOnly(policy.TestProjects?.ToArray()
                ?? throw new ArgumentException("Coverage test projects are required.", nameof(request))),
            Supplements = Array.AsReadOnly(supplements)
        };
        return request with
        {
            Policy = frozenPolicy,
            SupplementaryCaptureDirectories = Array.AsReadOnly(request.SupplementaryCaptureDirectories?.ToArray()
                ?? throw new ArgumentException("Supplementary capture directories are required.", nameof(request)))
        };
    }

    /// <summary>Checks the patch assertion at the consumer boundary, including all four v3 applicability dimensions.</summary>
    /// <remarks>V2 retains its historical PatchAccepted check. A v3 patch policy always requires complete metric
    /// assurance, even when the caller does not separately require the patch gate as its assertion.</remarks>
    internal static bool SatisfiesPatchAssurance(int contractVersion, bool requirePatch, bool patchRequested,
        CoverageFanInResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (contractVersion is not (2 or 3)) return false;
        if (contractVersion == 2)
            return !requirePatch || string.Equals(result.PatchOutcome, "PatchAccepted", StringComparison.Ordinal);
        if (!patchRequested)
            return !requirePatch && result.PatchOutcome == "PatchNotRequested" && result.PatchApplicability is null;
        return string.Equals(result.PatchOutcome, "PatchAccepted", StringComparison.Ordinal)
            && IsCompleteV3Applicability(result.PatchApplicability);
    }

    private static bool IsCompleteV3Applicability(CoveragePatchApplicability? applicability)
    {
        if (applicability?.Primary is null || applicability.Composed is null) return false;
        var metrics = new[]
        {
            applicability.Primary.Line, applicability.Primary.Branch,
            applicability.Composed.Line, applicability.Composed.Branch
        };
        if (metrics.Any(metric => metric is null || !IsMeasuredOrNotApplicable(metric))) return false;
        return metrics.Select(metric => metric!.ChangedLines).Distinct().Count() == 1;
    }

    private static bool IsMeasuredOrNotApplicable(CoveragePatchMetricResult metric)
        => metric.Outcome switch
        {
            CoveragePatchApplicabilityOutcome.Measured => metric.Reason == "reported-points"
                && metric.ChangedLines is >= 0 && metric.Measurable is > 0 && metric.Covered is >= 0
                && metric.Covered <= metric.Measurable && metric.Percent is >= 0 and <= 100,
            CoveragePatchApplicabilityOutcome.NotApplicable => metric.Reason == "no-reported-patch-points"
                && metric.ChangedLines is >= 0 && metric.Measurable == 0 && metric.Covered == 0 && metric.Percent is null,
            _ => false
        };

    private static object ResultProjection(CoverageFanInResult result, CoverageFanInPolicy policy)
    {
        var artifacts = result.Artifacts.Select(x => new { x.Role, x.Length, x.Sha256 }).ToArray();
        if (policy.ContractVersion == 3)
            return new
            {
                result.Operation,
                result.Outcome,
                result.OperationId,
                result.PolicyDigest,
                result.Effects,
                result.Accepted,
                result.Verified,
                result.PatchOutcome,
                result.Observation,
                result.PrimaryImported,
                result.EvaluationIntent,
                artifacts,
                diagnostics = result.Diagnostics,
                contractVersion = 3,
                reportProfileId = policy.ReportProfileId,
                patchApplicability = result.PatchApplicability
            };
        return new
        {
            result.Operation,
            result.Outcome,
            result.OperationId,
            result.PolicyDigest,
            result.Effects,
            result.Accepted,
            result.Verified,
            result.PatchOutcome,
            result.Observation,
            result.PrimaryImported,
            result.EvaluationIntent,
            artifacts,
            diagnostics = result.Diagnostics.Select(x => new CoverageDiagnosticV2Projection(
                x.Code, x.Stage, x.Field, x.Recovery, x.Documentation)).ToArray()
        };
    }

    private sealed record CoverageDiagnosticV2Projection(string Code, string Stage, string Field, string Recovery,
        string Documentation);
}
