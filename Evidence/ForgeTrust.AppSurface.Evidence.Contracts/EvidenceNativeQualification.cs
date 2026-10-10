namespace ForgeTrust.AppSurface.Evidence.Contracts;

/// <summary>Identifies the single private checkpoint compiled into a qualification image.</summary>
/// <remarks>These values are build metadata, not admission, a workload registration or a runtime selector.</remarks>
internal enum EvidenceNativeQualificationKind
{
    /// <summary>Ordinary execution; no private rendezvous, phase wait or root-injected cancellation.</summary>
    None,
    /// <summary>Private original-root-peer replacement checkpoint N04.</summary>
    PeerReplacement,
    /// <summary>Private original caller cancellation checkpoint N08, before allocation.</summary>
    CancellationBeforeAllocation,
    /// <summary>Private original caller cancellation checkpoint N09, after allocation and before activation.</summary>
    CancellationBeforeActivation,
    /// <summary>Private N11 synchronous worker input-factory stall.</summary>
    SynchronousWorkerStall,
    /// <summary>Private N12 normal leader exit with a live same-image output holder.</summary>
    LeaderExitWithDescendant,
}

/// <summary>Separates fixed private Linux qualification behavior from ordinary execution.</summary>
/// <remarks>
/// Ordinary builds select None. A private build may define exactly one of EVIDENCE_PRIVATE_N04,
/// EVIDENCE_PRIVATE_N08, EVIDENCE_PRIVATE_N09, EVIDENCE_PRIVATE_N11 or EVIDENCE_PRIVATE_N12 across the complete same-image project graph.
/// Arguments, environment variables, descriptors and public APIs cannot select a checkpoint.
/// Qualification still requires the original authenticated native owners and all ordinary guards;
/// selecting a checkpoint grants no admission or accepted consumer proof.
/// </remarks>
internal static class EvidenceNativeQualification
{
#if (EVIDENCE_PRIVATE_N04 && EVIDENCE_PRIVATE_N08) || (EVIDENCE_PRIVATE_N04 && EVIDENCE_PRIVATE_N09) || (EVIDENCE_PRIVATE_N08 && EVIDENCE_PRIVATE_N09) || (EVIDENCE_PRIVATE_N12 && (EVIDENCE_PRIVATE_N04 || EVIDENCE_PRIVATE_N08 || EVIDENCE_PRIVATE_N09 || EVIDENCE_PRIVATE_N11)) || (EVIDENCE_PRIVATE_N11 && (EVIDENCE_PRIVATE_N04 || EVIDENCE_PRIVATE_N08 || EVIDENCE_PRIVATE_N09))
#error A private qualification image must select exactly one checkpoint.
#endif

    /// <summary>Gets build-owned metadata; the production default has no pauses or injected signals.</summary>
    internal static EvidenceNativeQualificationKind Current
    {
        get
        {
#if EVIDENCE_PRIVATE_N04
            return EvidenceNativeQualificationKind.PeerReplacement;
#elif EVIDENCE_PRIVATE_N08
            return EvidenceNativeQualificationKind.CancellationBeforeAllocation;
#elif EVIDENCE_PRIVATE_N09
            return EvidenceNativeQualificationKind.CancellationBeforeActivation;
#elif EVIDENCE_PRIVATE_N11
            return EvidenceNativeQualificationKind.SynchronousWorkerStall;
#elif EVIDENCE_PRIVATE_N12
            return EvidenceNativeQualificationKind.LeaderExitWithDescendant;
#else
            return EvidenceNativeQualificationKind.None;
#endif
        }
    }

    /// <summary>Gets whether this image owns the private root-peer replacement rendezvous.</summary>
    internal static bool PeerReplacementEnabled => Current == EvidenceNativeQualificationKind.PeerReplacement;

    /// <summary>Gets whether this image owns the private caller cancellation barrier and signal.</summary>
    internal static bool CancellationEnabled => Current is EvidenceNativeQualificationKind.CancellationBeforeAllocation
        or EvidenceNativeQualificationKind.CancellationBeforeActivation;

    /// <summary>Gets whether the compiled image owns the closed N12 descendant procedure.</summary>
    internal static bool DescendantEnabled => Current == EvidenceNativeQualificationKind.LeaderExitWithDescendant;

    /// <summary>Gets whether the fixed private N11 image stalls at the real synchronous worker input-factory boundary.</summary>
    internal static bool WorkerStallEnabled => Current == EvidenceNativeQualificationKind.SynchronousWorkerStall;

    /// <summary>Creates metadata for the private stage wait only in a cancellation qualification image.</summary>
    /// <returns>Null for ordinary execution and peer replacement; otherwise an internal one-attempt checkpoint.</returns>
    internal static EvidenceOriginalCancellationCheckpoint? CreateCancellationCheckpoint() =>
        CancellationEnabled ? new EvidenceOriginalCancellationCheckpoint() : null;
}
