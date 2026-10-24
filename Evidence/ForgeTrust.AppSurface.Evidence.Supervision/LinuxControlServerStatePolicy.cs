namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Detached state for the original custody-owner gate.</summary>
internal readonly record struct LinuxControlServerCustodyFacts(
    bool ExecutionCompleted,
    bool IoJoined,
    bool LocalOwnersClosed,
    bool ConnectionCloseHealthy);

/// <summary>Detached state for the original negative-READY descriptor gate.</summary>
internal readonly record struct LinuxControlServerNegativeReadyFacts(
    bool NegativeReadyCommitted,
    bool DescriptorPresent);

/// <summary>Pure state predicates for existing control-server custody and completion gates.</summary>
internal static class LinuxControlServerStatePolicy
{
    /// <summary>Requires the original completed task and local I/O/owner-close flags before native ledger checks.</summary>
    internal static void RequireLocalCustody(LinuxControlServerCustodyFacts facts)
    {
        if (!facts.ExecutionCompleted || !facts.IoJoined || !facts.LocalOwnersClosed
            || !facts.ConnectionCloseHealthy)
            Invalid();
    }

    /// <summary>Requires the original committed negative READY event and retained descriptor data.</summary>
    internal static void RequireNegativeReady(LinuxControlServerNegativeReadyFacts facts)
    {
        if (!facts.NegativeReadyCommitted || !facts.DescriptorPresent)
            Invalid();
    }

    private static void Invalid() => throw LinuxEmptyObservationControlServer.Rejected();
}
