using Microsoft.Win32.SafeHandles;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Reads bounded bytes from a retained descriptor through actual EOF, including a one-byte excess probe.</summary>
/// <remarks>
/// This synchronous I/O procedure selects no path and authenticates no process. The native caller retains
/// its original descriptor, identity checks, byte ceiling, cancellation token and fixed rejection factory.
/// Positional reads start at zero without changing the caller's stream position. No retry or timer is added.
/// </remarks>
internal static class SupervisionBoundedFileRead
{
    /// <summary>Returns at most the original maximum bytes after a checked EOF, or rejects an excess byte.</summary>
    /// <param name="file">Borrowed actual descriptor, which remains owned and open by the caller.</param>
    /// <param name="maximum">The caller's existing nonnegative byte ceiling, leaving room for one excess byte.</param>
    /// <param name="token">The original token, checked immediately before and after every positional read.</param>
    /// <param name="reject">Original fixed exception factory, used only for bytes beyond the ceiling.</param>
    /// <returns>Complete bytes, including an empty result only after actual EOF.</returns>
    /// <remarks>Reads remain at most 4096 bytes. Native I/O and cancellation exceptions propagate unchanged.
    /// The result is detached data, never proof of procfs, cgroup membership, identity or admission.</remarks>
    internal static byte[] Read(SafeFileHandle file, int maximum, CancellationToken token, Func<Exception> reject)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(reject);
        if (maximum < 0 || maximum == int.MaxValue) throw new ArgumentOutOfRangeException(nameof(maximum));
        var bytes = new byte[maximum + 1];
        var offset = 0;
        while (offset < bytes.Length)
        {
            token.ThrowIfCancellationRequested();
            var count = RandomAccess.Read(file, bytes.AsSpan(offset, Math.Min(4096, bytes.Length - offset)), offset);
            token.ThrowIfCancellationRequested();
            if (count == 0) return bytes[..offset];
            offset += count;
            if (offset > maximum) throw reject();
        }
        throw reject();
    }
}
