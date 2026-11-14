using System.Buffers.Binary;
using System.Runtime.InteropServices;
using EvidenceReadinessTypedConsumer;

namespace ForgeTrust.AppSurface.Aspire.Testing.Tests;

public sealed class NativeDcpArchitectureTests
{
    private const int HeaderLength = 64;
    private const int PeOffset = HeaderLength;

    [Theory]
    [InlineData("MachO", 0x01000007u, Architecture.X64)]
    [InlineData("MachO", 0x0100000cu, Architecture.Arm64)]
    [InlineData("ELF", 62u, Architecture.X64)]
    [InlineData("ELF", 183u, Architecture.Arm64)]
    [InlineData("PE", 0x8664u, Architecture.X64)]
    [InlineData("PE", 0xaa64u, Architecture.Arm64)]
    public void Read_ReturnsArchitectureForSupported64BitLittleEndianHeaders(
        string format,
        uint machine,
        Architecture expected)
    {
        using var executable = new MemoryStream(CreateImage(format, machine), writable: false);

        Assert.Equal(expected, NativeDcpArchitecture.Read(executable));
    }

    [Theory]
    [InlineData("MachO", 0x01000012u)]
    [InlineData("ELF", 3u)]
    [InlineData("PE", 0x014cu)]
    public void Read_ReturnsNullForUnsupportedCpu(string format, uint machine)
    {
        using var executable = new MemoryStream(CreateImage(format, machine), writable: false);

        Assert.Null(NativeDcpArchitecture.Read(executable));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("MachO32")]
    [InlineData("ELF32")]
    [InlineData("ELFBigEndian")]
    public void Read_ReturnsNullForUnsupportedExecutableFormats(string format)
    {
        using var executable = new MemoryStream(CreateUnsupportedImage(format), writable: false);

        Assert.Null(NativeDcpArchitecture.Read(executable));
    }

    [Fact]
    public void Read_ReturnsNullForPeWithWrongSignature()
    {
        var bytes = CreateImage("PE", 0x8664);
        bytes[PeOffset] = (byte)'N';
        using var executable = new MemoryStream(bytes, writable: false);

        Assert.Null(NativeDcpArchitecture.Read(executable));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(63u)]
    [InlineData(65u)]
    [InlineData(uint.MaxValue)]
    public void Read_ReturnsNullForInvalidPeHeaderOffset(uint peOffset)
    {
        var bytes = CreateImage("PE", 0x8664);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(60, sizeof(uint)), peOffset);
        using var executable = new MemoryStream(bytes, writable: false);

        Assert.Null(NativeDcpArchitecture.Read(executable));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(HeaderLength - 1)]
    public void Read_ThrowsForTruncatedInitialHeader(int length)
    {
        using var executable = new MemoryStream(new byte[length], writable: false);

        Assert.Throws<EndOfStreamException>(() => NativeDcpArchitecture.Read(executable));
    }

    private static byte[] CreateImage(string format, uint machine)
    {
        var bytes = format == "PE" ? new byte[PeOffset + 6] : new byte[HeaderLength];
        switch (format)
        {
            case "MachO":
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, sizeof(uint)), 0xfeedfacf);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, sizeof(uint)), machine);
                break;
            case "ELF":
                bytes[0] = 0x7f;
                bytes[1] = (byte)'E';
                bytes[2] = (byte)'L';
                bytes[3] = (byte)'F';
                bytes[4] = 2;
                bytes[5] = 1;
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(18, sizeof(ushort)), checked((ushort)machine));
                break;
            case "PE":
                bytes[0] = (byte)'M';
                bytes[1] = (byte)'Z';
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(60, sizeof(uint)), PeOffset);
                bytes[PeOffset] = (byte)'P';
                bytes[PeOffset + 1] = (byte)'E';
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(PeOffset + 4, sizeof(ushort)), checked((ushort)machine));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported test image format.");
        }

        return bytes;
    }

    private static byte[] CreateUnsupportedImage(string format)
    {
        var bytes = new byte[HeaderLength];
        switch (format)
        {
            case "unknown":
                break;
            case "MachO32":
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, sizeof(uint)), 0xfeedface);
                break;
            case "ELF32":
                bytes[0] = 0x7f;
                bytes[1] = (byte)'E';
                bytes[2] = (byte)'L';
                bytes[3] = (byte)'F';
                bytes[4] = 1;
                bytes[5] = 1;
                break;
            case "ELFBigEndian":
                bytes[0] = 0x7f;
                bytes[1] = (byte)'E';
                bytes[2] = (byte)'L';
                bytes[3] = (byte)'F';
                bytes[4] = 2;
                bytes[5] = 2;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported test image format.");
        }

        return bytes;
    }
}
