using System.Text;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Aspire.Tests;

public sealed class EvidenceReceiptIdentityValidationCoverageTests
{
    private static readonly string Hash = new('a', 64);

    [Fact]
    public void TypedIdentityRoundTripsExistingCanonicalBytes()
    {
        var files = new[] { new EvidenceProducerInputFile("src/z.cs", 0x81a4, 1, Hash), new EvidenceProducerInputFile("src/a.cs", 0, 0, Hash) };
        var sourceIdentity = new EvidenceProducerIdentity(Hash, Hash,
            files,
            new Dictionary<string, string> { ["tools"] = Hash, ["files"] = Hash });
        var (identity, canonicalBytes) = EvidenceReceiptInputIdentity.Create(sourceIdentity);

        Assert.Equal(["files", "tools"], identity.ComponentHashes.Keys);
        Assert.Equal(Hash, identity.ComponentHashes["tools"]);
        Assert.Equal(["src/a.cs", "src/z.cs"], identity.Files.Select(file => file.RelativePath));
        var existingBytes = Encoding.UTF8.GetBytes(
            $$"""{"ComponentHashes":{"files":"{{Hash}}","tools":"{{Hash}}"},"Files":["""
            + $$"""{"LengthBytes":0,"Mode":0,"RelativePath":"src/a.cs","Sha256":"{{Hash}}"},"""
            + $$"""{"LengthBytes":1,"Mode":33188,"RelativePath":"src/z.cs","Sha256":"{{Hash}}"}]}""");
        Assert.Equal(existingBytes, canonicalBytes);
        var existing = EvidenceCanonicalJson.Deserialize<EvidenceReceiptInputIdentity>(existingBytes);
        Assert.Equal(identity.Files, existing.Files);
        Assert.Equal(existingBytes, existing.ValidateAndEncode());
    }

    [Theory]
    [InlineData("empty-name")]
    [InlineData("long-name")]
    [InlineData("unknown-name")]
    [InlineData("short-hash")]
    [InlineData("non-hex-hash")]
    public void PersistedIdentityRejectsInvalidComponentMetadata(string defect)
    {
        var name = defect switch { "empty-name" => "", "long-name" => new string('x', 129), "unknown-name" => "raw-environment-value", _ => "tools" };
        var hash = defect switch { "short-hash" => "abcd", "non-hex-hash" => new string('g', 64), _ => Hash };

        Assert.Throws<InvalidDataException>(() => new EvidenceReceiptInputIdentity(
            new Dictionary<string, string> { [name] = hash }, []).ValidateAndEncode());
    }

    [Theory]
    [InlineData("negative-length")]
    [InlineData("invalid-hash")]
    [InlineData("escaping-path")]
    [InlineData("duplicate-path")]
    public void PersistedIdentityRejectsInvalidOrAmbiguousFileInventory(string defect)
    {
        var file = new EvidenceProducerInputFile("src/input.cs", 0x81a4, 1, Hash);
        var files = defect switch
        {
            "negative-length" => new[] { file with { LengthBytes = -1 } },
            "invalid-hash" => [file with { Sha256 = new string('g', 64) }],
            "escaping-path" => [file with { RelativePath = "../outside.cs" }],
            _ => [file, file]
        };

        Assert.Throws<InvalidDataException>(() => new EvidenceReceiptInputIdentity(
            new Dictionary<string, string> { ["tools"] = Hash }, files).ValidateAndEncode());
    }

    [Fact]
    public void PersistedFileCountBoundIsIndependentOfPathAndByteValidation()
    {
        var files = Enumerable.Range(0, EvidenceProducerIdentityBuilder.MaximumFiles + 1)
            .Select(index => new EvidenceProducerInputFile("src/" + index, 0, 0, Hash))
            .ToArray();
        var identity = new EvidenceReceiptInputIdentity(new Dictionary<string, string> { ["tools"] = Hash }, files);
        Assert.InRange(EvidenceCanonicalJson.Serialize(identity).Length, 1, EvidenceProducerIdentityBuilder.MaximumMetadataBytes);
        Assert.NotEmpty((identity with { Files = files[..^1] }).ValidateAndEncode());
        Assert.Throws<InvalidDataException>(() => identity.ValidateAndEncode());
    }

    [Fact]
    public void EncodedMetadataLimitAppliesEvenWhenEveryIndividualFileRecordIsValid()
    {
        var suffix = new string('a', 4000) + ".cs";
        var files = Enumerable.Range(0, 5000).Select(index => new EvidenceProducerInputFile(
            "src/" + index + "/" + suffix, 0x81a4, 1, Hash)).ToArray();

        var failure = Assert.Throws<InvalidDataException>(() => new EvidenceReceiptInputIdentity(
            new Dictionary<string, string> { ["tools"] = Hash }, files).ValidateAndEncode());

        Assert.Contains("encoded-byte bound", failure.Message, StringComparison.Ordinal);
    }
}
