using Microsoft.Win32.SafeHandles;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Actual owned-file procedure controls only; no native process identity or protected owner is fabricated.</summary>
public sealed class SupervisionBoundedFileReadTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 8192)]
    [InlineData(1, 1)]
    [InlineData(1024, 1024)]
    [InlineData(4095, 4096)]
    [InlineData(4096, 4096)]
    [InlineData(4097, 8192)]
    [InlineData(8192, 8192)]
    public void ActualEofReturnsExactBinaryBytesWithoutChangingPositionOrClosingHandle(int length, int maximum)
    {
        using var file = OpenFile();
        var bytes = Contents(length);
        file.Write(bytes);
        file.Flush();
        file.Position = length / 2;
        var actual = SupervisionBoundedFileRead.Read(file.SafeFileHandle, maximum, default, Invalid);
        Assert.Equal(bytes, actual);
        Assert.Equal(length / 2, file.Position);
        Assert.False(file.SafeFileHandle.IsClosed);
        Assert.Equal(bytes, SupervisionBoundedFileRead.Read(file.SafeFileHandle, maximum, default, Invalid));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1024)]
    [InlineData(4095)]
    [InlineData(4096)]
    [InlineData(8192)]
    public void OneExcessByteRejectsByOriginalExceptionAndLeavesActualFileUnchanged(int maximum)
    {
        using var file = OpenFile();
        var bytes = Contents(maximum + 1);
        file.Write(bytes);
        file.Flush();
        var expected = Invalid();
        var actual = Assert.Throws<IOException>(() =>
            SupervisionBoundedFileRead.Read(file.SafeFileHandle, maximum, default, () => expected));
        Assert.Same(expected, actual);
        Assert.False(file.SafeFileHandle.IsClosed);
        Assert.Equal(bytes, SupervisionBoundedFileRead.Read(file.SafeFileHandle, maximum + 1, default, Invalid));
    }

    [Fact]
    public void PrecancelledOriginalTokenRejectsBeforeIoEvenWhenDescriptorIsClosed()
    {
        using var file = OpenFile();
        var handle = file.SafeFileHandle;
        file.Dispose();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var error = Assert.Throws<OperationCanceledException>(() =>
            SupervisionBoundedFileRead.Read(handle, 4096, cancelled.Token, Invalid));
        Assert.Equal(cancelled.Token, error.CancellationToken);
    }

    [Fact]
    public void ClosedDescriptorFailurePropagatesWithoutBeingReplacedByLimitRejection()
    {
        using var file = OpenFile();
        var handle = file.SafeFileHandle;
        file.Dispose();
        var rejections = 0;
        Assert.Throws<ObjectDisposedException>(() =>
            SupervisionBoundedFileRead.Read(handle, 4096, default, () => { rejections++; return Invalid(); }));
        Assert.Equal(0, rejections);
    }

    [Fact]
    public void MissingArgumentsAndImpossibleBufferBoundsRejectBeforeNativeIo()
    {
        using var invalid = new SafeFileHandle((nint)(-1), ownsHandle: false);
        Assert.Throws<ArgumentNullException>(() => SupervisionBoundedFileRead.Read(null!, 1, default, Invalid));
        Assert.Throws<ArgumentNullException>(() => SupervisionBoundedFileRead.Read(invalid, 1, default, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => SupervisionBoundedFileRead.Read(invalid, -1, default, Invalid));
        Assert.Throws<ArgumentOutOfRangeException>(() => SupervisionBoundedFileRead.Read(invalid, int.MaxValue, default, Invalid));
    }

    private static byte[] Contents(int count) => Enumerable.Range(0, count).Select(static n => (byte)(n % 251)).ToArray();
    private static IOException Invalid() => new("Fixed counted-read rejection.");
    private static FileStream OpenFile() => new(Path.Combine(Path.GetTempPath(), "issue779-counted-" + Guid.NewGuid().ToString("N")),
        FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
}
