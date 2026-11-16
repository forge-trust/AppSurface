using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Aspire.Tests;

public sealed class EvidenceReceiptIdentityValidationCoverageTests
{
    private static readonly string Hash = new('a', 64);

    [Fact]
    public void LegacyOptionalIdentityRemainsAbsentWhileSuppliedIdentityIsCanonicalized()
    {
        Assert.Null(EvidenceReceiptInputIdentity.Create(null, null));
        var files = new[] { new EvidenceReceiptInputFile("src/z.cs", Hash), new EvidenceReceiptInputFile("src/a.cs", Hash) };
        var identity = EvidenceReceiptInputIdentity.Create(
            new Dictionary<string, string> { ["tools"] = Hash.ToUpperInvariant(), ["files"] = Hash }, files)!;

        Assert.Equal(["files", "tools"], identity.ComponentHashes.Keys);
        Assert.Equal(Hash, identity.ComponentHashes["tools"]);
        Assert.Equal(["src/a.cs", "src/z.cs"], identity.Files.Select(file => file.RelativePath));
        Assert.Empty(EvidenceReceiptInputIdentity.Create(null, files)!.ComponentHashes);
        Assert.Empty(EvidenceReceiptInputIdentity.Create(new Dictionary<string, string> { ["tools"] = Hash }, null)!.Files);
    }

    [Theory]
    [InlineData("empty-name")]
    [InlineData("long-name")]
    [InlineData("unknown-name")]
    [InlineData("short-hash")]
    [InlineData("non-hex-hash")]
    public void OptionalIdentityRejectsInvalidComponentMetadata(string defect)
    {
        var name = defect switch { "empty-name" => "", "long-name" => new string('x', 129), "unknown-name" => "raw-environment-value", _ => "tools" };
        var hash = defect switch { "short-hash" => "abcd", "non-hex-hash" => new string('g', 64), _ => Hash };

        Assert.Throws<InvalidDataException>(() => EvidenceReceiptInputIdentity.Create(
            new Dictionary<string, string> { [name] = hash }, []));
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
            new Dictionary<string, string> { ["tools"] = Hash }, files).Validate());
    }

    [Theory]
    [InlineData("components")]
    [InlineData("files")]
    public void PersistedInventoryCountBoundsAreCheckedBeforeAcceptingMetadata(string dimension)
    {
        var components = dimension == "components"
            ? Enumerable.Range(0, EvidenceProducerIdentityBuilder.MaximumMaterialKeys + 1).ToDictionary(index => "component-" + index, _ => Hash)
            : new Dictionary<string, string>();
        var files = dimension == "files"
            ? Enumerable.Repeat(new EvidenceReceiptInputFile("src/input.cs", Hash), EvidenceProducerIdentityBuilder.MaximumFiles + 1).ToArray()
            : [];

        Assert.Throws<InvalidDataException>(() => new EvidenceReceiptInputIdentity(components, files).Validate());
    }

    [Fact]
    public void EncodedMetadataLimitAppliesEvenWhenEveryIndividualFileRecordIsValid()
    {
        var suffix = new string('a', 4000) + ".cs";
        var files = Enumerable.Range(0, 5000).Select(index => new EvidenceReceiptInputFile(
            "src/" + index + "/" + suffix, Hash, 0x81a4, 1)).ToArray();

        var failure = Assert.Throws<InvalidDataException>(() => new EvidenceReceiptInputIdentity(
            new Dictionary<string, string> { ["tools"] = Hash }, files).Validate());

        Assert.Contains("encoded-byte bound", failure.Message, StringComparison.Ordinal);
    }
}
