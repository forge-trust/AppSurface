using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using ForgeTrust.AppSurface.Web.Tailwind.Internal;

[assembly: ExcludeFromCodeCoverage(
    Justification = "This non-packable child-process harness is exercised by TailwindCacheProcessTests; the coverage collector cannot instrument the child process it launches.")]

if (args.Length == 6 && string.Equals(args[0], "resolve", StringComparison.Ordinal))
{
    await ResolveAsync(args[1], args[2], args[3], args[4], ParseDelayMilliseconds(args[5]));
    return;
}

if (args.Length == 4 && string.Equals(args[0], "resolve-hold-partial", StringComparison.Ordinal))
{
    await ResolveAndHoldPartialAsync(args[1], args[2], args[3]);
    return;
}

throw new ArgumentException("Expected 'resolve <cache-root> <payload-path> <outcome-path> <binary-ready-path-or-dash> <binary-delay-milliseconds>' or 'resolve-hold-partial <cache-root> <payload-path> <partial-ready-path>'.");

static async Task ResolveAsync(
    string cacheRoot,
    string payloadPath,
    string outcomePath,
    string binaryReadyPath,
    int binaryDelayMilliseconds)
{
    var payload = await File.ReadAllBytesAsync(payloadPath);
    var manifest = CreateManifest(payload);
    var asset = manifest.GetAsset("linux-x64");
    var resolver = new TailwindCliResolver(
        manifest,
        async (uri, cancellationToken) =>
        {
            if (uri.AbsolutePath.EndsWith("sha256sums.txt", StringComparison.Ordinal))
            {
                return Encoding.UTF8.GetBytes($"{asset.Sha256}  {asset.BinaryName}\n");
            }

            if (!string.Equals(binaryReadyPath, "-", StringComparison.Ordinal))
            {
                WriteReadySignal(binaryReadyPath, "binary-download-started");
            }

            if (binaryDelayMilliseconds > 0)
            {
                await Task.Delay(binaryDelayMilliseconds, cancellationToken);
            }

            return payload;
        },
        delay: static (delay, cancellationToken) => Task.Delay(delay, cancellationToken));

    var resolved = await resolver.ResolveAsync(
        new TailwindCliResolverOptions(null, AppContext.BaseDirectory, cacheRoot, manifest.Version, asset.Rid),
        CancellationToken.None);

    await File.WriteAllLinesAsync(outcomePath, [resolved.CacheState.ToString(), resolved.Path]);
}

static async Task ResolveAndHoldPartialAsync(string cacheRoot, string payloadPath, string partialReadyPath)
{
    var payload = await File.ReadAllBytesAsync(payloadPath);
    var manifest = CreateManifest(payload);
    var asset = manifest.GetAsset("linux-x64");
    var resolver = new TailwindCliResolver(
        manifest,
        (uri, _) => Task.FromResult(
            uri.AbsolutePath.EndsWith("sha256sums.txt", StringComparison.Ordinal)
                ? Encoding.UTF8.GetBytes($"{asset.Sha256}  {asset.BinaryName}\n")
                : payload),
        delay: static (_, cancellationToken) => Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken),
        afterPartialOpened: partialPath =>
        {
            WriteReadySignal(partialReadyPath, partialPath);
            Thread.Sleep(Timeout.Infinite);
        });

    await resolver.ResolveAsync(
        new TailwindCliResolverOptions(null, AppContext.BaseDirectory, cacheRoot, manifest.Version, asset.Rid),
        CancellationToken.None);
}

static TailwindReleaseManifest CreateManifest(byte[] payload)
{
    var digest = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
    var assets = new[] { "linux-x64", "linux-arm64", "osx-x64", "osx-arm64", "win-x64" }
        .Select(rid => new
        {
            rid,
            binaryName = TailwindRuntimeMap.GetRuntimeBinaryName(rid),
            sha256 = digest,
        });
    var json = System.Text.Json.JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        version = "4.1.18",
        baseUrl = "https://example.test/tailwind/",
        assets,
    });

    using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
    return TailwindReleaseManifest.Parse(stream);
}

static int ParseDelayMilliseconds(string value)
{
    return int.TryParse(value, out var milliseconds) && milliseconds >= 0
        ? milliseconds
        : throw new ArgumentException("The binary delay must be a non-negative integer number of milliseconds.");
}

static void WriteReadySignal(string path, string contents)
{
    var directory = Path.GetDirectoryName(path)
        ?? throw new ArgumentException("The ready signal path must include a directory.", nameof(path));
    var temporaryPath = Path.Join(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
    File.WriteAllText(temporaryPath, contents);
    File.Move(temporaryPath, path, overwrite: true);
}

/// <summary>Locates the non-packable cache-behavior child process from Tailwind tests.</summary>
public sealed class TailwindCacheTestHostMarker;
