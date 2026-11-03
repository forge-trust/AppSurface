namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Writes the caller's already bounded record bytes and literal line separators to its borrowed stream.</summary>
/// <remarks>
/// The native owner still creates and bounds each record, selects its original stream, performs every
/// surrounding identity/deadline check, records failures and closes the stream. This procedure does not
/// serialize, validate, authenticate, truncate, retain or acquire data or authority. Calls remain ordered
/// and are directly awaited without a retry, timer, cancellation proxy or background writer.
/// </remarks>
internal static class SupervisionRecordWrite
{
    /// <summary>Writes each original record, its single LF, then flushes once with the unchanged token.</summary>
    /// <param name="stream">Borrowed original writable stream; it remains open and caller-owned.</param>
    /// <param name="records">Caller-owned bounded records in their original order.</param>
    /// <param name="token">Original write token, passed unchanged to all writes and the final flush.</param>
    /// <remarks>No extra cancellation checks are introduced. Actual I/O and cancellation exceptions
    /// propagate by identity and stop further writes/flushes. Completion establishes only that these
    /// stream operations joined, never physical process exit, record validity or successful cleanup.</remarks>
    internal static async Task WriteLinesAsync(Stream stream, ReadOnlyMemory<byte>[] records, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(records);
        foreach (var bytes in records)
        {
            await stream.WriteAsync(bytes, token).ConfigureAwait(false);
            await stream.WriteAsync(new byte[] { (byte)'\n' }, token).ConfigureAwait(false);
        }
        await stream.FlushAsync(token).ConfigureAwait(false);
    }
}
