using System.Buffers;
using MessagePack;

public sealed class MessagePackDependencyTests
{
    [Fact]
    public void UntrustedObjectDeserialization_RejectsInvalidContainersBeforeReachingTheDepthLimit()
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        for (var depth = 0; depth < 10; depth++)
        {
            writer.WriteArrayHeader(1000);
        }

        for (var index = 0; index < 1000; index++)
        {
            writer.WriteNil();
        }

        writer.Flush();

        // An unpatched formatter allocates nested arrays until it hits the depth limit instead.
        var options = MessagePackSerializerOptions.Standard.WithSecurity(
            MessagePackSecurity.UntrustedData.WithMaximumObjectGraphDepth(2));
        var exception = Assert.Throws<MessagePackSerializationException>(() =>
            MessagePackSerializer.Deserialize<object>(buffer.WrittenMemory, options));

        Assert.IsType<EndOfStreamException>(exception.InnerException);
    }

    [Fact]
    public void UntrustedObjectDeserialization_PreservesValidNestedValues()
    {
        byte[] payload = [0x92, 0xa4, (byte)'h', (byte)'o', (byte)'s', (byte)'t', 0x91, 0xc3];
        var options = MessagePackSerializerOptions.Standard.WithSecurity(
            MessagePackSecurity.UntrustedData.WithMaximumObjectGraphDepth(2));

        var result = MessagePackSerializer.Deserialize<object>(payload, options);

        Assert.Collection(Assert.IsType<object[]>(result),
            value => Assert.Equal("host", value),
            value => Assert.Equal(true, Assert.Single(Assert.IsType<object[]>(value))));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void NestedContainers_CannotReuseTrailingBytesToJustifyAllocations(bool outerIsMap, bool innerIsMap)
    {
        // Both headers promise four child values, but only four nil bytes follow the inner header.
        byte[] payload =
        [
            outerIsMap ? (byte)0x82 : (byte)0x94,
            innerIsMap ? (byte)0x82 : (byte)0x94,
            0xc0, 0xc0, 0xc0, 0xc0
        ];

        Assert.Throws<EndOfStreamException>(() =>
        {
            var reader = new MessagePackReader(payload);
            ReadHeader(ref reader, outerIsMap);
            ReadHeader(ref reader, innerIsMap);
        });
    }

    [Fact]
    public void ValidNestedAndConsecutiveContainers_CanStillBeRead()
    {
        // An array containing a string and a map, followed by an independent array.
        byte[] payload =
        [
            0x92, 0xa4, (byte)'h', (byte)'o', (byte)'s', (byte)'t',
            0x81, 0xa1, (byte)'k', 0xc0, 0x91, 0xc0
        ];
        var reader = new MessagePackReader(payload);

        Assert.Equal(2, reader.ReadArrayHeader());
        Assert.Equal("host", reader.ReadString());
        Assert.Equal(1, reader.ReadMapHeader());
        Assert.Equal("k", reader.ReadString());
        reader.ReadNil();
        Assert.Equal(1, reader.ReadArrayHeader());
        reader.ReadNil();
        Assert.True(reader.End);
    }

    private static void ReadHeader(ref MessagePackReader reader, bool isMap)
    {
        if (isMap)
        {
            Assert.Equal(2, reader.ReadMapHeader());
        }
        else
        {
            Assert.Equal(4, reader.ReadArrayHeader());
        }
    }
}
