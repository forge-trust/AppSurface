using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.ReleaseContracts;

namespace ForgeTrust.AppSurface.Release.Tests;

public sealed class ReleaseArtifactBindingTests
{
    private const string Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherCommit = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string PackageDigest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string OciDigest = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

    [Fact]
    public void SyntheticPackageArchiveBindsExactBytesAndCompleteInventory()
    {
        var archive = CreatePackageArchive("Dotted.Package", "1.2.3-preview.4", "payload-v1");
        var asset = Asset("nuget:Dotted.Package:1.2.3-preview.4", "nuget", "Dotted.Package.1.2.3-preview.4.nupkg",
            "sha512", Convert.ToHexStringLower(SHA512.HashData(archive)));
        var manifestBytes = ManifestBytes([asset]);
        var verified = VerifyManifest(manifestBytes, [asset]);

        Assert.Equal(Identity(), verified.Identity);
        Assert.Equal(Hash(manifestBytes), verified.Sha256);

        var changedArchive = CreatePackageArchive("Dotted.Package", "1.2.3-preview.4", "payload-v2");
        var changed = asset with { Digest = Convert.ToHexStringLower(SHA512.HashData(changedArchive)) };
        AssertFailure(VerifiedReleaseManifest.VerifyManifest(manifestBytes, Hash(manifestBytes), Identity(), [changed]),
            ReleaseBindingFailure.AssetMismatch);
    }

    [Fact]
    public void SyntheticDeploymentBindsTwoOutcomesToOneReleaseAndRejectsWrongManifestOrTarget()
    {
        var ociBytes = """
            {
              "schemaVersion": 2,
              "mediaType": "application/vnd.oci.image.manifest.v1+json",
              "config": {
                "mediaType": "application/vnd.oci.image.config.v1+json",
                "digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "size": 2
              },
              "layers": []
            }
            """u8.ToArray();
        var asset = Asset("oci:registry.example/team/app:1.2.3", "oci", "registry.example/team/app:1.2.3",
            "sha256", Convert.ToHexStringLower(SHA256.HashData(ociBytes)));
        var manifestBytes = ManifestBytes([asset]);
        var verified = VerifyManifest(manifestBytes, [asset]);

        AssertOutcome(ReceiptBytes("promo-1", "staging", verified.Sha256, "release-approval", "succeeded"), verified,
            new PromotionIdentity("promo-1", "staging", "release-approval"), PromotionOutcome.Succeeded);
        AssertOutcome(ReceiptBytes("promo-2", "production", verified.Sha256, "change-42", "failed"), verified,
            new PromotionIdentity("promo-2", "production", "change-42"), PromotionOutcome.Failed);

        var otherBytes = ManifestBytes([asset], releaseId: "other");
        var otherManifest = Assert.IsType<ReleaseBindingResult<VerifiedReleaseManifest>.Success>(
            VerifiedReleaseManifest.VerifyManifest(otherBytes, Hash(otherBytes), Identity(releaseId: "other"), [asset])).Value;
        AssertFailure(VerifiedReleaseManifest.VerifyPromotionBinding(
            ReceiptBytes("promo-1", "staging", otherManifest.Sha256, "release-approval", "succeeded"), verified,
            new PromotionIdentity("promo-1", "staging", "release-approval")), ReleaseBindingFailure.PromotionBindingMismatch);
        AssertFailure(VerifiedReleaseManifest.VerifyPromotionBinding(
            ReceiptBytes("promo-1", "production", verified.Sha256, "release-approval", "succeeded"), verified,
            new PromotionIdentity("promo-1", "staging", "release-approval")), ReleaseBindingFailure.PromotionBindingMismatch);
    }

