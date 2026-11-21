using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ForgeTrust.AppSurface.ReleaseContracts;

internal sealed partial class VerifiedReleaseManifest
{
    private const string ManifestSchema = "appsurface-release-artifacts-v1";
    private const string ReceiptSchema = "appsurface-promotion-receipt-v1";
    private const int MaximumManifestBytes = 256 * 1024;
    private const int MaximumReceiptBytes = 16 * 1024;
    private const int MaximumAssets = 128;
    private const int MaximumTextBytes = 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly JsonDocumentOptions StrictJson = new()
    {
        AllowDuplicateProperties = false,
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 8,
    };

    /// <summary>Checks the exact manifest bytes, expected release identity and complete observed inventory.</summary>
    /// <remarks>Keep the supplied memory and observations stable for the duration of the call.</remarks>
    internal static ReleaseBindingResult<VerifiedReleaseManifest> VerifyManifest(
        ReadOnlyMemory<byte> manifestJson,
        string? expectedManifestSha256,
        ReleaseIdentity? expectedRelease,
        IReadOnlyList<ReleaseAsset>? observedAssets)
    {
        try
        {
            RequireWithinLimit(manifestJson.Length, MaximumManifestBytes);
            if (observedAssets is null)
            {
                throw new Rejection(ReleaseBindingFailure.InvalidRecord);
            }

            var count = ReadObservedCount(observedAssets);
            RequireWithinLimit(count, MaximumAssets);
            if (expectedRelease is null)
            {
                throw new Rejection(ReleaseBindingFailure.InvalidRecord);
            }

            RequireHex(RequireText(expectedManifestSha256), 64);
            ValidateIdentity(expectedRelease);

            var observations = new ReleaseAsset[count];
            for (var index = 0; index < count; index++)
            {
                var asset = ReadObservedAsset(observedAssets, index);
                ValidateAsset(asset);
                observations[index] = asset;
            }

            var bytes = manifestJson.ToArray();
            var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (!string.Equals(sha256, expectedManifestSha256, StringComparison.Ordinal))
            {
                throw new Rejection(ReleaseBindingFailure.ManifestDigestMismatch);
            }

            using var document = Parse(bytes);
            var root = document.RootElement;
            RequireSchema(root, ManifestSchema);
            RequireObjectSize(root, 5);
            var assets = RequireProperty(root, "assets", JsonValueKind.Array);
            var assetCount = assets.GetArrayLength();
            RequireWithinLimit(assetCount, MaximumAssets);
            if (assetCount == 0)
            {
                throw new Rejection(ReleaseBindingFailure.InvalidRecord);
            }

            var identity = new ReleaseIdentity(ReadString(root, "repository"), ReadString(root, "releaseId"),
                ReadString(root, "sourceCommit"));
            ValidateIdentity(identity);

            var expectedAssets = new Dictionary<string, ReleaseAsset>(assetCount, StringComparer.Ordinal);
            foreach (var element in assets.EnumerateArray())
            {
                RequireObjectSize(element, 5);
                var asset = new ReleaseAsset(ReadString(element, "id"), ReadString(element, "kind"),
                    ReadString(element, "locator"), ReadString(element, "digestAlgorithm"), ReadString(element, "digest"));
                ValidateAsset(asset);
                if (!expectedAssets.TryAdd(asset.Id, asset))
                {
                    throw new Rejection(ReleaseBindingFailure.InvalidRecord);
                }
            }

            if (identity != expectedRelease)
            {
                throw new Rejection(ReleaseBindingFailure.ReleaseIdentityMismatch);
            }

            if (observations.Length != assetCount)
            {
                throw new Rejection(ReleaseBindingFailure.AssetMismatch);
            }

            foreach (var observed in observations)
            {
                // Removing the entry also detects duplicates when counts agree.
                if (!expectedAssets.Remove(observed.Id, out var expected) || observed != expected)
                {
                    throw new Rejection(ReleaseBindingFailure.AssetMismatch);
                }
            }

            return new ReleaseBindingResult<VerifiedReleaseManifest>.Success(new VerifiedReleaseManifest(identity, sha256));
        }
        catch (Rejection error)
        {
            return new ReleaseBindingResult<VerifiedReleaseManifest>.Failure(error.Code);
        }
    }

    /// <summary>Checks a receipt against a verified manifest and the caller's promotion expectations.</summary>
    /// <remarks>The result reports the recorded outcome. The caller establishes authenticity and authorization.</remarks>
    internal static ReleaseBindingResult<PromotionOutcome> VerifyPromotionBinding(
        ReadOnlyMemory<byte> receiptJson,
        VerifiedReleaseManifest? manifest,
        PromotionIdentity? expectedPromotion)
    {
        try
        {
            RequireWithinLimit(receiptJson.Length, MaximumReceiptBytes);
            if (manifest is null || expectedPromotion is null)
            {
                throw new Rejection(ReleaseBindingFailure.InvalidRecord);
            }

            ValidateIdentity(expectedPromotion);
            using var document = Parse(receiptJson.ToArray());
            var root = document.RootElement;
            RequireSchema(root, ReceiptSchema);
            RequireObjectSize(root, 6);
            var outcome = RequireText(ReadString(root, "outcome")) switch
            {
                "succeeded" => PromotionOutcome.Succeeded,
                "failed" => PromotionOutcome.Failed,
                _ => throw new Rejection(ReleaseBindingFailure.InvalidRecord),
            };

            var identity = new PromotionIdentity(ReadString(root, "promotionId"), ReadString(root, "target"),
                ReadString(root, "authorizationRef"));
            ValidateIdentity(identity);
            var sha256 = RequireText(ReadString(root, "releaseManifestSha256"));
            RequireHex(sha256, 64);
            if (identity != expectedPromotion || !string.Equals(sha256, manifest.Sha256, StringComparison.Ordinal))
            {
                throw new Rejection(ReleaseBindingFailure.PromotionBindingMismatch);
            }

            return new ReleaseBindingResult<PromotionOutcome>.Success(outcome);
        }
        catch (Rejection error)
        {
            return new ReleaseBindingResult<PromotionOutcome>.Failure(error.Code);
        }
    }

