namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Copied observations used by the physical-settlement predicate; the pending snapshot remains original data.</summary>
internal readonly record struct LinuxProducerCleanupFacts(
    SupervisionPendingStartSnapshot Pending,
    bool DispatchAttempted,
    bool PhysicallySettled,
    bool CleanupFailed,
    bool StoppedTerminalObserved,
    bool GroupEmptyTwice,
    bool PumpJoined,
    bool OutputDrained,
    bool CollectionPending);

/// <summary>Data-only producer settlement policy; it grants no native, lifecycle or report authority.</summary>
internal static class LinuxProducerSettlementPolicy
{
    /// <summary>Requires the original report-readiness prefix before reading the shared quota latch.</summary>
    internal static void RequireReportPrefix(bool runCompletedSuccessfully, bool physicallySettled,
        bool normalTerminalObserved, bool stoppedTerminalObserved, SupervisionOutputReceipt? output)
    {
        if (!runCompletedSuccessfully || !physicallySettled || !normalTerminalObserved
            || !stoppedTerminalObserved || output is not { Successful: true } || output.DiscardedBytes != 0)
            throw LinuxSystemdBackend.InvalidControl();
    }

    /// <summary>Requires the report-readiness latches after the original shared quota read.</summary>
    internal static void RequireReportLatches(bool failed, bool cleanupFailed, bool groupEmptyTwice, bool pumpJoined)
    {
        if (failed || cleanupFailed || !groupEmptyTwice || !pumpJoined)
            throw LinuxSystemdBackend.InvalidControl();
    }

    /// <summary>Requires the exact cleanup facts while preserving failed-start and failed-stop distinctions.</summary>
    internal static void RequirePhysicalSettlement(LinuxProducerCleanupFacts facts)
    {
        var pending = facts.Pending;
        var startJoined = !pending.StartReserved || pending.StartJoined;
        var noProcessAcquired = !facts.DispatchAttempted;
        if (!facts.PhysicallySettled || !pending.Closed || !pending.StopJoined
            || pending.FirstFailure is SupervisionPendingStartFailure.StopFailed
                or SupervisionPendingStartFailure.StopCancelled
            || !startJoined || facts.CleanupFailed
            || !noProcessAcquired && (!facts.StoppedTerminalObserved || !facts.GroupEmptyTwice
                || !facts.PumpJoined || !facts.OutputDrained || facts.CollectionPending))
            throw LinuxSystemdBackend.InvalidControl();
    }
}