    [Theory]
    [InlineData("01-unknown-schema", "unsupported-schema")]
    [InlineData("02-duplicate-key", "invalid-record")]
    [InlineData("03-unknown-field", "invalid-record")]
    [InlineData("04-invalid-manifest-asset-digest", "invalid-record")]
    [InlineData("05-manifest-byte-limit", "input-limit-exceeded")]
    [InlineData("06-manifest-asset-count-limit", "input-limit-exceeded")]
    [InlineData("07-changed-manifest-bytes", "manifest-digest-mismatch")]
    [InlineData("08-wrong-release-identity", "release-identity-mismatch")]
    [InlineData("09-duplicate-manifest-asset-id", "invalid-record")]
    [InlineData("10-missing-observed-asset", "asset-mismatch")]
    [InlineData("11-extra-observed-asset", "asset-mismatch")]
    [InlineData("12-wrong-kind", "asset-mismatch")]
    [InlineData("13-wrong-locator", "asset-mismatch")]
    [InlineData("14-mismatched-observed-digest", "asset-mismatch")]
    [InlineData("15-wrong-promotion-manifest", "promotion-binding-mismatch")]
    [InlineData("16-wrong-promotion-id", "promotion-binding-mismatch")]
    [InlineData("17-wrong-target", "promotion-binding-mismatch")]
    [InlineData("18-wrong-authorization-reference", "promotion-binding-mismatch")]
    [InlineData("19-utf8-bom", "invalid-record")]
    [InlineData("20-control-character", "invalid-record")]
    [InlineData("21-blank-string", "invalid-record")]
    [InlineData("22-unknown-digest-algorithm", "invalid-record")]
    [InlineData("23-invalid-receipt-outcome", "invalid-record")]
    [InlineData("24-receipt-byte-limit", "input-limit-exceeded")]
    [InlineData("25-duplicate-observed-id", "asset-mismatch")]
    [InlineData("26-malformed-expected-manifest-digest", "invalid-record")]
    [InlineData("27-malformed-observed-digest", "invalid-record")]
    [InlineData("28-observed-asset-count-limit", "input-limit-exceeded")]
    [InlineData("29-malformed-expected-source-commit", "invalid-record")]
    [InlineData("30-string-byte-limit", "input-limit-exceeded")]
    public void Issue924NegativeCasesReturnSpecifiedCodeAndNoSuccess(string caseName, string expectedCode)
    {
        var asset = Asset();
        var assets = new[] { asset };
        var json = ManifestBytes(assets);
        var observations = assets;
        var pin = Hash(json);
        var identity = Identity();
        var receipt = ReceiptBytes("promotion-1", "production", pin, "approval-1", "succeeded");
        var promotion = new PromotionIdentity("promotion-1", "production", "approval-1");
        var verifyReceipt = false;

        switch (caseName)
        {
            case "01-unknown-schema": json = ManifestBytes(assets, schema: "unknown"); break;
            case "02-duplicate-key": json = RawManifest("\"schema\":\"appsurface-release-artifacts-v1\",\"schema\":\"appsurface-release-artifacts-v1\""); break;
            case "03-unknown-field": json = RawManifest("\"schema\":\"appsurface-release-artifacts-v1\",\"repository\":\"repo\",\"releaseId\":\"v1\",\"sourceCommit\":\"" + Commit + "\",\"assets\":[" + AssetJson(asset) + "],\"extra\":true"); break;
            case "04-invalid-manifest-asset-digest": json = ManifestBytes([asset with { Digest = "xyz" }]); break;
            case "05-manifest-byte-limit": json = new byte[256 * 1024 + 1]; break;
            case "06-manifest-asset-count-limit": json = ManifestBytes(Enumerable.Range(0, 129).Select(i => Asset(id: "asset-" + i)).ToArray()); break;
            case "07-changed-manifest-bytes": json = ManifestBytes(assets, releaseId: "changed"); pin = Hash(ManifestBytes(assets)); break;
            case "08-wrong-release-identity": identity = Identity(sourceCommit: OtherCommit); break;
            case "09-duplicate-manifest-asset-id": json = ManifestBytes([asset, asset with { Kind = "other" }]); break;
            case "10-missing-observed-asset": observations = []; break;
            case "11-extra-observed-asset": observations = [asset, Asset(id: "extra")]; break;
            case "12-wrong-kind": observations = [asset with { Kind = "other" }]; break;
            case "13-wrong-locator": observations = [asset with { Locator = "other" }]; break;
            case "14-mismatched-observed-digest": observations = [asset with { Digest = new string('f', 128) }]; break;
            case "15-wrong-promotion-manifest": verifyReceipt = true; receipt = ReceiptBytes("promotion-1", "production", new string('f', 64), "approval-1", "succeeded"); break;
            case "16-wrong-promotion-id": verifyReceipt = true; receipt = ReceiptBytes("other", "production", pin, "approval-1", "succeeded"); break;
            case "17-wrong-target": verifyReceipt = true; receipt = ReceiptBytes("promotion-1", "staging", pin, "approval-1", "succeeded"); break;
            case "18-wrong-authorization-reference": verifyReceipt = true; receipt = ReceiptBytes("promotion-1", "production", pin, "other", "succeeded"); break;
            case "19-utf8-bom": json = [0xef, 0xbb, 0xbf, .. json]; pin = Hash(json); break;
            case "20-control-character": json = ManifestBytes([asset with { Locator = "bad\u0001value" }]); break;
            case "21-blank-string": json = ManifestBytes([asset with { Locator = " " }]); break;
            case "22-unknown-digest-algorithm": json = ManifestBytes([asset with { DigestAlgorithm = "SHA256" }]); break;
            case "23-invalid-receipt-outcome": verifyReceipt = true; receipt = ReceiptBytes("promotion-1", "production", pin, "approval-1", "maybe"); break;
            case "24-receipt-byte-limit": verifyReceipt = true; receipt = new byte[16 * 1024 + 1]; break;
            case "25-duplicate-observed-id":
                json = ManifestBytes([asset, Asset(id: "second")]);
                observations = [asset, asset];
                break;
            case "26-malformed-expected-manifest-digest": pin = "ABC"; break;
            case "27-malformed-observed-digest": observations = [asset with { Digest = "ABC" }]; break;
            case "28-observed-asset-count-limit": observations = Enumerable.Range(0, 129).Select(i => Asset(id: "asset-" + i)).ToArray(); break;
            case "29-malformed-expected-source-commit": identity = Identity(sourceCommit: "abc123"); break;
            case "30-string-byte-limit": json = ManifestBytes([asset with { Locator = new string('x', 1025) }]); break;
            default: throw new ArgumentOutOfRangeException(nameof(caseName));
        }

        if (verifyReceipt)
        {
            var verified = VerifyManifest(ManifestBytes([asset]), [asset]);
            AssertFailure(VerifiedReleaseManifest.VerifyPromotionBinding(receipt, verified, promotion), expectedCode);
        }
        else
        {
            if (caseName is not ("07-changed-manifest-bytes" or "26-malformed-expected-manifest-digest"))
            {
                pin = Hash(json);
            }
            AssertFailure(VerifiedReleaseManifest.VerifyManifest(json, pin, identity, observations), expectedCode);
        }
    }

