using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Serializes the existing root control reply DTOs without performing validation or transport work.</summary>
/// <remarks>
/// These methods preserve the closed wire fields emitted by the control server. They do not authenticate an
/// owner, authorize work, apply bounds, inspect native state, or write a response. The caller retains each of
/// those responsibilities and keeps reply ordering and cancellation unchanged.
/// </remarks>
internal static class SupervisionControlReplyData
{
    /// <summary>Serializes the root-observed application-start metadata.</summary>
    /// <param name="started">The existing application-start receipt returned by the authenticated
    /// application operation.</param>
    /// <returns>Canonical UTF-8 JSON for the established application-start reply.</returns>
    internal static byte[] ApplicationStart(EvidenceLinuxApplicationStartReceipt started) =>
        ApplicationStart(started.LeaseId, started.AppHostPid, started.ApplicationUid,
            started.ApplicationGid, started.Cgroup);

    /// <summary>Serializes application-start reply data in the unchanged wire shape.</summary>
    /// <param name="leaseId">Existing receipt lease identifier.</param>
    /// <param name="appHostPid">Existing receipt process identifier.</param>
    /// <param name="applicationUid">Existing receipt application UID.</param>
    /// <param name="applicationGid">Existing receipt application GID.</param>
    /// <param name="cgroup">Existing receipt cgroup value.</param>
    /// <returns>Canonical UTF-8 JSON; values are copied without validation or normalization.</returns>
    internal static byte[] ApplicationStart(string leaseId, int appHostPid, uint applicationUid,
        uint applicationGid, string cgroup) => EvidenceCanonicalJson.Serialize(new
        {
            ok = true,
            lease_id = leaseId,
            apphost_pid = appHostPid,
            application_uid = applicationUid,
            application_gid = applicationGid,
            cgroup,
            owned = true
        });

    /// <summary>Serializes the root-observed application-readiness metadata.</summary>
    /// <param name="ready">The existing readiness receipt returned after the root's peer and HTTP checks.</param>
    /// <returns>Canonical UTF-8 JSON for the established readiness reply.</returns>
    internal static byte[] ApplicationReadiness(EvidenceLinuxApplicationResourceReceipt ready) =>
        ApplicationReadiness(ready.LeaseId, ready.ResourceId, ready.ApplicationUid, ready.Cgroup,
            ready.HttpStatus, ready.ReceivedBytes);

    /// <summary>Serializes readiness reply data in the unchanged wire shape.</summary>
    /// <param name="leaseId">Existing readiness receipt lease identifier.</param>
    /// <param name="resourceId">Existing readiness receipt resource identifier.</param>
    /// <param name="applicationUid">Existing readiness receipt application UID.</param>
    /// <param name="cgroup">Existing readiness receipt cgroup value.</param>
    /// <param name="httpStatus">Existing root-observed HTTP status.</param>
    /// <param name="receivedBytes">Existing root-observed response byte count.</param>
    /// <returns>Canonical UTF-8 JSON; values are copied without validation or normalization.</returns>
    internal static byte[] ApplicationReadiness(string leaseId, string resourceId, uint applicationUid,
        string cgroup, int httpStatus, long receivedBytes) => EvidenceCanonicalJson.Serialize(new
        {
            ok = true,
            lease_id = leaseId,
            resource_id = resourceId,
            application_uid = applicationUid,
            cgroup,
            kernel_peer_checked = true,
            http_status = httpStatus,
            healthy = true,
            received_bytes = receivedBytes
        });

    /// <summary>Serializes the existing completed restricted-process result.</summary>
    /// <param name="result">The root-selected run result after its original operation returns.</param>
    /// <returns>Canonical UTF-8 JSON for the established run reply.</returns>
    internal static byte[] Run(EvidenceRestrictedProcessResult result) =>
        Run(result.ExitCode, result.Stdout, result.Stderr, result.OutputTruncated, result.ReceivedBytes);

    /// <summary>Serializes run reply data in the unchanged wire shape.</summary>
    /// <param name="exitCode">The existing process exit code.</param>
    /// <param name="stdout">The existing captured standard output.</param>
    /// <param name="stderr">The existing captured standard error.</param>
    /// <param name="outputTruncated">The existing output truncation flag.</param>
    /// <param name="receivedBytes">The existing total received byte count.</param>
    /// <returns>Canonical UTF-8 JSON; strings are encoded by the canonical JSON writer.</returns>
    internal static byte[] Run(int exitCode, string stdout, string stderr, bool outputTruncated,
        long receivedBytes) => EvidenceCanonicalJson.Serialize(new
        {
            ok = true,
            exit_code = exitCode,
            stdout,
            stderr,
            output_truncated = outputTruncated,
            received_bytes = receivedBytes
        });

    /// <summary>Serializes the existing retained-report inventory projection.</summary>
    /// <param name="reports">The original ordered report metadata list.</param>
    /// <returns>Canonical UTF-8 JSON containing the established path and byte-length projection.</returns>
    internal static byte[] Inventory(IReadOnlyList<LinuxRetainedReportSet.ReportInfo> reports) =>
        EvidenceCanonicalJson.Serialize(new
        {
            ok = true,
            artifacts = reports.Select(static item =>
                new { path = item.RelativePath, length_bytes = item.Length }).ToArray()
        });

    /// <summary>Encodes one already-read artifact chunk in the existing base64 wire representation.</summary>
    /// <param name="bytes">The bytes returned by the existing retained-artifact read.</param>
    /// <returns>Base64 text for the exact byte sequence, without normalization.</returns>
    internal static string EncodeArtifactBytes(byte[] bytes) => Convert.ToBase64String(bytes);

    /// <summary>Serializes an already-encoded artifact chunk and the caller's original end-of-file result.</summary>
    /// <param name="bytesBase64">The original artifact bytes encoded before the caller samples the end flag.</param>
    /// <param name="end">The end flag sampled by the caller from the retained artifact operation.</param>
    /// <returns>Canonical UTF-8 JSON with base64-encoded bytes and the unchanged end flag.</returns>
    internal static byte[] Artifact(string bytesBase64, bool end) => EvidenceCanonicalJson.Serialize(new
    {
        ok = true,
        bytes_base64 = bytesBase64,
        end
    });
}
