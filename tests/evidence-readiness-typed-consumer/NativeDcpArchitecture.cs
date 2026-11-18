using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace EvidenceReadinessTypedConsumer;

/// <summary>Reads the native CPU from the DCP executable instead of inferring it from its installation path.</summary>
/// <remarks>Accepts the 64-bit little-endian Mach-O, ELF and PE formats distributed for supported Aspire hosts.</remarks>
internal static class NativeDcpArchitecture
{
    /// <summary>Returns the supported executable architecture, or null for an unsupported format or CPU.</summary>
    /// <exception cref="EndOfStreamException">The executable header is truncated.</exception>
    internal static Architecture? Read(Stream executable)
    {
        Span<byte> header = stackalloc byte[64];
        executable.ReadExactly(header);
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (magic == 0xfeedfacf)
            return BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) switch
            {
                0x01000007 => Architecture.X64,
                0x0100000c => Architecture.Arm64,
                _ => null,
            };
        if (magic == 0x464c457f && header[4] == 2 && header[5] == 1)
            return BinaryPrimitives.ReadUInt16LittleEndian(header[18..]) switch
            {
                62 => Architecture.X64,
                183 => Architecture.Arm64,
                _ => null,
            };
        if (header[0] != 'M' || header[1] != 'Z') return null;
        var peOffset = BinaryPrimitives.ReadUInt32LittleEndian(header[60..]);
        if (peOffset < header.Length || peOffset > executable.Length - 6) return null;
        executable.Position = peOffset;
        executable.ReadExactly(header[..6]);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x00004550) return null;
        return BinaryPrimitives.ReadUInt16LittleEndian(header[4..]) switch
        {
            0x8664 => Architecture.X64,
            0xaa64 => Architecture.Arm64,
            _ => null,
        };
    }
}
