using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Planner;

/// <summary>Re-resolves protected worker inputs without importing any subject policy, catalogue or verifier.</summary>
/// <remarks>The caller arms supervision before this operation and tracks the complete task under the admission deadline.</remarks>
internal static class EvidenceProtectedWorkerInputs
{
    /// <summary>Reads counted protected policy bytes and binds the launcher's byte digest before planning.</summary>
    internal static async Task<(EvidencePolicy Policy, EvidencePlan Plan, byte[]? DiffBytes)> ResolveAsync(
        EvidenceLinuxWorkerDescriptor descriptor, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(descriptor.PolicyFile, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var bytes = await EvidenceCanonicalJson.ReadBoundedInputAsync(input, EvidenceCanonicalJson.MaximumInputBytes, cancellationToken).ConfigureAwait(false);
        if (EvidenceDigest.Sha256(bytes) != descriptor.PolicySha256)
            throw new EvidenceAdmissionException("ASEVD403", "Protected policy bytes changed after launcher binding.");
        var policy = EvidenceCanonicalJson.Deserialize<EvidencePolicy>(bytes);
        var planner = new EvidencePlanner();
        var paths = descriptor.Paths.Select(static path => new NormalizedDiffPath(path)).ToList();
        byte[]? diffBytes = null;
        if (descriptor.DiffFile is not null)
        {
            // The launcher selects an immutable copied diff beneath protected tooling, not a subject-selected path.
            if (!descriptor.DiffFile.StartsWith(descriptor.ToolRoot.TrimEnd('/') + "/", StringComparison.Ordinal))
                throw new EvidenceAdmissionException("ASEVD403", "The diff source is not a protected snapshot.");
            await using var diff = new FileStream(descriptor.DiffFile, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            diffBytes = await EvidenceCanonicalJson.ReadBoundedInputAsync(diff, EvidenceCanonicalJson.MaximumInputBytes, cancellationToken).ConfigureAwait(false);
            if (EvidenceDigest.Sha256(diffBytes) != descriptor.DiffSha256)
                throw new EvidenceAdmissionException("ASEVD403", "Protected diff bytes changed after launcher binding.");
            paths.AddRange(EvidenceUnifiedDiffReader.Read(System.Text.Encoding.UTF8.GetString(diffBytes)));
        }

        return (policy, planner.Resolve(policy, paths), diffBytes);
    }

    /// <summary>Builds the internal registration facts solely from the protected policy and launcher descriptor.</summary>
    /// <param name="descriptor">Authenticated root descriptor with independently allocated worker identities.</param>
    /// <param name="protectedPolicy">Complete protected policy read under counted byte limits.</param>
    /// <param name="protectedResolvedPlan">Plan re-resolved from that policy and protected paths/diff.</param>
    /// <returns>Admission input facts; accepted consumer proof remains a separate closed registry decision.</returns>
    /// <remarks>
    /// V1 retains its producer/resource binding. V2 first resolves the complete application against the
    /// immutable compiled catalogue, then includes its definition, grants and full identity map in the
    /// capability digest. An empty compiled table rejects before any context or application lease exists.
    /// </remarks>
    internal static EvidenceAdmissionContext CreateContext(
        EvidenceLinuxWorkerDescriptor descriptor, EvidencePolicy protectedPolicy, EvidencePlan protectedResolvedPlan)
    {
        EvidenceClosedApplicationDefinition? application = null;
        if (descriptor.Schema == "evidence-worker-linux-v2" || descriptor.Application is not null)
        {
            // Structural descriptor parsing is not registration authority. Select only
            // a complete immutable compiled entry after protected policy/plan resolution.
            application = EvidenceClosedApplicationCatalogue.Resolve(protectedPolicy, protectedResolvedPlan, descriptor);
        }
        else if (descriptor.Schema != "evidence-worker-linux-v1")
        {
            throw new EvidenceAdmissionException("ASEVD404", "The protected worker protocol has no supported catalogue binding.");
        }

        var catalogueDigest = application is null ? EvidenceDigest.CanonicalSha256(new
        {
            Producers = protectedResolvedPlan.Profile.Producers,
            Resources = protectedResolvedPlan.Profile.Resources,
        }) : descriptor.Application!.CatalogueDigest;
        var allocationDigest = EvidenceDigest.CanonicalSha256(new
        {
            descriptor.OutputParentIdentity,
            descriptor.OutputSlot,
            descriptor.RunId,
            descriptor.WorkerUid,
            descriptor.WorkerGid,
        });
        var capabilitiesDigest = application is null ? EvidenceDigest.CanonicalSha256(new
        {
            descriptor.Provider,
            descriptor.Platform,
            ProtectedSecrets = false,
            SensitiveProjectionKeys = Array.Empty<string>(),
            RestrictedSubjectUid = descriptor.SubjectUid,
            RestrictedSubjectGid = descriptor.SubjectGid,
        }) : EvidenceDigest.CanonicalSha256(new
        {
            descriptor.Provider,
            descriptor.Platform,
            WorkerProtocol = descriptor.Schema,
            ProtectedSecrets = false,
            SensitiveProjectionKeys = Array.Empty<string>(),
            descriptor.WorkerUid,
            descriptor.WorkerGid,
            RestrictedSubjectUid = descriptor.SubjectUid,
            RestrictedSubjectGid = descriptor.SubjectGid,
            Application = application,
            descriptor.Application!.ApplicationUid,
            descriptor.Application.ApplicationGid,
            descriptor.Application.ResultsGid,
            descriptor.Application.ResourceAccessGid,
        });
        var assertion = new EvidenceEnvelopeAssertion("1.0", "appsurface-linux-protected-worker", "1.0",
            descriptor.Provider, descriptor.WorkflowIdentity, descriptor.RunId, descriptor.BaseRevision,
            descriptor.SubjectRevision, descriptor.ToolRoot, descriptor.SubjectRoot, descriptor.OutputParentIdentity.ToString(),
            string.Empty, allocationDigest, catalogueDigest, capabilitiesDigest, descriptor.ProofDigest, DateTimeOffset.UtcNow);
        return new EvidenceAdmissionContext(descriptor.RunId, protectedPolicy, protectedResolvedPlan.ChangedPaths,
            protectedResolvedPlan, protectedResolvedPlan.Profile.Producers, protectedResolvedPlan.Profile.Resources,
            descriptor.ObservationProfileIds, descriptor.ObservationProducerIds, [], false,
            AcceptedConsumerProof(descriptor), assertion,
            ObservationProducerClasses: [new("coverage", "1.0.0")]);
    }

    /// <summary>Excludes every provider/platform until a matching full immutable release acceptance is registered.</summary>
    /// <remarks>A successful preliminary mechanism probe is deliberately insufficient for this gate.</remarks>
    internal static bool AcceptedConsumerProof(EvidenceLinuxWorkerDescriptor descriptor)
    {
        // No full protected consumer/downstream acceptance exists yet. Updating this allowlist requires its
        // real positive/negative CI proofs and the consumer acceptance record; a request digest is not authority.
        return false;
    }

    /// <summary>Checks the protected identity-bound facts supplied to the shared admission rule.</summary>
    internal sealed class RegisteredVerifier(EvidenceLinuxWorkerSupervisor worker, EvidenceEnvelopeAssertion expected) : IEvidenceAdmissionVerifier
    {
        public ValueTask<EvidenceEnvelopeAssertion?> VerifyAsync(EvidencePlan plan, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<EvidenceEnvelopeAssertion?>(worker.IsArmed && AcceptedConsumerProof(worker.Descriptor) ? expected : null);
        }
    }
}
