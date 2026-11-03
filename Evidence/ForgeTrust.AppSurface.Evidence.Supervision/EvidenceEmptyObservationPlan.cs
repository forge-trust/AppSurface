using System.Text;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Resolves the checkpoint-one empty Observation procedure from counted, hash-bound policy data.</summary>
/// <remarks>
/// This preflight creates only a planner snapshot. It does not admit execution, register providers, open an output
/// lease or establish proof. The worker independently resolves its protected inputs and performs admission.
/// </remarks>
internal static class EvidenceEmptyObservationPlan
{
    /// <summary>Validates copied policy/diff bytes and resolves a genuine allowed empty targeted plan.</summary>
    /// <param name="request">Already parsed immutable launch metadata, without native authority.</param>
    /// <param name="policyBytes">Actual policy bytes, nonempty and at most the canonical JSON input limit.</param>
    /// <param name="expectedPolicySha256">Lowercase SHA-256 of the original policy bytes, not canonicalized JSON.</param>
    /// <param name="diffBytes">Actual diff bytes, present exactly when the request declares a protected diff.</param>
    /// <param name="token">Cancellation checked between parsing, validation and resolution phases.</param>
    /// <returns>A detached snapshot produced by the existing planner, with its genuine policy, diff and plan digests.</returns>
    /// <exception cref="EvidenceAdmissionException">Fixed ASEVD406 without input text or an inner exception.</exception>
    /// <exception cref="OperationCanceledException">The original caller token was cancelled.</exception>
    /// <remarks>
    /// All whitelisted profiles must be empty targeted profiles, even if not selected. Unmatched or mixed paths
    /// retain the planner's conservative fallback and are rejected. Synchronous planning does not renew a deadline.
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
            if (request is null || request.ObservationProducerIds.Count != 0
                || request.ObservationProfileIds.Count == 0 || !IsDigest(expectedPolicySha256))
                throw Rejected();

            var policyCopy = CopyCounted(policyBytes);
            if (EvidenceDigest.Sha256(policyCopy) != expectedPolicySha256) throw Rejected();
            token.ThrowIfCancellationRequested();
            var policy = EvidenceCanonicalJson.Deserialize<EvidencePolicy>(policyCopy);
            EvidencePlanner.ValidatePolicy(policy);
            var profiles = policy.Profiles.ToDictionary(static profile => profile.Id, StringComparer.Ordinal);
            foreach (var id in request.ObservationProfileIds)
            {
                token.ThrowIfCancellationRequested();
                if (!profiles.TryGetValue(id, out var profile) || !IsEmptyTargeted(profile)) throw Rejected();
            }

            var paths = request.Paths.Select(static path => new NormalizedDiffPath(path)).ToList();
            if (request.DiffFile is null)
            {
                if (diffBytes.HasValue) throw Rejected();
            }
            else
            {
                if (!diffBytes.HasValue) throw Rejected();
                var diffCopy = CopyCounted(diffBytes.Value);
                if (EvidenceDigest.Sha256(diffCopy) != request.DiffSha256) throw Rejected();
                token.ThrowIfCancellationRequested();
                // Match the existing protected worker's unified-diff decoding and planning semantics.
                paths.AddRange(EvidenceUnifiedDiffReader.Read(Encoding.UTF8.GetString(diffCopy)));
            }

            token.ThrowIfCancellationRequested();
            var plan = new EvidencePlanner().Resolve(policy, paths);
            if (!IsEmptyTargeted(plan.Profile) || plan.Profile.Id == policy.ConservativeProfileId
                || !request.ObservationProfileIds.Contains(plan.Profile.Id, StringComparer.Ordinal))
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

    /// <summary>Reads the actual retained launch input and rechecks its original native bindings and deadline.</summary>
    /// <param name="input">The genuine root-owned retained input; data parsing cannot construct it.</param>
    /// <param name="token">The original operation cancellation token.</param>
    /// <returns>Only the data plan; worker admission and lifecycle ownership remain separate requirements.</returns>
    /// <remarks>
    /// The input's reads independently verify byte hashes and retained filesystem identities. Full rechecks bracket
    /// this operation, including after synchronous resolution, using the input's original monotonic allowance.
    /// Native input failures remain the input's fixed diagnostic; invalid plan data yields ASEVD406.
    /// </remarks>
    internal static EvidencePlan FromInput(EvidenceProtectedLaunchInput input, CancellationToken token)
    {
        if (input is null) throw Rejected();
        input.Recheck(token);
        var policy = input.ReadPolicyBytes(token);
        var diff = input.ReadDiffBytes(token);
        var diffMemory = AsOptionalDiff(diff);
        var plan = Resolve(input.Request, policy, input.PolicySha256, diffMemory, token);
        input.Recheck(token);
        token.ThrowIfCancellationRequested();
        return plan;
    }

    /// <summary>Preserves absence when adapting copied optional diff bytes to read-only memory.</summary>
    /// <param name="diff">Copied diff bytes, or null when no diff is declared.</param>
    /// <returns>Nullable absence for null; present memory for every array, including an empty array.</returns>
    /// <remarks>
    /// This data-only conversion does not validate bytes or create protected input or admission. The memory
    /// views the supplied array; <see cref="Resolve"/> independently counts, copies and hash checks declared diff data.
    /// Explicit nullable null avoids converting null through the array-to-memory operator into present empty memory.
    /// </remarks>
    internal static ReadOnlyMemory<byte>? AsOptionalDiff(byte[]? diff) =>
        diff is null ? (ReadOnlyMemory<byte>?)null : new ReadOnlyMemory<byte>(diff);

    private static byte[] CopyCounted(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length is 0 or > EvidenceCanonicalJson.MaximumInputBytes) throw Rejected();
        return bytes.ToArray();
    }

    private static bool IsEmptyTargeted(EvidenceProfile profile) => profile.Scope == EvidenceProfileScope.Targeted
        && profile.Resources.Count == 0 && profile.Producers.Count == 0 && profile.Obligations.Count == 0;

    private static bool IsDigest(string? value) => value is { Length: 64 }
        && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static EvidenceAdmissionException Rejected() =>
        new("ASEVD406", "The protected empty Observation plan could not be validated.");
}
