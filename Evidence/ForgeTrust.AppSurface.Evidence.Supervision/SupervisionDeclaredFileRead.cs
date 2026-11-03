using Microsoft.Win32.SafeHandles;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Reads exactly the declared bytes of an already-open file and rejects truncation or growth.</summary>
/// <remarks>
/// This IO primitive selects no pathname and creates no handle, owner, custody, admission or native
/// authority. The native caller retains its original metadata/name checks, deadline, buffer ownership
/// and exception projection. It uses positional reads from offset zero and never changes a stream's
/// shared position. Each borrowed buffer segment is valid only during synchronous consumption.
/// </remarks>
internal static class SupervisionDeclaredFileRead
{
    /// <summary>Delivers each actual read once, then checks for a byte beyond the declared end.</summary>
    /// <param name="file">The caller's retained original file descriptor; it remains owned by the caller.</param>
    /// <param name="expectedLength">The caller's independently bounded retained length, no larger than Int64.MaxValue.</param>
    /// <param name="buffer">The caller's existing nonempty buffer; allocation, pooling and clearing remain caller-owned.</param>
    /// <param name="beforeRead">Original cancellation/deadline or diagnostic checks before every data read.</param>
    /// <param name="receive">Synchronous consumer; it must not retain the borrowed memory.</param>
    /// <param name="beforeEof">Original checks immediately before the one-byte declared-end probe.</param>
    /// <param name="reject">The caller's original fixed exception factory for premature EOF or excess bytes.</param>
    /// <remarks>
    /// A zero-length declaration still performs the end probe, but has no data-read callback. Partial
    /// reads remain bounded by the retained length. Exceptions from IO, checks or consumption propagate
    /// unchanged. No retry, timeout, replacement buffer or cancellation proxy is introduced. The caller
    /// must perform its original final metadata/name checks before accepting the data or digest.
    /// </remarks>
    internal static void Read(SafeFileHandle file, ulong expectedLength, byte[] buffer,
        Action beforeRead, Action<ReadOnlyMemory<byte>> receive, Action beforeEof, Func<Exception> reject)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(beforeRead);
        ArgumentNullException.ThrowIfNull(receive);
        ArgumentNullException.ThrowIfNull(beforeEof);
        ArgumentNullException.ThrowIfNull(reject);
        if (buffer.Length == 0) throw new ArgumentException("A nonempty read buffer is required.", nameof(buffer));
        if (expectedLength > long.MaxValue) throw new ArgumentOutOfRangeException(nameof(expectedLength));
        long offset = 0;
        while ((ulong)offset < expectedLength)
        {
            beforeRead();
            var count = (int)Math.Min((ulong)buffer.Length, expectedLength - (ulong)offset);
            var read = RandomAccess.Read(file, buffer.AsSpan(0, count), offset);
            if (read == 0) throw reject();
            receive(buffer.AsMemory(0, read));
            offset += read;
        }
        beforeEof();
        if (RandomAccess.Read(file, buffer.AsSpan(0, 1), offset) != 0) throw reject();
    }
}
