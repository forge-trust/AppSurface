using System.Buffers.Binary;

namespace ForgeTrust.AppSurface.Auth.Keycloak;

/// <summary>Read-only tar transport that rejects oversized headers and payloads before TarReader sees them.</summary>
internal sealed class TarBudgetReadStream : Stream
{
    private readonly Stream inner;
    private readonly byte[] header = new byte[512];
    private readonly long maximumBytes, maximumEntryBytes, maximumMetadataBytes, maximumSpecialMetadataBytes;
    private readonly int maximumEntries;
    private int headerPosition = 512, entries, zeroBlocks;
    private long payloadRemaining, consumed, metadata;
    private bool eof;

    internal TarBudgetReadStream(Stream inner, long maximumBytes, long maximumEntryBytes, int maximumEntries = 1_000_000,
        long maximumMetadataBytes = 4 * 1024 * 1024, long maximumSpecialMetadataBytes = 64 * 1024)
    { this.inner = inner; this.maximumBytes = maximumBytes; this.maximumEntryBytes = maximumEntryBytes; this.maximumEntries = maximumEntries; this.maximumMetadataBytes = maximumMetadataBytes; this.maximumSpecialMetadataBytes = maximumSpecialMetadataBytes; }

    internal long Consumed => consumed;
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> destination)
    {
        if (destination.Length == 0 || eof) return 0;
        if (headerPosition == 512 && payloadRemaining == 0)
        {
            int got = 0;
            while (got < 512) { int n = inner.Read(header, got, 512 - got); if (n == 0) { if (got != 0 || zeroBlocks < 2) throw new InvalidDataException("Truncated tar."); eof = true; return 0; } got += n; }
            Account(512); headerPosition = 0;
            if (header.AsSpan().IndexOfAnyExcept((byte)0) < 0) { zeroBlocks++; }
            else
            {
                if (zeroBlocks != 0 || ++entries > maximumEntries) throw new InvalidDataException("Invalid tar terminator or entry count.");
                long size = ParseOctal(header.AsSpan(124, 12));
                bool special = header[156] is (byte)'x' or (byte)'g' or (byte)'L' or (byte)'K';
                if (size > maximumEntryBytes || (special && size > maximumSpecialMetadataBytes)) throw new InvalidDataException("Tar member exceeds the bounded profile.");
                if (special && (metadata = checked(metadata + size)) > maximumMetadataBytes) throw new InvalidDataException("Tar metadata aggregate exceeds the bounded profile.");
                payloadRemaining = checked((size + 511) / 512 * 512);
            }
        }
        if (headerPosition < 512) { int n = Math.Min(destination.Length, 512 - headerPosition); header.AsSpan(headerPosition, n).CopyTo(destination); headerPosition += n; return n; }
        int wanted = (int)Math.Min(destination.Length, payloadRemaining); int read = inner.Read(destination[..wanted]);
        if (read == 0) throw new InvalidDataException("Truncated tar payload.");
        Account(read); payloadRemaining -= read; return read;
    }
    internal void Finish() { Span<byte> buffer = stackalloc byte[8192]; while (Read(buffer) != 0) { } }
    private void Account(int bytes) { consumed = checked(consumed + bytes); if (consumed > maximumBytes) throw new InvalidDataException("Tar expanded budget exceeded."); }
    private static long ParseOctal(ReadOnlySpan<byte> value)
    { long result = 0; bool started = false, ended = false; foreach (byte b in value) { if (b is 0 or 32) { if (started) ended = true; continue; } if (ended || b < '0' || b > '7') throw new InvalidDataException("Unsupported tar numeric encoding."); started = true; result = checked(result * 8 + b - '0'); } return result; }
    protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