    [Fact]
    public void ManifestAndReceiptAcceptTheirInclusiveByteCaps()
    {
        var asset = Asset();
        var manifest = PadJson(ManifestBytes([asset]), 256 * 1024);
        AssertSuccess(VerifiedReleaseManifest.VerifyManifest(manifest, Hash(manifest), Identity(), [asset]));

        var verified = VerifyManifest(ManifestBytes([asset]), [asset]);
        var receipt = PadJson(ReceiptBytes("promotion-1", "production", verified.Sha256, "approval-1", "succeeded"), 16 * 1024);
        AssertOutcome(VerifiedReleaseManifest.VerifyPromotionBinding(receipt, verified,
            new PromotionIdentity("promotion-1", "production", "approval-1")), PromotionOutcome.Succeeded);
    }

    [Fact]
    public void AuthoringAndObservingAcceptExactly128EntriesAndRejectEmptyInventories()
    {
        var assets = Enumerable.Range(0, 128).Select(i => Asset(id: $"asset-{i:D3}")).ToArray();
        AssertSuccess(VerifiedReleaseManifest.VerifyManifest(ManifestBytes(assets), Hash(ManifestBytes(assets)), Identity(), assets));
        AssertFailure(VerifiedReleaseManifest.VerifyManifest(ManifestBytes([]), Hash(ManifestBytes([])), Identity(), assets),
            ReleaseBindingFailure.InvalidRecord);
        AssertFailure(VerifiedReleaseManifest.VerifyManifest(ManifestBytes([Asset()]), Hash(ManifestBytes([Asset()])), Identity(), []),
            ReleaseBindingFailure.AssetMismatch);
    }

