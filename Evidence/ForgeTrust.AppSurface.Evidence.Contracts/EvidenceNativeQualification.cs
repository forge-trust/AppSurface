namespace ForgeTrust.AppSurface.Evidence.Contracts;

/// <summary>Identifies the single private checkpoint compiled into a qualification image.</summary>
/// <remarks>These values are build metadata, not admission, a workload registration or a runtime selector.</remarks>
internal enum EvidenceNativeQualificationKind
{
    /// <summary>Ordinary execution; no private rendezvous, phase wait or root-injected cancellation.</summary>
    None,
    /// <summary>Private original-root-peer replacement checkpoint N04.</summary>
    PeerReplacement,
    /// <summary>Private N07 original parent-identity substitution rendezvous.</summary>
    ParentIdentitySubstitution,
    /// <summary>Private original caller cancellation checkpoint N08, before allocation.</summary>
    CancellationBeforeAllocation,
    /// <summary>Private original caller cancellation checkpoint N09, after allocation and before activation.</summary>
    CancellationBeforeActivation,
    /// <summary>Private original stop racing an already reserved systemd worker start, N10.</summary>
    PendingStartRace,
    /// <summary>Private N11 synchronous worker input-factory stall.</summary>
    SynchronousWorkerStall,
    /// <summary>Private N12 normal leader exit with a live same-image output holder.</summary>
    LeaderExitWithDescendant,
    /// <summary>Private N15 owner death while the real worker start remains pending.</summary>
    PendingStartOwnerDeath,
    /// <summary>Private N16 accepted blocked workload with concurrent authenticated stop and wait.</summary>
    AcceptedBlockedWork,
    /// <summary>Private N13 owner SIGKILL while the accepted workload remains pending.</summary>
    OwnerKilledDuringAcceptedWork,
    /// <summary>Private N14 owner SIGSTOP until its existing systemd runtime deadline.</summary>
    OwnerStoppedDuringAcceptedWork,
}

