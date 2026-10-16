using System.Text;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Resolves checkpoint-two's closed, dependency-free coverage Observation plan from protected policy data.</summary>
/// <remarks>
/// This preflight returns only the existing planner's data snapshot. It does not select a runtime implementation,
/// admit work, create a producer lease, retain artifacts, or establish a coverage result. The selected declaration
/// must match the fixed coverage procedure and artifact contract; its numeric gate remains owned by the existing
/// restricted coverage producer. See <see href="coverage-plan.md">the coverage plan reference</see>.
/// </remarks>
internal static class EvidenceCoverageObservationPlan
{
    private const string SupportedProducerKind = "coverage";
    private const string SupportedProducerVersion = "1.0.0";
    private const string SupportedAssertionId = "appsurface/coverage/behavioral-patch@1";
    private const long MaximumCoverageReportBytes = 20L * 1024 * 1024;

    /// <summary>Validates copied policy/diff bytes and resolves one allowed targeted coverage plan.</summary>
    /// <param name="request">Parsed immutable launch metadata, without native authority.</param>
    /// <param name="policyBytes">Actual policy bytes, nonempty and within the canonical JSON input limit.</param>
    /// <param name="expectedPolicySha256">Lowercase SHA-256 of the original policy bytes.</param>
    /// <param name="diffBytes">Actual diff bytes, present exactly when the request declares a protected diff.</param>
    /// <param name="token">The original caller token, checked between validation and planner phases.</param>
    /// <returns>The existing planner's detached plan snapshot; it does not grant execution or artifact authority.</returns>
    /// <exception cref="EvidenceAdmissionException">Fixed ASEVD406 without input text or an inner exception.</exception>
    /// <exception cref="OperationCanceledException">The caller token was cancelled.</exception>
    /// <remarks>
    /// Every whitelisted profile must be either empty and targeted or contain exactly one compatible coverage
    /// producer with no resources. The resolved profile must be the non-conservative compatible coverage profile.
    /// Unmatched paths, renames whose previous path selects another profile, and mixed-profile diffs retain the
    /// planner's conservative fallback and are rejected. Resolution never renews the protected input deadline.
    /// </remarks>
    internal static EvidencePlan Resolve(
        EvidenceSupervisorRequest request,
        ReadOnlyMemory<byte> policyBytes,
        string expectedPolicySha256,
        ReadOnlyMemory<byte>? diffBytes = null,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            if (request is null || request.ObservationProfileIds.Count == 0
                || !IsDigest(expectedPolicySha256))
                throw Rejected();

            var policyCopy = CopyCounted(policyBytes);
            if (!string.Equals(EvidenceDigest.Sha256(policyCopy), expectedPolicySha256, StringComparison.Ordinal))
                throw Rejected();
            token.ThrowIfCancellationRequested();

            var policy = EvidenceCanonicalJson.Deserialize<EvidencePolicy>(policyCopy);
            EvidencePlanner.ValidatePolicy(policy);
            var profiles = policy.Profiles.ToDictionary(static profile => profile.Id, StringComparer.Ordinal);
            var allowedProducerIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in request.ObservationProfileIds)
            {
                token.ThrowIfCancellationRequested();
                if (!profiles.TryGetValue(id, out var profile) || !IsEmptyTargeted(profile)
                    && !IsCompatibleCoverageProfile(profile))
                    throw Rejected();
                foreach (var producer in profile.Producers)
                    allowedProducerIds.Add(producer.Id);
            }

            if (request.ObservationProducerIds.Any(id => !allowedProducerIds.Contains(id)))
                throw Rejected();

            var paths = request.Paths.Select(static path => new NormalizedDiffPath(path)).ToList();
            if (request.DiffFile is null)
            {
                if (diffBytes.HasValue) throw Rejected();
            }
            else
            {
                if (!diffBytes.HasValue) throw Rejected();
                var diffCopy = CopyCounted(diffBytes.Value);
                if (!string.Equals(EvidenceDigest.Sha256(diffCopy), request.DiffSha256, StringComparison.Ordinal))
                    throw Rejected();
                token.ThrowIfCancellationRequested();
                // Keep the protected worker's existing unified-diff decoding and path-planning semantics.
                paths.AddRange(EvidenceUnifiedDiffReader.Read(Encoding.UTF8.GetString(diffCopy)));
            }

