using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Encodes detached, closed producer diagnostic data without touching the filesystem.</summary>
/// <remarks>
/// Bytes describe observations, never successful completion, custody, account release or admission.
/// The native retainer validates the original owner and cleanup deadline before calling this encoder,
/// then independently applies its unchanged prefix and descriptor-relative root-file requirements.
/// No raw output, exception message, unit name or arbitrary state text is copied into this JSON.
/// </remarks>
internal static class LinuxProducerFailureState
{
    /// <summary>Encodes the existing 22-field private failure schema, normalizing unrecognized state text.</summary>
    /// <param name="output">Joined output observations; only its existing Drained projection is encoded.</param>
    /// <param name="stage">One of the nine fixed producer acquisition/lifecycle stage names.</param>
    /// <param name="normal">Optional retained normal-exit data; absent facts remain JSON null.</param>
    /// <param name="stopped">Optional retained stopped-unit data, not physical-exit proof.</param>
    /// <param name="pending">Atomic pending-start observations, without OS authority.</param>
    /// <param name="physicallySettled">Previously observed settlement, copied as diagnostic data only.</param>
    /// <param name="cleanupFailed">Sticky cleanup-failure projection.</param>
    /// <param name="groupEmptyTwice">Previously observed double group-check projection.</param>
    /// <param name="pumpJoined">Previously observed original-pump join projection.</param>
    /// <param name="capturedPid">Optional original captured PID, not a live process handle.</param>
    /// <param name="lastStop">Optional last unit sample; text is reduced to the fixed state vocabulary.</param>
    /// <returns>Canonical UTF-8 JSON. Producing these bytes grants no permission to persist them.</returns>
    internal static byte[] Encode(SupervisionOutputReceipt output, string stage, LinuxUnitProperties? normal,
        LinuxUnitProperties? stopped, SupervisionPendingStartSnapshot pending, bool physicallySettled,
        bool cleanupFailed, bool groupEmptyTwice, bool pumpJoined, uint? capturedPid, LinuxUnitProperties? lastStop)
    {
        if (stage is not ("binding" or "results" or "pipes" or "backend" or "start" or "running"
            or "identity" or "normal-exit" or "settlement")) throw LinuxProtectedDeployment.InvalidDeployment();
        return EvidenceCanonicalJson.Serialize(new
        {
            schema = "evidence-producer-failure-state-v1", stage,
            normal_code = normal?.ExecMainCode, normal_status = normal?.ExecMainStatus,
            stopped_code = stopped?.ExecMainCode, stopped_status = stopped?.ExecMainStatus,
            start_reserved = pending.StartReserved, start_joined = pending.StartJoined,
            stop_joined = pending.StopJoined, pending_failure = pending.FirstFailure.ToString(),
            physically_settled = physicallySettled, cleanup_failed = cleanupFailed,
            group_empty_twice = groupEmptyTwice, pump_joined = pumpJoined,
            captured_pid = capturedPid, last_main_pid = lastStop?.MainPid,
            last_exec_pid = lastStop?.ExecMainPid, last_code = lastStop?.ExecMainCode,
            last_status = lastStop?.ExecMainStatus, last_active = ClosedState(lastStop?.ActiveState),
            last_substate = ClosedState(lastStop?.SubState), output_drained = output.Drained,
        });
    }

    private static string ClosedState(string? value) => value is "active" or "inactive" or "deactivating"
        or "failed" or "running" or "exited" or "dead" or "stop" or "stop-sigterm" or "stop-sigkill"
        or "stop-post" or "final-sigterm" or "final-sigkill" ? value : "unknown";

}
