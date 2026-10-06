using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Checks detached checkpoint-one files without granting native ownership or evidence admission.</summary>
/// <remarks>
/// The native caller must first establish successful server completion, normal worker exit, physical joins and
/// root custody. This class checks only copied data. A structurally valid manifest is not accepted consumer proof.
/// </remarks>
internal static class EvidenceEmptyObservationFiles
{
    /// <summary>Gets the lower byte ceiling for the fixed seven-field canonical summary.</summary>
    internal const int MaximumSummaryBytes = 4096;

    /// <summary>Validates canonical files against the complete actual protected empty Observation plan.</summary>
    /// <param name="expected">The existing planner's protected expected plan, including policy and normalized paths.</param>
    /// <param name="planBytes">Canonical plan bytes, nonempty and bounded by the existing canonical JSON ceiling.</param>
    /// <param name="manifestBytes">Canonical manifest bytes with the same existing ceiling.</param>
    /// <param name="summaryBytes">Canonical summary bytes, nonempty and at most 4096 bytes.</param>
    /// <param name="token">Original caller cancellation; synchronous parsing never creates or renews a deadline.</param>
    /// <returns>A deserialized detached manifest, not an admission, enrollment, lease or native authority.</returns>
    /// <exception cref="EvidenceAdmissionException">Fixed ASEVD410 without supplied text or an inner exception.</exception>
    /// <exception cref="OperationCanceledException">The original caller token is cancelled.</exception>
    /// <remarks>
    /// All three lengths are checked before copying any input. Exact canonical bytes reject unknown properties,
    /// spelling aliases and alternate encodings which the general additive-compatible reader would otherwise accept.
    /// The expected plan is independently reconstructed with the real planner; digests alone do not replace full
    /// plan equality. The summary has exactly Mode, ClaimKind, Eligibility, ExecutionVerdict, EnvelopeStatus,
    /// Procedure and SandboxAttestation; its procedure is registered-protected-producer and attestation is false.
    /// </remarks>
    internal static EvidenceManifest Verify(EvidencePlan expected, ReadOnlyMemory<byte> planBytes,
        ReadOnlyMemory<byte> manifestBytes, ReadOnlyMemory<byte> summaryBytes, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            if (expected is null) throw Rejected();
            CheckLength(planBytes, EvidenceCanonicalJson.MaximumInputBytes);
            CheckLength(manifestBytes, EvidenceCanonicalJson.MaximumInputBytes);
            CheckLength(summaryBytes, MaximumSummaryBytes);
            var planCopy = planBytes.ToArray();
            var manifestCopy = manifestBytes.ToArray();
            var summaryCopy = summaryBytes.ToArray();
            token.ThrowIfCancellationRequested();

            var expectedBytes = EvidenceCanonicalJson.Serialize(expected);
            CheckLength(expectedBytes, EvidenceCanonicalJson.MaximumInputBytes);
            var snapshot = EvidenceCanonicalJson.Deserialize<EvidencePlan>(expectedBytes);
            if (snapshot.PolicySnapshot is null || snapshot.ChangedPaths.Count == 0
                || snapshot.Profile.Scope != EvidenceProfileScope.Targeted
                || snapshot.Profile.Resources.Count != 0 || snapshot.Profile.Producers.Count != 0
                || snapshot.Profile.Obligations.Count != 0)
                throw Rejected();

            token.ThrowIfCancellationRequested();
            var resolved = new EvidencePlanner().Resolve(snapshot.PolicySnapshot, snapshot.ChangedPaths);
            RequireSame(expectedBytes, EvidenceCanonicalJson.Serialize(resolved));
            var plan = ReadCanonical<EvidencePlan>(planCopy);
            RequireSame(expectedBytes, EvidenceCanonicalJson.Serialize(plan));
            token.ThrowIfCancellationRequested();

            var manifest = ReadCanonical<EvidenceManifest>(manifestCopy);
            if (manifest.Mode != EvidenceExecutionMode.Observation
                || manifest.ExecutionVerdict != EvidenceExecutionVerdict.Passed
                || manifest.ClaimKind != EvidenceClaimKind.ObservationOnly
                || manifest.Eligibility != EvidenceClaimEligibility.Informational
                || manifest.EnvelopeStatus != EvidenceEnvelopeStatus.NotRequired
                || manifest.EnvelopeAssertion is not null
                || manifest.ResourceResults.Count != 0 || manifest.ProducerResults.Count != 0
                || manifest.SelectedObligationIds.Count != 0 || manifest.ClosedObligationIds.Count != 0
                || manifest.UnmediatedObligationIds.Count != 0 || !ValidMetrics(manifest.Metrics)
                || !EvidenceManifestBuilder.Verify(plan, manifest))
                throw Rejected();

            token.ThrowIfCancellationRequested();
            var summary = EvidenceCanonicalJson.Deserialize<JsonElement>(summaryCopy, MaximumSummaryBytes);
            if (summary.ValueKind != JsonValueKind.Object) throw Rejected();
            RequireSame(summaryCopy, EvidenceCanonicalJson.Serialize(new
            {
                manifest.Mode, manifest.ClaimKind, manifest.Eligibility, manifest.ExecutionVerdict,
                manifest.EnvelopeStatus, Procedure = "registered-protected-producer", SandboxAttestation = false,
            }));
            token.ThrowIfCancellationRequested();
            return manifest;
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested || error.CancellationToken != token)
        {
            throw Rejected();
        }
        catch (Exception error) when (error is not OperationCanceledException
            and not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            throw Rejected();
        }
    }

    private static TValue ReadCanonical<TValue>(byte[] bytes)
    {
        var value = EvidenceCanonicalJson.Deserialize<TValue>(bytes);
        RequireSame(bytes, EvidenceCanonicalJson.Serialize(value));
        return value;
    }

    private static bool ValidMetrics(EvidenceExecutionMetrics metrics) => metrics.CleanupCompleted
        && metrics.TerminalFailureCode is null && metrics.CleanupDiagnostic is null
        && metrics.ResourceReadinessMilliseconds == 0 && metrics.ProducerMilliseconds == 0
        && metrics.PlanningMilliseconds >= 0 && metrics.CleanupMilliseconds >= 0
        && metrics.TotalMilliseconds >= metrics.PlanningMilliseconds
        && metrics.CleanupMilliseconds <= metrics.TotalMilliseconds - metrics.PlanningMilliseconds;

    private static void CheckLength(ReadOnlyMemory<byte> bytes, int maximum)
    {
        if (bytes.Length is 0 || bytes.Length > maximum) throw Rejected();
    }

    private static void RequireSame(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        if (!first.SequenceEqual(second)) throw Rejected();
    }

    private static EvidenceAdmissionException Rejected() =>
        new("ASEVD410", "The terminal empty Observation files could not be verified.");
}
