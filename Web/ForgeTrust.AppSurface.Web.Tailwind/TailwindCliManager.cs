using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ForgeTrust.AppSurface.Web.Tailwind.Internal;
using Microsoft.Extensions.Logging;

[assembly: InternalsVisibleTo("ForgeTrust.AppSurface.Web.Tailwind.Tests")]
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]

namespace ForgeTrust.AppSurface.Web.Tailwind;

/// <summary>
/// Resolves the Tailwind CLI used by development watch mode.
/// </summary>
/// <remarks>
/// Normal resolution uses the same package-pinned manifest and verified host cache as
/// the MSBuild task. This manager is intentionally the only place that may make the
/// development-only <c>PATH</c> fallback after verified resolution fails. Build mode
/// uses <see cref="TailwindCliResolver"/> directly and never searches <c>PATH</c>.
/// </remarks>
public class TailwindCliManager
{
    /// <summary>
    /// Represents the concrete process invocation required to launch the resolved Tailwind CLI.
    /// </summary>
    /// <param name="FileName">The executable or launcher to start.</param>
    /// <param name="Arguments">The complete ordered arguments to pass to <paramref name="FileName"/>.</param>
    internal sealed record TailwindCliInvocation(string FileName, IReadOnlyList<string> Arguments);

    private readonly ILogger<TailwindCliManager> _logger;
    private readonly string _binaryName = TailwindRuntimeMap.GetLocalBinaryName(IsCurrentOsPlatform);

    /// <summary>Initializes a manager for development watch resolution.</summary>
    /// <param name="logger">The logger used for resolution diagnostics.</param>
    public TailwindCliManager(ILogger<TailwindCliManager> logger)
    {
        _logger = logger;
    }

    /// <summary>Gets or sets a base directory override used by isolated tests.</summary>
    internal string? BaseDirectoryOverride { get; set; }

    /// <summary>Gets or sets a host RID override used only by isolated tests.</summary>
    internal string? RidOverride { get; set; }

    /// <summary>Gets or sets a cache-root override used by isolated tests.</summary>
    internal string? DownloadCacheRootOverride { get; set; }

    /// <summary>Gets or sets a release-manifest path override used by isolated tests.</summary>
    internal string? ReleaseManifestPathOverride { get; set; }

    /// <summary>Gets or sets an official-release download seam used by isolated tests.</summary>
    internal Func<Uri, CancellationToken, Task<byte[]>>? DownloadOverride { get; set; }

    /// <summary>Gets or sets a platform detector override used by isolated tests.</summary>
    internal static Func<OSPlatform, bool>? IsOSPlatformOverride { get; set; }

    /// <summary>Gets or sets a process-architecture override used by isolated tests.</summary>
    internal static Func<Architecture>? ProcessArchitectureOverride { get; set; }

