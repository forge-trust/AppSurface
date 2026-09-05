using System.Diagnostics;
using ForgeTrust.AppSurface.Web.Tailwind.Internal;

namespace ForgeTrust.AppSurface.Web.Tailwind.Tests;

/// <summary>
/// Verifies the cache protocol across real process boundaries rather than thread-only test seams.
/// </summary>
public sealed class TailwindCacheProcessTests : IDisposable
{
    private readonly string _tempRoot = Path.Join(Path.GetTempPath(), "tailwind-cache-process-tests-" + Guid.NewGuid().ToString("N"));

    public TailwindCacheProcessTests()
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
    public async Task Resolver_CoordinatesConcurrentProcessesAndPublishesOneVerifiedFinalEntry()
    {
        var payloadPath = await WritePayloadAsync("concurrent verified executable");
        var cacheRoot = Path.Join(_tempRoot, "concurrent-cache");
        var firstReadyPath = Path.Join(_tempRoot, "first-binary-download.ready");
        var firstOutcomePath = Path.Join(_tempRoot, "first.outcome");
        var secondOutcomePath = Path.Join(_tempRoot, "second.outcome");

        using var first = StartHost("resolve", cacheRoot, payloadPath, firstOutcomePath, firstReadyPath, "500");
        Process? second = null;
        try
        {
            await WaitForFileAsync(firstReadyPath);
            second = StartHost("resolve", cacheRoot, payloadPath, secondOutcomePath, "-", "0");

            await WaitForSuccessAsync(first);
            await WaitForSuccessAsync(second);

            var firstOutcome = await ReadOutcomeAsync(firstOutcomePath);
            var secondOutcome = await ReadOutcomeAsync(secondOutcomePath);
            var finalPath = GetFinalPath(cacheRoot);

            Assert.Equal("Acquired", firstOutcome.CacheState);
            Assert.Equal("Reused", secondOutcome.CacheState);
            Assert.Equal(finalPath, firstOutcome.Path);
            Assert.Equal(finalPath, secondOutcome.Path);
            Assert.Equal(await File.ReadAllBytesAsync(payloadPath), await File.ReadAllBytesAsync(finalPath));
            Assert.Empty(Directory.EnumerateFiles(cacheRoot, "*.partial-*", SearchOption.AllDirectories));
            Assert.Empty(Directory.EnumerateFiles(cacheRoot, "*.rejected-*", SearchOption.AllDirectories));
        }
        finally
        {
            await StopIfRunningAsync(first);
            if (second is not null)
            {
                await StopIfRunningAsync(second);
                second.Dispose();
            }
        }
    }

    [Fact]
    public async Task Resolver_RecoversAfterOwnerDeathWithAnOpenLockAndPartialFile()
    {
        var payloadPath = await WritePayloadAsync("recovered verified executable");
        var cacheRoot = Path.Join(_tempRoot, "recovery-cache");
        var partialReadyPath = Path.Join(_tempRoot, "partial-open.ready");
        var recoveryOutcomePath = Path.Join(_tempRoot, "recovery.outcome");

        using var owner = StartHost("resolve-hold-partial", cacheRoot, payloadPath, partialReadyPath);
        Process? recovery = null;
        try
        {
            await WaitForFileAsync(partialReadyPath);
            var abandonedPartialPath = await File.ReadAllTextAsync(partialReadyPath);
            var finalPath = GetFinalPath(cacheRoot);
            var abandonedRejectedPath = finalPath + ".rejected-" + Guid.NewGuid().ToString("N");
            await File.WriteAllTextAsync(abandonedRejectedPath, "not executable");

            await StopIfRunningAsync(owner);

            recovery = StartHost("resolve", cacheRoot, payloadPath, recoveryOutcomePath, "-", "0");
            await WaitForSuccessAsync(recovery);
            var outcome = await ReadOutcomeAsync(recoveryOutcomePath);

            Assert.Equal("Acquired", outcome.CacheState);
            Assert.Equal(finalPath, outcome.Path);
            Assert.Equal(await File.ReadAllBytesAsync(payloadPath), await File.ReadAllBytesAsync(finalPath));
            Assert.True(File.Exists(finalPath + ".lock"));
            Assert.True(File.Exists(abandonedPartialPath));
            Assert.True(File.Exists(abandonedRejectedPath));
            Assert.DoesNotContain(outcome.Path, ".partial-", StringComparison.Ordinal);
            Assert.DoesNotContain(outcome.Path, ".rejected-", StringComparison.Ordinal);
        }
        finally
        {
            await StopIfRunningAsync(owner);
            if (recovery is not null)
            {
                await StopIfRunningAsync(recovery);
                recovery.Dispose();
            }
        }
    }

    private Process StartHost(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(typeof(TailwindCacheTestHostMarker).Assembly.Location);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("The Tailwind cache test host could not start.");
    }

    private async Task<string> WritePayloadAsync(string contents)
    {
        var payloadPath = Path.Join(_tempRoot, "payload-" + Guid.NewGuid().ToString("N") + ".bin");
        await File.WriteAllTextAsync(payloadPath, contents);
        return payloadPath;
    }

    private string GetFinalPath(string cacheRoot)
    {
        var binaryName = TailwindRuntimeMap.GetRuntimeBinaryName("linux-x64")
            ?? throw new InvalidOperationException("The linux-x64 binary name is required for the cache test host.");
        return TailwindDownloadCache.GetRuntimeBinaryPath(cacheRoot, "4.1.18", "linux-x64", binaryName);
    }

    private static async Task WaitForFileAsync(string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!File.Exists(path))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
        }
    }

    private static async Task WaitForSuccessAsync(Process process)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await process.WaitForExitAsync(timeout.Token);
        var error = await process.StandardError.ReadToEndAsync(timeout.Token);
        Assert.True(process.ExitCode == 0, $"Tailwind cache test host failed with exit code {process.ExitCode}: {error}");
    }

    private static async Task StopIfRunningAsync(Process process)
    {
        if (process.HasExited)
        {
            return;
        }

        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
    }

    private static async Task<CacheOutcome> ReadOutcomeAsync(string path)
    {
        var lines = await File.ReadAllLinesAsync(path);
        Assert.Equal(2, lines.Length);
        return new CacheOutcome(lines[0], lines[1]);
    }

    private sealed record CacheOutcome(string CacheState, string Path);
}
