using System.Security.Cryptography;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Encodes private failure diagnostics from the original completed output collector.</summary>
/// <remarks>
/// This detached data encoder grants no process, READY, custody, account cleanup or admission authority.
/// Raw stderr is base64 encoded only for the fixed private cancellation image's protected root log.
/// Ordinary execution never calls this emitter. A missing receipt remains unavailable; neither a failed
/// native inspection nor a joined stream receipt establishes physical settlement.
/// </remarks>
internal static class LinuxJoinedWorkerOutputDiagnostic
{
    /// <summary>Maximum private retained stderr prefix; actual received-byte accounting is unchanged.</summary>
    internal const int MaximumPrefixBytes = 65_536;
    /// <summary>Maximum encoded private diagnostic, including the base64 prefix and closed metadata.</summary>
    internal const int MaximumJsonBytes = 98_304;

    /// <summary>Copies bounded diagnostic data; callers must independently establish receipt provenance.</summary>
    /// <param name="generation">Original generation identifier as data, not an authenticated owner.</param>
    /// <param name="output">Immutable receipt from the completed original collector, or null when unavailable.</param>
    /// <returns>Bounded canonical JSON with explicit absence of native settlement and acceptance authority.</returns>
    /// <exception cref="EvidenceAdmissionException">Fixed ASEVD410 for inconsistent detached input data.</exception>
    /// <remarks>No stream read, callback, wait, deadline, exception text or successful-run projection is introduced.</remarks>
    internal static byte[] EncodeDetached(Guid generation, SupervisionOutputReceipt? output)
    {
        if (generation == Guid.Empty) throw Rejected();
        if (output is not null)
        {
            Validate(output.Stdout);
            Validate(output.Stderr);
            if (!Enum.IsDefined(output.Failure) || output.ReceivedBytes < 0
                || output.Stdout.ReceivedBytes > long.MaxValue - output.Stderr.ReceivedBytes
                || output.ReceivedBytes != output.Stdout.ReceivedBytes + output.Stderr.ReceivedBytes
                || output.ReceivedByteLimit is <= 0 or > EvidenceRunBudgetLimits.MaximumProcessOutputBytes)
                throw Rejected();
        }
        var stderr = output?.Stderr;
        var prefix = stderr is null ? Array.Empty<byte>()
            : stderr.Prefix.AsSpan(0, Math.Min(stderr.Prefix.Length, MaximumPrefixBytes)).ToArray();
        var bytes = EvidenceCanonicalJson.Serialize(new
        {
            schema = "issue779-joined-worker-output-diagnostic-v1",
            generation = generation.ToString("N"),
            status = output is null ? "unavailable" : "joined",
            source = "original-output-collector",
            received_bytes = output?.ReceivedBytes,
            received_byte_limit = output?.ReceivedByteLimit,
            stdout = output is null ? null : StreamData(output.Stdout),
            stderr = output is null ? null : new
            {
                received_bytes = stderr!.ReceivedBytes,
                retained_bytes = prefix.Length,
                prefix_sha256 = Convert.ToHexStringLower(SHA256.HashData(prefix)),
                prefix_base64 = Convert.ToBase64String(prefix),
                prefix_truncated = stderr.ReceivedBytes > prefix.Length,
                eof = stderr.EndOfStream,
                failure = FailureName(stderr.Failure)
            },
            failure = output is null ? "unavailable" : FailureName(output.Failure),
            quota_exceeded = output?.QuotaExceeded,
            stop_signal_failed = output?.StopSignalFailed,
            physical_settlement_unknown = true,
            native_authority = false,
            native_acceptance = false
        });
        if (bytes.Length > MaximumJsonBytes) throw Rejected();
        return bytes;
    }

    private static object StreamData(SupervisionOutputStreamReceipt stream)
    {
        var prefix = stream.Prefix.AsSpan(0, Math.Min(stream.Prefix.Length, MaximumPrefixBytes));
        return new
        {
            received_bytes = stream.ReceivedBytes,
            retained_bytes = prefix.Length,
            prefix_sha256 = Convert.ToHexStringLower(SHA256.HashData(prefix)),
            prefix_truncated = stream.ReceivedBytes > prefix.Length,
            eof = stream.EndOfStream,
            failure = FailureName(stream.Failure)
        };
    }

    private static void Validate(SupervisionOutputStreamReceipt stream)
    {
        if (stream is null || stream.Prefix.IsDefault || stream.ReceivedBytes < 0
            || stream.ReceivedBytes < stream.Prefix.Length || !Enum.IsDefined(stream.Failure)
            || stream.Prefix.Length > EvidenceRunBudgetLimits.RetainedOutputPrefixBytesPerStream)
            throw Rejected();
    }

    private static string FailureName(SupervisionOutputFailure failure) => failure switch
    {
        SupervisionOutputFailure.None => "none",
        SupervisionOutputFailure.QuotaExceeded => "quota-exceeded",
        SupervisionOutputFailure.ReadFailed => "read-failed",
        SupervisionOutputFailure.Cancelled => "cancelled",
        _ => throw Rejected()
    };

    private static EvidenceAdmissionException Rejected() => new("ASEVD410", "The private joined output diagnostic was rejected.");
}