/// <summary>Separates fixed private Linux qualification behavior from ordinary execution.</summary>
/// <remarks>
/// Ordinary builds select None. A private build may define exactly one of EVIDENCE_PRIVATE_N04,
/// EVIDENCE_PRIVATE_N07, EVIDENCE_PRIVATE_N08, EVIDENCE_PRIVATE_N09, EVIDENCE_PRIVATE_N10, EVIDENCE_PRIVATE_N11, EVIDENCE_PRIVATE_N12, EVIDENCE_PRIVATE_N13, EVIDENCE_PRIVATE_N14, EVIDENCE_PRIVATE_N15 or EVIDENCE_PRIVATE_N16 across the complete same-image project graph.
/// Arguments, environment variables, descriptors and public APIs cannot select a checkpoint.
/// Qualification still requires the original authenticated native owners and all ordinary guards;
/// selecting a checkpoint grants no admission or accepted consumer proof.
/// </remarks>
internal static class EvidenceNativeQualification
{
#if (EVIDENCE_PRIVATE_N04 && (EVIDENCE_PRIVATE_N07 || EVIDENCE_PRIVATE_N08 || EVIDENCE_PRIVATE_N09 || EVIDENCE_PRIVATE_N10 || EVIDENCE_PRIVATE_N11 || EVIDENCE_PRIVATE_N12 || EVIDENCE_PRIVATE_N13 || EVIDENCE_PRIVATE_N14 || EVIDENCE_PRIVATE_N15 || EVIDENCE_PRIVATE_N16)) || (EVIDENCE_PRIVATE_N07 && (EVIDENCE_PRIVATE_N08 || EVIDENCE_PRIVATE_N09 || EVIDENCE_PRIVATE_N10 || EVIDENCE_PRIVATE_N11 || EVIDENCE_PRIVATE_N12 || EVIDENCE_PRIVATE_N13 || EVIDENCE_PRIVATE_N14 || EVIDENCE_PRIVATE_N15 || EVIDENCE_PRIVATE_N16)) || (EVIDENCE_PRIVATE_N08 && (EVIDENCE_PRIVATE_N09 || EVIDENCE_PRIVATE_N10 || EVIDENCE_PRIVATE_N11 || EVIDENCE_PRIVATE_N12 || EVIDENCE_PRIVATE_N13 || EVIDENCE_PRIVATE_N14 || EVIDENCE_PRIVATE_N15 || EVIDENCE_PRIVATE_N16)) || (EVIDENCE_PRIVATE_N09 && (EVIDENCE_PRIVATE_N10 || EVIDENCE_PRIVATE_N11 || EVIDENCE_PRIVATE_N12 || EVIDENCE_PRIVATE_N13 || EVIDENCE_PRIVATE_N14 || EVIDENCE_PRIVATE_N15 || EVIDENCE_PRIVATE_N16)) || (EVIDENCE_PRIVATE_N10 && (EVIDENCE_PRIVATE_N11 || EVIDENCE_PRIVATE_N12 || EVIDENCE_PRIVATE_N13 || EVIDENCE_PRIVATE_N14 || EVIDENCE_PRIVATE_N15 || EVIDENCE_PRIVATE_N16)) || (EVIDENCE_PRIVATE_N11 && (EVIDENCE_PRIVATE_N12 || EVIDENCE_PRIVATE_N13 || EVIDENCE_PRIVATE_N14 || EVIDENCE_PRIVATE_N15 || EVIDENCE_PRIVATE_N16)) || (EVIDENCE_PRIVATE_N12 && (EVIDENCE_PRIVATE_N13 || EVIDENCE_PRIVATE_N14 || EVIDENCE_PRIVATE_N15 || EVIDENCE_PRIVATE_N16)) || (EVIDENCE_PRIVATE_N13 && (EVIDENCE_PRIVATE_N14 || EVIDENCE_PRIVATE_N15 || EVIDENCE_PRIVATE_N16)) || (EVIDENCE_PRIVATE_N14 && (EVIDENCE_PRIVATE_N15 || EVIDENCE_PRIVATE_N16)) || (EVIDENCE_PRIVATE_N15 && (EVIDENCE_PRIVATE_N16))
#error A private qualification image must select exactly one checkpoint.
#endif

    /// <summary>Gets build-owned metadata; the production default has no pauses or injected signals.</summary>
    internal static EvidenceNativeQualificationKind Current
    {
        get
        {
#if EVIDENCE_PRIVATE_N04
            return EvidenceNativeQualificationKind.PeerReplacement;
#elif EVIDENCE_PRIVATE_N07
            return EvidenceNativeQualificationKind.ParentIdentitySubstitution;
#elif EVIDENCE_PRIVATE_N08
            return EvidenceNativeQualificationKind.CancellationBeforeAllocation;
#elif EVIDENCE_PRIVATE_N09
            return EvidenceNativeQualificationKind.CancellationBeforeActivation;
#elif EVIDENCE_PRIVATE_N10
            return EvidenceNativeQualificationKind.PendingStartRace;
#elif EVIDENCE_PRIVATE_N11
            return EvidenceNativeQualificationKind.SynchronousWorkerStall;
#elif EVIDENCE_PRIVATE_N12
            return EvidenceNativeQualificationKind.LeaderExitWithDescendant;
#elif EVIDENCE_PRIVATE_N15
            return EvidenceNativeQualificationKind.PendingStartOwnerDeath;
#elif EVIDENCE_PRIVATE_N16
            return EvidenceNativeQualificationKind.AcceptedBlockedWork;
#elif EVIDENCE_PRIVATE_N13
            return EvidenceNativeQualificationKind.OwnerKilledDuringAcceptedWork;
#elif EVIDENCE_PRIVATE_N14
            return EvidenceNativeQualificationKind.OwnerStoppedDuringAcceptedWork;
#else
            return EvidenceNativeQualificationKind.None;
#endif
        }
    }