    private static int ReadObservedCount(IReadOnlyList<ReleaseAsset> observedAssets)
    {
        try
        {
            var count = observedAssets.Count;
            return count >= 0 ? count : throw new Rejection(ReleaseBindingFailure.InvalidRecord);
        }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException)
        {
            throw new Rejection(ReleaseBindingFailure.InvalidRecord);
        }
    }

    private static ReleaseAsset ReadObservedAsset(IReadOnlyList<ReleaseAsset> observedAssets, int index)
    {
        try
        {
            return observedAssets[index] ?? throw new Rejection(ReleaseBindingFailure.InvalidRecord);
        }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException
            or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            throw new Rejection(ReleaseBindingFailure.InvalidRecord);
        }
    }

    private static JsonDocument Parse(byte[] bytes)
    {
        try
        {
            // Validate all raw UTF-8 before JsonDocument performs structural checks.
            _ = StrictUtf8.GetCharCount(bytes);
            return JsonDocument.Parse(bytes, StrictJson);
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or InvalidOperationException)
        {
            throw new Rejection(ReleaseBindingFailure.InvalidRecord);
        }
    }

    private static void RequireSchema(JsonElement root, string expected)
    {
        var schema = RequireText(ReadString(root, "schema"));
        if (!string.Equals(schema, expected, StringComparison.Ordinal))
        {
            throw new Rejection(ReleaseBindingFailure.UnsupportedSchema);
        }
    }

    private static void RequireObjectSize(JsonElement element, int fieldCount)
    {
        if (element.ValueKind != JsonValueKind.Object || element.GetPropertyCount() != fieldCount)
        {
            throw new Rejection(ReleaseBindingFailure.InvalidRecord);
        }
    }

    private static JsonElement RequireProperty(JsonElement element, string name, JsonValueKind kind)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var property) ||
            property.ValueKind != kind)
        {
            throw new Rejection(ReleaseBindingFailure.InvalidRecord);
        }

        return property;
    }

    private static string ReadString(JsonElement element, string name)
    {
        var property = RequireProperty(element, name, JsonValueKind.String);
        try
        {
            return property.GetString()!;
        }
        catch (InvalidOperationException)
        {
            // An escaped unpaired surrogate can fail here after parsing has completed.
            throw new Rejection(ReleaseBindingFailure.InvalidRecord);
        }
    }

    private static void ValidateIdentity(ReleaseIdentity identity)
    {
        RequireText(identity.Repository);
        RequireText(identity.ReleaseId);
        var commit = RequireText(identity.SourceCommit);
        RequireHex(commit, commit.Length == 40 ? 40 : 64);
    }

    private static void ValidateIdentity(PromotionIdentity identity)
    {
        RequireText(identity.PromotionId);
        RequireText(identity.Target);
        RequireText(identity.AuthorizationRef);
    }

    private static void ValidateAsset(ReleaseAsset asset)
    {
        RequireText(asset.Id);
        RequireText(asset.Kind);
        RequireText(asset.Locator);
        var length = RequireText(asset.DigestAlgorithm) switch
        {
            "sha256" => 64,
            "sha512" => 128,
            _ => throw new Rejection(ReleaseBindingFailure.InvalidRecord),
        };
        RequireHex(RequireText(asset.Digest), length);
    }

    private static string RequireText(string? value)
    {
        if (value is null)
        {
            throw new Rejection(ReleaseBindingFailure.InvalidRecord);
        }

        // Valid UTF-8 byte length is at least the number of UTF-16 code units.
        RequireWithinLimit(value.Length, MaximumTextBytes);
        try
        {
            RequireWithinLimit(StrictUtf8.GetByteCount(value), MaximumTextBytes);
        }
        catch (EncoderFallbackException)
        {
            throw new Rejection(ReleaseBindingFailure.InvalidRecord);
        }

        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
        {
            throw new Rejection(ReleaseBindingFailure.InvalidRecord);
        }

        return value;
    }

    private static void RequireHex(string value, int length)
    {
        if (value.Length != length || value.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new Rejection(ReleaseBindingFailure.InvalidRecord);
        }
    }

    private static void RequireWithinLimit(int actual, int maximum)
    {
        if (actual > maximum)
        {
            throw new Rejection(ReleaseBindingFailure.InputLimitExceeded);
        }
    }

    private sealed class Rejection(string code) : Exception
    {
        internal string Code { get; } = code;
    }
}
