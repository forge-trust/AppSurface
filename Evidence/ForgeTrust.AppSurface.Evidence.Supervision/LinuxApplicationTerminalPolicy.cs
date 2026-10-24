using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Checks only the sampled application terminal identity; it performs no process or group I/O.</summary>
/// <remarks>The caller retains recipe-state, cgroup, cleanup, and ledger checks. A nonzero main result is
/// stable terminal data and remains a workload failure decision made by the caller.</remarks>
internal static class LinuxApplicationTerminalPolicy
{
    /// <summary>Requires stable ExecMain data and, when captured, the retained AppHost PID binding.</summary>
    /// <exception cref="EvidenceAdmissionException">Thrown with the existing fixed ASEVD410 rejection diagnostic when sampled data is inconsistent.</exception>
    internal static void RequireStableMain(LinuxUnitProperties first, LinuxUnitProperties second, uint? appHostPid)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        if (first.ExecMainPid != second.ExecMainPid
            || first.ExecMainCode != second.ExecMainCode
            || first.ExecMainStatus != second.ExecMainStatus
            || appHostPid is not null && second.ExecMainPid != appHostPid.Value)
            throw LinuxApplicationProcess.Rejected();
    }
}