    /// <summary>Gets whether this image owns the private root-peer replacement rendezvous.</summary>
    internal static bool PeerReplacementEnabled => Current == EvidenceNativeQualificationKind.PeerReplacement;

    /// <summary>Gets whether this private image owns the fixed N07 parent-substitution rendezvous.</summary>
    /// <remarks>Ordinary builds perform no N07 rendezvous or allocation-fault transfer.</remarks>
    internal static bool ParentReplacementEnabled => Current == EvidenceNativeQualificationKind.ParentIdentitySubstitution;

    /// <summary>Gets whether this image owns the private caller cancellation barrier and signal.</summary>
    internal static bool CancellationEnabled => Current is EvidenceNativeQualificationKind.CancellationBeforeAllocation
        or EvidenceNativeQualificationKind.CancellationBeforeActivation;

    /// <summary>Gets whether the compiled image owns the closed N12 descendant procedure.</summary>
    internal static bool DescendantEnabled => Current == EvidenceNativeQualificationKind.LeaderExitWithDescendant;

    /// <summary>Gets whether this image observes the original pending-start stop race.</summary>
    internal static bool PendingStartRaceEnabled => Current == EvidenceNativeQualificationKind.PendingStartRace;

    /// <summary>Gets whether the fixed private N11 image stalls at the real synchronous worker input-factory boundary.</summary>
    internal static bool WorkerStallEnabled => Current == EvidenceNativeQualificationKind.SynchronousWorkerStall;

    /// <summary>Gets whether this image owns the fixed N16 blocked-work procedure.</summary>
    internal static bool AcceptedBlockedWorkEnabled => Current is EvidenceNativeQualificationKind.AcceptedBlockedWork
        or EvidenceNativeQualificationKind.OwnerKilledDuringAcceptedWork
        or EvidenceNativeQualificationKind.OwnerStoppedDuringAcceptedWork;

    /// <summary>Gets whether this private image holds accepted work for external owner termination.</summary>
    internal static bool OwnerDeathAcceptedWorkEnabled => Current is EvidenceNativeQualificationKind.OwnerKilledDuringAcceptedWork
        or EvidenceNativeQualificationKind.OwnerStoppedDuringAcceptedWork;

    /// <summary>Gets whether the N16 image owns its concurrent STOP/WAIT procedure.</summary>
    internal static bool N16StopWaitEnabled => Current == EvidenceNativeQualificationKind.AcceptedBlockedWork;

    /// <summary>Validates the closed bounded synchronization frame without treating it as authority.</summary>
    internal static bool IsOwnerDeathAcceptedWorkPhaseFrame(ReadOnlySpan<byte> frame)
    {
        var line = OwnerDeathAcceptedWorkPhaseLine;
        return line is not null && frame.SequenceEqual(System.Text.Encoding.ASCII.GetBytes(line + "\n"));
    }

    /// <summary>Gets the single fixed synchronization line emitted after the real acceptance write commits.</summary>
    internal static string? OwnerDeathAcceptedWorkPhaseLine => Current switch
    {
        EvidenceNativeQualificationKind.OwnerKilledDuringAcceptedWork => "NATIVE_PRIVATE_PHASE:N13:ACCEPTED_BLOCKED_WORK",
        EvidenceNativeQualificationKind.OwnerStoppedDuringAcceptedWork => "NATIVE_PRIVATE_PHASE:N14:ACCEPTED_BLOCKED_WORK",
        _ => null
    };

    /// <summary>Gets whether this image owns the fixed N15 pending-start death barrier.</summary>
    internal static bool PendingStartOwnerDeathEnabled => Current == EvidenceNativeQualificationKind.PendingStartOwnerDeath;

    /// <summary>Creates metadata for the private stage wait only in a cancellation qualification image.</summary>
    /// <returns>Null for ordinary execution and peer replacement; otherwise an internal one-attempt checkpoint.</returns>
    internal static EvidenceOriginalCancellationCheckpoint? CreateCancellationCheckpoint() =>
        CancellationEnabled ? new EvidenceOriginalCancellationCheckpoint() : null;
}
