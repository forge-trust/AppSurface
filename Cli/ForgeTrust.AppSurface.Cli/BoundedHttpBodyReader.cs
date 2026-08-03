using System.Buffers;

namespace ForgeTrust.AppSurface.Cli;

/// <summary>
/// Reads an HTTP response body without retaining more than a caller-provided byte limit.
/// </summary>
/// <remarks>
/// Callers must inspect <see cref="BoundedHttpBody.Truncated"/> before parsing the retained bytes. A truncated body is
/// useful for bounded diagnostics, but it is not a complete protocol message and must not be treated as one.
/// </remarks>
internal static class BoundedHttpBodyReader
{
    private const int MaximumReadBufferBytes = 81920;
    private const int InitialBufferCapacityBytes = 4096;

    /// <summary>
    /// Reads one content stream up to the configured byte limit.
    /// </summary>
    /// <param name="content">The response content to read.</param>
    /// <param name="maxBodyBytes">The maximum number of bytes retained.</param>
    /// <param name="cancellationToken">Cancels the bounded read.</param>
    /// <returns>The retained bytes and whether additional bytes were discarded.</returns>
    public static async Task<BoundedHttpBody> ReadAsync(
        HttpContent content,
        int maxBodyBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (maxBodyBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBodyBytes));
        }

        var rentedBuffer = ArrayPool<byte>.Shared.Rent(Math.Min(maxBodyBytes, MaximumReadBufferBytes));
        try
        {
            await using var stream = await content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream(Math.Min(maxBodyBytes, InitialBufferCapacityBytes));
            var total = 0;
            while (total < maxBodyBytes)
            {
                var read = await stream.ReadAsync(
                    rentedBuffer.AsMemory(0, Math.Min(rentedBuffer.Length, maxBodyBytes - total)),
                    cancellationToken);
                if (read == 0)
                {
                    return new BoundedHttpBody(buffer.ToArray(), false);
                }

                buffer.Write(rentedBuffer, 0, read);
                total += read;
            }

            var probe = await stream.ReadAsync(rentedBuffer.AsMemory(0, 1), cancellationToken);
            return new BoundedHttpBody(buffer.ToArray(), probe != 0);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedBuffer);
        }
    }
}

/// <summary>
/// Captures the bounded result of one HTTP response-body read.
/// </summary>
/// <param name="Bytes">The retained bytes, capped at the requested maximum.</param>
/// <param name="Truncated">Whether bytes beyond the requested maximum were discarded.</param>
internal sealed record BoundedHttpBody(byte[] Bytes, bool Truncated);
