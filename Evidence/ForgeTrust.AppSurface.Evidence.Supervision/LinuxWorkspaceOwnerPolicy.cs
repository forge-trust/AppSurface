using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Enforces the existing scalar owner and generated-root predicates for workspace wrappers.</summary>
/// <remarks>
/// The callers perform their reference-equality checks before these methods. These methods inspect only
/// immutable layout data and preserve the existing workspace-specific admission exception and predicate
/// ordering; they do not authenticate an account holder, acquire a lock, open a native path, or grant
/// authority. See <see href="workspace-policy.md">the workspace policy reference</see>.
/// </remarks>
internal static class LinuxWorkspaceOwnerPolicy
{
    /// <summary>Checks the producer account snapshot and its owner-derived root name.</summary>
    /// <param name="identity">The immutable five-scalar account snapshot captured by the producer layout.</param>
    /// <param name="rootName">The immutable generated producer root name.</param>
    /// <param name="ownerRunId">The authenticated owner generation supplied by the wrapper.</param>
    /// <exception cref="EvidenceAdmissionException">Thrown with the original producer layout error.</exception>
    internal static void RequireProducerData(LinuxRunAccountSnapshot identity, string rootName, Guid ownerRunId)
    {
        if (identity.WorkerUid is 0 or uint.MaxValue || identity.SubjectUid is 0 or uint.MaxValue
            || identity.WorkerUid == identity.SubjectUid || identity.WorkerGid is 0 or uint.MaxValue
            || identity.SubjectGid is 0 or uint.MaxValue || identity.ResultsGid is 0 or uint.MaxValue
            || identity.WorkerGid == identity.SubjectGid || identity.WorkerGid == identity.ResultsGid
            || identity.SubjectGid == identity.ResultsGid
            || !StringComparer.Ordinal.Equals(rootName,
                "appsurface-evidence-producer-data-" + ownerRunId.ToString("N")))
            throw LinuxProducerWorkspaceLayout.Invalid();
    }

    /// <summary>Checks the application layout generation and its owner-derived root name.</summary>
    /// <param name="layoutRunId">The immutable generation stored by the application layout.</param>
    /// <param name="rootName">The immutable generated application root name.</param>
    /// <param name="ownerRunId">The authenticated owner generation supplied by the wrapper.</param>
    /// <exception cref="EvidenceAdmissionException">Thrown with the original application layout error.</exception>
    internal static void RequireApplicationData(Guid layoutRunId, string rootName, Guid ownerRunId)
    {
        if (layoutRunId == Guid.Empty
            || rootName != "appsurface-app-" + ownerRunId.ToString("N"))
            throw LinuxApplicationWorkspaceLayout.Invalid();
    }
}
