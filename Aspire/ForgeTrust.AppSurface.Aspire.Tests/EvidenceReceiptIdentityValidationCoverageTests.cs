using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Aspire.Tests;

public sealed class EvidenceReceiptIdentityValidationCoverageTests
{
    private static readonly string Hash = new('a', 64);

    [Fact]
    public void TypedIdentityIsCanonicalizedBeforePersistence()
    {
        var files = new[] { new EvidenceReceiptInputFile("src/z.cs", Hash), new EvidenceReceiptInputFile("src/a.cs", Hash) };
        var sourceIdentity = new EvidenceProducerIdentity(Hash, Hash,
            files.Select(static file => new EvidenceProducerInputFile(file.RelativePath, file.Mode, file.LengthBytes, file.Sha256)).ToArray(),
            new Dictionary<string, string> { ["tools"] = Hash, ["files"] = Hash });
        var (identity, canonicalBytes) = EvidenceReceiptInputIdentity.Create(sourceIdentity);

        Assert.Equal(["files", "tools"], identity.ComponentHashes.Keys);
        Assert.Equal(Hash, identity.ComponentHashes["tools"]);
        Assert.Equal(["src/a.cs", "src/z.cs"], identity.Files.Select(file => file.RelativePath));
        Assert.Equal(EvidenceCanonicalJson.Serialize(identity), canonicalBytes);
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
        var file = new EvidenceReceiptInputFile("src/input.cs", Hash, 0x81a4, 1);
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

    [Theory]
    [InlineData("components")]
    [InlineData("files")]
    public void PersistedInventoryCountBoundsAreCheckedBeforeAcceptingMetadata(string dimension)
    {
        var components = dimension == "components"
            ? Enumerable.Range(0, 65).ToDictionary(index => "component-" + index, _ => Hash)
            : new Dictionary<string, string>();
        var files = dimension == "files"
            ? Enumerable.Repeat(new EvidenceReceiptInputFile("src/input.cs", Hash), EvidenceProducerIdentityBuilder.MaximumFiles + 1).ToArray()
            : [];

        Assert.Throws<InvalidDataException>(() => new EvidenceReceiptInputIdentity(components, files).ValidateAndEncode());
    }

    [Fact]
    public void EncodedMetadataLimitAppliesEvenWhenEveryIndividualFileRecordIsValid()
    {
        var suffix = new string('a', 4000) + ".cs";
        var files = Enumerable.Range(0, 5000).Select(index => new EvidenceReceiptInputFile(
            "src/" + index + "/" + suffix, Hash, 0x81a4, 1)).ToArray();

        var failure = Assert.Throws<InvalidDataException>(() => new EvidenceReceiptInputIdentity(
            new Dictionary<string, string> { ["tools"] = Hash }, files).ValidateAndEncode());

        Assert.Contains("encoded-byte bound", failure.Message, StringComparison.Ordinal);
    }
}
