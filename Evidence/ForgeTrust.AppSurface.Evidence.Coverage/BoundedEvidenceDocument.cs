namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>Reads an owned metadata file into memory while enforcing its byte limit.</summary>
internal static class BoundedEvidenceDocument
{
    /// <summary>Reads up to the limit and probes for one extra byte to detect growth.</summary>
    /// <param name="stream">A readable, seekable document stream at its starting position. The caller retains ownership.</param>
    /// <param name="maximumBytes">The inclusive byte limit.</param>
    /// <param name="cancellationToken">Cancellation to observe before and after reads.</param>
    /// <returns>The captured bytes, or null if the length check or a read exceeds the limit.</returns>
    internal static byte[]? Read(Stream stream, int maximumBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead) throw new ArgumentException("Evidence document stream must be readable.", nameof(stream));
        if (maximumBytes < 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        cancellationToken.ThrowIfCancellationRequested();
        var reportedLength = stream.Length;
        if (reportedLength < 0 || reportedLength > maximumBytes) return null;

        using var memory = new MemoryStream((int)Math.Min(reportedLength, 64 * 1024));
        var buffer = new byte[16 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = (long)maximumBytes - memory.Length;
            var count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining + 1));
            cancellationToken.ThrowIfCancellationRequested();
            if (count == 0) return memory.ToArray();
            if (count > remaining) return null;
            memory.Write(buffer, 0, count);
        }
    }
}
