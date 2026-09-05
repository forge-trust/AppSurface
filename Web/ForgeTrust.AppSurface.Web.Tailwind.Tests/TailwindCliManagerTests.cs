using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FakeItEasy;
using ForgeTrust.AppSurface.Web.Tailwind.Internal;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.AppSurface.Web.Tailwind.Tests;

[Collection(nameof(TailwindCliManagerStaticStateCollection))]
public sealed class TailwindCliManagerTests : IDisposable
{
    private static readonly string[] SupportedRids = ["linux-x64", "linux-arm64", "osx-x64", "osx-arm64", "win-x64"];
    private readonly string _tempRoot = Path.Join(Path.GetTempPath(), "tailwind-cli-manager-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ILogger<TailwindCliManager> _logger = A.Fake<ILogger<TailwindCliManager>>();
    private readonly string? _originalPath = Environment.GetEnvironmentVariable("PATH");

    public TailwindCliManagerTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        TailwindCliManager.IsOSPlatformOverride = null;
        TailwindCliManager.ProcessArchitectureOverride = null;
        Environment.SetEnvironmentVariable("PATH", _originalPath);
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Fact]
    public void ReleaseManifest_ParsesPinnedFiveAssetContract()
    {
        var manifest = TailwindReleaseManifest.LoadFromFile(GetRepositoryManifestPath());

        Assert.Equal("4.1.18", manifest.Version);
        Assert.Equal("https", manifest.BaseUri.Scheme);
        Assert.Equal(
            "/tailwindlabs/tailwindcss/releases/download/v4.1.18/tailwindcss-linux-x64",
            new Uri(manifest.BaseUri, "tailwindcss-linux-x64").AbsolutePath);
        foreach (var rid in SupportedRids)
        {
            var asset = manifest.GetAsset(rid);
            Assert.Equal(TailwindRuntimeMap.GetRuntimeBinaryName(rid), asset.BinaryName);
            Assert.Matches("^[0-9a-f]{64}$", asset.Sha256);
        }
    }

    [Theory]
    [InlineData("4.1.18", true)]
    [InlineData("04.1.18", false)]
    [InlineData("4.1.18-preview", false)]
    [InlineData("4.1.18 ", false)]
    [InlineData("4.1.2147483648", false)]
    public void ReleaseManifest_CanonicalVersionContract_IsEnforced(string version, bool expected)
    {
        Assert.Equal(expected, TailwindReleaseManifest.IsCanonicalStableVersion(version));
    }

    [Fact]
    public void ReleaseManifest_EmbeddedCopyMatchesThePackedSourceManifest()
    {
        var assembly = typeof(TailwindCliManager).Assembly;
        var resourceName = Assert.Single(
            assembly.GetManifestResourceNames(),
            static name => name.EndsWith(".tailwind.release.json", StringComparison.Ordinal));
        var stream = assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var embeddedText = reader.ReadToEnd();

        Assert.Equal(File.ReadAllText(GetRepositoryManifestPath()), embeddedText);
        Assert.Equal("4.1.18", TailwindReleaseManifest.LoadEmbedded(assembly).Version);
    }

