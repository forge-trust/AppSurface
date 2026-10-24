namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Data-only worker settlement predicates; no owner, lifecycle or native authority is created.</summary>
internal static class LinuxWorkerSettlementPolicy
{
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
}
