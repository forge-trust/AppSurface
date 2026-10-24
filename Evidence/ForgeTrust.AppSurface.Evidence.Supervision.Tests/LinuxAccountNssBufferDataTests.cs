using System.Runtime.InteropServices;
using System.Text;
using ForgeTrust.AppSurface.Evidence.Supervision;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Bounded NSS buffer decoding tests only; no NSS lookup, root identity, or account authority is created.</summary>
public sealed class LinuxAccountNssBufferDataTests
{
    [Theory]
    [InlineData(0, 1, true)]
    [InlineData(3, 1, true)]
    [InlineData(4, 1, false)]
    [InlineData(3, 2, false)]
    [InlineData(-1, 1, false)]
    public void ContainsAcceptsOnlyRangesInsideThePinnedCallerBuffer(int offset, int length, bool expected)
    {
        var buffer = new byte[4];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var address = handle.AddrOfPinnedObject();
            Assert.Equal(expected, LinuxAccountNssBufferData.Contains(offset < 0 ? 0 : address + offset, length, address, buffer.Length));
        }
        finally { handle.Free(); }
    }

    [Fact]
    public void ReadStringAcceptsStrictUtf8AndTheExactFieldBoundary()
    {
        var value = Encoding.UTF8.GetBytes("é\0");
        var valueHandle = GCHandle.Alloc(value, GCHandleType.Pinned);
        try
        {
            var address = valueHandle.AddrOfPinnedObject();
            Assert.Equal("é", LinuxAccountNssBufferData.ReadString(address, address, value.Length, 2));
        }
        finally { valueHandle.Free(); }

        var boundary = new byte[] { (byte)'a', (byte)'b', 0 };
        var boundaryHandle = GCHandle.Alloc(boundary, GCHandleType.Pinned);
        try
        {
            var address = boundaryHandle.AddrOfPinnedObject();
            Assert.Equal("ab", LinuxAccountNssBufferData.ReadString(address, address, boundary.Length, 2));
        }
        finally { boundaryHandle.Free(); }
    }

    [Theory]
    [MemberData(nameof(InvalidStrings))]
    public void ReadStringRejectsNullOutOfRangeUnterminatedInvalidUtf8AndOverlongData(byte[] bytes, int offset, int size, int maximum)
    {
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var bufferAddress = handle.AddrOfPinnedObject();
            var address = offset < 0 ? (nint)0 : bufferAddress + offset;
            var error = Assert.Throws<LinuxRunAccountException>(() =>
                LinuxAccountNssBufferData.ReadString(address, bufferAddress, size, maximum));
            Assert.Equal(LinuxRunAccountFailure.NssFailed, error.Failure);
            Assert.Null(error.InnerException);
        }
        finally { handle.Free(); }
    }

    public static IEnumerable<object[]> InvalidStrings()
    {
        yield return [new byte[] { 0 }, -1, 1, 1];
        yield return [new byte[] { (byte)'x', 0 }, 2, 2, 1];
        yield return [new byte[] { (byte)'x' }, 0, 1, 1];
        yield return [new byte[] { 0xc3, 0x28, 0 }, 0, 3, 2];
        yield return [new byte[] { (byte)'a', (byte)'b', 0 }, 0, 3, 1];
    }
}
