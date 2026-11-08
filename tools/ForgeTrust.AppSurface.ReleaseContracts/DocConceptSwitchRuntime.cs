using System.Security.Cryptography;

namespace ForgeTrust.AppSurface.ReleaseContracts;

/// <summary>
/// Build-owned delivery contract for the packaged AppSurface Docs version-switch assets.
/// </summary>
/// <remarks>
/// Docs and Release embed the same linked source files. The public output names are caller-mounted under
/// <c>{RouteRootPath}/versions/switch</c>; they are not filesystem paths and callers must use their own route
/// builders. The JavaScript source hash, CSP source expression, and SRI value are all derived from the exact
/// embedded runtime bytes so they cannot drift as separately maintained constants.
/// </remarks>
internal static class DocConceptSwitchRuntime
{
    internal const string RuntimeRoutePath = "versions/switch/runtime.js";
    internal const string StylesheetRoutePath = "versions/switch/switch.css";
    internal const string RuntimeAssetFileName = "docs-version-switch.js";
    internal const string StylesheetAssetFileName = "docs-version-switch.css";
    internal const string RootAttribute = "data-docs-version-switch";
    internal const string FallbackAttribute = "data-docs-version-fallback";
    internal const string ToggleAttribute = "data-docs-version-toggle";
    internal const string OptionsAttribute = "data-docs-version-options";
    internal const string DataScriptAttribute = "data-docs-version-data";
    internal const string NoticeAttribute = "data-docs-version-notice";
    internal const string ExplanationId = "docs-version-explanation";
    internal const string CurrentIdentityProperty = "target";
    internal const string IncomingRowsProperty = "incoming";
    internal const string SourceKeyQueryParameter = "docsFrom";
    internal const string AnchorIdsAttribute = "data-docs-version-anchor-ids";

    private const string RuntimeResourceName = "ForgeTrust.AppSurface.ReleaseContracts.Assets.docs-version-switch.js";
    private const string StylesheetResourceName = "ForgeTrust.AppSurface.ReleaseContracts.Assets.docs-version-switch.css";
    private static readonly Lazy<RuntimeAsset> Runtime = new(ReadRuntimeAsset, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<byte[]> Stylesheet = new(ReadStylesheetBytes, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Gets the lowercase hexadecimal SHA-256 digest of the exact embedded JavaScript bytes.</summary>
    internal static string RuntimeSha256Hex => Runtime.Value.Sha256Hex;

    /// <summary>Gets the unquoted CSP hash source expression, such as <c>sha256-AbCd...</c>.</summary>
    internal static string RuntimeCspHashSource => Runtime.Value.Integrity;

    /// <summary>Gets the exact SRI attribute value for the embedded JavaScript bytes.</summary>
    internal static string RuntimeSubresourceIntegrity => Runtime.Value.Integrity;

    /// <summary>Returns a defensive copy of the exact JavaScript bytes embedded into ReleaseContracts.</summary>
    internal static byte[] ReadRuntimeBytes() => (byte[])Runtime.Value.Bytes.Clone();

    /// <summary>Returns a defensive copy of the exact stylesheet bytes embedded into ReleaseContracts.</summary>
    internal static byte[] ReadStylesheetBytesForCaller() => (byte[])Stylesheet.Value.Clone();

    private static RuntimeAsset ReadRuntimeAsset()
    {
        var bytes = ReadEmbeddedBytes(RuntimeResourceName);
        var digest = SHA256.HashData(bytes);
        return new RuntimeAsset(
            bytes,
            Convert.ToHexString(digest).ToLowerInvariant(),
            $"sha256-{Convert.ToBase64String(digest)}");
    }

    private static byte[] ReadStylesheetBytes()
    {
        return ReadEmbeddedBytes(StylesheetResourceName);
    }

    private static byte[] ReadEmbeddedBytes(string resourceName)
    {
        using var stream = typeof(DocConceptSwitchRuntime).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"The packaged version-switch asset resource '{resourceName}' is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private sealed record RuntimeAsset(byte[] Bytes, string Sha256Hex, string Integrity);
}
