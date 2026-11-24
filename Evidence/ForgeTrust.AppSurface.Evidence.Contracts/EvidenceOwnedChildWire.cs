using System.Text;

namespace ForgeTrust.AppSurface.Evidence.Contracts;

/// <summary>Defines the shared cooperative child channel configuration and bounded UTF-8 frames.</summary>
internal static class EvidenceOwnedChildWire
{
    /// <summary>Identifies the current-user-only invocation pipe.</summary>
    internal const string PipeEnvironmentName = "APPSURFACE_EVIDENCE_CHILD_PIPE";
    /// <summary>Identifies the current Evidence execution.</summary>
    internal const string ExecutionEnvironmentName = "APPSURFACE_EVIDENCE_CHILD_EXECUTION_ID";
    /// <summary>Authenticates enrollment in the current execution.</summary>
    internal const string NonceEnvironmentName = "APPSURFACE_EVIDENCE_CHILD_NONCE";

    /// <summary>Writes and flushes a frame after checking its byte count, including the trailing newline.</summary>
    /// <param name="stream">The invocation channel stream.</param>
    /// <param name="value">The protocol message without its terminator.</param>
    /// <param name="cancellationToken">The endpoint's protocol deadline or cancellation token.</param>
    /// <param name="oversizedFrameError">Optional endpoint-specific exception for a locally oversized frame.</param>
    /// <returns>A task that completes after the frame is flushed.</returns>
    internal static async Task WriteFrameAsync(Stream stream, string value, CancellationToken cancellationToken,
        Func<Exception>? oversizedFrameError = null)
    {
        var bytes = Encoding.UTF8.GetBytes(value + "\n");
        if (bytes.Length > EvidenceOwnedChild.MaximumFrameBytes)
            throw oversizedFrameError?.Invoke() ?? new IOException("Child protocol frame exceeds its limit.");

        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads exactly one bounded frame, leaving any following bytes for the next call.</summary>
    /// <param name="stream">The invocation channel stream.</param>
    /// <param name="cancellationToken">The endpoint's protocol deadline or cancellation token.</param>
    /// <returns>The decoded message, or null for EOF before any frame byte.</returns>
    /// <exception cref="IOException">The frame is truncated or exceeds the encoded byte limit.</exception>
    internal static async Task<string?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new byte[EvidenceOwnedChild.MaximumFrameBytes];
        var length = 0;
        while (length < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(length, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0) return length == 0 ? null : throw new IOException("Truncated child protocol frame.");
            if (bytes[length++] == (byte)'\n') return Encoding.UTF8.GetString(bytes, 0, length - 1);
        }
        throw new IOException("Child protocol frame exceeds its limit.");
    }
}
