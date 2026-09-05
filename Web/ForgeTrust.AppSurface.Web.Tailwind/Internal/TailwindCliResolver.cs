using System.Buffers;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ForgeTrust.AppSurface.Web.Tailwind.Internal;

/// <summary>
/// Resolves one verified Tailwind executable for the current build host.
/// </summary>
/// <remarks>
/// This internal linked-source service is compiled into both the MSBuild task and the
/// web assembly. It owns cache identity, manifest validation, acquisition, hash
/// verification, and cancellation. Callers retain their distinct process policies:
/// build never searches <c>PATH</c>, while development watch may do so only after a
/// no-override resolver failure.
/// </remarks>
internal sealed class TailwindCliResolver
{
    private const int MaximumChecksumBytes = 1024 * 1024;
    private const long MaximumBinaryBytes = 200L * 1024 * 1024;
    private static readonly HttpClient SharedHttpClient = new();
    private readonly TailwindReleaseManifest _manifest;
    private readonly Func<Uri, CancellationToken, Task<byte[]>>? _downloadOverride;
    private readonly HttpClient _httpClient;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<string, string?> _getEnvironmentVariable;
    private readonly Func<string> _getCurrentRid;
    private readonly Func<string, string, bool> _isVerifiedFinal;
    private readonly Action<string>? _afterLockOpened;
    private readonly Action<string>? _afterPartialOpened;
    private readonly Action<string> _deleteFile;
    private readonly Func<bool> _isWindows;
    private readonly long _maximumBinaryBytes;
    private readonly Func<string, string, string, string, string> _getRuntimeBinaryPath;

    /// <summary>
    /// Initializes a resolver with the supplied checked-in release manifest.
    /// </summary>
    /// <param name="manifest">The package-owned digest manifest.</param>
    /// <param name="download">Optional test seam for official release downloads.</param>
    /// <param name="getEnvironmentVariable">Optional environment lookup seam.</param>
    /// <param name="getCurrentRid">Optional host RID seam.</param>
    /// <param name="delay">Optional retry-delay seam used by deterministic tests.</param>
    /// <param name="httpClient">Optional HTTP client seam used by deterministic tests.</param>
    /// <param name="isVerifiedFinal">Optional final-cache verification seam used by deterministic tests.</param>
    /// <param name="afterLockOpened">Optional post-open lock-race seam used by deterministic tests.</param>
    /// <param name="afterPartialOpened">Optional post-open partial-file seam used by real-process cache tests.</param>
    /// <param name="deleteFile">Optional owned-artifact cleanup seam used by deterministic tests.</param>
    /// <param name="isWindows">Optional platform seam used by deterministic tests.</param>
    /// <param name="maximumBinaryBytes">Optional binary-size limit seam used by deterministic tests.</param>
    /// <param name="getRuntimeBinaryPath">Optional cache-path seam used by deterministic tests.</param>
    public TailwindCliResolver(
        TailwindReleaseManifest manifest,
        Func<Uri, CancellationToken, Task<byte[]>>? download = null,
        Func<string, string?>? getEnvironmentVariable = null,
        Func<string>? getCurrentRid = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        HttpClient? httpClient = null,
        Func<string, string, bool>? isVerifiedFinal = null,
        Action<string>? afterLockOpened = null,
        Action<string>? afterPartialOpened = null,
        Action<string>? deleteFile = null,
        Func<bool>? isWindows = null,
        long maximumBinaryBytes = MaximumBinaryBytes,
        Func<string, string, string, string, string>? getRuntimeBinaryPath = null)
    {
        _manifest = manifest;
        _downloadOverride = download;
        _httpClient = httpClient ?? SharedHttpClient;
        _delay = delay ?? Task.Delay;
        _getEnvironmentVariable = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        _getCurrentRid = getCurrentRid ?? (() => TailwindRuntimeMap.GetCurrentRid());
        _isVerifiedFinal = isVerifiedFinal ?? IsVerifiedFinal;
        _afterLockOpened = afterLockOpened;
        _afterPartialOpened = afterPartialOpened;
        _deleteFile = deleteFile ?? File.Delete;
        _isWindows = isWindows ?? OperatingSystem.IsWindows;
        _maximumBinaryBytes = maximumBinaryBytes;
        _getRuntimeBinaryPath = getRuntimeBinaryPath ?? TailwindDownloadCache.GetRuntimeBinaryPath;
    }

