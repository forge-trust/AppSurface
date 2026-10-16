using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Verifies detached terminal files for a resolved checkpoint-two coverage Observation.</summary>
/// <remarks>
/// This pure-data verifier checks canonical bindings and one declared Cobertura artifact. It does not establish
/// worker execution, root custody, producer authenticity, admission, XML validity, numeric gate success, or native
/// acceptance. Its caller must establish any required execution and custody facts independently. See
/// <see href="coverage-files.md">the coverage files reference</see>.
/// </remarks>
internal static class EvidenceCoverageObservationFiles
{
    /// <summary>Maximum accepted canonical bytes for the fixed seven-field summary.</summary>
    internal const int MaximumSummaryBytes = 4096;

    private const string SupportedAssertionId = "appsurface/coverage/behavioral-patch@1";
    private const string SupportedProducerKind = "coverage";
    private const string SupportedProducerVersion = "1.0.0";
    private const string CoverageArtifactLogicalName = "coverage-report";
    private const string CoverageArtifactRelativePath = "merged/coverage.cobertura.xml";
    private const string CoverageArtifactMediaType = "application/xml";
    private const long MaximumCoverageArtifactBytes = 20L * 1024 * 1024;

    /// <summary>Verifies canonical detached files and one copied artifact against a fully resolved coverage plan.</summary>
    /// <param name="expected">The planner-resolved plan, including its policy snapshot and normalized paths.</param>
    /// <param name="planBytes">Canonical plan bytes bounded by the existing canonical JSON limit.</param>
    /// <param name="manifestBytes">Canonical manifest bytes bounded by the existing canonical JSON limit.</param>
    /// <param name="summaryBytes">The exact seven-field canonical Observation summary.</param>
    /// <param name="artifactBytes">One artifact keyed by <c>producerId/merged/coverage.cobertura.xml</c>.</param>
    /// <param name="token">The original caller token; parsing does not create or renew a deadline.</param>
    /// <returns>A detached manifest whose structure matches the resolved plan; never an admission or proof.</returns>
    /// <exception cref="EvidenceAdmissionException">Fixed ASEVD410 without supplied text or an inner exception.</exception>
    /// <exception cref="OperationCanceledException">The original caller token is cancelled.</exception>
    /// <remarks>
    /// Input lengths are checked before any input memory is copied. The expected plan is independently reconstructed
    /// by the existing <see cref="EvidencePlanner"/> and compared in full.
    /// The single metadata record and actual copied artifact bytes must agree on the fixed declared slot, length and
    /// SHA-256. Policy validation covers every profile, including unselected profiles. The summary contains exactly
    /// Mode, ClaimKind, Eligibility, ExecutionVerdict, EnvelopeStatus, Procedure
    /// and SandboxAttestation; the fixed procedure is registered-protected-producer and attestation is false.
    /// </remarks>
    internal static EvidenceManifest Verify(
        EvidencePlan expected,
        ReadOnlyMemory<byte> planBytes,
        ReadOnlyMemory<byte> manifestBytes,
        ReadOnlyMemory<byte> summaryBytes,
        IReadOnlyDictionary<string, ReadOnlyMemory<byte>> artifactBytes,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            if (expected is null || artifactBytes is null || artifactBytes.Count != 1
                || expected.Profile is null || expected.Profile.Producers.Count != 1
                || string.IsNullOrWhiteSpace(expected.Profile.Producers[0].Id))
                throw Rejected();

            CheckLength(planBytes, EvidenceCanonicalJson.MaximumInputBytes);
            CheckLength(manifestBytes, EvidenceCanonicalJson.MaximumInputBytes);
            CheckLength(summaryBytes, MaximumSummaryBytes);
            var physicalArtifactPath = expected.Profile.Producers[0].Id + "/" + CoverageArtifactRelativePath;
            if (!artifactBytes.TryGetValue(physicalArtifactPath, out var artifactMemory))
                throw Rejected();
            CheckLength(artifactMemory, MaximumCoverageArtifactBytes);

            var planCopy = planBytes.ToArray();
            var manifestCopy = manifestBytes.ToArray();
            var summaryCopy = summaryBytes.ToArray();
            var artifactCopy = artifactMemory.ToArray();
            token.ThrowIfCancellationRequested();

            var expectedBytes = EvidenceCanonicalJson.Serialize(expected);
            CheckLength(expectedBytes, EvidenceCanonicalJson.MaximumInputBytes);
            var snapshot = EvidenceCanonicalJson.Deserialize<EvidencePlan>(expectedBytes);
            var policy = snapshot.PolicySnapshot;
            if (policy is null || snapshot.ChangedPaths.Count == 0
                || snapshot.Profile.Scope != EvidenceProfileScope.Targeted
                || snapshot.Profile.Id == policy.ConservativeProfileId
                || snapshot.Profile.Resources.Count != 0
                || snapshot.Profile.Producers.Count != 1
                || !IsCompatibleCoverageProducer(snapshot.Profile.Producers[0]))
                throw Rejected();

            token.ThrowIfCancellationRequested();
            EvidencePlanner.ValidatePolicy(policy);
            var resolved = new EvidencePlanner().Resolve(policy, snapshot.ChangedPaths);
            RequireSame(expectedBytes, EvidenceCanonicalJson.Serialize(resolved));
            var plan = ReadCanonical<EvidencePlan>(planCopy);
            RequireSame(expectedBytes, EvidenceCanonicalJson.Serialize(plan));
            token.ThrowIfCancellationRequested();

            var producer = snapshot.Profile.Producers[0];
            var manifest = ReadCanonical<EvidenceManifest>(manifestCopy);
            if (manifest.Mode != EvidenceExecutionMode.Observation
                || manifest.ExecutionVerdict != EvidenceExecutionVerdict.Passed
                || manifest.ClaimKind != EvidenceClaimKind.ObservationOnly
                || manifest.Eligibility != EvidenceClaimEligibility.Informational
                || manifest.EnvelopeStatus != EvidenceEnvelopeStatus.NotRequired
                || manifest.EnvelopeAssertion is not null
                || manifest.ResourceResults.Count != 0
                || manifest.ProducerResults.Count != 1
                || manifest.UnmediatedObligationIds.Count != 0
                || !ValidMetrics(manifest.Metrics, manifest.ProducerResults[0])
                || !ValidProducerResult(producer, manifest.ProducerResults[0], artifactCopy)
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

    private static bool IsCompatibleCoverageProducer(EvidenceProducerDeclaration producer)
    {
        if (producer.Kind != SupportedProducerKind || producer.Version != SupportedProducerVersion
            || producer.RequiredResources.Count != 0 || producer.TimeoutSeconds is < 1 or > 1800
            || producer.CoverageGate is null || producer.AssertionIds.Count != 1
            || producer.AssertionIds[0] != SupportedAssertionId || producer.ArtifactSlots.Count != 1)
            return false;

        var slot = producer.ArtifactSlots[0];
        return slot.LogicalName == CoverageArtifactLogicalName
            && slot.RelativeRoot == "merged" && slot.MediaType == CoverageArtifactMediaType
            && slot.Required && slot.MaximumBytes == MaximumCoverageArtifactBytes;
    }

    private static bool ValidProducerResult(EvidenceProducerDeclaration producer,
        EvidenceProducerResult result, byte[] artifactBytes)
    {
        if (result.ProducerId != producer.Id || result.Outcome != EvidenceProducerOutcome.Passed
            || result.SatisfiedAssertionIds.Count != 1
            || result.SatisfiedAssertionIds[0] != SupportedAssertionId || result.ElapsedMilliseconds < 0
            || result.Artifacts is null || result.Artifacts.Count != 1)
            return false;

        var artifact = result.Artifacts[0];
        return EvidenceArtifactValidation.AreValid(producer, result.Artifacts)
            && artifact.LogicalName == CoverageArtifactLogicalName
            && artifact.RelativePath == CoverageArtifactRelativePath
            && artifact.MediaType == CoverageArtifactMediaType
            && artifact.LengthBytes == artifactBytes.LongLength
            && artifact.Sha256 == EvidenceDigest.Sha256(artifactBytes);
    }

    private static bool ValidMetrics(EvidenceExecutionMetrics metrics, EvidenceProducerResult result)
    {
        if (!metrics.CleanupCompleted || metrics.TerminalFailureCode is not null
            || metrics.CleanupDiagnostic is not null || metrics.ResourceReadinessMilliseconds != 0
            || metrics.PlanningMilliseconds < 0 || metrics.ProducerMilliseconds < 0
            || metrics.CleanupMilliseconds < 0 || metrics.TotalMilliseconds < 0
            || result.ElapsedMilliseconds < 0)
            return false;

        // Current CLI manifests report cleanup state but leave total/stage wall-clock metrics at zero.
        // Treat zero total as unreported; enforce arithmetic consistency only when a total was supplied.
        if (metrics.TotalMilliseconds == 0)
            return metrics.PlanningMilliseconds == 0 && metrics.ProducerMilliseconds == 0
                && metrics.CleanupMilliseconds == 0;

        if (metrics.PlanningMilliseconds > metrics.TotalMilliseconds) return false;
        var remaining = metrics.TotalMilliseconds - metrics.PlanningMilliseconds;
        if (metrics.ProducerMilliseconds > remaining) return false;
        remaining -= metrics.ProducerMilliseconds;
        return metrics.CleanupMilliseconds <= remaining;
    }

    private static TValue ReadCanonical<TValue>(byte[] bytes)
    {
        var value = EvidenceCanonicalJson.Deserialize<TValue>(bytes);
        RequireSame(bytes, EvidenceCanonicalJson.Serialize(value));
        return value;
    }

    private static void CheckLength(ReadOnlyMemory<byte> bytes, long maximum)
    {
        if (bytes.Length is 0 || bytes.Length > maximum) throw Rejected();
    }

    private static void RequireSame(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        if (!first.SequenceEqual(second)) throw Rejected();
    }

    private static EvidenceAdmissionException Rejected() =>
        new("ASEVD410", "The terminal coverage Observation files could not be verified.");
}