    /// <summary>
    /// Resolves a verified host-cache executable, then makes one development-only PATH attempt.
    /// </summary>
    /// <returns>The absolute path or launcher path for development watch mode.</returns>
    /// <exception cref="FileNotFoundException">Thrown when neither verified resolution nor PATH yields a CLI.</exception>
    public virtual string GetTailwindPath()
    {
        return GetTailwindPathAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Resolves a verified host-cache executable, then makes one development-only PATH attempt.
    /// </summary>
    /// <param name="cancellationToken">Cancellation for cache locking, downloads, and retry delays.</param>
    /// <returns>The absolute path or launcher path for development watch mode.</returns>
    /// <exception cref="FileNotFoundException">Thrown when neither verified resolution nor PATH yields a CLI.</exception>
    /// <remarks>
    /// Watch hosts should prefer this asynchronous method so shutdown can cancel a cold-cache acquisition. The
    /// synchronous <see cref="GetTailwindPath"/> wrapper remains for existing callers that cannot await resolution.
    /// </remarks>
    public virtual async Task<string> GetTailwindPathAsync(CancellationToken cancellationToken)
    {
        TailwindCliResolutionException? resolutionFailure = null;
        try
        {
            var manifest = string.IsNullOrWhiteSpace(ReleaseManifestPathOverride)
                ? TailwindReleaseManifest.LoadEmbedded(typeof(TailwindCliManager).Assembly)
                : TailwindReleaseManifest.LoadFromFile(ReleaseManifestPathOverride);
            var resolver = new TailwindCliResolver(manifest, DownloadOverride);
            var resolved = await resolver.ResolveAsync(
                    new TailwindCliResolverOptions(
                        ExplicitCliPath: null,
                        ExplicitPathBaseDirectory: BaseDirectoryOverride ?? AppContext.BaseDirectory,
                        CacheRoot: DownloadCacheRootOverride,
                        TailwindVersion: manifest.Version,
                        RidOverride: RidOverride ?? GetCurrentRid()),
                    cancellationToken);
            _logger.LogDebug("Resolved verified Tailwind CLI from {CacheState}: {Path}", resolved.CacheState, resolved.Path);
            return resolved.Path;
        }
        catch (TailwindCliResolutionException ex)
        {
            resolutionFailure = ex;
            _logger.LogDebug(ex, "Verified Tailwind CLI resolution failed with {Classification}; watch will try PATH once.", ex.Failure);
        }
        catch (InvalidDataException ex)
        {
            _logger.LogDebug(ex, "The Tailwind release manifest is invalid; watch will try PATH once.");
        }

        if (TryGetFromPath(_binaryName, out var path))
        {
            _logger.LogDebug("Found Tailwind CLI in PATH: {Path}", path);
            return path;
        }

        throw new FileNotFoundException(
            "Tailwind CLI was not available from the verified host cache or development PATH. Configure TailwindOptions.CliPath, prewarm TailwindDownloadCacheRoot, or install tailwindcss on PATH for development watch mode.",
            _binaryName,
            resolutionFailure);
    }

    /// <summary>Builds the invocation needed to execute a resolved CLI path.</summary>
    /// <param name="tailwindPath">The resolved executable or Windows shell shim path.</param>
    /// <param name="tailwindArgs">Ordered Tailwind arguments.</param>
    /// <returns>A direct executable or shell-shim invocation.</returns>
    internal static TailwindCliInvocation BuildInvocation(string tailwindPath, IReadOnlyList<string> tailwindArgs)
    {
        var invocation = TailwindInvocationBuilder.Build(tailwindPath, tailwindArgs, IsCurrentOsPlatform);
        return new TailwindCliInvocation(invocation.FileName, invocation.Arguments);
    }

    /// <summary>Gets the supported Tailwind RID for the current process host.</summary>
    public static string GetCurrentRid()
    {
        return TailwindRuntimeMap.GetCurrentRid(IsCurrentOsPlatform, ProcessArchitectureOverride);
    }

    /// <summary>Maps an operating system and architecture pair to a Tailwind host RID.</summary>
    internal static string ResolveRid(OSPlatform osPlatform, Architecture architecture)
    {
        return TailwindRuntimeMap.ResolveRid(osPlatform, architecture);
    }

    private static bool IsCurrentOsPlatform(OSPlatform platform)
    {
        return IsOSPlatformOverride?.Invoke(platform) ?? RuntimeInformation.IsOSPlatform(platform);
    }

    private static bool TryGetFromPath(string fileName, out string path)
    {
        path = string.Empty;
        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathVariable))
        {
            return false;
        }

        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var candidateName in EnumeratePathSearchNames(fileName))
            {
                var candidate = Path.Join(directory, candidateName);
                if (File.Exists(candidate))
                {
                    path = candidate;
                    return true;
                }
            }
        }

        return false;
    }

    private static IEnumerable<string> EnumeratePathSearchNames(string fileName)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { fileName };
        yield return fileName;

        if (!IsCurrentOsPlatform(OSPlatform.Windows))
        {
            yield break;
        }

        var baseName = Path.GetFileNameWithoutExtension(fileName);
        foreach (var extension in GetWindowsPathExtensions())
        {
            var candidate = baseName + extension;
            if (seen.Add(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<string> GetWindowsPathExtensions()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var extension in new[] { ".exe", ".cmd", ".ps1" })
        {
            if (seen.Add(extension))
            {
                yield return extension;
            }
        }

        var pathExtensions = Environment.GetEnvironmentVariable("PATHEXT");
        if (string.IsNullOrWhiteSpace(pathExtensions))
        {
            yield break;
        }

        foreach (var rawExtension in pathExtensions.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var extension = rawExtension.StartsWith(".", StringComparison.Ordinal) ? rawExtension : "." + rawExtension;
            if (seen.Add(extension))
            {
                yield return extension;
            }
        }
    }
}