    /// <summary>
    /// Resolves an explicit override or a cache-backed executable for the current build host.
    /// </summary>
    /// <param name="options">Resolution inputs from build or watch policy.</param>
    /// <param name="cancellationToken">Cancellation for lock waiting, downloads, and writes.</param>
    /// <returns>The resolved executable path and provenance.</returns>
    public async Task<TailwindResolvedCli> ResolveAsync(TailwindCliResolverOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.IsNullOrWhiteSpace(options.ExplicitCliPath))
        {
            return ResolveExplicitPath(options.ExplicitCliPath, options.ExplicitPathBaseDirectory);
        }

        if (string.IsNullOrWhiteSpace(options.TailwindVersion))
        {
            throw new TailwindCliResolutionException(
                TailwindCliResolutionFailure.MissingVersion,
                "Tailwind CSS version could not be resolved.");
        }

        if (!TailwindReleaseManifest.IsCanonicalStableVersion(options.TailwindVersion))
        {
            throw new TailwindCliResolutionException(
                TailwindCliResolutionFailure.InvalidVersion,
                "Tailwind CSS version must be canonical stable major.minor.patch.",
                version: options.TailwindVersion);
        }

        if (!string.Equals(options.TailwindVersion, _manifest.Version, StringComparison.Ordinal))
        {
            throw new TailwindCliResolutionException(
                TailwindCliResolutionFailure.InvalidVersion,
                "TailwindVersion does not match the package release manifest.",
                version: options.TailwindVersion);
        }

        var rid = options.RidOverride ?? _getCurrentRid();
        if (string.Equals(rid, "unknown", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(TailwindRuntimeMap.GetRuntimeBinaryName(rid)))
        {
            throw new TailwindCliResolutionException(
                TailwindCliResolutionFailure.UnsupportedRid,
                "Tailwind CSS could not determine a supported runtime identifier for the current build host.",
                rid);
        }

        var asset = _manifest.GetAsset(rid);
        string? cacheRoot;
        try
        {
            cacheRoot = ResolveCacheRoot(options.CacheRoot, _getEnvironmentVariable);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new TailwindCliResolutionException(
                TailwindCliResolutionFailure.InvalidCache,
                "The configured Tailwind download cache root is invalid.",
                rid,
                _manifest.Version,
                ex);
        }
        if (cacheRoot is null)
        {
            throw new TailwindCliResolutionException(
                TailwindCliResolutionFailure.NoCacheRoot,
                "No user or CI Tailwind download cache root could be determined.",
                rid,
                _manifest.Version);
        }

        var finalPath = _getRuntimeBinaryPath(cacheRoot, _manifest.Version, rid, asset.BinaryName);
        try
        {
            EnsureSafeCachePath(cacheRoot, finalPath);
            if (_isVerifiedFinal(finalPath, asset.Sha256))
            {
                return new TailwindResolvedCli(finalPath, rid, _manifest.Version, TailwindCliCacheState.Reused);
            }

            return await AcquireAsync(cacheRoot, finalPath, asset, cancellationToken);
        }
        catch (TailwindCliResolutionException)
        {
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new TailwindCliResolutionException(
                TailwindCliResolutionFailure.NonWritableRoot,
                "The Tailwind download cache root is not writable.",
                rid,
                _manifest.Version,
                ex);
        }
        catch (IOException ex)
        {
            throw new TailwindCliResolutionException(
                TailwindCliResolutionFailure.InvalidCache,
                "The Tailwind download cache cannot provide the required local filesystem semantics.",
                rid,
                _manifest.Version,
                ex);
        }
    }

    /// <summary>
    /// Resolves one trusted explicit CLI path without loading a manifest or touching the host cache.
    /// </summary>
    /// <param name="explicitCliPath">The configured absolute or base-directory-relative CLI path.</param>
    /// <param name="baseDirectory">The base directory for a relative explicit path.</param>
    /// <returns>The validated explicit CLI path.</returns>
    internal static TailwindResolvedCli ResolveExplicitPath(string explicitCliPath, string baseDirectory)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(explicitCliPath, baseDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new TailwindCliResolutionException(
                TailwindCliResolutionFailure.InvalidCliPath,
                "The explicit Tailwind CLI path is invalid.",
                innerException: ex);
        }

        if (!File.Exists(fullPath))
        {
            throw new TailwindCliResolutionException(
                TailwindCliResolutionFailure.InvalidCliPath,
                "The explicit Tailwind CLI path does not exist.");
        }

