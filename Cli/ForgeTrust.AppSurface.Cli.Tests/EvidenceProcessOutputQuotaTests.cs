using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class EvidenceProcessOutputQuotaTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessRunner_ChargesReceivedBytesBeyondRetainedPrefix(bool streamed)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = TestDirectory.Create();
        var quota = EvidenceRunByteQuota.CreateProcessOutput(2 * 1024 * 1024);
        var request = CreateRequest(
            directory.Path,
            "head -c 1200000 /dev/zero",
            quota,
            streamed ? Path.Join(directory.Path, "child.log") : null);

        var result = await new CliWrapCoverageRunProcessRunner().RunAsync(request, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1_200_000, quota.AccountedBytes);
        if (streamed)
        {
            Assert.Equal(1_200_000, new FileInfo(request.OutputFile!).Length);
        }
        else
        {
            Assert.True(result.OutputTruncated);
            Assert.Contains("[output truncated after 1048576 bytes]", result.Output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ProcessRunner_SharedQuotaCountsBothStreamsAndSerialCommands_AndRejectsLaterLaunches()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = TestDirectory.Create();
        var quota = EvidenceRunByteQuota.CreateProcessOutput(100_000);
        var runner = new CliWrapCoverageRunProcessRunner();

        var first = await runner.RunAsync(
            CreateRequest(directory.Path, "head -c 40000 /dev/zero & head -c 40000 /dev/zero >&2 & wait", quota),
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, first.ExitCode);
        Assert.Equal(80_000, quota.AccountedBytes);

        var second = await runner.RunAsync(
            CreateRequest(directory.Path, "head -c 10000 /dev/zero & head -c 10000 /dev/zero >&2 & wait", quota),
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, second.ExitCode);
        Assert.Equal(100_000, quota.AccountedBytes);

        var overLimit = await Assert.ThrowsAsync<CoverageExecutionException>(() => runner.RunAsync(
            CreateRequest(directory.Path, "printf x", quota),
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("ASCOV420", overLimit.Message, StringComparison.Ordinal);

        var marker = Path.Join(directory.Path, "must-not-launch");
        var overflow = await Assert.ThrowsAsync<CoverageExecutionException>(() => runner.RunAsync(
            CreateRequest(directory.Path, $": > '{marker}'", quota),
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("ASCOV420", overflow.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(marker));
        Assert.True(quota.IsFailed);
    }

    [Fact]
    public async Task ProcessRunner_OverflowCancelsAndDrainsChildBeforeReturning()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = TestDirectory.Create();
        var quota = EvidenceRunByteQuota.CreateProcessOutput(100_000);
        var runner = new CliWrapCoverageRunProcessRunner();
        var request = CreateRequest(
            directory.Path,
            "head -c 10000000 /dev/zero & head -c 10000000 /dev/zero >&2 & wait",
            quota);

        var exception = await Assert.ThrowsAsync<CoverageExecutionException>(() => runner.RunAsync(request, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Contains("ASCOV420", exception.Message, StringComparison.Ordinal);
        Assert.True(quota.IsFailed);
        // Rejected chunks are never admitted into the quota. Pump chunk sizes and ordering
        // vary, so only the approved byte bound and immutable rejection are observable here.
        Assert.InRange(quota.AccountedBytes, 0, quota.Limit);
        Assert.False(quota.TryChargeReceived(1));
    }

    private static CoverageRunProcessRequest CreateRequest(
        string workingDirectory,
        string script,
        EvidenceRunByteQuota quota,
        string? outputFile = null)
        => new(
            "/bin/sh",
            ["-c", script],
            workingDirectory,
            outputFile,
            null,
            CoverageRunProcessLease.Detached(),
            quota);
}
