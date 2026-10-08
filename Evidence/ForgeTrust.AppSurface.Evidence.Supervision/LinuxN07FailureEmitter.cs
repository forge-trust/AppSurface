using System.Text;
using System.Text.Json;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Publishes a bounded PRIVATE raw-pair line and closed failure-only line from joined original-holder data.</summary>
/// <remarks>Base64 raw worker bytes are PRIVATE evidence, not safe public output. The existing native caller
/// authenticates original references/joins and root600 capture. Original cleanup cancellation has no fresh timer.
/// The root execution awaits the write itself; external original containment still owns stalled native I/O.
/// A line is detached data, not root custody, account-release permission or a native acceptance receipt.</remarks>
internal static class LinuxN07FailureEmitter
{
    internal const int MaximumJsonBytes = 6144;

    /// <summary>Encodes closed joined observations and actual server task status; never successful settlement.</summary>
    internal static byte[] Encode(LinuxN07FailureSettlement settlement, string descriptorSha256, TaskStatus serverStatus)
    {
        ArgumentNullException.ThrowIfNull(settlement);
        if (descriptorSha256.Length != 64 || descriptorSha256.Any(value => value is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
            || serverStatus is not (TaskStatus.RanToCompletion or TaskStatus.Faulted or TaskStatus.Canceled))
            throw new InvalidOperationException("N07 root failure data rejected.");
        using var data = JsonDocument.Parse(settlement.Bytes);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "issue779-n07-root-failure-observation-v1", ready_descriptor_sha256 = descriptorSha256,
            server = new { completed = true, status = serverStatus.ToString(),
                completed_successfully = serverStatus == TaskStatus.RanToCompletion },
            failure_settlement = data.RootElement,
            observation_only = true, native_authority = false, native_acceptance = false,
        });
        if (bytes.Length is 0 or > MaximumJsonBytes) throw new InvalidOperationException("N07 root failure data rejected.");
        return bytes;
    }

    /// <summary>Awaits raw-pair then closed root stderr lines before unchanged account close attempts.</summary>
    /// <remarks>Both frames are encoded before either write. Use only existing root-private600 bounded capture.
    /// The original worker/server/owner reference and join guards stay at the native caller; this data method
    /// creates no writer capability. Missing/partial/late transfer is failure, never truncated acceptance.</remarks>
    internal static async Task WriteAsync(LinuxN07FailureSettlement settlement, string descriptorSha256,
        TaskStatus serverStatus, CancellationToken cleanupToken)
    {
        cleanupToken.ThrowIfCancellationRequested();
        var bytes = Encode(settlement, descriptorSha256, serverStatus);
        var raw = settlement.SerializePrivateStreams(cleanupToken);
        await Console.Error.WriteLineAsync(Encoding.UTF8.GetString(raw).AsMemory(), cleanupToken).ConfigureAwait(false);
        cleanupToken.ThrowIfCancellationRequested();
        await Console.Error.WriteLineAsync(Encoding.UTF8.GetString(bytes).AsMemory(), cleanupToken).ConfigureAwait(false);
        cleanupToken.ThrowIfCancellationRequested();
    }
}