    [Fact]
    public void TextLimitCountsDecodedUtf8BytesInclusively()
    {
        var asset = Asset(locator: new string('x', 1024));
        var asciiBoundary = ManifestBytes([asset]);
        AssertSuccess(VerifiedReleaseManifest.VerifyManifest(asciiBoundary, Hash(asciiBoundary), Identity(), [asset]));
        AssertFailure(VerifiedReleaseManifest.VerifyManifest(
            ManifestBytes([asset with { Locator = new string('x', 1025) }]),
            Hash(ManifestBytes([asset with { Locator = new string('x', 1025) }])), Identity(), [asset with { Locator = new string('x', 1025) }]),
            ReleaseBindingFailure.InputLimitExceeded);

        var unicode = new string('é', 512);
        var unicodeAsset = Asset(locator: unicode);
        var escapedJson = ManifestText([unicodeAsset]);
        var escapedBytes = Utf8(escapedJson);
        var rawBytes = Utf8(escapedJson.Replace("\\u00E9", "é", StringComparison.OrdinalIgnoreCase));
        Assert.True(rawBytes.Length < escapedBytes.Length);
        AssertSuccess(VerifiedReleaseManifest.VerifyManifest(escapedBytes, Hash(escapedBytes), Identity(), [unicodeAsset]));
        AssertSuccess(VerifiedReleaseManifest.VerifyManifest(rawBytes, Hash(rawBytes), Identity(), [unicodeAsset]));

        var overLimit = Asset(locator: unicode + "x");
        var overLimitBytes = ManifestBytes([overLimit]);
        AssertFailure(VerifiedReleaseManifest.VerifyManifest(overLimitBytes, Hash(overLimitBytes), Identity(), [unicodeAsset]),
            ReleaseBindingFailure.InputLimitExceeded);
        AssertFailure(VerifiedReleaseManifest.VerifyManifest(rawBytes, Hash(rawBytes), Identity(), [overLimit]),
            ReleaseBindingFailure.InputLimitExceeded);

        var supplementaryAsset = Asset(locator: "release-😀");
        var supplementary = ManifestBytes([supplementaryAsset]);
        AssertSuccess(VerifiedReleaseManifest.VerifyManifest(supplementary, Hash(supplementary), Identity(), [supplementaryAsset]));
    }

