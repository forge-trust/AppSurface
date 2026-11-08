using System.Security.Cryptography;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>Reads an optional concept artifact through the caller-owned archive and trusted-path boundary.</summary>
/// <remarks>
/// The portable concept core consumes detached bytes. This adapter confines filesystem access, checks the complete
/// UTF-8 artifact budget before and during reading, and proves the final bytes against the caller's trust anchor.
/// A rejected artifact is never supplied to the parser as a truncated prefix.
/// </remarks>
internal static class AppSurfaceDocsConceptFileReader
{
    /// <summary>Reads one ordinary, bounded, digest-pinned file beneath a trusted root.</summary>
    /// <param name="root">Already validated trusted directory.</param>
    /// <param name="relativePath">Slash-separated relative artifact path.</param>
    /// <param name="expectedSha256">SHA-256 hex digest from verified archive metadata or the trusted catalog.</param>
    /// <param name="limit">Effective shared rewritten-file and switch-artifact budget.</param>
    /// <param name="expectedLength">Archive length, or null for an independently pinned bridge.</param>
    /// <returns>Complete verified bytes detached from the filesystem.</returns>
    /// <exception cref="InvalidDataException">The artifact is unsafe, oversized, changed, or not pinned.</exception>
    internal static byte[] Read(
        string root,
        string relativePath,
        string expectedSha256,
        long limit,
        long? expectedLength = null)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || relativePath.IndexOfAny(['\\', ':', '?', '#', '%']) >= 0
            || relativePath.StartsWith('/')
            || relativePath.Split('/').Any(segment => segment.Length == 0 || segment is "." or "..")
            || string.IsNullOrWhiteSpace(expectedSha256)
            || expectedSha256.Length != 64
            || !expectedSha256.All(char.IsAsciiHexDigit)
            || !AppSurfaceDocsTrustedReleasePathGuard.TryValidateFileCandidate(root, relativePath, out var path, out _))
        {
            throw new InvalidDataException("Concept artifact must be an ordinary confined file with a SHA-256 pin.");
        }

        using var stream = File.OpenRead(path);
        return ReadVerifiedStream(stream, expectedSha256, limit, expectedLength);
    }

    /// <summary>Reads and verifies a bounded complete stream, including streams that grow after their initial length check.</summary>
    /// <param name="stream">Caller-owned readable file stream; the caller retains disposal ownership.</param>
    /// <param name="expectedSha256">Already validated exact artifact digest.</param>
    /// <param name="limit">Effective per-artifact byte budget.</param>
    /// <param name="expectedLength">Expected verified archive length, or null for a pinned bridge.</param>
    /// <returns>Complete detached verified bytes, never a truncated prefix.</returns>
    internal static byte[] ReadVerifiedStream(Stream stream, string expectedSha256, long limit, long? expectedLength = null)
    {
        if (stream.Length > limit || (expectedLength is { } length && stream.Length != length))
        {
            throw new InvalidDataException("Concept artifact length exceeds the configured budget or verified inventory.");
        }

        using var output = new MemoryStream();
        Span<byte> buffer = stackalloc byte[8192];
        int read;
        while ((read = stream.Read(buffer)) != 0)
        {
            if (output.Length + read > limit)
            {
                throw new InvalidDataException("Concept artifact grew beyond the configured budget while reading.");
            }

            output.Write(buffer[..read]);
        }

        var bytes = output.ToArray();
        if ((expectedLength is { } finalLength && bytes.LongLength != finalLength)
            || !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Concept artifact bytes no longer match their verified pin.");
        }

        return bytes;
    }
}
