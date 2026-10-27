using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactJsonTests
{
    [Theory]
    [InlineData("{\"Name\":\"a\",\"Name\":\"b\"}")]
    [InlineData("{\"Name\":\"a\",\"Values\":[{\"value\":1,\"value\":2}]}")]
    [InlineData("null")]
    [InlineData("{")]
    public void Read_RejectsAmbiguousOrInvalidDocuments(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        Assert.Throws<InvalidDataException>(() => CoverageArtifactJson.Read<JsonRecord>(stream, 256,
            new JsonSerializerOptions { MaxDepth = 16 }));
        Assert.True(stream.CanRead);
    }

    [Fact]
    public void Read_EnforcesExactByteBoundAndPreservesInputStream()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"Name\":\"value\"}");
        using var stream = new MemoryStream(bytes);
        var value = CoverageArtifactJson.Read<JsonRecord>(stream, bytes.Length, new JsonSerializerOptions { MaxDepth = 16 });
        Assert.Equal("value", value.Name);
        Assert.True(stream.CanRead);
        stream.Position = 0;
        Assert.Throws<InvalidDataException>(() => CoverageArtifactJson.Read<JsonRecord>(stream, bytes.Length - 1,
            new JsonSerializerOptions { MaxDepth = 16 }));
    }

    [Fact]
    public void Read_RejectsUnknownFieldsWhenSchemaDisallowsThem()
    {
        using var stream = new MemoryStream("{\"other\":1}"u8.ToArray());
        Assert.Throws<InvalidDataException>(() => CoverageArtifactJson.Read<JsonRecord>(stream, 256,
            new JsonSerializerOptions { MaxDepth = 16, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow }));
    }

    public sealed record JsonRecord(string Name);
}