    [Theory]
    [InlineData("manifest-empty", "invalid-record")]
    [InlineData("manifest-wrong-root", "invalid-record")]
    [InlineData("manifest-wrong-assets-type", "invalid-record")]
    [InlineData("manifest-non-object-asset", "invalid-record")]
    [InlineData("manifest-missing-field", "invalid-record")]
    [InlineData("manifest-null-field", "invalid-record")]
    [InlineData("manifest-non-string-field", "invalid-record")]
    [InlineData("manifest-comment", "invalid-record")]
    [InlineData("manifest-trailing-comma", "invalid-record")]
    [InlineData("manifest-second-root", "invalid-record")]
    [InlineData("manifest-depth", "invalid-record")]
    [InlineData("manifest-invalid-utf8", "invalid-record")]
    [InlineData("manifest-unpaired-value-surrogate", "invalid-record")]
    [InlineData("manifest-unpaired-property-surrogate", "invalid-record")]
    [InlineData("manifest-escaped-control", "invalid-record")]
    [InlineData("receipt-empty", "invalid-record")]
    [InlineData("receipt-wrong-root", "invalid-record")]
    [InlineData("receipt-missing-field", "invalid-record")]
    [InlineData("receipt-null-field", "invalid-record")]
    [InlineData("receipt-non-string-field", "invalid-record")]
    [InlineData("receipt-comment", "invalid-record")]
    [InlineData("receipt-trailing-comma", "invalid-record")]
    [InlineData("receipt-second-root", "invalid-record")]
    [InlineData("receipt-depth", "invalid-record")]
    [InlineData("receipt-invalid-utf8", "invalid-record")]
    [InlineData("receipt-bom", "invalid-record")]
    [InlineData("receipt-duplicate-key", "invalid-record")]
    [InlineData("receipt-unknown-field", "invalid-record")]
    [InlineData("receipt-escaped-control", "invalid-record")]
    public void StrictParserRejectsMalformedShapesForBothRecords(string shape, string expectedCode)
    {
        var asset = Asset();
        var manifest = ManifestBytes([asset]);
        var verified = VerifyManifest(manifest, [asset]);
        var receipt = ReceiptBytes("promotion-1", "production", verified.Sha256, "approval-1", "succeeded");
        byte[] input;
        var isReceipt = shape.StartsWith("receipt-", StringComparison.Ordinal);
        switch (shape)
        {
            case "manifest-empty": case "receipt-empty": input = []; break;
            case "manifest-wrong-root": input = Utf8("[]"); break;
            case "manifest-wrong-assets-type": input = Utf8(ManifestText([]).Replace("\"assets\":[]", "\"assets\":null", StringComparison.Ordinal)); break;
            case "manifest-non-object-asset": input = Utf8(ManifestText([]).Replace("\"assets\":[]", "\"assets\":[null]", StringComparison.Ordinal)); break;
            case "manifest-missing-field": input = Utf8("{\"schema\":\"appsurface-release-artifacts-v1\"}"); break;
            case "manifest-null-field": input = Utf8("{\"schema\":null}"); break;
            case "manifest-non-string-field": input = Utf8("{\"schema\":42}"); break;
            case "manifest-comment": input = Utf8("/*x*/" + ManifestText([asset])); break;
            case "manifest-trailing-comma": input = Utf8(ManifestText([asset]).Replace("}", ",}", StringComparison.Ordinal)); break;
            case "manifest-second-root": input = [.. manifest, .. Utf8(" {}")]; break;
            case "manifest-depth":
            case "receipt-depth":
                // An unsupported discriminator would win if the deep input passed parsing.
                input = Utf8("{\"schema\":\"future-schema\",\"nested\":" + new string('[', 8) + "0" + new string(']', 8) + "}");
                break;
            case "manifest-invalid-utf8": input = [0xff, 0xfe]; break;
            case "manifest-unpaired-value-surrogate": input = Utf8(ManifestText([asset]).Replace("\"repository\":\"repo\"", "\"repository\":\"\\uD800\"", StringComparison.Ordinal)); break;
            case "manifest-unpaired-property-surrogate": input = Utf8("{\"\\uD800\":1}"); break;
            case "manifest-escaped-control": input = Utf8(ManifestText([asset]).Replace("\"repository\":\"repo\"", "\"repository\":\"\\u0001\"", StringComparison.Ordinal)); break;
            case "receipt-wrong-root": input = Utf8("[]"); break;
            case "receipt-missing-field": input = Utf8("{\"schema\":\"appsurface-promotion-receipt-v1\"}"); break;
            case "receipt-null-field": input = Utf8("{\"schema\":null}"); break;
            case "receipt-non-string-field": input = Utf8("{\"schema\":42}"); break;
            case "receipt-comment": input = Utf8("//x\n" + ReceiptText("promotion-1", "production", verified.Sha256, "approval-1", "succeeded")); break;
            case "receipt-trailing-comma": input = Utf8(ReceiptText("promotion-1", "production", verified.Sha256, "approval-1", "succeeded").Replace("}", ",}", StringComparison.Ordinal)); break;
            case "receipt-second-root": input = [.. receipt, .. Utf8(" {}")]; break;
            case "receipt-invalid-utf8": input = [0xff, 0xfe]; break;
            case "receipt-bom": input = [0xef, 0xbb, 0xbf, .. receipt]; break;
            case "receipt-duplicate-key": input = Utf8(ReceiptText("promotion-1", "production", verified.Sha256, "approval-1", "succeeded").Replace("\"schema\":", "\"schema\":\"appsurface-promotion-receipt-v1\",\"schema\":", StringComparison.Ordinal)); break;
            case "receipt-unknown-field": input = Utf8(ReceiptText("promotion-1", "production", verified.Sha256, "approval-1", "succeeded").Replace("}", ",\"extra\":1}", StringComparison.Ordinal)); break;
            case "receipt-escaped-control": input = Utf8(ReceiptText("promotion-1", "production", verified.Sha256, "approval-1", "succeeded").Replace("production", "\\u0001", StringComparison.Ordinal)); break;
            default: throw new ArgumentOutOfRangeException(nameof(shape));
        }
        if (isReceipt)
            AssertFailure(VerifiedReleaseManifest.VerifyPromotionBinding(input, verified,
                new PromotionIdentity("promotion-1", "production", "approval-1")), expectedCode);
        else
            AssertFailure(VerifiedReleaseManifest.VerifyManifest(input, Hash(input), Identity(), [asset]), expectedCode);
    }

