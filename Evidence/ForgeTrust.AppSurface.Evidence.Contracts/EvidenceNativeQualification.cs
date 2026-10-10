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
}

/// <summary>Separates fixed private Linux qualification behavior from ordinary execution.</summary>
/// <remarks>
/// Ordinary builds select None. A private build may define exactly one of EVIDENCE_PRIVATE_N04,
/// EVIDENCE_PRIVATE_N08 or EVIDENCE_PRIVATE_N09 across the complete same-image project graph.
/// Arguments, environment variables, descriptors and public APIs cannot select a checkpoint.
/// Qualification still requires the original authenticated native owners and all ordinary guards;
/// selecting a checkpoint grants no admission or accepted consumer proof.
/// </remarks>
internal static class EvidenceNativeQualification
{
#if (EVIDENCE_PRIVATE_N04 && EVIDENCE_PRIVATE_N08) || (EVIDENCE_PRIVATE_N04 && EVIDENCE_PRIVATE_N09) || (EVIDENCE_PRIVATE_N08 && EVIDENCE_PRIVATE_N09)
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

    /// <summary>Creates metadata for the private stage wait only in a cancellation qualification image.</summary>
    /// <returns>Null for ordinary execution and peer replacement; otherwise an internal one-attempt checkpoint.</returns>
    internal static EvidenceOriginalCancellationCheckpoint? CreateCancellationCheckpoint() =>
        CancellationEnabled ? new EvidenceOriginalCancellationCheckpoint() : null;
}