    [Fact]
    public void ReleaseManifest_RejectsAnIncompleteOrUntrustedContract()
    {
        var path = Path.Join(_tempRoot, "invalid.release.json");
        File.WriteAllText(path, "{\"schemaVersion\":1,\"version\":\"4.1.18\",\"baseUrl\":\"http://example.test\",\"assets\":[]}");

        var exception = Assert.Throws<InvalidDataException>(() => TailwindReleaseManifest.LoadFromFile(path));

        Assert.Contains("HTTPS", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolver_ExplicitPath_BypassesManifestCacheAndNetwork()
    {
        var explicitPath = Path.Join(_tempRoot, "tools", "tailwindcss");
        Directory.CreateDirectory(Path.GetDirectoryName(explicitPath)!);
        await File.WriteAllTextAsync(explicitPath, "custom executable");
        var downloadCalls = 0;
        var resolver = new TailwindCliResolver(
            TailwindReleaseManifest.LoadFromFile(GetRepositoryManifestPath()),
            (_, _) =>
            {
                downloadCalls++;
                return Task.FromResult(Array.Empty<byte>());
            });

        var resolved = await resolver.ResolveAsync(
            new TailwindCliResolverOptions(explicitPath, _tempRoot, Path.Join(_tempRoot, "cache"), null, "unknown"),
            CancellationToken.None);

        Assert.Equal(Path.GetFullPath(explicitPath), resolved.Path);
        Assert.Equal(TailwindCliCacheState.Explicit, resolved.CacheState);
        Assert.Equal(0, downloadCalls);
    }

    [Fact]
    public async Task Resolver_AcquiresThenReusesOnlyThePinnedHostEntry()
    {
        var payload = Encoding.UTF8.GetBytes("verified test executable");
        var manifestPath = WriteControlledManifest(payload);
        var manifest = TailwindReleaseManifest.LoadFromFile(manifestPath);
        var rid = "linux-x64";
        var asset = manifest.GetAsset(rid);
        var cacheRoot = Path.Join(_tempRoot, "cache");
        var downloadCalls = 0;
        var resolver = new TailwindCliResolver(manifest, (uri, _) =>
        {
            downloadCalls++;
            return Task.FromResult(CreateDownload(uri, asset, payload));
        });
        var options = new TailwindCliResolverOptions(null, _tempRoot, cacheRoot, manifest.Version, rid);

        var acquired = await resolver.ResolveAsync(options, CancellationToken.None);
        var reused = await resolver.ResolveAsync(options, CancellationToken.None);

        Assert.Equal(TailwindCliCacheState.Acquired, acquired.CacheState);
        Assert.Equal(TailwindCliCacheState.Reused, reused.CacheState);
        Assert.Equal(2, downloadCalls);
        Assert.Equal(
            TailwindDownloadCache.GetRuntimeBinaryPath(cacheRoot, manifest.Version, rid, asset.BinaryName),
            acquired.Path);
        Assert.True(File.Exists(acquired.Path));
    }

    [Fact]
    public async Task Resolver_AcquiresThroughTheProductionHttpPipeline()
    {
        var payload = Encoding.UTF8.GetBytes("http pipeline executable");
        var manifest = TailwindReleaseManifest.LoadFromFile(WriteControlledManifest(payload));
        var asset = manifest.GetAsset("linux-x64");
        var requests = new List<string>();
        using var client = new HttpClient(new QueueHttpMessageHandler(request =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            return request.RequestUri.AbsolutePath.EndsWith("sha256sums.txt", StringComparison.Ordinal)
                ? CreateHttpResponse(HttpStatusCode.OK, CreateChecksums(asset, payload))
                : CreateHttpResponse(HttpStatusCode.OK, payload);
        }));
        var resolver = new TailwindCliResolver(manifest, httpClient: client);

        var resolved = await resolver.ResolveAsync(
            new TailwindCliResolverOptions(null, _tempRoot, Path.Join(_tempRoot, "cache"), manifest.Version, asset.Rid),
            CancellationToken.None);

        Assert.Equal(TailwindCliCacheState.Acquired, resolved.CacheState);
        Assert.Equal(payload, await File.ReadAllBytesAsync(resolved.Path));
        Assert.Equal(["/tailwind/sha256sums.txt", "/tailwind/tailwindcss-linux-x64"], requests);
    }

    [Fact]
    public async Task Resolver_RetriesTransientChecksumFailureThroughTheProductionHttpPipeline()
    {
        var payload = Encoding.UTF8.GetBytes("http retry executable");
        var manifest = TailwindReleaseManifest.LoadFromFile(WriteControlledManifest(payload));
        var asset = manifest.GetAsset("linux-x64");
        var checksumAttempts = 0;
        var delayCalls = 0;
        using var client = new HttpClient(new QueueHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("sha256sums.txt", StringComparison.Ordinal))
            {
                checksumAttempts++;
                return checksumAttempts == 1
                    ? CreateHttpResponse(HttpStatusCode.ServiceUnavailable, Array.Empty<byte>())
                    : CreateHttpResponse(HttpStatusCode.OK, CreateChecksums(asset, payload));
            }

            return CreateHttpResponse(HttpStatusCode.OK, payload);
        }));
        var resolver = new TailwindCliResolver(
            manifest,
            delay: (_, _) =>
            {
                delayCalls++;
                return Task.CompletedTask;
            },
            httpClient: client);

        var resolved = await resolver.ResolveAsync(
            new TailwindCliResolverOptions(null, _tempRoot, Path.Join(_tempRoot, "cache"), manifest.Version, asset.Rid),
            CancellationToken.None);

        Assert.Equal(TailwindCliCacheState.Acquired, resolved.CacheState);
        Assert.Equal(2, checksumAttempts);
        Assert.Equal(1, delayCalls);
    }

    [Fact]
    public async Task Resolver_RetriesTransientBinaryFailureThroughTheProductionHttpPipeline()
    {
        var payload = Encoding.UTF8.GetBytes("http binary retry executable");
        var manifest = TailwindReleaseManifest.LoadFromFile(WriteControlledManifest(payload));
        var asset = manifest.GetAsset("linux-x64");
        var binaryAttempts = 0;
        var delayCalls = 0;
        using var client = new HttpClient(new QueueHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("sha256sums.txt", StringComparison.Ordinal))
            {
                return CreateHttpResponse(HttpStatusCode.OK, CreateChecksums(asset, payload));
            }

            binaryAttempts++;
            return binaryAttempts == 1
                ? CreateHttpResponse(HttpStatusCode.ServiceUnavailable, Array.Empty<byte>())
                : CreateHttpResponse(HttpStatusCode.OK, payload);
        }));
        var resolver = new TailwindCliResolver(
            manifest,
            delay: (_, _) =>
            {
                delayCalls++;
                return Task.CompletedTask;
            },
            httpClient: client);

        var resolved = await resolver.ResolveAsync(
            new TailwindCliResolverOptions(null, _tempRoot, Path.Join(_tempRoot, "cache"), manifest.Version, asset.Rid),
            CancellationToken.None);

        Assert.Equal(TailwindCliCacheState.Acquired, resolved.CacheState);
        Assert.Equal(2, binaryAttempts);
        Assert.Equal(1, delayCalls);
    }

    [Fact]
    public async Task Resolver_RejectsOversizedChecksumResponsesThroughTheProductionHttpPipeline()
    {
        var payload = Encoding.UTF8.GetBytes("http oversized checksum executable");
        var manifest = TailwindReleaseManifest.LoadFromFile(WriteControlledManifest(payload));
        var asset = manifest.GetAsset("linux-x64");
        var checksumAttempts = 0;
        var delayCalls = 0;
        using var client = new HttpClient(new QueueHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("sha256sums.txt", StringComparison.Ordinal))
            {
                checksumAttempts++;
                return CreateOversizedHttpResponse(1024 * 1024 + 1L);
            }

            return CreateHttpResponse(HttpStatusCode.OK, payload);
        }));
        var resolver = new TailwindCliResolver(
            manifest,
            delay: (_, _) =>
            {
                delayCalls++;
                return Task.CompletedTask;
            },
            httpClient: client);

        var exception = await Assert.ThrowsAsync<TailwindCliResolutionException>(() => resolver.ResolveAsync(
            new TailwindCliResolverOptions(null, _tempRoot, Path.Join(_tempRoot, "cache"), manifest.Version, asset.Rid),
            CancellationToken.None));

        Assert.Equal(TailwindCliResolutionFailure.RetryExhausted, exception.Failure);
        Assert.Equal(5, checksumAttempts);
        Assert.Equal(4, delayCalls);
    }

    [Fact]
    public async Task Resolver_RejectsInvalidCacheAndExplicitPathInputsBeforeAcquisition()
    {
        var resolver = new TailwindCliResolver(TailwindReleaseManifest.LoadFromFile(GetRepositoryManifestPath()));

        var cacheException = await Assert.ThrowsAsync<TailwindCliResolutionException>(() => resolver.ResolveAsync(
            new TailwindCliResolverOptions(null, _tempRoot, "\0", "4.1.18", "linux-x64"),
            CancellationToken.None));
        var pathException = Assert.Throws<TailwindCliResolutionException>(() =>
            TailwindCliResolver.ResolveExplicitPath("\0", _tempRoot));

        Assert.Equal(TailwindCliResolutionFailure.InvalidCache, cacheException.Failure);
        Assert.Equal(TailwindCliResolutionFailure.InvalidCliPath, pathException.Failure);
    }

    [Fact]
    public async Task Resolver_ReusesACacheEntryPublishedWhileItWaitsForTheEntryLock()
    {
        var payload = Encoding.UTF8.GetBytes("lock publication executable");
        var manifest = TailwindReleaseManifest.LoadFromFile(WriteControlledManifest(payload));
        var asset = manifest.GetAsset("linux-x64");
        var cacheRoot = Path.Join(_tempRoot, "cache");
        var finalPath = TailwindDownloadCache.GetRuntimeBinaryPath(cacheRoot, manifest.Version, asset.Rid, asset.BinaryName);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        using var heldLock = new FileStream(finalPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var lockReleased = false;
        var resolver = new TailwindCliResolver(
            manifest,
            delay: (_, _) =>
            {
                File.WriteAllBytes(finalPath, payload);
                heldLock.Dispose();
                lockReleased = true;
                return Task.CompletedTask;
            });

        var resolved = await resolver.ResolveAsync(
            new TailwindCliResolverOptions(null, _tempRoot, cacheRoot, manifest.Version, asset.Rid),
            CancellationToken.None);

        Assert.True(lockReleased);
        Assert.Equal(TailwindCliCacheState.Reused, resolved.CacheState);
        Assert.Equal(payload, await File.ReadAllBytesAsync(resolved.Path));
    }

    [Fact]
    public async Task Resolver_RejectsBinaryWhenDownloadedSumsDoNotMatchPinnedDigest()
    {
        var payload = Encoding.UTF8.GetBytes("untrusted executable");
        var manifestPath = WriteControlledManifest(Encoding.UTF8.GetBytes("trusted executable"));
        var manifest = TailwindReleaseManifest.LoadFromFile(manifestPath);
        var asset = manifest.GetAsset("linux-x64");
        var resolver = new TailwindCliResolver(manifest, (uri, _) =>
        {
            var sums = $"{Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant()}  ./{asset.BinaryName}\n";
            return Task.FromResult(uri.AbsolutePath.EndsWith("sha256sums.txt", StringComparison.Ordinal)
                ? Encoding.UTF8.GetBytes(sums)
                : payload);
        });
        var cacheRoot = Path.Join(_tempRoot, "cache");

        var exception = await Assert.ThrowsAsync<TailwindCliResolutionException>(() => resolver.ResolveAsync(
            new TailwindCliResolverOptions(null, _tempRoot, cacheRoot, manifest.Version, "linux-x64"),
            CancellationToken.None));

        Assert.Equal(TailwindCliResolutionFailure.ChecksumFailure, exception.Failure);
        Assert.Empty(Directory.EnumerateFiles(cacheRoot, "*.partial-*", SearchOption.AllDirectories));
        Assert.False(File.Exists(TailwindDownloadCache.GetRuntimeBinaryPath(cacheRoot, manifest.Version, "linux-x64", asset.BinaryName)));
    }

    [Fact]
    public async Task Resolver_RejectsDownloadedBinaryWhenThePinnedChecksumDoesNotMatch()
    {
        var trustedPayload = Encoding.UTF8.GetBytes("trusted binary checksum");
        var untrustedPayload = Encoding.UTF8.GetBytes("untrusted binary checksum");
        var manifest = TailwindReleaseManifest.LoadFromFile(WriteControlledManifest(trustedPayload));
        var asset = manifest.GetAsset("linux-x64");
        var resolver = new TailwindCliResolver(manifest, (uri, _) => Task.FromResult(
            uri.AbsolutePath.EndsWith("sha256sums.txt", StringComparison.Ordinal)
                ? Encoding.UTF8.GetBytes($"{asset.Sha256}  ./{asset.BinaryName}\n")
                : untrustedPayload));

        var exception = await Assert.ThrowsAsync<TailwindCliResolutionException>(() => resolver.ResolveAsync(
            new TailwindCliResolverOptions(null, _tempRoot, Path.Join(_tempRoot, "cache"), manifest.Version, asset.Rid),
            CancellationToken.None));

        Assert.Equal(TailwindCliResolutionFailure.ChecksumFailure, exception.Failure);
    }

    [Fact]
    public async Task Resolver_RejectsAnEntryThatFailsPostPublicationVerification()
    {
        var payload = Encoding.UTF8.GetBytes("post-publication verification executable");
        var manifest = TailwindReleaseManifest.LoadFromFile(WriteControlledManifest(payload));
        var asset = manifest.GetAsset("linux-x64");
        var resolver = new TailwindCliResolver(
            manifest,
            (uri, _) => Task.FromResult(CreateDownload(uri, asset, payload)),
            isVerifiedFinal: static (_, _) => false);

        var exception = await Assert.ThrowsAsync<TailwindCliResolutionException>(() => resolver.ResolveAsync(
            new TailwindCliResolverOptions(null, _tempRoot, Path.Join(_tempRoot, "cache"), manifest.Version, asset.Rid),
            CancellationToken.None));

        Assert.Equal(TailwindCliResolutionFailure.InvalidCache, exception.Failure);
        Assert.Contains("not verified after publication", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Resolver_PreservesTheOriginalFailureWhenOwnedArtifactCleanupFails(bool unauthorized)
    {
        var payload = Encoding.UTF8.GetBytes("cleanup failure executable");
        var manifest = TailwindReleaseManifest.LoadFromFile(WriteControlledManifest(payload));
        var asset = manifest.GetAsset("linux-x64");
        var resolver = new TailwindCliResolver(
            manifest,
            (uri, _) => Task.FromResult(CreateDownload(uri, asset, payload)),
            isVerifiedFinal: static (_, _) => false,
            deleteFile: _ =>
            {
                if (unauthorized)
                {
                    throw new UnauthorizedAccessException("cleanup denied");
                }

                throw new IOException("cleanup locked");
            });

        var exception = await Assert.ThrowsAsync<TailwindCliResolutionException>(() => resolver.ResolveAsync(
            new TailwindCliResolverOptions(null, _tempRoot, Path.Join(_tempRoot, "cache"), manifest.Version, asset.Rid),
            CancellationToken.None));

        Assert.Equal(TailwindCliResolutionFailure.InvalidCache, exception.Failure);
    }

    [Fact]
    public async Task Resolver_SkipsUnixModeChangesForWindowsHosts()
    {
        var payload = Encoding.UTF8.GetBytes("windows mode executable");
        var manifest = TailwindReleaseManifest.LoadFromFile(WriteControlledManifest(payload));
        var asset = manifest.GetAsset("linux-x64");
        var resolver = new TailwindCliResolver(
            manifest,
            (uri, _) => Task.FromResult(CreateDownload(uri, asset, payload)),
            isWindows: static () => true);

        var resolved = await resolver.ResolveAsync(
            new TailwindCliResolverOptions(null, _tempRoot, Path.Join(_tempRoot, "cache"), manifest.Version, asset.Rid),
            CancellationToken.None);

        Assert.Equal(TailwindCliCacheState.Acquired, resolved.CacheState);
    }

    [Fact]
    public async Task Resolver_RejectsUnknownHostOnlyWhenNoExplicitPathWasSupplied()
    {
        var resolver = new TailwindCliResolver(TailwindReleaseManifest.LoadFromFile(GetRepositoryManifestPath()));

        var exception = await Assert.ThrowsAsync<TailwindCliResolutionException>(() => resolver.ResolveAsync(
            new TailwindCliResolverOptions(null, _tempRoot, Path.Join(_tempRoot, "cache"), "4.1.18", "unknown"),
            CancellationToken.None));

        Assert.Equal(TailwindCliResolutionFailure.UnsupportedRid, exception.Failure);
    }

    [Theory]
    [InlineData(null, (int)TailwindCliResolutionFailure.MissingVersion)]
    [InlineData("4.1.18-preview", (int)TailwindCliResolutionFailure.InvalidVersion)]
    [InlineData("4.1.19", (int)TailwindCliResolutionFailure.InvalidVersion)]
    public async Task Resolver_RejectsMissingInvalidOrManifestMismatchedVersions(string? version, int expectedFailure)
    {
        var resolver = new TailwindCliResolver(TailwindReleaseManifest.LoadFromFile(GetRepositoryManifestPath()));

        var exception = await Assert.ThrowsAsync<TailwindCliResolutionException>(() => resolver.ResolveAsync(
            new TailwindCliResolverOptions(null, _tempRoot, Path.Join(_tempRoot, "cache"), version, "linux-x64"),
            CancellationToken.None));

        Assert.Equal((TailwindCliResolutionFailure)expectedFailure, exception.Failure);
    }

    [Fact]
    public async Task Resolver_UsesNoCacheRootDiagnosticWithoutStartingADownload()
    {
        var downloadCalls = 0;
        var resolver = new TailwindCliResolver(
            TailwindReleaseManifest.LoadFromFile(GetRepositoryManifestPath()),
            (_, _) =>
            {
                downloadCalls++;
                return Task.FromResult(Array.Empty<byte>());
            },
            _ => null);

        var exception = await Assert.ThrowsAsync<TailwindCliResolutionException>(() => resolver.ResolveAsync(
            new TailwindCliResolverOptions(null, _tempRoot, null, "4.1.18", "linux-x64"),
            CancellationToken.None));

        Assert.Equal(TailwindCliResolutionFailure.NoCacheRoot, exception.Failure);
        Assert.Equal(0, downloadCalls);
    }

    [Fact]
    public async Task Resolver_ReplacesAnUnverifiedCacheEntryOnlyAfterVerifyingTheReplacement()
    {
        var payload = Encoding.UTF8.GetBytes("replacement executable");
        var manifest = TailwindReleaseManifest.LoadFromFile(WriteControlledManifest(payload));
        var asset = manifest.GetAsset("linux-x64");
        var cacheRoot = Path.Join(_tempRoot, "cache");
        var finalPath = TailwindDownloadCache.GetRuntimeBinaryPath(cacheRoot, manifest.Version, asset.Rid, asset.BinaryName);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        await File.WriteAllTextAsync(finalPath, "tampered cache entry");
        var resolver = CreateResolver(manifest, asset, payload);

        var resolved = await resolver.ResolveAsync(
            new TailwindCliResolverOptions(null, _tempRoot, cacheRoot, manifest.Version, asset.Rid),
            CancellationToken.None);

        Assert.Equal(TailwindCliCacheState.Acquired, resolved.CacheState);
        Assert.Equal(payload, await File.ReadAllBytesAsync(finalPath));
        Assert.Empty(Directory.EnumerateFiles(cacheRoot, "*.partial-*", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(cacheRoot, "*.rejected-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Resolver_RemovesARejectedCacheEntryWhenAReplacementFails()
    {
        var trustedPayload = Encoding.UTF8.GetBytes("trusted replacement executable");
        var untrustedPayload = Encoding.UTF8.GetBytes("untrusted replacement executable");
        var manifest = TailwindReleaseManifest.LoadFromFile(WriteControlledManifest(trustedPayload));
        var asset = manifest.GetAsset("linux-x64");
        var cacheRoot = Path.Join(_tempRoot, "cache");
        var finalPath = TailwindDownloadCache.GetRuntimeBinaryPath(cacheRoot, manifest.Version, asset.Rid, asset.BinaryName);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        await File.WriteAllTextAsync(finalPath, "corrupt cache entry");
        var resolver = new TailwindCliResolver(manifest, (uri, _) =>
        {
            var sums = $"{Convert.ToHexString(SHA256.HashData(untrustedPayload)).ToLowerInvariant()}  ./{asset.BinaryName}\n";
            return Task.FromResult(uri.AbsolutePath.EndsWith("sha256sums.txt", StringComparison.Ordinal)
                ? Encoding.UTF8.GetBytes(sums)
                : untrustedPayload);
        });

        var exception = await Assert.ThrowsAsync<TailwindCliResolutionException>(() => resolver.ResolveAsync(
            new TailwindCliResolverOptions(null, _tempRoot, cacheRoot, manifest.Version, asset.Rid),
            CancellationToken.None));

        Assert.Equal(TailwindCliResolutionFailure.ChecksumFailure, exception.Failure);
        Assert.False(File.Exists(finalPath));
        Assert.Empty(Directory.EnumerateFiles(cacheRoot, "*.rejected-*", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(cacheRoot, "*.partial-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Resolver_RetriesTransientOfficialReleaseDownloadsWithABoundedDelay()
    {
        var payload = Encoding.UTF8.GetBytes("retry executable");
        var manifest = TailwindReleaseManifest.LoadFromFile(WriteControlledManifest(payload));
        var asset = manifest.GetAsset("linux-x64");
        var downloadCalls = 0;
        var delayCalls = 0;
        var resolver = new TailwindCliResolver(
            manifest,
            (uri, _) =>
            {
                downloadCalls++;
                if (downloadCalls == 1)
                {
                    throw new HttpRequestException("transient test failure");
                }

                return Task.FromResult(CreateDownload(uri, asset, payload));
            },
            delay: (_, _) =>
            {
                delayCalls++;
                return Task.CompletedTask;
            });

        var resolved = await resolver.ResolveAsync(
            new TailwindCliResolverOptions(null, _tempRoot, Path.Join(_tempRoot, "cache"), manifest.Version, asset.Rid),
            CancellationToken.None);

        Assert.Equal(TailwindCliCacheState.Acquired, resolved.CacheState);
        Assert.Equal(3, downloadCalls);
        Assert.Equal(1, delayCalls);
    }

    [Fact]
    public async Task Resolver_ReportsRetryExhaustionAfterTheBoundedOfficialReleaseAttempts()
    {
        var payload = Encoding.UTF8.GetBytes("retry exhaustion executable");
        var manifest = TailwindReleaseManifest.LoadFromFile(WriteControlledManifest(payload));
        var downloadCalls = 0;
        var delayCalls = 0;
        var resolver = new TailwindCliResolver(
            manifest,
            (_, _) =>
            {
                downloadCalls++;
                throw new IOException("offline test failure");
            },
            delay: (_, _) =>
            {
                delayCalls++;
                return Task.CompletedTask;
            });

        var exception = await Assert.ThrowsAsync<TailwindCliResolutionException>(() => resolver.ResolveAsync(
            new TailwindCliResolverOptions(null, _tempRoot, Path.Join(_tempRoot, "cache"), manifest.Version, "linux-x64"),
            CancellationToken.None));

        Assert.Equal(TailwindCliResolutionFailure.RetryExhausted, exception.Failure);
        Assert.Equal(5, downloadCalls);
        Assert.Equal(4, delayCalls);
    }

    [Fact]
    public async Task Resolver_ReportsLockTimeoutAfterAllBoundedLockAttempts()
    {
        var payload = Encoding.UTF8.GetBytes("lock timeout executable");
        var manifest = TailwindReleaseManifest.LoadFromFile(WriteControlledManifest(payload));
        var asset = manifest.GetAsset("linux-x64");
        var cacheRoot = Path.Join(_tempRoot, "cache");
        var finalPath = TailwindDownloadCache.GetRuntimeBinaryPath(cacheRoot, manifest.Version, asset.Rid, asset.BinaryName);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        await using var heldLock = new FileStream(finalPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var delayCalls = 0;
        var resolver = new TailwindCliResolver(
            manifest,
            (_, _) => Task.FromResult(CreateDownload(new Uri("https://example.test/sha256sums.txt"), asset, payload)),
            delay: (_, _) =>
            {
                delayCalls++;
                return Task.CompletedTask;
            });

        var exception = await Assert.ThrowsAsync<TailwindCliResolutionException>(() => resolver.ResolveAsync(
            new TailwindCliResolverOptions(null, _tempRoot, cacheRoot, manifest.Version, asset.Rid),
            CancellationToken.None));

        Assert.Equal(TailwindCliResolutionFailure.LockTimeout, exception.Failure);
        Assert.Equal(4, delayCalls);
        Assert.IsAssignableFrom<IOException>(exception.InnerException);
    }

    [Fact]
    public async Task Resolver_RejectsALockThatBecomesASymbolicLinkAfterOpening()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var payload = Encoding.UTF8.GetBytes("lock race executable");
        var manifest = TailwindReleaseManifest.LoadFromFile(WriteControlledManifest(payload));
        var asset = manifest.GetAsset("linux-x64");
        var cacheRoot = Path.Join(_tempRoot, "cache");
        var finalPath = TailwindDownloadCache.GetRuntimeBinaryPath(cacheRoot, manifest.Version, asset.Rid, asset.BinaryName);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        var lockPath = finalPath + ".lock";
        await File.WriteAllTextAsync(lockPath, "ordinary lock file");
        var linkTarget = Path.Join(_tempRoot, "lock-race-target");
        await File.WriteAllTextAsync(linkTarget, "not a lock");
        var downloadCalls = 0;
        var resolver = new TailwindCliResolver(
            manifest,
            (_, _) =>
            {
                downloadCalls++;
                return Task.FromResult(Array.Empty<byte>());
            },
            afterLockOpened: path =>
            {
                File.Delete(path);
                File.CreateSymbolicLink(path, linkTarget);
            });

        var exception = await Assert.ThrowsAsync<TailwindCliResolutionException>(() => resolver.ResolveAsync(
            new TailwindCliResolverOptions(null, _tempRoot, cacheRoot, manifest.Version, asset.Rid),
            CancellationToken.None));

        Assert.Equal(TailwindCliResolutionFailure.InvalidCache, exception.Failure);
        Assert.Equal(0, downloadCalls);
    }

    [Fact]
    public async Task Resolver_ReportsInvalidCacheWhenTheConfiguredCacheRootIsAFile()
    {
        var payload = Encoding.UTF8.GetBytes("cache root file executable");
        var manifest = TailwindReleaseManifest.LoadFromFile(WriteControlledManifest(payload));
        var cacheRoot = Path.Join(_tempRoot, "cache-root-file");
        await File.WriteAllTextAsync(cacheRoot, "not a directory");
        var resolver = new TailwindCliResolver(manifest);

        var exception = await Assert.ThrowsAsync<TailwindCliResolutionException>(() => resolver.ResolveAsync(
            new TailwindCliResolverOptions(null, _tempRoot, cacheRoot, manifest.Version, "linux-x64"),
            CancellationToken.None));

        Assert.Equal(TailwindCliResolutionFailure.InvalidCache, exception.Failure);
        Assert.IsAssignableFrom<IOException>(exception.InnerException);
    }

    [Fact]
    public async Task Resolver_RejectsCachePathsThatEscapeTheConfiguredRoot()
    {
        var payload = Encoding.UTF8.GetBytes("cache escape executable");
        var manifest = TailwindReleaseManifest.LoadFromFile(WriteControlledManifest(payload));
        var cacheRoot = Path.Join(_tempRoot, "cache");
        var escapedPath = Path.Join(_tempRoot, "outside-cache", "tailwindcss");
        var resolver = new TailwindCliResolver(
            manifest,
            getRuntimeBinaryPath: (_, _, _, _) => escapedPath);

        var exception = await Assert.ThrowsAsync<TailwindCliResolutionException>(() => resolver.ResolveAsync(
            new TailwindCliResolverOptions(null, _tempRoot, cacheRoot, manifest.Version, "linux-x64"),
            CancellationToken.None));

        Assert.Equal(TailwindCliResolutionFailure.InvalidCache, exception.Failure);
        Assert.Contains("escaped its configured cache root", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolver_ReportsANonWritableCacheRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var payload = Encoding.UTF8.GetBytes("non-writable cache executable");
        var manifest = TailwindReleaseManifest.LoadFromFile(WriteControlledManifest(payload));
        var cacheRoot = Path.Join(_tempRoot, "non-writable-cache");
        Directory.CreateDirectory(cacheRoot);
        var originalMode = File.GetUnixFileMode(cacheRoot);
        File.SetUnixFileMode(cacheRoot, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var resolver = new TailwindCliResolver(manifest);

            var exception = await Assert.ThrowsAsync<TailwindCliResolutionException>(() => resolver.ResolveAsync(
                new TailwindCliResolverOptions(null, _tempRoot, cacheRoot, manifest.Version, "linux-x64"),
                CancellationToken.None));

            Assert.Equal(TailwindCliResolutionFailure.NonWritableRoot, exception.Failure);
            Assert.IsType<UnauthorizedAccessException>(exception.InnerException);
        }
        finally
        {
            File.SetUnixFileMode(cacheRoot, originalMode);
        }
    }

    [Fact]
    public async Task Resolver_RejectsOversizedChecksumOverridesBeforeParsing()
    {
        var payload = Encoding.UTF8.GetBytes("oversized checksum override executable");
        var manifest = TailwindReleaseManifest.LoadFromFile(WriteControlledManifest(payload));
        var downloadCalls = 0;
        var delayCalls = 0;
        var oversizedChecksums = new byte[1024 * 1024 + 1];
        var resolver = new TailwindCliResolver(
            manifest,
            (_, _) =>
            {
                downloadCalls++;
                return Task.FromResult(oversizedChecksums);
            },
            delay: (_, _) =>
            {
                delayCalls++;
                return Task.CompletedTask;
            });

        var exception = await Assert.ThrowsAsync<TailwindCliResolutionException>(() => resolver.ResolveAsync(
            new TailwindCliResolverOptions(null, _tempRoot, Path.Join(_tempRoot, "cache"), manifest.Version, "linux-x64"),
            CancellationToken.None));

        Assert.Equal(TailwindCliResolutionFailure.RetryExhausted, exception.Failure);
        Assert.Equal(5, downloadCalls);
        Assert.Equal(4, delayCalls);
    }

    [Fact]
    public async Task Resolver_PropagatesChecksumDownloadCancellationWithoutRetrying()
    {
        var payload = Encoding.UTF8.GetBytes("checksum cancellation executable");
        var manifest = TailwindReleaseManifest.LoadFromFile(WriteControlledManifest(payload));
        var resolver = new TailwindCliResolver(
            manifest,
            (_, _) => Task.FromCanceled<byte[]>(new CancellationToken(canceled: true)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.ResolveAsync(
            new TailwindCliResolverOptions(null, _tempRoot, Path.Join(_tempRoot, "cache"), manifest.Version, "linux-x64"),
            CancellationToken.None));
    }

    [Fact]
    public async Task Resolver_PropagatesBinaryDownloadCancellationWithoutRetrying()
    {
        var payload = Encoding.UTF8.GetBytes("binary cancellation executable");
        var manifest = TailwindReleaseManifest.LoadFromFile(WriteControlledManifest(payload));
        var asset = manifest.GetAsset("linux-x64");
        var resolver = new TailwindCliResolver(
            manifest,
            (uri, _) => uri.AbsolutePath.EndsWith("sha256sums.txt", StringComparison.Ordinal)
                ? Task.FromResult(CreateDownload(uri, asset, payload))
                : Task.FromCanceled<byte[]>(new CancellationToken(canceled: true)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.ResolveAsync(
            new TailwindCliResolverOptions(null, _tempRoot, Path.Join(_tempRoot, "cache"), manifest.Version, asset.Rid),
            CancellationToken.None));
    }

    [Fact]
    public async Task Resolver_RejectsOversizedBinaryResponsesThroughTheProductionHttpPipeline()
    {
        var payload = Encoding.UTF8.GetBytes("oversized binary executable");
        var manifest = TailwindReleaseManifest.LoadFromFile(WriteControlledManifest(payload));
        var asset = manifest.GetAsset("linux-x64");
        var binaryAttempts = 0;
        var delayCalls = 0;
        using var client = new HttpClient(new QueueHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("sha256sums.txt", StringComparison.Ordinal))
            {
                return CreateHttpResponse(HttpStatusCode.OK, CreateChecksums(asset, payload));
            }

            binaryAttempts++;
            return CreateOversizedHttpResponse(9);
        }));
        var resolver = new TailwindCliResolver(
            manifest,
            delay: (_, _) =>
            {
                delayCalls++;
                return Task.CompletedTask;
            },
            httpClient: client,
            maximumBinaryBytes: 8);

        var exception = await Assert.ThrowsAsync<TailwindCliResolutionException>(() => resolver.ResolveAsync(
            new TailwindCliResolverOptions(null, _tempRoot, Path.Join(_tempRoot, "cache"), manifest.Version, asset.Rid),
            CancellationToken.None));

        Assert.Equal(TailwindCliResolutionFailure.RetryExhausted, exception.Failure);
        Assert.Equal(5, binaryAttempts);
        Assert.Equal(4, delayCalls);
    }

    [Fact]
    public async Task Resolver_RejectsOversizedBinaryOverridesBeforeWritingThePartialFile()
    {
        var payload = Encoding.UTF8.GetBytes("oversized binary override executable");
        var manifest = TailwindReleaseManifest.LoadFromFile(WriteControlledManifest(payload));
        var asset = manifest.GetAsset("linux-x64");
        var delayCalls = 0;
        var resolver = new TailwindCliResolver(
            manifest,
            (uri, _) => Task.FromResult(
                uri.AbsolutePath.EndsWith("sha256sums.txt", StringComparison.Ordinal)
                    ? CreateDownload(uri, asset, payload)
                    : new byte[9]),
            delay: (_, _) =>
            {
                delayCalls++;
                return Task.CompletedTask;
            },
            maximumBinaryBytes: 8);

        var exception = await Assert.ThrowsAsync<TailwindCliResolutionException>(() => resolver.ResolveAsync(
            new TailwindCliResolverOptions(null, _tempRoot, Path.Join(_tempRoot, "cache"), manifest.Version, asset.Rid),
            CancellationToken.None));

        Assert.Equal(TailwindCliResolutionFailure.RetryExhausted, exception.Failure);
        Assert.Equal(4, delayCalls);
    }

    [Fact]
    public async Task Resolver_RejectsUnknownLengthBinaryStreamsThatExceedTheSizeLimit()
    {
        var payload = Encoding.UTF8.GetBytes("binary stream executable");
        var manifest = TailwindReleaseManifest.LoadFromFile(WriteControlledManifest(payload));
        var asset = manifest.GetAsset("linux-x64");
        var delayCalls = 0;
        using var client = new HttpClient(new QueueHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("sha256sums.txt", StringComparison.Ordinal))
            {
                return CreateHttpResponse(HttpStatusCode.OK, CreateChecksums(asset, payload));
            }

            return CreateStreamingHttpResponse(new byte[9]);
        }));
        var resolver = new TailwindCliResolver(
            manifest,
            delay: (_, _) =>
            {
                delayCalls++;
                return Task.CompletedTask;
            },
            httpClient: client,
            maximumBinaryBytes: 8);

        var exception = await Assert.ThrowsAsync<TailwindCliResolutionException>(() => resolver.ResolveAsync(
            new TailwindCliResolverOptions(null, _tempRoot, Path.Join(_tempRoot, "cache"), manifest.Version, asset.Rid),
            CancellationToken.None));

        Assert.Equal(TailwindCliResolutionFailure.RetryExhausted, exception.Failure);
        Assert.Equal(4, delayCalls);
    }

    [Fact]
    public async Task Resolver_RejectsChecksumStreamsThatExceedTheSizeLimit()
    {
        var payload = Encoding.UTF8.GetBytes("oversized checksum stream executable");
        var manifest = TailwindReleaseManifest.LoadFromFile(WriteControlledManifest(payload));
        var asset = manifest.GetAsset("linux-x64");
        var delayCalls = 0;
        using var client = new HttpClient(new QueueHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("sha256sums.txt", StringComparison.Ordinal))
            {
                return CreateStreamingHttpResponse(new byte[1024 * 1024 + 1]);
            }

            return CreateHttpResponse(HttpStatusCode.OK, CreateChecksums(asset, payload));
        }));
        var resolver = new TailwindCliResolver(
            manifest,
            delay: (_, _) =>
            {
                delayCalls++;
                return Task.CompletedTask;
            },
            httpClient: client);

        var exception = await Assert.ThrowsAsync<TailwindCliResolutionException>(() => resolver.ResolveAsync(
            new TailwindCliResolverOptions(null, _tempRoot, Path.Join(_tempRoot, "cache"), manifest.Version, asset.Rid),
            CancellationToken.None));

        Assert.Equal(TailwindCliResolutionFailure.RetryExhausted, exception.Failure);
        Assert.Equal(4, delayCalls);
    }

    [Theory]
    [InlineData("not-a-checksum-line", "does not contain")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  other-file", "does not contain")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF  ./tailwindcss-linux-x64", "malformed or duplicate")]
    public async Task Resolver_RejectsMalformedOrMissingSelectedChecksumEntries(string checksumLine, string expectedMessage)
    {
        var payload = Encoding.UTF8.GetBytes("checksum format executable");
        var manifest = TailwindReleaseManifest.LoadFromFile(WriteControlledManifest(payload));
        var asset = manifest.GetAsset("linux-x64");
        var resolver = new TailwindCliResolver(manifest, (uri, _) => Task.FromResult(
            uri.AbsolutePath.EndsWith("sha256sums.txt", StringComparison.Ordinal)
                ? Encoding.UTF8.GetBytes(checksumLine + "\n")
                : payload));

        var exception = await Assert.ThrowsAsync<TailwindCliResolutionException>(() => resolver.ResolveAsync(
            new TailwindCliResolverOptions(null, _tempRoot, Path.Join(_tempRoot, "cache"), manifest.Version, asset.Rid),
            CancellationToken.None));

        Assert.Equal(TailwindCliResolutionFailure.ChecksumFailure, exception.Failure);
        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolver_RejectsDuplicateSelectedChecksumEntries()
    {
        var payload = Encoding.UTF8.GetBytes("duplicate checksum executable");
        var manifest = TailwindReleaseManifest.LoadFromFile(WriteControlledManifest(payload));
        var asset = manifest.GetAsset("linux-x64");
        var checksum = $"{asset.Sha256}  ./{asset.BinaryName}\n{asset.Sha256}  *{asset.BinaryName}\n";
        var resolver = new TailwindCliResolver(manifest, (uri, _) => Task.FromResult(
            uri.AbsolutePath.EndsWith("sha256sums.txt", StringComparison.Ordinal)
                ? Encoding.UTF8.GetBytes(checksum)
                : payload));

        var exception = await Assert.ThrowsAsync<TailwindCliResolutionException>(() => resolver.ResolveAsync(
            new TailwindCliResolverOptions(null, _tempRoot, Path.Join(_tempRoot, "cache"), manifest.Version, asset.Rid),
            CancellationToken.None));

        Assert.Equal(TailwindCliResolutionFailure.ChecksumFailure, exception.Failure);
        Assert.Contains("malformed or duplicate", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolver_HonorsCancellationWhileWaitingForAnEntryLock()
    {
        var payload = Encoding.UTF8.GetBytes("lock executable");
        var manifest = TailwindReleaseManifest.LoadFromFile(WriteControlledManifest(payload));
        var asset = manifest.GetAsset("linux-x64");
        var cacheRoot = Path.Join(_tempRoot, "cache");
        var finalPath = TailwindDownloadCache.GetRuntimeBinaryPath(cacheRoot, manifest.Version, asset.Rid, asset.BinaryName);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        await using var heldLock = new FileStream(finalPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var cancellationTokenSource = new CancellationTokenSource();
        var resolver = new TailwindCliResolver(
            manifest,
            (_, _) => Task.FromResult(CreateDownload(new Uri("https://example.test/sha256sums.txt"), asset, payload)),
            delay: (_, _) =>
            {
                cancellationTokenSource.Cancel();
                return Task.FromCanceled(cancellationTokenSource.Token);
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.ResolveAsync(
            new TailwindCliResolverOptions(null, _tempRoot, cacheRoot, manifest.Version, asset.Rid),
            cancellationTokenSource.Token));
    }

    [Fact]
    public async Task Resolver_RejectsASymbolicLinkCacheEntryWithoutDownloading()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var payload = Encoding.UTF8.GetBytes("linked executable");
        var manifest = TailwindReleaseManifest.LoadFromFile(WriteControlledManifest(payload));
        var asset = manifest.GetAsset("linux-x64");
        var cacheRoot = Path.Join(_tempRoot, "cache");
        var finalPath = TailwindDownloadCache.GetRuntimeBinaryPath(cacheRoot, manifest.Version, asset.Rid, asset.BinaryName);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        var target = Path.Join(_tempRoot, "linked-target");
        await File.WriteAllTextAsync(target, "not trusted");
        File.CreateSymbolicLink(finalPath, target);
        var downloadCalls = 0;
        var resolver = new TailwindCliResolver(
            manifest,
            (_, _) =>
            {
                downloadCalls++;
                return Task.FromResult(Array.Empty<byte>());
            });

        var exception = await Assert.ThrowsAsync<TailwindCliResolutionException>(() => resolver.ResolveAsync(
            new TailwindCliResolverOptions(null, _tempRoot, cacheRoot, manifest.Version, asset.Rid),
            CancellationToken.None));

        Assert.Equal(TailwindCliResolutionFailure.InvalidCache, exception.Failure);
        Assert.Equal(0, downloadCalls);
    }

    [Fact]
    public async Task Resolver_RejectsASymbolicLinkCacheLockWithoutDownloading()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var payload = Encoding.UTF8.GetBytes("locked executable");
        var manifest = TailwindReleaseManifest.LoadFromFile(WriteControlledManifest(payload));
        var asset = manifest.GetAsset("linux-x64");
        var cacheRoot = Path.Join(_tempRoot, "cache");
        var finalPath = TailwindDownloadCache.GetRuntimeBinaryPath(cacheRoot, manifest.Version, asset.Rid, asset.BinaryName);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        var target = Path.Join(_tempRoot, "linked-lock-target");
        await File.WriteAllTextAsync(target, "not a cache lock");
        File.CreateSymbolicLink(finalPath + ".lock", target);
        var downloadCalls = 0;
        var resolver = new TailwindCliResolver(
            manifest,
            (_, _) =>
            {
                downloadCalls++;
                return Task.FromResult(Array.Empty<byte>());
            });

        var exception = await Assert.ThrowsAsync<TailwindCliResolutionException>(() => resolver.ResolveAsync(
            new TailwindCliResolverOptions(null, _tempRoot, cacheRoot, manifest.Version, asset.Rid),
            CancellationToken.None));

        Assert.Equal(TailwindCliResolutionFailure.InvalidCache, exception.Failure);
        Assert.Equal(0, downloadCalls);
    }

    [Fact]
    public async Task Resolver_RejectsASymbolicLinkCacheRootWithoutDownloading()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var payload = Encoding.UTF8.GetBytes("linked cache root executable");
        var manifest = TailwindReleaseManifest.LoadFromFile(WriteControlledManifest(payload));
        var cacheRoot = Path.Join(_tempRoot, "cache-root");
        var target = Path.Join(_tempRoot, "cache-root-target");
        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(cacheRoot, target);
        var downloadCalls = 0;
        var resolver = new TailwindCliResolver(
            manifest,
            (_, _) =>
            {
                downloadCalls++;
                return Task.FromResult(Array.Empty<byte>());
            });

        var exception = await Assert.ThrowsAsync<TailwindCliResolutionException>(() => resolver.ResolveAsync(
            new TailwindCliResolverOptions(null, _tempRoot, cacheRoot, manifest.Version, "linux-x64"),
            CancellationToken.None));

        Assert.Equal(TailwindCliResolutionFailure.InvalidCache, exception.Failure);
        Assert.Equal(0, downloadCalls);
    }

    [Fact]
    public void Manager_UsesDevelopmentPathAfterVerifiedResolutionFailure()
    {
        var pathDirectory = Path.Join(_tempRoot, "path");
        Directory.CreateDirectory(pathDirectory);
        var expected = Path.Join(pathDirectory, TailwindRuntimeMap.GetLocalBinaryName());
        File.WriteAllText(expected, "path shim");
        Environment.SetEnvironmentVariable("PATH", pathDirectory);
        var invalidManifest = Path.Join(_tempRoot, "invalid.release.json");
        File.WriteAllText(invalidManifest, "{}");
        var manager = new TailwindCliManager(_logger)
        {
            ReleaseManifestPathOverride = invalidManifest,
            DownloadCacheRootOverride = Path.Join(_tempRoot, "cache")
        };

        Assert.Equal(expected, manager.GetTailwindPath());
    }

    [Fact]
    public void Manager_UsesVerifiedCacheBeforeMakingItsSingleDevelopmentPathFallback()
    {
        var payload = Encoding.UTF8.GetBytes("manager cache executable");
        var manifestPath = WriteControlledManifest(payload);
        var manifest = TailwindReleaseManifest.LoadFromFile(manifestPath);
        var asset = manifest.GetAsset("linux-x64");
        var pathDirectory = Path.Join(_tempRoot, "path");
        Directory.CreateDirectory(pathDirectory);
        var fallback = Path.Join(pathDirectory, TailwindRuntimeMap.GetLocalBinaryName());
        File.WriteAllText(fallback, "fallback path executable");
        Environment.SetEnvironmentVariable("PATH", pathDirectory);
        var manager = new TailwindCliManager(_logger)
        {
            ReleaseManifestPathOverride = manifestPath,
            DownloadCacheRootOverride = Path.Join(_tempRoot, "cache"),
            RidOverride = asset.Rid,
            DownloadOverride = (uri, _) => Task.FromResult(CreateDownload(uri, asset, payload))
        };

        var resolved = manager.GetTailwindPath();

        Assert.NotEqual(fallback, resolved);
        Assert.Equal(payload, File.ReadAllBytes(resolved));
    }

    [Fact]
    public void Manager_ReportsResolutionFailureWhenPathContainsNoCli()
    {
        var pathDirectory = Path.Join(_tempRoot, "empty-path");
        Directory.CreateDirectory(pathDirectory);
        Environment.SetEnvironmentVariable("PATH", pathDirectory);
        var manager = new TailwindCliManager(_logger)
        {
            ReleaseManifestPathOverride = GetRepositoryManifestPath(),
            DownloadCacheRootOverride = Path.Join(_tempRoot, "cache"),
            RidOverride = "unknown"
        };

        var exception = Assert.Throws<FileNotFoundException>(() => manager.GetTailwindPath());

        Assert.Contains("verified host cache or development PATH", exception.Message, StringComparison.Ordinal);
        var resolutionFailure = Assert.IsType<TailwindCliResolutionException>(exception.InnerException);
        Assert.Equal(TailwindCliResolutionFailure.UnsupportedRid, resolutionFailure.Failure);
    }

    [Fact]
    public void Manager_ReportsResolutionFailureWhenPathIsEmpty()
    {
        Environment.SetEnvironmentVariable("PATH", string.Empty);
        var manager = new TailwindCliManager(_logger)
        {
            ReleaseManifestPathOverride = GetRepositoryManifestPath(),
            DownloadCacheRootOverride = Path.Join(_tempRoot, "cache"),
            RidOverride = "unknown"
        };

        Assert.Throws<FileNotFoundException>(() => manager.GetTailwindPath());
    }

    [Fact]
    public void Manager_LoadsEmbeddedManifestBeforeItsSinglePathFallback()
    {
        Environment.SetEnvironmentVariable("PATH", string.Empty);
        var manager = new TailwindCliManager(_logger)
        {
            BaseDirectoryOverride = _tempRoot,
            DownloadCacheRootOverride = Path.Join(_tempRoot, "cache"),
            RidOverride = "unknown"
        };

        var exception = Assert.Throws<FileNotFoundException>(() => manager.GetTailwindPath());

        var resolutionFailure = Assert.IsType<TailwindCliResolutionException>(exception.InnerException);
        Assert.Equal(TailwindCliResolutionFailure.UnsupportedRid, resolutionFailure.Failure);
    }

    [Fact]
    public void BuildInvocation_UsesCommandPromptForWindowsCommandShim()
    {
        TailwindCliManager.IsOSPlatformOverride = platform => platform == OSPlatform.Windows;

        var invocation = TailwindCliManager.BuildInvocation("C:\\tools\\tailwind.cmd", ["-i", "input.css", "--watch"]);

        Assert.Equal("cmd.exe", invocation.FileName);
        Assert.Equal(["/d", "/c", "C:\\tools\\tailwind.cmd", "-i", "input.css", "--watch"], invocation.Arguments);
    }

    [Fact]
    public void RuntimeMap_PreservesSupportedHostMapping()
    {
        Assert.Equal("win-x64", TailwindCliManager.ResolveRid(OSPlatform.Windows, Architecture.Arm64));
        Assert.Equal("linux-x64", TailwindCliManager.ResolveRid(OSPlatform.Linux, Architecture.X64));
        Assert.Equal("osx-arm64", TailwindCliManager.ResolveRid(OSPlatform.OSX, Architecture.Arm64));
    }

    [Fact]
    public void RuntimeMap_MapsEverySupportedHostAndFailsClosedForUnknownHosts()
    {
        Assert.Equal("win-x64", TailwindRuntimeMap.GetCurrentRid(platform => platform == OSPlatform.Windows, () => Architecture.X64));
        Assert.Equal("win-x64", TailwindRuntimeMap.GetCurrentRid(platform => platform == OSPlatform.Windows, () => Architecture.Arm64));
        Assert.Equal("linux-x64", TailwindRuntimeMap.GetCurrentRid(platform => platform == OSPlatform.Linux, () => Architecture.X64));
        Assert.Equal("linux-arm64", TailwindRuntimeMap.GetCurrentRid(platform => platform == OSPlatform.Linux, () => Architecture.Arm64));
        Assert.Equal("osx-x64", TailwindRuntimeMap.GetCurrentRid(platform => platform == OSPlatform.OSX, () => Architecture.X64));
        Assert.Equal("osx-arm64", TailwindRuntimeMap.GetCurrentRid(platform => platform == OSPlatform.OSX, () => Architecture.Arm64));
        Assert.Equal("unknown", TailwindRuntimeMap.GetCurrentRid(_ => false, () => Architecture.X64));
        Assert.Equal("unknown", TailwindRuntimeMap.ResolveRid(OSPlatform.Linux, Architecture.X86));
        Assert.Null(TailwindRuntimeMap.GetRuntimeBinaryName("unknown"));
        Assert.Equal("tailwindcss.exe", TailwindRuntimeMap.GetLocalBinaryName(platform => platform == OSPlatform.Windows));
        Assert.Equal("tailwindcss", TailwindRuntimeMap.GetLocalBinaryName(_ => false));
    }

    [Fact]
    public void DownloadCache_UsesDocumentedEnvironmentPrecedence()
    {
        Assert.Equal(Path.Join("/xdg", "forgetrust", "appsurface", "tailwind"), GetDefaultCacheRoot(("XDG_CACHE_HOME", "/xdg"), ("LOCALAPPDATA", "/local")));
        Assert.Equal(Path.Join("/local", "ForgeTrust", "AppSurface", "Tailwind"), GetDefaultCacheRoot(("LOCALAPPDATA", "/local"), ("HOME", "/home")));
        Assert.Equal(Path.Join("/home", ".cache", "forgetrust", "appsurface", "tailwind"), GetDefaultCacheRoot(("HOME", "/home"), ("USERPROFILE", "/profile")));
        Assert.Equal(Path.Join("/profile", ".cache", "forgetrust", "appsurface", "tailwind"), GetDefaultCacheRoot(("USERPROFILE", "/profile")));
        Assert.Null(GetDefaultCacheRoot());
    }

    [Fact]
    public void InvocationBuilder_HandlesPowerShellAndDirectExecutablesWithoutShellWrapping()
    {
        var arguments = new[] { "-i", "app.css" };

        var powershell = TailwindInvocationBuilder.Build("C:\\tools\\tailwind.ps1", arguments, platform => platform == OSPlatform.Windows);
        var directWindows = TailwindInvocationBuilder.Build("C:\\tools\\tailwind.exe", arguments, platform => platform == OSPlatform.Windows);
        var directUnix = TailwindInvocationBuilder.Build("/tools/tailwindcss", arguments, _ => false);

        Assert.Equal("powershell.exe", powershell.FileName);
        Assert.Equal(["-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "C:\\tools\\tailwind.ps1", "-i", "app.css"], powershell.Arguments);
        Assert.Equal("C:\\tools\\tailwind.exe", directWindows.FileName);
        Assert.Equal(arguments, directWindows.Arguments);
        Assert.Equal("/tools/tailwindcss", directUnix.FileName);
        Assert.Equal(arguments, directUnix.Arguments);
        Assert.Throws<ArgumentException>(() => TailwindInvocationBuilder.Build(" ", arguments, _ => false));
        Assert.Throws<ArgumentNullException>(() => TailwindInvocationBuilder.Build("/tools/tailwindcss", null!, _ => false));
    }

    [Theory]
    [InlineData("{", "not valid JSON")]
    [InlineData("{\"schemaVersion\":2,\"version\":\"4.1.18\",\"baseUrl\":\"https://example.test\",\"assets\":[]}", "Unsupported")]
    public void ReleaseManifest_ParseRejectsMalformedOrUnsupportedDocuments(string json, string expectedMessage)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var exception = Assert.Throws<InvalidDataException>(() => TailwindReleaseManifest.Parse(stream));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseManifest_ParseRejectsUnsafeAssetIdentity()
    {
        var json = CreateManifestJson(rid => rid == "linux-x64" ? "../tailwindcss" : TailwindRuntimeMap.GetRuntimeBinaryName(rid)!);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var exception = Assert.Throws<InvalidDataException>(() => TailwindReleaseManifest.Parse(stream));

        Assert.Contains("linux-x64", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseManifest_LoadEmbeddedRejectsAssembliesWithoutTheManifestResource()
    {
        var exception = Assert.Throws<InvalidDataException>(() => TailwindReleaseManifest.LoadEmbedded(typeof(TailwindCliManagerTests).Assembly));

        Assert.Contains("not embedded", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("null", "empty")]
    [InlineData("{\"schemaVersion\":1,\"version\":\"4.1.18-preview\",\"baseUrl\":\"https://example.test\",\"assets\":[]}", "canonical stable")]
    [InlineData("{\"schemaVersion\":1,\"version\":\"4.1.18\",\"baseUrl\":\"https://example.test\",\"assets\":[{\"rid\":\"linux-x64\",\"binaryName\":\"tailwindcss-linux-x64\",\"sha256\":\"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\"},{\"rid\":\"linux-x64\",\"binaryName\":\"tailwindcss-linux-x64\",\"sha256\":\"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\"}]}", "unsupported or duplicate")]
    public void ReleaseManifest_ParseRejectsEmptyInvalidVersionAndDuplicateAssets(string json, string expectedMessage)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var exception = Assert.Throws<InvalidDataException>(() => TailwindReleaseManifest.Parse(stream));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseManifest_ParseRejectsAnIncompleteAssetSetAndUnknownLookup()
    {
        const string digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var incomplete = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            version = "4.1.18",
            baseUrl = "https://example.test",
            assets = new[] { new { rid = "linux-x64", binaryName = "tailwindcss-linux-x64", sha256 = digest } }
        });
        using var incompleteStream = new MemoryStream(Encoding.UTF8.GetBytes(incomplete));

        var incompleteException = Assert.Throws<InvalidDataException>(() => TailwindReleaseManifest.Parse(incompleteStream));
        var manifest = TailwindReleaseManifest.LoadFromFile(GetRepositoryManifestPath());
        var unknownAssetException = Assert.Throws<InvalidDataException>(() => manifest.GetAsset("unknown"));

        Assert.Contains("exactly the five", incompleteException.Message, StringComparison.Ordinal);
        Assert.Contains("does not contain", unknownAssetException.Message, StringComparison.Ordinal);
    }

    private TailwindCliResolver CreateResolver(TailwindReleaseManifest manifest, TailwindReleaseAsset asset, byte[] payload)
    {
        return new TailwindCliResolver(manifest, (uri, _) => Task.FromResult(CreateDownload(uri, asset, payload)));
    }

    private string WriteControlledManifest(byte[] payload, string version = "4.1.18")
    {
        var digest = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var assets = SupportedRids.Select(rid => new
        {
            rid,
            binaryName = TailwindRuntimeMap.GetRuntimeBinaryName(rid),
            sha256 = digest
        });
        var path = Path.Join(_tempRoot, "tailwind.release.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            version,
            baseUrl = "https://example.test/tailwind/",
            assets
        }));
        return path;
    }

    private static byte[] CreateDownload(Uri uri, TailwindReleaseAsset asset, byte[] payload)
    {
        if (!uri.AbsolutePath.EndsWith("sha256sums.txt", StringComparison.Ordinal))
        {
            return payload;
        }

        var sums = $"{asset.Sha256}  ./{asset.BinaryName}\n";
        return Encoding.UTF8.GetBytes(sums);
    }

    private static byte[] CreateChecksums(TailwindReleaseAsset asset, byte[] payload)
    {
        return Encoding.UTF8.GetBytes($"{Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant()}  ./{asset.BinaryName}\n");
    }

    private static HttpResponseMessage CreateHttpResponse(HttpStatusCode statusCode, byte[] content)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new ByteArrayContent(content)
        };
    }

    private static HttpResponseMessage CreateOversizedHttpResponse(long contentLength)
    {
        var content = new ByteArrayContent(Array.Empty<byte>());
        content.Headers.ContentLength = contentLength;
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content
        };
    }

    private static HttpResponseMessage CreateStreamingHttpResponse(byte[] content)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnknownLengthContent(content)
        };
    }

    private sealed class QueueHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed class UnknownLengthContent(byte[] content) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            return stream.WriteAsync(content, CancellationToken.None).AsTask();
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            return Task.FromResult<Stream>(new MemoryStream(content, writable: false));
        }
    }

    private static string? GetDefaultCacheRoot(params (string Name, string Value)[] values)
    {
        var environment = values.ToDictionary(static value => value.Name, static value => value.Value, StringComparer.Ordinal);
        return TailwindDownloadCache.GetDefaultRoot(name => environment.TryGetValue(name, out var value) ? value : null);
    }

    private static string CreateManifestJson(Func<string, string> binaryNameForRid)
    {
        const string digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        return JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            version = "4.1.18",
            baseUrl = "https://example.test/tailwind/",
            assets = SupportedRids.Select(rid => new { rid, binaryName = binaryNameForRid(rid), sha256 = digest })
        });
    }

    private static string GetRepositoryManifestPath()
    {
        var projectDirectory = Path.GetDirectoryName(typeof(TailwindCliManager).Assembly.Location)!;
        for (var current = new DirectoryInfo(projectDirectory); current is not null; current = current.Parent)
        {
            var candidate = Path.Join(current.FullName, "Web", "ForgeTrust.AppSurface.Web.Tailwind", "tailwind.release.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("Could not locate tailwind.release.json from the test assembly.");
    }
}
