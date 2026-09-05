extern alias TailwindTasks;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TaskInternal = TailwindTasks::ForgeTrust.AppSurface.Web.Tailwind.Internal;

namespace ForgeTrust.AppSurface.Web.Tailwind.Tests;

/// <summary>
/// Verifies the resolver as it is compiled into the isolated MSBuild task assembly.
/// </summary>
/// <remarks>
/// The package's web runtime and its MSBuild task intentionally compile the same
/// resolver source into separate assemblies. These tests exercise the task copy with
/// deterministic in-memory release responses so the shipped build-time binary keeps
/// the same verified-cache contract as the runtime copy.
/// </remarks>
public sealed class TailwindTaskResolverTests : IDisposable
{
    private static readonly string[] SupportedRids = ["linux-x64", "linux-arm64", "osx-x64", "osx-arm64", "win-x64"];
    private readonly string _tempRoot = Path.Join(Path.GetTempPath(), "tailwind-task-resolver-tests-" + Guid.NewGuid().ToString("N"));

    public TailwindTaskResolverTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Manifest_ParsesTheFiveHostContract()
    {
        var manifest = TaskInternal.TailwindReleaseManifest.LoadFromFile(GetRepositoryManifestPath());

        Assert.Equal("4.1.18", manifest.Version);
        Assert.Equal("https", manifest.BaseUri.Scheme);
        foreach (var rid in SupportedRids)
        {
            var asset = manifest.GetAsset(rid);
            Assert.Equal(TaskInternal.TailwindRuntimeMap.GetRuntimeBinaryName(rid), asset.BinaryName);
            Assert.Matches("^[0-9a-f]{64}$", asset.Sha256);
        }
    }

    [Theory]
    [InlineData("4.1.18", true)]
    [InlineData("04.1.18", false)]
    [InlineData("4.1.18-preview", false)]
    [InlineData("4.1.18 ", false)]
    [InlineData("4.1.2147483648", false)]
    public void Manifest_RejectsNonCanonicalVersions(string version, bool expected)
    {
        Assert.Equal(expected, TaskInternal.TailwindReleaseManifest.IsCanonicalStableVersion(version));
    }