    [Theory]
    [InlineData("unknown", "unsupported-schema")]
    [InlineData("", "invalid-record")]
    [InlineData("null", "invalid-record")]
    [InlineData("42", "invalid-record")]
    public void ReceiptSchemaHasIndependentClosedDiscriminator(string schema, string expectedCode)
    {
        var asset = Asset();
        var verified = VerifyManifest(ManifestBytes([asset]), [asset]);
        var json = ReceiptText("promotion-1", "production", verified.Sha256, "approval-1", "succeeded");
        json = json.Replace("appsurface-promotion-receipt-v1", schema, StringComparison.Ordinal);
        if (schema == "null") json = json.Replace("\"schema\":\"null\"", "\"schema\":null", StringComparison.Ordinal);
        if (schema == "42") json = json.Replace("\"schema\":\"42\"", "\"schema\":42", StringComparison.Ordinal);
        AssertFailure(VerifiedReleaseManifest.VerifyPromotionBinding(Utf8(json), verified,
            new PromotionIdentity("promotion-1", "production", "approval-1")), expectedCode);
    }

    [Fact]
    public void ExactBytesOrderAndDetachedTokenArePreserved()
    {
        var asset = Asset();
        var first = Utf8(ManifestText([asset]));
        var whitespace = Utf8(" { \n " + ManifestText([asset])[1..^1] + " } ");
        var firstPin = Hash(first);
        var token = VerifyManifest(first, [asset]);
        AssertSuccess(VerifiedReleaseManifest.VerifyManifest(whitespace, Hash(whitespace), Identity(), [asset]));
        AssertFailure(VerifiedReleaseManifest.VerifyManifest(whitespace, Hash(first), Identity(), [asset]),
            ReleaseBindingFailure.ManifestDigestMismatch);

        first.AsSpan().Fill((byte)'x');
        Assert.Equal(Identity(), token.Identity);
        Assert.Equal(firstPin, token.Sha256);
    }

    [Fact]
    public void InventoryComparisonIsCompleteOrdinalAndOrderIndependent()
    {
        var upper = Asset(id: "Pkg.Name", locator: "Opaque:CaseSensitive");
        var lower = Asset(id: "pkg.name", locator: "Opaque:CaseSensitive");
        var other = Asset(id: "other");
        var bytes = ManifestBytes([upper, other]);
        AssertSuccess(VerifiedReleaseManifest.VerifyManifest(bytes, Hash(bytes), Identity(), [other, upper]));
        AssertFailure(VerifiedReleaseManifest.VerifyManifest(bytes, Hash(bytes), Identity(), [lower, other]), ReleaseBindingFailure.AssetMismatch);
        AssertFailure(VerifiedReleaseManifest.VerifyManifest(bytes, Hash(bytes), Identity(), [upper with { Locator = "opaQUE:CaseSensitive" }, other]), ReleaseBindingFailure.AssetMismatch);
        var bothCaseIds = ManifestBytes([upper, lower]);
        AssertSuccess(VerifiedReleaseManifest.VerifyManifest(bothCaseIds, Hash(bothCaseIds), Identity(), [lower, upper]));
        AssertFailure(VerifiedReleaseManifest.VerifyManifest(bytes, Hash(bytes), Identity(), [upper with { DigestAlgorithm = "sha256", Digest = OciDigest }, other]), ReleaseBindingFailure.AssetMismatch);
    }

    [Fact]
    public void CallerValidationAndDigestLengthsAreStrict()
    {
        var asset = Asset();
        var longCommit = new string('a', 64);
        var bytes = ManifestBytes([asset], sourceCommit: longCommit);
        AssertSuccess(VerifiedReleaseManifest.VerifyManifest(bytes, Hash(bytes), Identity(sourceCommit: longCommit), [asset]));
        AssertFailure(VerifiedReleaseManifest.VerifyManifest(bytes, Hash(bytes).ToUpperInvariant(), Identity(), [asset]), ReleaseBindingFailure.InvalidRecord);
        AssertFailure(VerifiedReleaseManifest.VerifyManifest(bytes, Hash(bytes), Identity(sourceCommit: new string('A', 40)), [asset]), ReleaseBindingFailure.InvalidRecord);
        AssertFailure(VerifiedReleaseManifest.VerifyManifest(bytes, Hash(bytes), new ReleaseIdentity("repo\ud800", "v1", Commit), [asset]), ReleaseBindingFailure.InvalidRecord);
        AssertFailure(VerifiedReleaseManifest.VerifyManifest(bytes, Hash(bytes), null, [asset]), ReleaseBindingFailure.InvalidRecord);
        AssertFailure(VerifiedReleaseManifest.VerifyManifest(bytes, Hash(bytes), Identity(), [asset with { DigestAlgorithm = "sha256", Digest = new string('a', 128) }]), ReleaseBindingFailure.InvalidRecord);
        AssertFailure(VerifiedReleaseManifest.VerifyPromotionBinding(ReadOnlyMemory<byte>.Empty, null, null), ReleaseBindingFailure.InvalidRecord);
    }

