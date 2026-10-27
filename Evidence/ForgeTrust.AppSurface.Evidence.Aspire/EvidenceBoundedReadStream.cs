namespace ForgeTrust.AppSurface.Evidence.Aspire;

/// <summary>Owns a forward-only read stream and enforces a cumulative byte limit and caller cancellation.</summary>
/// <remarks>Used for untrusted fan-in JSON and XML. The limit is checked after every read, including after file growth.</remarks>
internal sealed class EvidenceBoundedReadStream(Stream inner, long limit, CancellationToken cancellationToken) : Stream
{
    private long _count;

    public override int Read(byte[] buffer, int offset, int count)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var read = inner.Read(buffer, offset, count);
        Count(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ct.ThrowIfCancellationRequested();
        var read = await inner.ReadAsync(buffer, ct).ConfigureAwait(false);
        Count(read);
        return read;
    }

    private void Count(int read)
    {
        _count += read;
        if (_count > limit) throw new InvalidDataException("Evidence exceeds its size limit.");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
