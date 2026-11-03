namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Detached natural-terminal and joined-output data for the original worker completion guard.</summary>
/// <remarks>This snapshot contains no worker, owner, lifecycle, or native completion authority.</remarks>
internal readonly record struct LinuxWorkerCompletionFacts(
    LinuxUnitProperties? NaturalTerminal,
    SupervisionOutputReceipt? Output);

/// <summary>Copied prerequisite values for the two original joined-failure capture paths.</summary>
/// <remarks>This data cannot create a worker, owner, task join, native observation or custody authority.</remarks>
internal readonly record struct LinuxFailedCaptureReadiness(
    bool LifetimeStartJoined,
    bool LifetimeStopJoined,
    bool PendingStartReserved,
    bool PendingStartJoined,
    bool PendingClosed,
    bool PendingStopJoined,
    bool ExitCompleted);

/// <summary>Copied final-worker observations and the one atomic pending-start snapshot.</summary>
/// <remarks>This record is detached data; it cannot prove or create any observation or native completion.</remarks>
internal readonly record struct LinuxWorkerFinalSettlementFacts(
    bool Failed,
    bool UnitJoined,
    bool GroupJoined,
    bool PipesClosed,
    bool MonitorJoined,
    SupervisionPendingStartSnapshot Pending);

/// <summary>Final local settlement decision, preserving the pending-adjusted group result for failure capture.</summary>
internal readonly record struct LinuxWorkerFinalSettlementResult(bool GroupJoined, bool Failed);

/// <summary>Data-only worker settlement predicates; no owner, lifecycle or native authority is created.</summary>
internal static class LinuxWorkerSettlementPolicy
{
    /// <summary>Combines final copied joins with the exact original pending-start settlement predicate.</summary>
    /// <remarks>
    /// This does not add a <c>Closed</c> requirement: the original finalizer checks pending stop, failure,
    /// and a reserved-start join only. Native observations and the caller's sticky failure/publication remain
    /// owned by <c>LinuxWorkerProcess.FinalizeAsync</c>.
    /// </remarks>
    internal static LinuxWorkerFinalSettlementResult EvaluateFinalSettlement(LinuxWorkerFinalSettlementFacts facts)
    {
        var pending = facts.Pending;
        var groupJoined = facts.GroupJoined && pending.StopJoined && !pending.IsFailed
            && (!pending.StartReserved || pending.StartJoined);
        return new(groupJoined, facts.Failed || !facts.UnitJoined || !groupJoined
            || !facts.PipesClosed || !facts.MonitorJoined);
    }

    /// <summary>Matches the original successful natural terminal and successful joined output receipts.</summary>
    internal static bool HasSuccessfulTerminalAndOutput(LinuxWorkerCompletionFacts facts) =>
        facts.NaturalTerminal is { ExecMainCode: 1, ExecMainStatus: 0 }
        && facts.Output is { Successful: true };

    /// <summary>Requires the original worker custody prefix before output or native group observations.</summary>
    internal static void RequirePrefix(bool stopJoined, bool physicallySettled, bool pendingStopJoined,
        bool pendingFailed, bool startReservationJoined, bool exitCompleted)
    {
        if (!stopJoined || !physicallySettled || !pendingStopJoined || pendingFailed
            || !startReservationJoined || !exitCompleted)
            throw LinuxSystemdBackend.InvalidControl();
    }

    /// <summary>Requires the original drained output result before the caller reads the run budget again.</summary>
    internal static void RequireOutputDrain(bool outputDrained)
    {
        if (!outputDrained)
            throw LinuxSystemdBackend.InvalidControl();
    }

    /// <summary>Requires the exact joined holder prerequisites shared by N07 and later failure-only capture.</summary>
    /// <remarks>Callers retain their original token/reference, descriptor, and native-owner checks around this data policy.</remarks>
    internal static void RequireFailedCaptureReadiness(LinuxFailedCaptureReadiness facts)
    {
        if (!facts.LifetimeStartJoined || !facts.LifetimeStopJoined || !facts.PendingStartReserved
            || !facts.PendingStartJoined || !facts.PendingClosed || !facts.PendingStopJoined
            || !facts.ExitCompleted)
            throw LinuxSystemdBackend.InvalidControl();
    }
}