    [Fact]
    public void RejectsMissingOrUnreadableObservationsAndNullCallerFields()
    {
        var bytes = ManifestBytes([Asset()]);
        var pin = Hash(bytes);
        AssertFailure(VerifiedReleaseManifest.VerifyManifest(bytes, pin, Identity(), null), ReleaseBindingFailure.InvalidRecord);
        AssertFailure(VerifiedReleaseManifest.VerifyManifest(bytes, pin, Identity(), [null!]), ReleaseBindingFailure.InvalidRecord);
        AssertFailure(VerifiedReleaseManifest.VerifyManifest(bytes, null, Identity(), [Asset()]), ReleaseBindingFailure.InvalidRecord);
        AssertFailure(VerifiedReleaseManifest.VerifyManifest(bytes, pin, Identity(), new UnreadableInventory(null)), ReleaseBindingFailure.InvalidRecord);
        AssertFailure(VerifiedReleaseManifest.VerifyManifest(bytes, pin, Identity(), new UnreadableInventory(-1)), ReleaseBindingFailure.InvalidRecord);
        AssertFailure(VerifiedReleaseManifest.VerifyManifest(bytes, pin, Identity(), new UnreadableInventory(1)), ReleaseBindingFailure.InvalidRecord);
        AssertFailure(VerifiedReleaseManifest.VerifyManifest(bytes, pin, Identity(), new UnreadableInventory(1, unsupported: true)), ReleaseBindingFailure.InvalidRecord);
    }

    private sealed class UnreadableInventory(int? count, bool unsupported = false) : IReadOnlyList<ReleaseAsset>
    {
        public int Count => count ?? throw new InvalidOperationException();

        public ReleaseAsset this[int index] => unsupported
            ? throw new NotSupportedException()
            : throw new ArgumentOutOfRangeException(nameof(index));

        public IEnumerator<ReleaseAsset> GetEnumerator() => throw new NotSupportedException();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void FailurePrecedenceIsStableAcrossPairedDefects()
    {
        var valid = ManifestBytes([Asset()]);
        AssertFailure(VerifiedReleaseManifest.VerifyManifest(new byte[256 * 1024 + 1], "bad", null, null), ReleaseBindingFailure.InputLimitExceeded);
        AssertFailure(VerifiedReleaseManifest.VerifyManifest(valid, new string('f', 64), Identity(), [Asset(digest: "bad")]), ReleaseBindingFailure.InvalidRecord);
        AssertFailure(VerifiedReleaseManifest.VerifyManifest(Utf8("{"), new string('f', 64), Identity(), [Asset()]), ReleaseBindingFailure.ManifestDigestMismatch);
        AssertFailure(VerifiedReleaseManifest.VerifyManifest(ManifestBytes([Asset()], schema: "unknown"), Hash(ManifestBytes([Asset()], schema: "unknown")), Identity(sourceCommit: OtherCommit), []), ReleaseBindingFailure.UnsupportedSchema);
        AssertFailure(VerifiedReleaseManifest.VerifyManifest(valid, Hash(valid), Identity(sourceCommit: OtherCommit), []), ReleaseBindingFailure.ReleaseIdentityMismatch);
        AssertFailure(VerifiedReleaseManifest.VerifyManifest(valid, Hash(valid), null, Enumerable.Range(0, 129).Select(i => Asset(id: $"a{i}")).ToArray()), ReleaseBindingFailure.InputLimitExceeded);

        var verified = VerifyManifest(valid, [Asset()]);
        var malformedOutcome = Utf8(ReceiptText("wrong", "wrong", verified.Sha256, "wrong", "invalid"));
        AssertFailure(VerifiedReleaseManifest.VerifyPromotionBinding(malformedOutcome, verified,
            new PromotionIdentity("expected", "target", "auth")), ReleaseBindingFailure.InvalidRecord);
    }

