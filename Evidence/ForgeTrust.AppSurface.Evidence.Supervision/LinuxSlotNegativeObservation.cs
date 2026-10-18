#if EVIDENCE_PRIVATE_N05 || EVIDENCE_PRIVATE_N06 || EVIDENCE_QUALIFICATION_DATA_TESTS
using System.Text;
using System.Text.Json;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Bounded detached N05/N06 records; data never creates a native owner or acceptance proof.</summary>
internal static class LinuxSlotNegativeObservation
{
    internal const int MaximumInspectionBytes = 1024;
    internal const int MaximumWorkerBytes = 2048;
    internal const string WorkerTerminal = "ASEVD409: Fresh output allocation or activation failed. Fix: use an explicit mode and supported protected worker. See start-here/evidencehost.md.\n";

    /// <summary>Projects a fixed fixture after actual descriptor checks; pure calls validate shape only.</summary>
    internal static byte[] EncodeInspection(string caseId, string phase, LinuxProtectedMetadata parent,
        LinuxProtectedMetadata occupied, LinuxProtectedMetadata sentinel, string sha256,
        LinuxProtectedMetadata? target = null)
    {
        if (caseId is not ("N05" or "N06") || phase is not ("setup" or "post_join")
            || parent.Inode == 0 || parent.Uid == 0 || parent.Gid == 0 || parent.Mode != 0x41c0
            || occupied.Inode == 0 || occupied.Inode == parent.Inode || sentinel.Inode == 0
            || sentinel.LinkCount != 1 || sentinel.Length is 0 or > 128 || sentinel.Mode != 0x8180
            || !Digest(sha256)) throw Rejected();
        if (caseId == "N05" && (target is not null || occupied.Mode != 0x41c0
            || occupied.Uid != parent.Uid || occupied.Gid != parent.Gid
            || sentinel.Uid != parent.Uid || sentinel.Gid != parent.Gid)) throw Rejected();
        if (caseId == "N06" && (target is null || target.Value.Inode == 0
            || target.Value.Mode != 0x41c8 || target.Value.Uid == 0 || target.Value.Gid == 0
            || occupied.Mode != 0xa1ff || occupied.Uid != 0 || occupied.Gid != 0
            || occupied.LinkCount != 1 || occupied.Length != 14
            || sentinel.Uid != 0 || sentinel.Gid != 0)) throw Rejected();
        var bytes = caseId == "N05"
            ? JsonSerializer.SerializeToUtf8Bytes(new { schema = "issue779-n05-slot-inspection-v1", phase,
                parent = Identity(parent), slot = Identity(occupied), sentinel = Identity(sentinel), sha256 })
            : JsonSerializer.SerializeToUtf8Bytes(new { schema = "issue779-n06-symlink-inspection-v1", phase,
                parent = Identity(parent), target = Identity(target!.Value), link = Identity(occupied),
                target_sha256 = sha256, sentinel = Identity(sentinel), sha256 });
        if (bytes.Length > MaximumInspectionBytes) throw Rejected();
        return bytes;
    }

    /// <summary>Parses only complete joined streams and the exact existing allocation failure, without I/O.</summary>
    /// <remarks>The original worker holder must establish physical joins before supplying its output receipt.</remarks>
    internal static (byte[] Projection, byte[] Raw) EncodeJoinedOutput(string caseId, SupervisionOutputReceipt output)
    {
        try
        {
            if (caseId is not ("N05" or "N06") || output is null || !output.Successful
                || output.Stdout.Prefix.IsDefault || output.Stderr.Prefix.IsDefault
                || output.Stdout.ReceivedBytes != 0 || output.Stdout.Prefix.Length != 0
                || output.Stderr.ReceivedBytes is <= 0 or > MaximumWorkerBytes
                || output.Stderr.ReceivedBytes != output.Stderr.Prefix.Length || output.DiscardedBytes != 0)
                throw Rejected();
            var stderr = output.Stderr.Prefix.AsSpan();
            var newline = stderr.IndexOf((byte)'\n');
            if (newline is <= 0 or >= 1024 || stderr.Contains((byte)'\r')
                || !stderr[(newline + 1)..].SequenceEqual(Encoding.UTF8.GetBytes(WorkerTerminal))) throw Rejected();
            using var document = JsonDocument.Parse(stderr[..newline].ToArray());
            var allocation = document.RootElement;
            string[] names = ["schema", "phase", "operation", "stageOutcome", "terminalCode", "errorClass", "nativeErrno"];
            var properties = allocation.ValueKind == JsonValueKind.Object ? allocation.EnumerateObject().ToArray() : [];
            if (properties.Length != names.Length || properties.Select(static item => item.Name).Distinct(StringComparer.Ordinal).Count() != names.Length
                || properties.Any(item => !names.Contains(item.Name, StringComparer.Ordinal))
                || allocation.GetProperty("schema").GetString() != "evidence-allocation-failure-v1"
                || allocation.GetProperty("phase").GetString() != "Allocation"
                || allocation.GetProperty("operation").GetString() != "CreateSlot"
                || allocation.GetProperty("stageOutcome").GetString() != "Failed"
                || allocation.GetProperty("terminalCode").GetString() != "StageFailed"
                || allocation.GetProperty("errorClass").GetString() != "Io") throw Rejected();
            var errno = allocation.GetProperty("nativeErrno");
            if (errno.ValueKind != JsonValueKind.Null && (!errno.TryGetInt32(out var number) || number is < 1 or > 4095)) throw Rejected();
            var prefix = "issue779-" + caseId.ToLowerInvariant();
            var projection = JsonSerializer.SerializeToUtf8Bytes(new { schema = prefix + "-joined-worker-output-v1",
                origin = "joined-worker-output", allocation, terminal_diagnostic = "ASEVD409", stdout_bytes = 0,
                stderr_bytes = stderr.Length, native_authority = false });
            var raw = JsonSerializer.SerializeToUtf8Bytes(new { schema = prefix + "-joined-worker-raw-v1",
                stdout_base64 = string.Empty, stderr_base64 = Convert.ToBase64String(stderr), stdout_bytes = 0,
                stderr_bytes = stderr.Length, native_authority = false });
            if (projection.Length > 1024 || raw.Length > 4096) throw Rejected();
            return (projection, raw);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException
            or KeyNotFoundException or FormatException or OverflowException) { throw Rejected(); }
    }

    private static object Identity(LinuxProtectedMetadata value) => new { device_major = value.DeviceMajor,
        device_minor = value.DeviceMinor, inode = value.Inode, uid = value.Uid, gid = value.Gid, mode = value.Mode,
        links = value.LinkCount, length = value.Length };
    private static bool Digest(string? value) => value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static InvalidOperationException Rejected() => new("ASEVD410: Private slot observation rejected.");
}

#endif