            token.ThrowIfCancellationRequested();
            var plan = new EvidencePlanner().Resolve(policy, paths);
            if (string.Equals(plan.Profile.Id, policy.ConservativeProfileId, StringComparison.Ordinal)
                || !request.ObservationProfileIds.Contains(plan.Profile.Id, StringComparer.Ordinal)
                || !IsCompatibleCoverageProfile(plan.Profile)
                || !request.ObservationProducerIds.Contains(plan.Profile.Producers[0].Id, StringComparer.Ordinal))
                throw Rejected();

            token.ThrowIfCancellationRequested();
            return plan;
        }
        catch (Exception error) when (error is not OperationCanceledException
            and not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            throw Rejected();
        }
    }

    /// <summary>Reads the actual retained launch input and brackets planning with full native/deadline rechecks.</summary>
    /// <param name="input">The retained root-owned request and deployment; data parsing cannot construct it.</param>
    /// <param name="token">The original operation cancellation token.</param>
    /// <returns>Only the planner snapshot; worker admission and artifact custody remain separate requirements.</returns>
    /// <exception cref="EvidenceAdmissionException">Invalid plan data yields fixed ASEVD406; retained-input failures keep their fixed diagnostic.</exception>
    /// <exception cref="OperationCanceledException">The original caller token was cancelled.</exception>
    /// <remarks>
    /// Policy and diff reads recheck their retained file bindings. Full request/deployment rechecks run before the
    /// reads and after synchronous resolution, using the input's original monotonic allowance.
    /// </remarks>
    internal static EvidencePlan FromInput(EvidenceProtectedLaunchInput input, CancellationToken token)
    {
        if (input is null) throw Rejected();
        input.Recheck(token);
        var policy = input.ReadPolicyBytes(token);
        var diff = input.ReadDiffBytes(token);
        var plan = Resolve(input.Request, policy, input.PolicySha256, AsOptionalDiff(diff), token);
        input.Recheck(token);
        token.ThrowIfCancellationRequested();
        return plan;
    }

    /// <summary>Preserves absent diff data when adapting copied optional bytes to read-only memory.</summary>
    /// <param name="diff">Copied bytes, or null when the protected request declares no diff.</param>
    /// <returns>Nullable absence for null; present memory for every array, including an empty array.</returns>
    /// <remarks>
    /// The returned memory views the supplied array. <see cref="Resolve"/> independently counts, copies and checks
    /// the digest for every declared diff. Explicit nullable null avoids converting a null array to present empty memory.
    /// </remarks>
    internal static ReadOnlyMemory<byte>? AsOptionalDiff(byte[]? diff) =>
        diff is null ? (ReadOnlyMemory<byte>?)null : new ReadOnlyMemory<byte>(diff);

    private static bool IsEmptyTargeted(EvidenceProfile profile) =>
        profile.Scope == EvidenceProfileScope.Targeted && profile.Resources.Count == 0
        && profile.Producers.Count == 0 && profile.Obligations.Count == 0;

    private static bool IsCompatibleCoverageProfile(EvidenceProfile profile) =>
        profile.Scope == EvidenceProfileScope.Targeted && profile.Resources.Count == 0
        && profile.Producers.Count == 1 && IsCompatibleCoverageProducer(profile.Producers[0]);

    private static bool IsCompatibleCoverageProducer(EvidenceProducerDeclaration producer)
    {
        if (producer.Kind != SupportedProducerKind || producer.Version != SupportedProducerVersion
            || producer.RequiredResources.Count != 0 || producer.TimeoutSeconds is < 1 or > 1800
            || producer.CoverageGate is null || producer.AssertionIds.Count != 1
            || producer.AssertionIds[0] != SupportedAssertionId || producer.ArtifactSlots.Count != 1)
            return false;

        var slot = producer.ArtifactSlots[0];
        return slot.LogicalName == "coverage-report" && slot.RelativeRoot == "merged"
            && slot.MediaType == "application/xml" && slot.Required
            && slot.MaximumBytes == MaximumCoverageReportBytes;
    }

    private static byte[] CopyCounted(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length is 0 or > EvidenceCanonicalJson.MaximumInputBytes) throw Rejected();
        return bytes.ToArray();
    }

    private static bool IsDigest(string? value) => value is { Length: 64 }
        && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static EvidenceAdmissionException Rejected() =>
        new("ASEVD406", "The protected coverage Observation plan could not be validated.");
}