    private static ReleaseIdentity Identity(string sourceCommit = Commit, string releaseId = "release-1") => new("repo", releaseId, sourceCommit);

    private static ReleaseAsset Asset(string id = "nuget:Example.Package:1.2.3-preview.4", string kind = "nuget",
        string locator = "Example.Package.1.2.3-preview.4.nupkg", string digestAlgorithm = "sha512", string digest = PackageDigest) =>
        new(id, kind, locator, digestAlgorithm, digest);

    private static byte[] ManifestBytes(IReadOnlyList<ReleaseAsset> assets, string schema = "appsurface-release-artifacts-v1", string releaseId = "release-1", string sourceCommit = Commit) =>
        Utf8(ManifestText(assets, schema, releaseId, sourceCommit));

    private static string ManifestText(IReadOnlyList<ReleaseAsset> assets, string schema = "appsurface-release-artifacts-v1", string releaseId = "release-1", string sourceCommit = Commit) =>
        JsonSerializer.Serialize(new
        {
            schema,
            repository = "repo",
            releaseId,
            sourceCommit,
            assets = assets.Select(asset => new
            {
                id = asset.Id,
                kind = asset.Kind,
                locator = asset.Locator,
                digestAlgorithm = asset.DigestAlgorithm,
                digest = asset.Digest,
            })
        });

    private static string AssetJson(ReleaseAsset asset) => JsonSerializer.Serialize(new
    {
        id = asset.Id,
        kind = asset.Kind,
        locator = asset.Locator,
        digestAlgorithm = asset.DigestAlgorithm,
        digest = asset.Digest,
    });

    private static byte[] RawManifest(string properties) => Utf8("{" + properties + "}");

    private static string ReceiptText(string promotionId, string target, string manifestSha256, string authorizationRef, string outcome) =>
        JsonSerializer.Serialize(new
        {
            schema = "appsurface-promotion-receipt-v1",
            promotionId,
            target,
            releaseManifestSha256 = manifestSha256,
            authorizationRef,
            outcome
        });

    private static byte[] ReceiptBytes(string promotionId, string target, string manifestSha256, string authorizationRef, string outcome) =>
        Utf8(ReceiptText(promotionId, target, manifestSha256, authorizationRef, outcome));

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static VerifiedReleaseManifest VerifyManifest(byte[] bytes, IReadOnlyList<ReleaseAsset> assets) =>
        Assert.IsType<ReleaseBindingResult<VerifiedReleaseManifest>.Success>(
            VerifiedReleaseManifest.VerifyManifest(bytes, Hash(bytes), Identity(), assets)).Value;

    private static void AssertSuccess(ReleaseBindingResult<VerifiedReleaseManifest> result) =>
        Assert.IsType<ReleaseBindingResult<VerifiedReleaseManifest>.Success>(result);

    private static void AssertFailure<T>(ReleaseBindingResult<T> result, string code)
    {
        var failure = Assert.IsType<ReleaseBindingResult<T>.Failure>(result);
        Assert.Equal(code, failure.Code);
    }

    private static void AssertOutcome(byte[] bytes, VerifiedReleaseManifest manifest, PromotionIdentity identity, PromotionOutcome outcome) =>
        AssertOutcome(VerifiedReleaseManifest.VerifyPromotionBinding(bytes, manifest, identity), outcome);

    private static void AssertOutcome(ReleaseBindingResult<PromotionOutcome> result, PromotionOutcome outcome) =>
        Assert.Equal(outcome, Assert.IsType<ReleaseBindingResult<PromotionOutcome>.Success>(result).Value);

    private static byte[] PadJson(byte[] json, int length)
    {
        var output = Enumerable.Repeat((byte)' ', length).ToArray();
        json.CopyTo(output, 0);
        output[^1] = (byte)' ';
        return output;
    }

    private static byte[] CreatePackageArchive(string id, string version, string payload)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(archive, $"{id}.nuspec", $"<package><metadata><id>{id}</id><version>{version}</version></metadata></package>");
            AddEntry(archive, "lib/net10.0/package.dll", payload);
        }
        return stream.ToArray();
    }

    private static void AddEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        entry.LastWriteTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
