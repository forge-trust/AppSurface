namespace ForgeTrust.AppSurface.ReleaseContracts;

/// <summary>Caller-supplied identity of one release, including its full Git commit ID.</summary>
internal sealed record ReleaseIdentity(string Repository, string ReleaseId, string SourceCommit);

/// <summary>Exact inventory entry. The caller chooses the ID, kind and opaque locator conventions.</summary>
internal sealed record ReleaseAsset(string Id, string Kind, string Locator, string DigestAlgorithm, string Digest);

/// <summary>Caller-supplied binding expectations for one promotion attempt.</summary>
internal sealed record PromotionIdentity(string PromotionId, string Target, string AuthorizationRef);

/// <summary>The outcome recorded by a bound receipt.</summary>
internal enum PromotionOutcome
{
    Succeeded,
    Failed,
}

/// <summary>Stable failure codes for the offline release binding contract.</summary>
internal static class ReleaseBindingFailure
{
    internal const string InvalidRecord = "invalid-record";
    internal const string UnsupportedSchema = "unsupported-schema";
    internal const string InputLimitExceeded = "input-limit-exceeded";
    internal const string ManifestDigestMismatch = "manifest-digest-mismatch";
    internal const string ReleaseIdentityMismatch = "release-identity-mismatch";
    internal const string AssetMismatch = "asset-mismatch";
    internal const string PromotionBindingMismatch = "promotion-binding-mismatch";
}

/// <summary>A verification result containing either a success value or one stable failure code.</summary>
internal abstract class ReleaseBindingResult<T>
{
    private ReleaseBindingResult()
    {
    }

    /// <summary>Successful verification with a value.</summary>
    internal sealed class Success(T value) : ReleaseBindingResult<T>
    {
        internal T Value { get; } = value;
    }

    /// <summary>Rejected verification, with no success value.</summary>
    internal sealed class Failure(string code) : ReleaseBindingResult<T>
    {
        internal string Code { get; } = code;
    }
}

/// <summary>Detached release identity and exact manifest digest from a successful verification.</summary>
internal sealed partial class VerifiedReleaseManifest
{
    private VerifiedReleaseManifest(ReleaseIdentity identity, string sha256)
    {
        Identity = identity;
        Sha256 = sha256;
    }

    internal ReleaseIdentity Identity { get; }

    internal string Sha256 { get; }
}
