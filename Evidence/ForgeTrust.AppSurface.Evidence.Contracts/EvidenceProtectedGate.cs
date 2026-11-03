namespace ForgeTrust.AppSurface.Evidence.Contracts;

/// <summary>Expected facts obtained through the consumer's protected gate channel, never from uploaded subject files.</summary>
/// <param name="RunId">Exact current run and attempt.</param>
/// <param name="BaseRevision">Expected immutable protected tool/policy revision.</param>
/// <param name="SubjectRevision">Expected tested revision.</param>
/// <param name="WorkflowIdentity">Expected immutable protected workflow.</param>
/// <param name="PolicyDigest">Expected protected policy digest.</param>
/// <param name="AcceptanceProofDigest">Expected current successful full consumer/platform proof.</param>
/// <param name="OutputIdentity">Expected actual allocated output handle identity.</param>
/// <param name="VerifierId">Exact protected verifier registration.</param>
/// <param name="VerifierVersion">Exact protected verifier version.</param>
/// <param name="Provider">Accepted CI provider.</param>
/// <param name="CatalogueDigest">Expected closed producer/resource catalogue digest.</param>
/// <param name="CapabilitiesDigest">Expected secret-free capability digest.</param>
/// <param name="AllocationPolicyDigest">Expected protected allocation policy digest.</param>
/// <param name="ToolRootIdentity">Expected protected tool root identity.</param>
/// <param name="SubjectRootIdentity">Expected separately restricted subject root identity.</param>
/// <param name="OutputParentIdentity">Expected protected output allocation parent identity.</param>
/// <param name="AllowReleaseValidatedNotAttested">Explicit release opt-in to this limited assertion; defaults false.</param>
public sealed record EvidenceProtectedGateExpectation(string RunId, string BaseRevision, string SubjectRevision,
    string WorkflowIdentity, string PolicyDigest, string AcceptanceProofDigest, string OutputIdentity,
    string VerifierId, string VerifierVersion, string Provider, string CatalogueDigest,
    string CapabilitiesDigest, string AllocationPolicyDigest, string ToolRootIdentity,
    string SubjectRootIdentity, string OutputParentIdentity,
    bool AllowReleaseValidatedNotAttested = false);

/// <summary>Evaluates protected downstream Evidence claims without rerunning subject work or minting runtime admission.</summary>
/// <remarks>
/// The caller must obtain both the expectation and collected artifacts through the protected channel. Structural
/// hashes and an uploaded plan/manifest pair cannot authenticate their own origin. This API does not independently
/// attest a sandbox or application correctness; it checks the recorded protected procedure and current identities.
/// </remarks>
public static class EvidenceProtectedGate
{
    /// <summary>Requires a current Trusted assertion, closed obligations, valid structural bindings and completed cleanup.</summary>
    /// <param name="plan">Plan selected by protected base policy.</param>
    /// <param name="manifest">Protected collected manifest, or null for missing/quarantined output.</param>
    /// <param name="expected">Current facts supplied by the protected gate consumer.</param>
    /// <returns>True only for an eligible matching protected claim; missing or stale output returns false.</returns>
    public static bool Allows(EvidencePlan plan, EvidenceManifest? manifest, EvidenceProtectedGateExpectation expected)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(expected);
        if (manifest is null || manifest.Mode != EvidenceExecutionMode.Trusted
            || manifest.ExecutionVerdict != EvidenceExecutionVerdict.Passed
            || manifest.EnvelopeStatus != EvidenceEnvelopeStatus.ValidatedNotAttested
            || !manifest.Metrics.CleanupCompleted || manifest.Metrics.TerminalFailureCode is not null
            || manifest.UnmediatedObligationIds.Count != 0
            || plan.PolicyDigest != expected.PolicyDigest
            || manifest.EnvelopeAssertion is not { } assertion
            || !EvidenceAdmission.ValidAssertion(assertion, expected.RunId)
            || assertion.BaseRevision != expected.BaseRevision || assertion.SubjectRevision != expected.SubjectRevision
            || assertion.WorkflowIdentity != expected.WorkflowIdentity || assertion.OutputIdentity != expected.OutputIdentity
            || assertion.AcceptanceProofDigest != expected.AcceptanceProofDigest
            || assertion.VerifierId != expected.VerifierId || assertion.VerifierVersion != expected.VerifierVersion
            || assertion.Provider != expected.Provider || assertion.CatalogueDigest != expected.CatalogueDigest
            || assertion.CapabilitiesDigest != expected.CapabilitiesDigest
            || assertion.AllocationPolicyDigest != expected.AllocationPolicyDigest
            || assertion.ToolRootIdentity != expected.ToolRootIdentity
            || assertion.SubjectRootIdentity != expected.SubjectRootIdentity
            || assertion.OutputParentIdentity != expected.OutputParentIdentity
            || string.IsNullOrWhiteSpace(expected.OutputIdentity)
            || !EvidenceManifestBuilder.Verify(plan, manifest))
        {
            return false;
        }

        return manifest.ClaimKind switch
        {
            EvidenceClaimKind.TargetedComplete or EvidenceClaimKind.NoEvidenceRequired => manifest.Eligibility == EvidenceClaimEligibility.PullRequestGate,
            EvidenceClaimKind.ReleaseComplete => manifest.Eligibility == EvidenceClaimEligibility.ReleaseGate && expected.AllowReleaseValidatedNotAttested,
            _ => false,
        };
    }
}