    [Fact]
    public void Manifest_RejectsAnUntrustedOrIncompleteContract()
    {
        var path = Path.Join(_tempRoot, "invalid.release.json");
        File.WriteAllText(path, "{\"schemaVersion\":1,\"version\":\"4.1.18\",\"baseUrl\":\"http://example.test\",\"assets\":[]}");

        var exception = Assert.Throws<InvalidDataException>(() => TaskInternal.TailwindReleaseManifest.LoadFromFile(path));

        Assert.Contains("HTTPS", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Manifest_ParseRejectsUnsafeTaskAssemblyAssetIdentity()
    {
        var json = CreateManifestJson(rid => rid == "linux-x64" ? "../tailwindcss" : TaskInternal.TailwindRuntimeMap.GetRuntimeBinaryName(rid)!);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var exception = Assert.Throws<InvalidDataException>(() => TaskInternal.TailwindReleaseManifest.Parse(stream));

        Assert.Contains("linux-x64", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeMapAndCacheRoot_UseTheTaskAssemblyHostRules()
    {
        Assert.Equal("win-x64", TaskInternal.TailwindRuntimeMap.GetCurrentRid(platform => platform == OSPlatform.Windows, () => System.Runtime.InteropServices.Architecture.Arm64));
        Assert.Equal("linux-arm64", TaskInternal.TailwindRuntimeMap.GetCurrentRid(platform => platform == OSPlatform.Linux, () => System.Runtime.InteropServices.Architecture.Arm64));
        Assert.Equal("osx-x64", TaskInternal.TailwindRuntimeMap.GetCurrentRid(platform => platform == OSPlatform.OSX, () => System.Runtime.InteropServices.Architecture.X64));
        Assert.Equal("unknown", TaskInternal.TailwindRuntimeMap.GetCurrentRid(_ => false, () => System.Runtime.InteropServices.Architecture.X64));
        Assert.Equal("tailwindcss.exe", TaskInternal.TailwindRuntimeMap.GetLocalBinaryName(platform => platform == OSPlatform.Windows));
        Assert.Equal("tailwindcss", TaskInternal.TailwindRuntimeMap.GetLocalBinaryName(_ => false));

        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["XDG_CACHE_HOME"] = "/xdg",
            ["LOCALAPPDATA"] = "/local",
            ["HOME"] = "/home",
            ["USERPROFILE"] = "/profile"
        };
        Assert.Equal(Path.Join("/xdg", "forgetrust", "appsurface", "tailwind"), TaskInternal.TailwindDownloadCache.GetDefaultRoot(name => environment.TryGetValue(name, out var value) ? value : null));
        environment.Remove("XDG_CACHE_HOME");
        Assert.Equal(Path.Join("/local", "ForgeTrust", "AppSurface", "Tailwind"), TaskInternal.TailwindDownloadCache.GetDefaultRoot(name => environment.TryGetValue(name, out var value) ? value : null));
    }

    [Fact]
    public void InvocationBuilder_UsesOnlyTrustedWindowsShimLaunchers()
    {
        var arguments = new[] { "-i", "app.css" };

        var command = TaskInternal.TailwindInvocationBuilder.Build("C:\\tools\\tailwind.cmd", arguments, platform => platform == OSPlatform.Windows);
        var powershell = TaskInternal.TailwindInvocationBuilder.Build("C:\\tools\\tailwind.ps1", arguments, platform => platform == OSPlatform.Windows);
        var direct = TaskInternal.TailwindInvocationBuilder.Build("/tools/tailwindcss", arguments, _ => false);

        Assert.Equal("cmd.exe", command.FileName);
        Assert.Equal(["/d", "/c", "C:\\tools\\tailwind.cmd", "-i", "app.css"], command.Arguments);
        Assert.Equal("powershell.exe", powershell.FileName);
        Assert.Equal("/tools/tailwindcss", direct.FileName);
        Assert.Equal(arguments, direct.Arguments);
    }

    [Fact]
    public async Task Resolver_UsesAnExplicitPathWithoutAReleaseDownload()
    {
        var explicitPath = Path.Join(_tempRoot, "tools", "tailwindcss");
        Directory.CreateDirectory(Path.GetDirectoryName(explicitPath)!);
        await File.WriteAllTextAsync(explicitPath, "custom executable");
        var downloadCalls = 0;
        var resolver = new TaskInternal.TailwindCliResolver(
            TaskInternal.TailwindReleaseManifest.LoadFromFile(GetRepositoryManifestPath()),
            (_, _) =>
            {
                downloadCalls++;
                return Task.FromResult(Array.Empty<byte>());
            });

        var resolved = await resolver.ResolveAsync(
            new TaskInternal.TailwindCliResolverOptions(explicitPath, _tempRoot, Path.Join(_tempRoot, "cache"), null, "unknown"),
            CancellationToken.None);

        Assert.Equal(Path.GetFullPath(explicitPath), resolved.Path);
        Assert.Equal(TaskInternal.TailwindCliCacheState.Explicit, resolved.CacheState);
        Assert.Equal(0, downloadCalls);
    }

    [Fact]
    public async Task Resolver_AcquiresAndThenReusesOnlyThePinnedHostEntry()
    {
        var payload = Encoding.UTF8.GetBytes("verified task executable");
        var manifest = TaskInternal.TailwindReleaseManifest.LoadFromFile(WriteControlledManifest(payload));
        var asset = manifest.GetAsset("linux-x64");
        var cacheRoot = Path.Join(_tempRoot, "cache");
        var downloadCalls = 0;
        var resolver = new TaskInternal.TailwindCliResolver(manifest, (uri, _) =>
        {
            downloadCalls++;
            return Task.FromResult(CreateDownload(uri, asset, payload));
        });
        var options = new TaskInternal.TailwindCliResolverOptions(null, _tempRoot, cacheRoot, manifest.Version, asset.Rid);

        var acquired = await resolver.ResolveAsync(options, CancellationToken.None);
        var reused = await resolver.ResolveAsync(options, CancellationToken.None);

        Assert.Equal(TaskInternal.TailwindCliCacheState.Acquired, acquired.CacheState);
        Assert.Equal(TaskInternal.TailwindCliCacheState.Reused, reused.CacheState);
        Assert.Equal(2, downloadCalls);
        Assert.Equal(
            TaskInternal.TailwindDownloadCache.GetRuntimeBinaryPath(cacheRoot, manifest.Version, asset.Rid, asset.BinaryName),
            acquired.Path);
        Assert.True(File.Exists(acquired.Path));
    }

    [Fact]
    public async Task Resolver_TaskAssemblyAcquiresThroughTheProductionHttpPipeline()
    {
        var payload = Encoding.UTF8.GetBytes("task http pipeline executable");
        var manifest = TaskInternal.TailwindReleaseManifest.LoadFromFile(WriteControlledManifest(payload));
        var asset = manifest.GetAsset("linux-x64");
        var requests = new List<string>();
        using var client = new HttpClient(new TaskHttpMessageHandler(request =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            return request.RequestUri.AbsolutePath.EndsWith("sha256sums.txt", StringComparison.Ordinal)
                ? CreateHttpResponse(HttpStatusCode.OK, CreateChecksums(asset, payload))
                : CreateHttpResponse(HttpStatusCode.OK, payload);
        }));
        var resolver = new TaskInternal.TailwindCliResolver(manifest, httpClient: client);

        var resolved = await resolver.ResolveAsync(
            new TaskInternal.TailwindCliResolverOptions(null, _tempRoot, Path.Join(_tempRoot, "cache"), manifest.Version, asset.Rid),
            CancellationToken.None);

        Assert.Equal(TaskInternal.TailwindCliCacheState.Acquired, resolved.CacheState);
        Assert.Equal(payload, await File.ReadAllBytesAsync(resolved.Path));
        Assert.Equal(["/tailwind/sha256sums.txt", "/tailwind/tailwindcss-linux-x64"], requests);
    }

    [Fact]
    public async Task Resolver_TaskAssemblyRetriesTransientBinaryFailureThroughTheProductionHttpPipeline()
    {
        var payload = Encoding.UTF8.GetBytes("task http retry executable");
        var manifest = TaskInternal.TailwindReleaseManifest.LoadFromFile(WriteControlledManifest(payload));
        var asset = manifest.GetAsset("linux-x64");
        var binaryAttempts = 0;
        var delayCalls = 0;
        using var client = new HttpClient(new TaskHttpMessageHandler(request =>
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
        var resolver = new TaskInternal.TailwindCliResolver(
            manifest,
            delay: (_, _) =>
            {
                delayCalls++;
                return Task.CompletedTask;
            },
            httpClient: client);

        var resolved = await resolver.ResolveAsync(
            new TaskInternal.TailwindCliResolverOptions(null, _tempRoot, Path.Join(_tempRoot, "cache"), manifest.Version, asset.Rid),
            CancellationToken.None);

        Assert.Equal(TaskInternal.TailwindCliCacheState.Acquired, resolved.CacheState);
        Assert.Equal(2, binaryAttempts);
        Assert.Equal(1, delayCalls);
    }

    [Fact]
    public async Task Resolver_RejectsAReleaseWhoseChecksumDisagreesWithThePinnedDigest()
    {
        var payload = Encoding.UTF8.GetBytes("untrusted task executable");
        var manifest = TaskInternal.TailwindReleaseManifest.LoadFromFile(WriteControlledManifest(Encoding.UTF8.GetBytes("trusted task executable")));
        var asset = manifest.GetAsset("linux-x64");
        var resolver = new TaskInternal.TailwindCliResolver(manifest, (uri, _) =>
        {
            var sums = $"{Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant()}  ./{asset.BinaryName}\n";
            return Task.FromResult(uri.AbsolutePath.EndsWith("sha256sums.txt", StringComparison.Ordinal)
                ? Encoding.UTF8.GetBytes(sums)
                : payload);
        });
        var cacheRoot = Path.Join(_tempRoot, "cache");

        var exception = await Assert.ThrowsAsync<TaskInternal.TailwindCliResolutionException>(() => resolver.ResolveAsync(
            new TaskInternal.TailwindCliResolverOptions(null, _tempRoot, cacheRoot, manifest.Version, asset.Rid),
            CancellationToken.None));

        Assert.Equal(TaskInternal.TailwindCliResolutionFailure.ChecksumFailure, exception.Failure);
        Assert.Empty(Directory.EnumerateFiles(cacheRoot, "*.partial-*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(null, (int)TaskInternal.TailwindCliResolutionFailure.MissingVersion)]
    [InlineData("4.1.18-preview", (int)TaskInternal.TailwindCliResolutionFailure.InvalidVersion)]
    [InlineData("4.1.19", (int)TaskInternal.TailwindCliResolutionFailure.InvalidVersion)]
    public async Task Resolver_RejectsMissingOrMismatchedVersions(string? version, int expectedFailure)
    {
        var resolver = new TaskInternal.TailwindCliResolver(TaskInternal.TailwindReleaseManifest.LoadFromFile(GetRepositoryManifestPath()));

        var exception = await Assert.ThrowsAsync<TaskInternal.TailwindCliResolutionException>(() => resolver.ResolveAsync(
            new TaskInternal.TailwindCliResolverOptions(null, _tempRoot, Path.Join(_tempRoot, "cache"), version, "linux-x64"),
            CancellationToken.None));

        Assert.Equal((TaskInternal.TailwindCliResolutionFailure)expectedFailure, exception.Failure);
    }

    [Fact]
    public async Task Resolver_RejectsAnUnknownHostBeforeAcquisition()
    {
        var resolver = new TaskInternal.TailwindCliResolver(TaskInternal.TailwindReleaseManifest.LoadFromFile(GetRepositoryManifestPath()));

        var exception = await Assert.ThrowsAsync<TaskInternal.TailwindCliResolutionException>(() => resolver.ResolveAsync(
            new TaskInternal.TailwindCliResolverOptions(null, _tempRoot, Path.Join(_tempRoot, "cache"), "4.1.18", "unknown"),
            CancellationToken.None));

        Assert.Equal(TaskInternal.TailwindCliResolutionFailure.UnsupportedRid, exception.Failure);
    }

    [Fact]
    public async Task Resolver_RequiresACacheRootBeforeDownloading()
    {
        var downloadCalls = 0;
        var resolver = new TaskInternal.TailwindCliResolver(
            TaskInternal.TailwindReleaseManifest.LoadFromFile(GetRepositoryManifestPath()),
            (_, _) =>
            {
                downloadCalls++;
                return Task.FromResult(Array.Empty<byte>());
            },
            _ => null);

        var exception = await Assert.ThrowsAsync<TaskInternal.TailwindCliResolutionException>(() => resolver.ResolveAsync(
            new TaskInternal.TailwindCliResolverOptions(null, _tempRoot, null, "4.1.18", "linux-x64"),
            CancellationToken.None));

        Assert.Equal(TaskInternal.TailwindCliResolutionFailure.NoCacheRoot, exception.Failure);
        Assert.Equal(0, downloadCalls);
    }

    [Fact]
    public async Task Resolver_ReplacesAnUnverifiedEntryAndCleansUpItsRejectedPredecessor()
    {
        var payload = Encoding.UTF8.GetBytes("replacement task executable");
        var manifest = TaskInternal.TailwindReleaseManifest.LoadFromFile(WriteControlledManifest(payload));
        var asset = manifest.GetAsset("linux-x64");
        var cacheRoot = Path.Join(_tempRoot, "cache");
        var finalPath = TaskInternal.TailwindDownloadCache.GetRuntimeBinaryPath(cacheRoot, manifest.Version, asset.Rid, asset.BinaryName);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        await File.WriteAllTextAsync(finalPath, "tampered cache entry");
        var resolver = CreateResolver(manifest, asset, payload);

        var resolved = await resolver.ResolveAsync(
            new TaskInternal.TailwindCliResolverOptions(null, _tempRoot, cacheRoot, manifest.Version, asset.Rid),
            CancellationToken.None);

        Assert.Equal(TaskInternal.TailwindCliCacheState.Acquired, resolved.CacheState);
        Assert.Equal(payload, await File.ReadAllBytesAsync(finalPath));
        Assert.Empty(Directory.EnumerateFiles(cacheRoot, "*.partial-*", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(cacheRoot, "*.rejected-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Resolver_CleansUpARejectedEntryWhenReplacementVerificationFails()
    {
        var trustedPayload = Encoding.UTF8.GetBytes("trusted task replacement executable");
        var untrustedPayload = Encoding.UTF8.GetBytes("untrusted task replacement executable");
        var manifest = TaskInternal.TailwindReleaseManifest.LoadFromFile(WriteControlledManifest(trustedPayload));
        var asset = manifest.GetAsset("linux-x64");
        var cacheRoot = Path.Join(_tempRoot, "cache");
        var finalPath = TaskInternal.TailwindDownloadCache.GetRuntimeBinaryPath(cacheRoot, manifest.Version, asset.Rid, asset.BinaryName);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        await File.WriteAllTextAsync(finalPath, "corrupt cache entry");
        var resolver = new TaskInternal.TailwindCliResolver(manifest, (uri, _) =>
        {
            var sums = $"{Convert.ToHexString(SHA256.HashData(untrustedPayload)).ToLowerInvariant()}  ./{asset.BinaryName}\n";
            return Task.FromResult(uri.AbsolutePath.EndsWith("sha256sums.txt", StringComparison.Ordinal)
                ? Encoding.UTF8.GetBytes(sums)
                : untrustedPayload);
        });

        var exception = await Assert.ThrowsAsync<TaskInternal.TailwindCliResolutionException>(() => resolver.ResolveAsync(
            new TaskInternal.TailwindCliResolverOptions(null, _tempRoot, cacheRoot, manifest.Version, asset.Rid),
            CancellationToken.None));

        Assert.Equal(TaskInternal.TailwindCliResolutionFailure.ChecksumFailure, exception.Failure);
        Assert.False(File.Exists(finalPath));
        Assert.Empty(Directory.EnumerateFiles(cacheRoot, "*.rejected-*", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(cacheRoot, "*.partial-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Resolver_RetriesTransientReleaseFailuresWithABoundedDelay()
    {
        var payload = Encoding.UTF8.GetBytes("retry task executable");
        var manifest = TaskInternal.TailwindReleaseManifest.LoadFromFile(WriteControlledManifest(payload));
        var asset = manifest.GetAsset("linux-x64");
        var downloadCalls = 0;
        var delayCalls = 0;
        var resolver = new TaskInternal.TailwindCliResolver(
            manifest,
            (uri, _) =>
            {
                downloadCalls++;
                if (downloadCalls == 1)
                {
                    throw new HttpRequestException("transient task test failure");
                }

                return Task.FromResult(CreateDownload(uri, asset, payload));
            },
            delay: (_, _) =>
            {
                delayCalls++;
                return Task.CompletedTask;
            });

        var resolved = await resolver.ResolveAsync(
            new TaskInternal.TailwindCliResolverOptions(null, _tempRoot, Path.Join(_tempRoot, "cache"), manifest.Version, asset.Rid),
            CancellationToken.None);

        Assert.Equal(TaskInternal.TailwindCliCacheState.Acquired, resolved.CacheState);
        Assert.Equal(3, downloadCalls);
        Assert.Equal(1, delayCalls);
    }

    [Fact]
    public async Task Resolver_ReportsRetryExhaustionAfterBoundedOfficialReleaseAttempts()
    {
        var manifest = TaskInternal.TailwindReleaseManifest.LoadFromFile(WriteControlledManifest(Encoding.UTF8.GetBytes("retry exhaustion task executable")));
        var downloadCalls = 0;
        var delayCalls = 0;
        var resolver = new TaskInternal.TailwindCliResolver(
            manifest,
            (_, _) =>
            {
                downloadCalls++;
                throw new IOException("offline task test failure");
            },
            delay: (_, _) =>
            {
                delayCalls++;
                return Task.CompletedTask;
            });

        var exception = await Assert.ThrowsAsync<TaskInternal.TailwindCliResolutionException>(() => resolver.ResolveAsync(
            new TaskInternal.TailwindCliResolverOptions(null, _tempRoot, Path.Join(_tempRoot, "cache"), manifest.Version, "linux-x64"),
            CancellationToken.None));

        Assert.Equal(TaskInternal.TailwindCliResolutionFailure.RetryExhausted, exception.Failure);
        Assert.Equal(5, downloadCalls);
        Assert.Equal(4, delayCalls);
    }

    [Fact]
    public async Task Resolver_TaskAssemblyReportsLockTimeoutAfterAllBoundedLockAttempts()
    {
        var payload = Encoding.UTF8.GetBytes("task lock timeout executable");
        var manifest = TaskInternal.TailwindReleaseManifest.LoadFromFile(WriteControlledManifest(payload));
        var asset = manifest.GetAsset("linux-x64");
        var cacheRoot = Path.Join(_tempRoot, "cache");
        var finalPath = TaskInternal.TailwindDownloadCache.GetRuntimeBinaryPath(cacheRoot, manifest.Version, asset.Rid, asset.BinaryName);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        await using var heldLock = new FileStream(finalPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var delayCalls = 0;
        var resolver = new TaskInternal.TailwindCliResolver(
            manifest,
            (_, _) => Task.FromResult(CreateDownload(new Uri("https://example.test/sha256sums.txt"), asset, payload)),
            delay: (_, _) =>
            {
                delayCalls++;
                return Task.CompletedTask;
            });

        var exception = await Assert.ThrowsAsync<TaskInternal.TailwindCliResolutionException>(() => resolver.ResolveAsync(
            new TaskInternal.TailwindCliResolverOptions(null, _tempRoot, cacheRoot, manifest.Version, asset.Rid),
            CancellationToken.None));

        Assert.Equal(TaskInternal.TailwindCliResolutionFailure.LockTimeout, exception.Failure);
        Assert.Equal(4, delayCalls);
        Assert.IsType<IOException>(exception.InnerException);
    }

    [Fact]
    public async Task Resolver_TaskAssemblyRejectsAnUntrustedDownloadedBinary()
    {
        var trustedPayload = Encoding.UTF8.GetBytes("trusted task binary checksum");
        var untrustedPayload = Encoding.UTF8.GetBytes("untrusted task binary checksum");
        var manifest = TaskInternal.TailwindReleaseManifest.LoadFromFile(WriteControlledManifest(trustedPayload));
        var asset = manifest.GetAsset("linux-x64");
        var resolver = new TaskInternal.TailwindCliResolver(manifest, (uri, _) => Task.FromResult(
            uri.AbsolutePath.EndsWith("sha256sums.txt", StringComparison.Ordinal)
                ? Encoding.UTF8.GetBytes($"{asset.Sha256}  ./{asset.BinaryName}\n")
                : untrustedPayload));

        var exception = await Assert.ThrowsAsync<TaskInternal.TailwindCliResolutionException>(() => resolver.ResolveAsync(
            new TaskInternal.TailwindCliResolverOptions(null, _tempRoot, Path.Join(_tempRoot, "cache"), manifest.Version, asset.Rid),
            CancellationToken.None));

        Assert.Equal(TaskInternal.TailwindCliResolutionFailure.ChecksumFailure, exception.Failure);
    }

    [Theory]
    [InlineData("not-a-checksum-line", "does not contain")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  other-file", "does not contain")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF  ./tailwindcss-linux-x64", "malformed or duplicate")]
    public async Task Resolver_TaskAssemblyRejectsMalformedOrMissingSelectedChecksumEntries(string checksumLine, string expectedMessage)
    {
        var payload = Encoding.UTF8.GetBytes("task checksum format executable");
        var manifest = TaskInternal.TailwindReleaseManifest.LoadFromFile(WriteControlledManifest(payload));
        var asset = manifest.GetAsset("linux-x64");
        var resolver = new TaskInternal.TailwindCliResolver(manifest, (uri, _) => Task.FromResult(
            uri.AbsolutePath.EndsWith("sha256sums.txt", StringComparison.Ordinal)
                ? Encoding.UTF8.GetBytes(checksumLine + "\n")
                : payload));

        var exception = await Assert.ThrowsAsync<TaskInternal.TailwindCliResolutionException>(() => resolver.ResolveAsync(
            new TaskInternal.TailwindCliResolverOptions(null, _tempRoot, Path.Join(_tempRoot, "cache"), manifest.Version, asset.Rid),
            CancellationToken.None));

        Assert.Equal(TaskInternal.TailwindCliResolutionFailure.ChecksumFailure, exception.Failure);
        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolver_TaskAssemblyRejectsDuplicateSelectedChecksumEntries()
    {
        var payload = Encoding.UTF8.GetBytes("task duplicate checksum executable");
        var manifest = TaskInternal.TailwindReleaseManifest.LoadFromFile(WriteControlledManifest(payload));
        var asset = manifest.GetAsset("linux-x64");
        var checksum = $"{asset.Sha256}  ./{asset.BinaryName}\n{asset.Sha256}  *{asset.BinaryName}\n";
        var resolver = new TaskInternal.TailwindCliResolver(manifest, (uri, _) => Task.FromResult(
            uri.AbsolutePath.EndsWith("sha256sums.txt", StringComparison.Ordinal)
                ? Encoding.UTF8.GetBytes(checksum)
                : payload));

        var exception = await Assert.ThrowsAsync<TaskInternal.TailwindCliResolutionException>(() => resolver.ResolveAsync(
            new TaskInternal.TailwindCliResolverOptions(null, _tempRoot, Path.Join(_tempRoot, "cache"), manifest.Version, asset.Rid),
            CancellationToken.None));

        Assert.Equal(TaskInternal.TailwindCliResolutionFailure.ChecksumFailure, exception.Failure);
        Assert.Contains("malformed or duplicate", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolver_HonorsCancellationWhileWaitingForAnEntryLock()
    {
        var payload = Encoding.UTF8.GetBytes("lock task executable");
        var manifest = TaskInternal.TailwindReleaseManifest.LoadFromFile(WriteControlledManifest(payload));
        var asset = manifest.GetAsset("linux-x64");
        var cacheRoot = Path.Join(_tempRoot, "cache");
        var finalPath = TaskInternal.TailwindDownloadCache.GetRuntimeBinaryPath(cacheRoot, manifest.Version, asset.Rid, asset.BinaryName);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        await using var heldLock = new FileStream(finalPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var cancellationTokenSource = new CancellationTokenSource();
        var resolver = new TaskInternal.TailwindCliResolver(
            manifest,
            (_, _) => Task.FromResult(CreateDownload(new Uri("https://example.test/sha256sums.txt"), asset, payload)),
            delay: (_, _) =>
            {
                cancellationTokenSource.Cancel();
                return Task.FromCanceled(cancellationTokenSource.Token);
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.ResolveAsync(
            new TaskInternal.TailwindCliResolverOptions(null, _tempRoot, cacheRoot, manifest.Version, asset.Rid),
            cancellationTokenSource.Token));
    }

    [Fact]
    public async Task Resolver_RejectsSymbolicLinkCacheArtifactsWithoutDownloading()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var payload = Encoding.UTF8.GetBytes("linked task executable");
        var manifest = TaskInternal.TailwindReleaseManifest.LoadFromFile(WriteControlledManifest(payload));
        var asset = manifest.GetAsset("linux-x64");
        var cacheRoot = Path.Join(_tempRoot, "cache");
        var finalPath = TaskInternal.TailwindDownloadCache.GetRuntimeBinaryPath(cacheRoot, manifest.Version, asset.Rid, asset.BinaryName);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        var target = Path.Join(_tempRoot, "linked-target");
        await File.WriteAllTextAsync(target, "not trusted");
        File.CreateSymbolicLink(finalPath, target);
        var downloadCalls = 0;
        var resolver = new TaskInternal.TailwindCliResolver(
            manifest,
            (_, _) =>
            {
                downloadCalls++;
                return Task.FromResult(Array.Empty<byte>());
            });

        var exception = await Assert.ThrowsAsync<TaskInternal.TailwindCliResolutionException>(() => resolver.ResolveAsync(
            new TaskInternal.TailwindCliResolverOptions(null, _tempRoot, cacheRoot, manifest.Version, asset.Rid),
            CancellationToken.None));

        Assert.Equal(TaskInternal.TailwindCliResolutionFailure.InvalidCache, exception.Failure);
        Assert.Equal(0, downloadCalls);
    }

    [Fact]
    public void Manifest_TaskAssemblyRejectsEmptyInvalidVersionAndDuplicateAssets()
    {
        const string digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var invalidVersion = "{\"schemaVersion\":1,\"version\":\"4.1.18-preview\",\"baseUrl\":\"https://example.test\",\"assets\":[]}";
        var duplicateAssets = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            version = "4.1.18",
            baseUrl = "https://example.test",
            assets = new[]
            {
                new { rid = "linux-x64", binaryName = "tailwindcss-linux-x64", sha256 = digest },
                new { rid = "linux-x64", binaryName = "tailwindcss-linux-x64", sha256 = digest }
            }
        });
        using var emptyStream = new MemoryStream(Encoding.UTF8.GetBytes("null"));
        using var invalidVersionStream = new MemoryStream(Encoding.UTF8.GetBytes(invalidVersion));
        using var duplicateAssetsStream = new MemoryStream(Encoding.UTF8.GetBytes(duplicateAssets));

        var emptyException = Assert.Throws<InvalidDataException>(() => TaskInternal.TailwindReleaseManifest.Parse(emptyStream));
        var versionException = Assert.Throws<InvalidDataException>(() => TaskInternal.TailwindReleaseManifest.Parse(invalidVersionStream));
        var duplicateException = Assert.Throws<InvalidDataException>(() => TaskInternal.TailwindReleaseManifest.Parse(duplicateAssetsStream));

        Assert.Contains("empty", emptyException.Message, StringComparison.Ordinal);
        Assert.Contains("canonical stable", versionException.Message, StringComparison.Ordinal);
        Assert.Contains("unsupported or duplicate", duplicateException.Message, StringComparison.Ordinal);
    }

    private TaskInternal.TailwindCliResolver CreateResolver(
        TaskInternal.TailwindReleaseManifest manifest,
        TaskInternal.TailwindReleaseAsset asset,
        byte[] payload)
    {
        return new TaskInternal.TailwindCliResolver(manifest, (uri, _) => Task.FromResult(CreateDownload(uri, asset, payload)));
    }

    private string WriteControlledManifest(byte[] payload, string version = "4.1.18")
    {
        var digest = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var assets = SupportedRids.Select(rid => new
        {
            rid,
            binaryName = TaskInternal.TailwindRuntimeMap.GetRuntimeBinaryName(rid),
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

    private static byte[] CreateDownload(Uri uri, TaskInternal.TailwindReleaseAsset asset, byte[] payload)
    {
        if (!uri.AbsolutePath.EndsWith("sha256sums.txt", StringComparison.Ordinal))
        {
            return payload;
        }

        var sums = $"{asset.Sha256}  ./{asset.BinaryName}\n";
        return Encoding.UTF8.GetBytes(sums);
    }

    private static byte[] CreateChecksums(TaskInternal.TailwindReleaseAsset asset, byte[] payload)
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

    private sealed class TaskHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responseFactory(request));
        }
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
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
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
