using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Projects original joined coverage-stream accounting into closed, bounded diagnostic data.</summary>
/// <remarks>No output prefix, exception, pathname, identity capability or success authority is serialized.</remarks>
internal static class LinuxCoverageOutputObservation
{
    /// <summary>Encodes exact aggregate/pair counters after their original collection tasks have joined.</summary>
    internal static byte[] Encode(SupervisionRunOutputReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return EvidenceCanonicalJson.Serialize(new
        {
            schema = "evidence-coverage-output-v1",
            registered_pairs = receipt.RegisteredPairs,
            received_bytes = receipt.ReceivedBytes,
            received_byte_limit = receipt.ReceivedByteLimit,
            failure = receipt.Failure.ToString(),
            quota_exceeded = receipt.QuotaExceeded,
            stop_signal_failed = receipt.StopSignalFailed,
            pairs = receipt.Pairs.Select(static pair => new
            {
                received_bytes = pair.ReceivedBytes,
                stdout_bytes = pair.Stdout.ReceivedBytes,
                stderr_bytes = pair.Stderr.ReceivedBytes,
                stdout_eof = pair.Stdout.EndOfStream,
                stderr_eof = pair.Stderr.EndOfStream,
                failure = pair.Failure.ToString(),
                quota_exceeded = pair.QuotaExceeded,
            }).ToArray(),
        });
    }
}
