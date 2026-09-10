using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ForgeTrust.AppSurface.Web.Tailwind.Internal;

/// <summary>
/// Describes one checked-in Tailwind standalone CLI release.
/// </summary>
/// <remarks>
/// The manifest is packaged with the main Tailwind package and is the authenticity
/// anchor for downloaded executables. The release-provided <c>sha256sums.txt</c> is
/// checked as an audit signal, but it cannot be the sole source of trust because it is
/// downloaded from the same location as the executable.
/// </remarks>
internal sealed partial class TailwindReleaseManifest
{
    private const int SchemaVersion = 1;
    private static readonly string[] SupportedRids = ["linux-x64", "linux-arm64", "osx-x64", "osx-arm64", "win-x64"];
    private readonly IReadOnlyDictionary<string, TailwindReleaseAsset> _assets;

    private TailwindReleaseManifest(string version, Uri baseUri, IReadOnlyDictionary<string, TailwindReleaseAsset> assets)
    {
        Version = version;
        BaseUri = baseUri;
        _assets = assets;
    }

    /// <summary>Gets the canonical stable Tailwind version.</summary>
    public string Version { get; }

    /// <summary>Gets the official release directory containing the binary assets.</summary>
    public Uri BaseUri { get; }

    /// <summary>
    /// Reads and validates a manifest from a packed targets directory or source tree.
    /// </summary>
    /// <param name="path">The manifest file path.</param>
    /// <returns>The validated release manifest.</returns>
    public static TailwindReleaseManifest LoadFromFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Parse(stream);
    }

    /// <summary>
    /// Reads the manifest embedded in the runtime web assembly for development watch mode.
    /// </summary>
    /// <param name="assembly">The assembly that embeds <c>tailwind.release.json</c>.</param>
    /// <returns>The validated release manifest.</returns>
    public static TailwindReleaseManifest LoadEmbedded(Assembly assembly)
    {
        var resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(static name => name.EndsWith(".tailwind.release.json", StringComparison.Ordinal));
        if (resourceName is null)
        {
            throw new InvalidDataException("The Tailwind release manifest is not embedded in the web assembly.");
        }

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidDataException("The embedded Tailwind release manifest could not be opened.");
        return Parse(stream);
    }

    /// <summary>
    /// Parses and validates a release manifest stream.
    /// </summary>
    /// <param name="stream">The JSON stream to parse.</param>
    /// <returns>The validated release manifest.</returns>
    public static TailwindReleaseManifest Parse(Stream stream)
    {
        TailwindReleaseManifestDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<TailwindReleaseManifestDocument>(stream, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The Tailwind release manifest is not valid JSON.", ex);
        }

        if (document is null)
        {
            throw new InvalidDataException("The Tailwind release manifest is empty.");
        }

        if (document.SchemaVersion != SchemaVersion)
        {
            throw new InvalidDataException($"Unsupported Tailwind release manifest schema '{document.SchemaVersion}'.");
        }

        if (!IsCanonicalStableVersion(document.Version))
        {
            throw new InvalidDataException("The Tailwind release manifest version is not canonical stable major.minor.patch.");
        }

        if (!Uri.TryCreate(document.BaseUrl, UriKind.Absolute, out var baseUri)
            || !string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The Tailwind release manifest baseUrl must be an absolute HTTPS URL.");
        }

        var assets = new Dictionary<string, TailwindReleaseAsset>(StringComparer.Ordinal);
        foreach (var asset in document.Assets ?? [])
        {
            if (string.IsNullOrWhiteSpace(asset.Rid)
                || !SupportedRids.Contains(asset.Rid, StringComparer.Ordinal)
                || !assets.TryAdd(asset.Rid, new TailwindReleaseAsset(asset.Rid, asset.BinaryName ?? string.Empty, asset.Sha256 ?? string.Empty)))
            {
                throw new InvalidDataException("The Tailwind release manifest has an unsupported or duplicate asset RID.");
            }

            var expectedName = TailwindRuntimeMap.GetRuntimeBinaryName(asset.Rid);
            if (!string.Equals(asset.BinaryName, expectedName, StringComparison.Ordinal)
                || !IsSafeFileName(asset.BinaryName)
                || !IsLowercaseSha256(asset.Sha256))
            {
                throw new InvalidDataException($"The Tailwind release manifest asset '{asset.Rid}' is invalid.");
            }
        }

        if (assets.Count != SupportedRids.Length || SupportedRids.Any(rid => !assets.ContainsKey(rid)))
        {
            throw new InvalidDataException("The Tailwind release manifest must contain exactly the five supported Tailwind assets.");
        }

        var releaseDirectoryUri = new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
        return new TailwindReleaseManifest(document.Version!, releaseDirectoryUri, assets);
    }

    /// <summary>Gets the pinned asset metadata for a mapped host RID.</summary>
    /// <param name="rid">The current build host RID.</param>
    /// <returns>The release asset.</returns>
    public TailwindReleaseAsset GetAsset(string rid)
    {
        return _assets.TryGetValue(rid, out var asset)
            ? asset
            : throw new InvalidDataException($"The Tailwind release manifest does not contain '{rid}'.");
    }

    /// <summary>Determines whether a value is the allowed canonical Tailwind version form.</summary>
    /// <param name="version">The candidate version, or <see langword="null"/>.</param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="version"/> is a stable <c>major.minor.patch</c> value
    /// without leading zeroes, whitespace, or integer overflow; otherwise, <see langword="false"/>.
    /// </returns>
    public static bool IsCanonicalStableVersion(string? version)
    {
        return version is not null
            && CanonicalStableVersionRegex().IsMatch(version)
            && version.Split('.').All(static part => int.TryParse(part, out _));
    }

    private static bool IsLowercaseSha256(string? value)
    {
        return value is not null && LowercaseSha256Regex().IsMatch(value);
    }

    [GeneratedRegex(@"\A(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\z", RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalStableVersionRegex();

    [GeneratedRegex(@"\A[0-9a-f]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex LowercaseSha256Regex();

    private static bool IsSafeFileName(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && !Path.IsPathRooted(value)
            && Path.GetFileName(value) == value
            && value.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) < 0;
    }

    private sealed class TailwindReleaseManifestDocument
    {
        public int SchemaVersion { get; init; }

        public string? Version { get; init; }

        public string? BaseUrl { get; init; }

        public List<TailwindReleaseAssetDocument>? Assets { get; init; }
    }

    private sealed class TailwindReleaseAssetDocument
    {
        public string? Rid { get; init; }

        public string? BinaryName { get; init; }

        public string? Sha256 { get; init; }
    }
}

/// <summary>Represents one trusted Tailwind release asset.</summary>
/// <param name="Rid">The build-host runtime identifier.</param>
/// <param name="BinaryName">The standalone executable file name.</param>
/// <param name="Sha256">The package-pinned lowercase SHA-256 digest.</param>
internal sealed record TailwindReleaseAsset(string Rid, string BinaryName, string Sha256);

/// <summary>Describes a resolved executable and how it was obtained.</summary>
/// <param name="Path">The absolute executable path.</param>
/// <param name="Rid">The selected host RID, or <c>explicit</c> for an override.</param>
/// <param name="Version">The selected Tailwind version, or <c>explicit</c> for an override.</param>
/// <param name="CacheState">Whether the executable was explicit, reused, or acquired.</param>
internal sealed record TailwindResolvedCli(string Path, string Rid, string Version, TailwindCliCacheState CacheState);

/// <summary>Identifies the source of a resolved Tailwind executable.</summary>
internal enum TailwindCliCacheState
{
    /// <summary>An explicitly configured executable path bypassed cache resolution.</summary>
    Explicit,

    /// <summary>A previously acquired and reverified host-cache executable was reused.</summary>
    Reused,

    /// <summary>A missing host-cache executable was downloaded, verified, and published.</summary>
    Acquired
}

/// <summary>Classifies a deterministic Tailwind CLI resolution failure.</summary>
internal enum TailwindCliResolutionFailure
{
    /// <summary>The required release manifest could not be located or parsed.</summary>
    MissingManifest,

    /// <summary>The active build host has no supported Tailwind runtime identifier.</summary>
    UnsupportedRid,

    /// <summary>An explicitly configured Tailwind executable path is invalid.</summary>
    InvalidCliPath,

    /// <summary>The requested Tailwind version is absent.</summary>
    MissingVersion,

    /// <summary>The requested Tailwind version is malformed or differs from the pinned manifest version.</summary>
    InvalidVersion,

    /// <summary>No usable host-cache root is configured for CLI acquisition.</summary>
    NoCacheRoot,

    /// <summary>The configured cache root or one of its entries crosses a filesystem trust boundary.</summary>
    InvalidCache,

    /// <summary>The official checksum payload does not validate the manifest-pinned binary digest.</summary>
    ChecksumFailure,

    /// <summary>The configured host-cache root cannot be created or written.</summary>
    NonWritableRoot,

    /// <summary>An official release request failed without exhausting its retry policy.</summary>
    NetworkFailure,

    /// <summary>An official release payload exceeded its bounded download size.</summary>
    DownloadSizeLimit,

    /// <summary>All retryable official release requests failed.</summary>
    RetryExhausted,

    /// <summary>Another process retained the cache-entry lock past the bounded wait period.</summary>
    LockTimeout
}

/// <summary>Represents a resolver failure that callers can map to stable build or watch diagnostics.</summary>
internal sealed class TailwindCliResolutionException : Exception
{
    /// <summary>Creates a classified Tailwind CLI resolution failure.</summary>
    /// <param name="failure">The deterministic failure classification.</param>
    /// <param name="message">The safe, user-facing failure message.</param>
    /// <param name="rid">The affected host RID, when known.</param>
    /// <param name="version">The affected Tailwind version, when known.</param>
    /// <param name="innerException">The underlying failure, when available.</param>
    public TailwindCliResolutionException(TailwindCliResolutionFailure failure, string message, string? rid = null, string? version = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
        Rid = rid;
        Version = version;
    }

    /// <summary>Gets the stable failure classification.</summary>
    public TailwindCliResolutionFailure Failure { get; }

    /// <summary>Gets the affected host RID, when known.</summary>
    public string? Rid { get; }

    /// <summary>Gets the affected Tailwind version, when known.</summary>
    public string? Version { get; }
}
