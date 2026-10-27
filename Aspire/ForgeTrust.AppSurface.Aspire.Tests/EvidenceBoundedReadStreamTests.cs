using ForgeTrust.AppSurface.Evidence.Aspire;

namespace ForgeTrust.AppSurface.Aspire.Tests;

public sealed class EvidenceBoundedReadStreamTests
{
    [Fact]
    public async Task Read_CombinesSynchronousAndAsynchronousReadsUnderOneBudget()
    {
        using var input = new MemoryStream([1, 2, 3]);
        using var bounded = new EvidenceBoundedReadStream(input, 3, CancellationToken.None);
        var bytes = new byte[3];

        Assert.Equal(1, bounded.Read(bytes, 0, 1));
        Assert.Equal(2, await bounded.ReadAsync(bytes.AsMemory(1)));
        Assert.Equal(new byte[] { 1, 2, 3 }, bytes);
        Assert.Equal(0, bounded.Read(bytes, 0, 1));
        Assert.Equal(0, await bounded.ReadAsync(bytes.AsMemory()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Read_RejectsBytesThatArriveAfterTheLimit(bool asynchronous)
    {
        using var input = new MemoryStream([1, 2, 3]);
        using var bounded = new EvidenceBoundedReadStream(input, 2, CancellationToken.None);
        var bytes = new byte[2];
        Assert.Equal(2, bounded.Read(bytes, 0, 2));

        if (asynchronous)
            await Assert.ThrowsAsync<InvalidDataException>(async () => Assert.Equal(1, await bounded.ReadAsync(bytes.AsMemory())));
        else
            Assert.Throws<InvalidDataException>(() => bounded.Read(bytes, 0, 1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Read_HonorsTheOwnerCancellationBeforeConsumingBytes(bool asynchronous)
    {
        using var input = new MemoryStream([1]);
        using var owner = new CancellationTokenSource();
        using var bounded = new EvidenceBoundedReadStream(input, 1, owner.Token);
        owner.Cancel();
        var bytes = new byte[1];

        if (asynchronous)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => Assert.Equal(1, await bounded.ReadAsync(bytes.AsMemory())));
        else
            Assert.ThrowsAny<OperationCanceledException>(() => bounded.Read(bytes, 0, 1));
        Assert.Equal(0, input.Position);
    }

    [Fact]
    public async Task ReadAsync_HonorsPerReadCancellationBeforeConsumingBytes()
    {
        using var input = new MemoryStream([1]);
        using var bounded = new EvidenceBoundedReadStream(input, 1, CancellationToken.None);
        using var perRead = new CancellationTokenSource();
        perRead.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            Assert.Equal(1, await bounded.ReadAsync(new byte[1].AsMemory(), perRead.Token)));
        Assert.Equal(0, input.Position);
    }

    [Fact]
    public void StreamContract_IsForwardOnlyAndDisposalClosesItsOwnedInput()
    {
        var input = new MemoryStream([1, 2]);
        var bounded = new EvidenceBoundedReadStream(input, 2, CancellationToken.None);

        Assert.True(bounded.CanRead);
        Assert.False(bounded.CanSeek);
        Assert.False(bounded.CanWrite);
        Assert.Equal(2, bounded.Length);
        Assert.Equal(0, bounded.Position);
        bounded.Flush();
        Assert.Throws<NotSupportedException>(() => bounded.Position = 0);
        Assert.Throws<NotSupportedException>(() => bounded.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => bounded.SetLength(0));
        Assert.Throws<NotSupportedException>(() => bounded.Write([3], 0, 1));
        bounded.Dispose();
        Assert.False(input.CanRead);
    }
}
