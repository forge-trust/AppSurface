using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Planner;

/// <summary>
/// Binds an independently captured Git change to the closed pull-request policy selection.
/// </summary>
/// <remarks>
/// A v2 plan proves internal consistency only. A gate must supply its own trusted policy and Git
/// object store to <see cref="VerifyAsync"/>, and must separately check the current PR identity
/// and the subject job's provenance immediately before issuing a verdict.
/// </remarks>
public static class EvidenceRevisionPlanBuilder
{
    /// <summary>
    /// Resolves a v2 plan from exact, full commit IDs and fixed-options Git diff/status bytes.
    /// </summary>
    /// <param name="planner">The policy resolver.</param>
    /// <param name="policy">Trusted, base-owned pull-request policy.</param>
    /// <param name="snapshot">Captured exact Git change.</param>
    /// <param name="runIdentity">Optional trusted controller-captured PR/run identity; local rehearsal omits it.</param>
    /// <returns>A canonical digest-bound v2 plan.</returns>
    public static EvidencePlan ResolveForPullRequest(
        EvidencePlanner planner,
        EvidencePolicy policy,
        EvidenceGitChangeSnapshot snapshot,
        EvidencePullRequestRunIdentity? runIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (runIdentity is not null && (runIdentity.RepositoryId <= 0
            || runIdentity.HeadRepositoryId <= 0
            || runIdentity.PullRequestNumber <= 0
            || string.IsNullOrWhiteSpace(runIdentity.TargetBranch)
            || runIdentity.TargetBranch.Length > 128
            || runIdentity.WorkflowRunId <= 0
            || runIdentity.WorkflowRunAttempt <= 0))
        {
            throw new EvidencePlanningException(
                "ASEVD140",
                "The controller PR/run identity is incomplete or malformed.",
                "Capture numeric repository, PR, run, and attempt IDs and a bounded target branch from the trusted event.");
        }

        var local = planner.ResolveForGate(policy, snapshot.ChangedPaths);
        if (local.Profile.Scope == EvidenceProfileScope.Release)
        {
            throw new EvidencePlanningException(
                "ASEVD138",
                "A pull-request event selected a release-only evidence profile.",
                "Use a targeted profile for PR changes; run release evidence only from a protected release event.");
        }

        var draft = local with
        {
            ContractVersion = "2.0",
            BaseRevision = snapshot.BaseRevision,
            HeadRevision = snapshot.HeadRevision,
            SourceDiffDigest = snapshot.SourceDiffDigest,
            NameStatusDigest = snapshot.NameStatusDigest,
            PullRequestRunIdentity = runIdentity,
            PlanDigest = string.Empty,
        };
        return draft with { PlanDigest = EvidenceDigest.CanonicalSha256(draft) };
    }

    /// <summary>
    /// Recreates a v2 pull-request plan using a trusted policy and exact Git objects, rejecting
    /// a modified policy, revision pair, diff, status inventory, profile, or canonical plan digest.
    /// This method does not authenticate the external PR event or job that supplied the plan.
    /// </summary>
    /// <param name="planner">The policy resolver.</param>
    /// <param name="trustedPolicy">Policy read from the trusted base revision.</param>
    /// <param name="repositoryPath">Trusted Git object store containing both commits.</param>
    /// <param name="plan">Untrusted plan to compare with the independently resolved one.</param>
    /// <param name="cancellationToken">Cancellation for bounded Git operations.</param>
    /// <param name="expectedRunIdentity">Separately trusted PR/run identity for a CI gate; omit only for local rehearsal.</param>
    /// <returns>A task that completes only when the plan matches the trusted inputs.</returns>
    public static async Task VerifyAsync(
        EvidencePlanner planner,
        EvidencePolicy trustedPolicy,
        string repositoryPath,
        EvidencePlan plan,
        CancellationToken cancellationToken = default,
        EvidencePullRequestRunIdentity? expectedRunIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(trustedPolicy);
        ArgumentNullException.ThrowIfNull(plan);
        if (!string.Equals(plan.ContractVersion, "2.0", StringComparison.Ordinal)
            || plan.BaseRevision is null
            || plan.HeadRevision is null)
        {
            throw new EvidencePlanningException(
                "ASEVD139",
                "The supplied plan is not revision-bound v2 evidence.",
                "Regenerate the plan from complete base/head commit IDs and a trusted base-owned policy.");
        }

        if (expectedRunIdentity is not null && !Equals(plan.PullRequestRunIdentity, expectedRunIdentity))
        {
            throw new EvidencePlanningException(
                "ASEVD139",
                "The plan PR/run identity differs from the trusted controller identity.",
                "Discard the supplied plan and capture a new one for the authoritative PR, run, and attempt.");
        }

        var snapshot = await EvidenceGitChangeCapture.CaptureAsync(
            repositoryPath, plan.BaseRevision, plan.HeadRevision, cancellationToken).ConfigureAwait(false);
        var expected = ResolveForPullRequest(planner, trustedPolicy, snapshot, expectedRunIdentity ?? plan.PullRequestRunIdentity);
        if (!EvidenceCanonicalJson.Serialize(expected).AsSpan().SequenceEqual(EvidenceCanonicalJson.Serialize(plan)))
        {
            throw new EvidencePlanningException(
                "ASEVD139",
                "The revision-bound plan does not match the trusted policy or exact Git change.",
                "Discard the supplied plan and rerun planning from the base-owned policy and fetched commit objects.");
        }
    }
}