        return new TailwindResolvedCli(fullPath, "explicit", "explicit", TailwindCliCacheState.Explicit);
    }

    private async Task<TailwindResolvedCli> AcquireAsync(
        string cacheRoot,
        string finalPath,
        TailwindReleaseAsset asset,
        CancellationToken cancellationToken)
    {
        var entryDirectory = Path.GetDirectoryName(finalPath)
            ?? throw new IOException("The Tailwind cache entry does not have a parent directory.");
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(entryDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSafeCachePath(cacheRoot, finalPath);

        await using var lockStream = await AcquireLockAsync(finalPath + ".lock", asset.Rid, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (_isVerifiedFinal(finalPath, asset.Sha256))
        {
            return new TailwindResolvedCli(finalPath, asset.Rid, _manifest.Version, TailwindCliCacheState.Reused);
        }

        string? rejectedPath = null;
        if (File.Exists(finalPath))
        {
            rejectedPath = finalPath + ".rejected-" + Guid.NewGuid().ToString("N");
            File.Move(finalPath, rejectedPath);
        }

        var partialPath = finalPath + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            var sums = await DownloadSmallPayloadWithRetryAsync(new Uri(_manifest.BaseUri, "sha256sums.txt"), asset.Rid, cancellationToken);
            var upstreamDigest = ParseChecksum(sums, asset.BinaryName);
            if (!string.Equals(upstreamDigest, asset.Sha256, StringComparison.Ordinal))
            {
                throw new TailwindCliResolutionException(
                    TailwindCliResolutionFailure.ChecksumFailure,
                    "The official Tailwind checksum does not match the package-pinned digest.",
                    asset.Rid,
                    _manifest.Version);
            }

            var actualDigest = await DownloadBinaryWithRetryAsync(
                new Uri(_manifest.BaseUri, asset.BinaryName),
                partialPath,
                asset.Rid,
                cancellationToken);
            if (!string.Equals(actualDigest, asset.Sha256, StringComparison.Ordinal))
            {
                throw new TailwindCliResolutionException(
                    TailwindCliResolutionFailure.ChecksumFailure,
                    "The downloaded Tailwind executable does not match the package-pinned digest.",
                    asset.Rid,
                    _manifest.Version);
            }

            cancellationToken.ThrowIfCancellationRequested();
            SetUnixExecutableBit(partialPath);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(partialPath, finalPath, overwrite: true);
            cancellationToken.ThrowIfCancellationRequested();

            if (!_isVerifiedFinal(finalPath, asset.Sha256))
            {
                throw new TailwindCliResolutionException(
                    TailwindCliResolutionFailure.InvalidCache,
                    "The Tailwind cache entry was not verified after publication.",
                    asset.Rid,
                    _manifest.Version);
            }

            return new TailwindResolvedCli(finalPath, asset.Rid, _manifest.Version, TailwindCliCacheState.Acquired);
        }
        finally
        {
            TryDeleteOwnedArtifact(partialPath);
            if (rejectedPath is not null)
            {
                TryDeleteOwnedArtifact(rejectedPath);
            }
        }
    }

    private async Task<FileStream> AcquireLockAsync(string lockPath, string rid, CancellationToken cancellationToken)
    {
        const int retries = 4;
        const int retryDelayMilliseconds = 5000;

        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return OpenSafeLockFile(lockPath);
            }
            catch (IOException) when (attempt < retries)
            {
                await _delay(TimeSpan.FromMilliseconds(retryDelayMilliseconds), cancellationToken);
            }
            catch (IOException ex)
            {
                throw new TailwindCliResolutionException(
                    TailwindCliResolutionFailure.LockTimeout,
                    "Tailwind CLI acquisition could not obtain its cache-entry lock.",
                    rid,
                    _manifest.Version,
                    ex);
            }
        }

    }

    private FileStream OpenSafeLockFile(string lockPath)
    {
        if (!File.Exists(lockPath))
        {
            return new FileStream(lockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        }

        if (IsLink(lockPath))
        {
            throw new TailwindCliResolutionException(
                TailwindCliResolutionFailure.InvalidCache,
                "The Tailwind cache lock is a symbolic link or reparse point.");
        }

        var lockStream = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        _afterLockOpened?.Invoke(lockPath);
        if (IsLink(lockPath))
        {
            lockStream.Dispose();
            throw new TailwindCliResolutionException(
                TailwindCliResolutionFailure.InvalidCache,
                "The Tailwind cache lock became a symbolic link or reparse point.");
        }

        return lockStream;
    }

    private async Task<byte[]> DownloadSmallPayloadWithRetryAsync(Uri uri, string rid, CancellationToken cancellationToken)
    {
        const int retries = 4;
        const int retryDelayMilliseconds = 5000;
        Exception? lastException = null;

        for (var attempt = 0; attempt <= retries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var payload = _downloadOverride is null
                    ? await DownloadSmallPayloadAsync(uri, cancellationToken)
                    : await _downloadOverride(uri, cancellationToken);
                if (payload.Length > MaximumChecksumBytes)
                {
                    throw new IOException("The official Tailwind checksum response exceeded the supported size limit.");
                }

                return payload;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                lastException = ex;
                if (attempt < retries)
                {
                    await _delay(TimeSpan.FromMilliseconds(retryDelayMilliseconds), cancellationToken);
                }
            }
        }

        throw new TailwindCliResolutionException(
            TailwindCliResolutionFailure.RetryExhausted,
            "Tailwind CLI acquisition exhausted its official release download retries.",
            rid,
            _manifest.Version,
            lastException);
    }

    private async Task<string> DownloadBinaryWithRetryAsync(
        Uri uri,
        string destinationPath,
        string rid,
        CancellationToken cancellationToken)
    {
        const int retries = 4;
        const int retryDelayMilliseconds = 5000;
        Exception? lastException = null;

        for (var attempt = 0; attempt <= retries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (_downloadOverride is not null)
                {
                    var bytes = await _downloadOverride(uri, cancellationToken);
                    if (bytes.LongLength > _maximumBinaryBytes)
                    {
                        throw new IOException("The Tailwind executable response exceeded the supported size limit.");
                    }

                    await WriteBinaryBytesToNewFileAsync(destinationPath, bytes, cancellationToken);
                    return GetDigest(destinationPath);
                }

                return await DownloadBinaryToFileAsync(uri, destinationPath, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                lastException = ex;
                TryDeleteOwnedArtifact(destinationPath);
                if (attempt < retries)
                {
                    await _delay(TimeSpan.FromMilliseconds(retryDelayMilliseconds), cancellationToken);
                }
            }
        }

        throw new TailwindCliResolutionException(
            TailwindCliResolutionFailure.RetryExhausted,
            "Tailwind CLI acquisition exhausted its official release download retries.",
            rid,
            _manifest.Version,
            lastException);
    }

    private async Task<byte[]> DownloadSmallPayloadAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumChecksumBytes)
        {
            throw new IOException("The official Tailwind checksum response exceeded the supported size limit.");
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            int bytesRead;
            while ((bytesRead = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) != 0)
            {
                if (output.Length + bytesRead > MaximumChecksumBytes)
                {
                    throw new IOException("The official Tailwind checksum response exceeded the supported size limit.");
                }

                await output.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return output.ToArray();
    }

    private async Task<string> DownloadBinaryToFileAsync(Uri uri, string destinationPath, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long contentLength && contentLength > _maximumBinaryBytes)
        {
            throw new IOException("The Tailwind executable response exceeded the supported size limit.");
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        _afterPartialOpened?.Invoke(destinationPath);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            long totalBytes = 0;
            int bytesRead;
            while ((bytesRead = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) != 0)
            {
                totalBytes += bytesRead;
                if (totalBytes > _maximumBinaryBytes)
                {
                    throw new IOException("The Tailwind executable response exceeded the supported size limit.");
                }

                hash.AppendData(buffer, 0, bytesRead);
                await output.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        await output.FlushAsync(cancellationToken);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private async Task WriteBinaryBytesToNewFileAsync(string destinationPath, byte[] bytes, CancellationToken cancellationToken)
    {
        await using var output = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        _afterPartialOpened?.Invoke(destinationPath);
        await output.WriteAsync(bytes, cancellationToken);
        await output.FlushAsync(cancellationToken);
    }

    private static string? ResolveCacheRoot(string? configuredRoot, Func<string, string?>? getEnvironmentVariable = null)
    {
        var root = string.IsNullOrWhiteSpace(configuredRoot)
            ? TailwindDownloadCache.GetDefaultRoot(getEnvironmentVariable)
            : configuredRoot;
        return string.IsNullOrWhiteSpace(root) ? null : Path.GetFullPath(root);
    }

    private static string ParseChecksum(byte[] sumsBytes, string binaryName)
    {
        string? match = null;
        foreach (var line in Encoding.UTF8.GetString(sumsBytes).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var tokens = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2)
            {
                continue;
            }

            var fileName = tokens[1].TrimStart('*');
            if (fileName.StartsWith("./", StringComparison.Ordinal))
            {
                fileName = fileName[2..];
            }

            if (!string.Equals(fileName, binaryName, StringComparison.Ordinal))
            {
                continue;
            }

            if (!IsLowercaseSha256(tokens[0]) || match is not null)
            {
                throw new TailwindCliResolutionException(
                    TailwindCliResolutionFailure.ChecksumFailure,
                    "The official Tailwind checksum file has a malformed or duplicate matching entry.");
            }

            match = tokens[0];
        }

        return match ?? throw new TailwindCliResolutionException(
            TailwindCliResolutionFailure.ChecksumFailure,
            "The official Tailwind checksum file does not contain the selected executable.");
    }

    private static bool IsVerifiedFinal(string path, string expectedDigest)
    {
        return File.Exists(path) && !IsLink(path) && MatchesDigest(path, expectedDigest);
    }

    private static bool MatchesDigest(string path, string expectedDigest)
    {
        var actual = GetDigest(path);
        return string.Equals(actual, expectedDigest, StringComparison.Ordinal);
    }

    private static string GetDigest(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void EnsureSafeCachePath(string cacheRoot, string finalPath)
    {
        var root = Path.GetFullPath(cacheRoot);
        var entryDirectory = Path.GetDirectoryName(finalPath)
            ?? throw new IOException("The Tailwind cache entry does not have a parent directory.");

        foreach (var directory in EnumerateExistingDirectories(root, entryDirectory))
        {
            if (IsLink(directory))
            {
                throw new TailwindCliResolutionException(
                    TailwindCliResolutionFailure.InvalidCache,
                    "The Tailwind cache root contains a symbolic link or reparse point.");
            }
        }

        if (File.Exists(finalPath) && IsLink(finalPath))
        {
            throw new TailwindCliResolutionException(
                TailwindCliResolutionFailure.InvalidCache,
                "The Tailwind cache executable is a symbolic link or reparse point.");
        }
    }

    private static IEnumerable<string> EnumerateExistingDirectories(string root, string entryDirectory)
    {
        var relativeEntryDirectory = Path.GetRelativePath(root, entryDirectory);
        if (relativeEntryDirectory == ".."
            || relativeEntryDirectory.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(relativeEntryDirectory))
        {
            throw new TailwindCliResolutionException(
                TailwindCliResolutionFailure.InvalidCache,
                "The Tailwind cache entry escaped its configured cache root.");
        }

        var current = Path.GetFullPath(root);
        if (Directory.Exists(current))
        {
            yield return current;
        }

        foreach (var segment in relativeEntryDirectory.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Join(current, segment);
            if (Directory.Exists(current))
            {
                yield return current;
            }
        }
    }

    private static bool IsLink(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            return true;
        }

        return Directory.Exists(path)
            ? new DirectoryInfo(path).LinkTarget is not null
            : new FileInfo(path).LinkTarget is not null;
    }

    private void SetUnixExecutableBit(string path)
    {
        if (OperatingSystem.IsWindows() || _isWindows())
        {
            return;
        }

        var mode = File.GetUnixFileMode(path);
        File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
    }

    private static bool IsLowercaseSha256(string value)
    {
        return value.Length == 64 && value.All(static character => char.IsAsciiHexDigit(character) && !char.IsUpper(character));
    }

    private void TryDeleteOwnedArtifact(string path)
    {
        try
        {
            _deleteFile(path);
        }
        catch (IOException)
        {
            // Cleanup cannot invalidate a verified final binary or hide the original failure.
        }
        catch (UnauthorizedAccessException)
        {
            // Cleanup cannot invalidate a verified final binary or hide the original failure.
        }
    }
}

/// <summary>Contains one build or watch resolver request.</summary>
/// <param name="ExplicitCliPath">An optional explicit executable path.</param>
/// <param name="ExplicitPathBaseDirectory">The directory used to resolve relative explicit paths.</param>
/// <param name="CacheRoot">An optional configured cache root.</param>
/// <param name="TailwindVersion">The version supplied by the package targets.</param>
/// <param name="RidOverride">An internal test-only host RID override.</param>
internal sealed record TailwindCliResolverOptions(
    string? ExplicitCliPath,
    string ExplicitPathBaseDirectory,
    string? CacheRoot,
    string? TailwindVersion,
    string? RidOverride = null);
