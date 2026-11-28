using System.Text;

namespace ForgeTrust.AppSurface.Evidence.Contracts;

/// <summary>Defines the shared bounded UTF-8 frames for cooperative child enrollment.</summary>
internal static class EvidenceOwnedChildWire
{
    private const string BeginVerb = "BEGIN";
    private const string AcknowledgementPrefix = "ACK|";
    private const string CompletionPrefix = "COMPLETE|";
    private const string DonePrefix = "DONE|";
    private const string RejectionPrefix = "REJECT|";

    /// <summary>Formats the child enrollment request using the existing wire representation.</summary>
    /// <param name="executionId">The execution identifier supplied by the invocation.</param>
    /// <param name="nonce">The enrollment nonce supplied by the invocation.</param>
    /// <returns>The BEGIN frame payload without its newline terminator.</returns>
    internal static string FormatBegin(string executionId, string nonce) => $"{BeginVerb}|{executionId}|{nonce}";

    /// <summary>Parses a BEGIN payload with exactly its existing three fields.</summary>
    /// <param name="value">The decoded frame payload.</param>
    /// <param name="executionId">The execution identifier, or an empty string when parsing fails.</param>
    /// <param name="nonce">The nonce, or an empty string when parsing fails.</param>
    /// <returns>True when the payload has the BEGIN keyword and exactly two data fields.</returns>
    internal static bool TryParseBegin(string? value, out string executionId, out string nonce)
    {
        executionId = string.Empty;
        nonce = string.Empty;
        var parts = value?.Split('|');
        if (parts is not { Length: 3 } || !string.Equals(parts[0], BeginVerb, StringComparison.Ordinal)) return false;
        executionId = parts[1];
        nonce = parts[2];
        return true;
    }

    /// <summary>Formats the enrollment acknowledgement using a canonical N-format child identifier.</summary>
    /// <param name="childId">The admitted child identifier.</param>
    /// <returns>The ACK frame payload without its newline terminator.</returns>
    internal static string FormatAcknowledgement(Guid childId) => AcknowledgementPrefix + childId.ToString("N");

    /// <summary>Parses an ACK payload containing an N-format child identifier.</summary>
    /// <param name="value">The decoded frame payload.</param>
    /// <param name="childId">The parsed child identifier, or <see cref="Guid.Empty"/> when parsing fails.</param>
    /// <returns>True when the payload contains ACK followed by a valid N-format GUID.</returns>
    internal static bool TryParseAcknowledgement(string? value, out Guid childId)
    {
        childId = Guid.Empty;
        return value is not null
            && value.StartsWith(AcknowledgementPrefix, StringComparison.Ordinal)
            && Guid.TryParseExact(value.AsSpan(AcknowledgementPrefix.Length), "N", out childId);
    }

    /// <summary>Formats the child completion request using a canonical N-format identifier.</summary>
    /// <param name="childId">The enrolled child identifier.</param>
    /// <returns>The COMPLETE frame payload without its newline terminator.</returns>
    internal static string FormatCompletion(Guid childId) => CompletionPrefix + childId.ToString("N");

    /// <summary>Checks for an exact COMPLETE payload matching the admitted child.</summary>
    /// <param name="value">The decoded frame payload.</param>
    /// <param name="childId">The admitted child identifier.</param>
    /// <returns>True only for the exact canonical completion payload.</returns>
    internal static bool IsCompletion(string? value, Guid childId)
        => string.Equals(value, FormatCompletion(childId), StringComparison.Ordinal);

    /// <summary>Formats the completion acknowledgement using a canonical N-format identifier.</summary>
    /// <param name="childId">The completed child identifier.</param>
    /// <returns>The DONE frame payload without its newline terminator.</returns>
    internal static string FormatDone(Guid childId) => DonePrefix + childId.ToString("N");

    /// <summary>Checks for an exact DONE payload matching the enrolled child.</summary>
    /// <param name="value">The decoded frame payload.</param>
    /// <param name="childId">The enrolled child identifier.</param>
    /// <returns>True only for the exact canonical completion acknowledgement.</returns>
    internal static bool IsDoneAcknowledgement(string? value, Guid childId)
        => string.Equals(value, FormatDone(childId), StringComparison.Ordinal);

    /// <summary>Formats a rejection payload using the existing reason text.</summary>
    /// <param name="reason">The protocol rejection reason.</param>
    /// <returns>The REJECT frame payload without its newline terminator.</returns>
    internal static string FormatRejection(string reason) => RejectionPrefix + reason;

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
