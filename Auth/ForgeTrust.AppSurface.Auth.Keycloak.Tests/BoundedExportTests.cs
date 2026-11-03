using System.Diagnostics;
using ForgeTrust.AppSurface.Auth.Keycloak;

namespace ForgeTrust.AppSurface.Auth.Keycloak.Tests;

public sealed class BoundedExportTests
{
    private static readonly string FixedWriter = OperatingSystem.IsWindows() ? "" : "/usr/bin/printf";

    [Fact]
    public async Task SuccessfulExportPublishesOnlyCompleteStdout()
    {
        if (OperatingSystem.IsWindows()) return;
        var output = NewOutputPath();
        await BoundedProcess.RunToFileAsync(FixedWriter, ["payload"], output, 1024 * 1024, TimeSpan.FromSeconds(10), default);

        Assert.True(File.Exists(output));
        Assert.Equal("payload", await File.ReadAllTextAsync(output));
        File.Delete(output);
    }

    [Fact]
    public async Task SuccessfulExportPreservesBinaryBytesExactly()
    {
        if (OperatingSystem.IsWindows()) return;
        var output = NewOutputPath();
        await BoundedProcess.RunToFileAsync("/bin/sh", ["-c", "printf '\\000\\001\\377payload'"], output, 1024 * 1024, TimeSpan.FromSeconds(10), default);

        Assert.Equal(new byte[] { 0, 1, 255, (byte)'p', (byte)'a', (byte)'y', (byte)'l', (byte)'o', (byte)'a', (byte)'d' }, await File.ReadAllBytesAsync(output));
        File.Delete(output);
    }

    [Fact]
    public async Task StartFailureLeavesFinalOutputAbsent()
    {
        var output = NewOutputPath();

        await Assert.ThrowsAsync<BoundedProcessException>(() => BoundedProcess.RunToFileAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), [], output, 1024, TimeSpan.FromSeconds(10), default));

        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task OutputLimitLeavesFinalOutputAbsent()
    {
        if (OperatingSystem.IsWindows()) return;
        var output = NewOutputPath();

        var failure = await Assert.ThrowsAsync<BoundedProcessException>(() => BoundedProcess.RunToFileAsync(FixedWriter, ["payload"], output, 1, TimeSpan.FromSeconds(10), default));
        Assert.Equal(ProcessStopReason.OutputLimit, failure.Reason);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task ExistingOutputIsPreserved()
    {
        if (OperatingSystem.IsWindows()) return;
        var output = NewOutputPath();
        await File.WriteAllTextAsync(output, "keep");

        await Assert.ThrowsAsync<IOException>(() => BoundedProcess.RunToFileAsync(FixedWriter, ["replace"], output, 1024, TimeSpan.FromSeconds(10), default));

        Assert.Equal("keep", await File.ReadAllTextAsync(output));
        File.Delete(output);
    }

    [Fact]
    public async Task NonzeroExitLeavesFinalOutputAbsent()
    {
        if (OperatingSystem.IsWindows()) return;
        var output = NewOutputPath();

        await Assert.ThrowsAsync<BoundedProcessException>(() => BoundedProcess.RunToFileAsync("/bin/sh", ["-c", "printf partial; printf error >&2; exit 7"], output, 1024, TimeSpan.FromSeconds(10), default));

        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task CancellationKillsProcessAndLeavesFinalOutputAbsent()
    {
        if (OperatingSystem.IsWindows()) return;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var output = NewOutputPath();

        await Assert.ThrowsAsync<OperationCanceledException>(() => BoundedProcess.RunToFileAsync("/bin/sleep", ["30"], output, 1024, TimeSpan.FromSeconds(10), cancellation.Token));

        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task DeadlineKillsProcessWithinBoundedCleanup()
    {
        if (OperatingSystem.IsWindows()) return;
        var output = NewOutputPath();

        var failure = await Assert.ThrowsAsync<BoundedProcessException>(() =>
            BoundedProcess.RunToFileAsync("/bin/sleep", ["30"], output, 1024, TimeSpan.FromMilliseconds(500), default));

        Assert.Equal(ProcessStopReason.Deadline, failure.Reason);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task InvalidLimitsAndRelativeOutputsFailBeforeStarting()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => BoundedProcess.RunToFileAsync("", [], NewOutputPath(), 1, TimeSpan.FromSeconds(1), default));
        await Assert.ThrowsAsync<ArgumentException>(() => BoundedProcess.RunToFileAsync("unused", [], "relative", 1, TimeSpan.FromSeconds(1), default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => BoundedProcess.RunToFileAsync("unused", [], NewOutputPath(), 0, TimeSpan.FromSeconds(1), default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => BoundedProcess.RunToFileAsync("unused", [], NewOutputPath(), 1024L * 1024 * 1024 + 1, TimeSpan.FromSeconds(1), default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => BoundedProcess.RunToFileAsync("unused", [], NewOutputPath(), 1, TimeSpan.Zero, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => BoundedProcess.RunToFileAsync("unused", [], NewOutputPath(), 1, TimeSpan.FromSeconds(301), default));
    }

    [Fact]
    public async Task ExistingOutputDirectoryIsPreserved()
    {
        var output = NewOutputPath();
        Directory.CreateDirectory(output);

        await Assert.ThrowsAsync<IOException>(() => BoundedProcess.RunToFileAsync("unused", [], output, 1, TimeSpan.FromSeconds(1), default));

        Assert.True(Directory.Exists(output));
        Directory.Delete(output);
    }

    [Fact]
    public async Task PreCancelledRequestDoesNotStartProcess()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var output = NewOutputPath();

        await Assert.ThrowsAsync<OperationCanceledException>(() => BoundedProcess.RunToFileAsync("unused", [], output, 1, TimeSpan.FromSeconds(1), cancellation.Token));

        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task CancellationAfterChildMarkerStopsTheChildAndLeavesOutputAbsent()
    {
        if (OperatingSystem.IsWindows()) return;
        var output = NewOutputPath();
        var marker = NewOutputPath() + ".started";
        using var cancellation = new CancellationTokenSource();
        var export = BoundedProcess.RunToFileAsync("/bin/sh", ["-c", $"printf started > '{marker}'; while :; do :; done"], output, 1024, TimeSpan.FromSeconds(10), cancellation.Token);
        try
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(1);
            while (!File.Exists(marker) && DateTime.UtcNow < deadline) await Task.Delay(10);
            Assert.True(File.Exists(marker));
            cancellation.Cancel();

            var failure = await Assert.ThrowsAsync<BoundedProcessException>(() => export);
            Assert.Equal(ProcessStopReason.Cancelled, failure.Reason);
            Assert.False(File.Exists(output));
        }
        finally
        {
            cancellation.Cancel();
            try { await export; } catch { }
            File.Delete(marker);
            File.Delete(output);
        }
    }

    [Fact]
    public async Task StderrOverflowStopsWithoutPublishingOutput()
    {
        if (OperatingSystem.IsWindows()) return;
        var output = NewOutputPath();

        var failure = await Assert.ThrowsAsync<BoundedProcessException>(() => BoundedProcess.RunToFileAsync(
            "/bin/sh", ["-c", "dd if=/dev/zero bs=1048577 count=1 >&2"], output, 1024 * 1024, TimeSpan.FromSeconds(10), default));

        Assert.Equal(ProcessStopReason.OutputLimit, failure.Reason);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task StderrIsDrainedWithoutPublishingItsBytes()
    {
        if (OperatingSystem.IsWindows()) return;
        var output = NewOutputPath();

        await Assert.ThrowsAsync<BoundedProcessException>(() => BoundedProcess.RunToFileAsync(
            "/bin/sh", ["-c", "printf diagnostic >&2; exit 1"], output, 1024, TimeSpan.FromSeconds(10), default));

        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task RunAsyncReturnsRedactedBoundedTextFromBothStreams()
    {
        if (OperatingSystem.IsWindows()) return;

        var result = await BoundedProcess.RunAsync(
            "/bin/sh", ["-c", "printf 'token-value'; printf 'diagnostic' >&2"], null,
            TimeSpan.FromSeconds(10), 1024, ["token-value"], default);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<redacted>", result.Output);
        Assert.Equal("diagnostic", result.Error);
    }

    [Fact]
    public async Task RunAsyncStartFailureIsTypedAndDoesNotPublishOutput()
    {
        var failure = await Assert.ThrowsAsync<BoundedProcessException>(() =>
            BoundedProcess.RunAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), [], null,
                TimeSpan.FromSeconds(1), 1024, [], default));

        Assert.Equal(ProcessStopReason.Start, failure.Reason);
    }

    [Fact]
    public async Task RunAsyncOutputLimitStopsCombinedStreams()
    {
        if (OperatingSystem.IsWindows()) return;

        var failure = await Assert.ThrowsAsync<BoundedProcessException>(() =>
            BoundedProcess.RunAsync("/bin/sh", ["-c", "printf 123456; printf 123456 >&2"], null,
                TimeSpan.FromSeconds(10), 5, [], default));

        Assert.Equal(ProcessStopReason.OutputLimit, failure.Reason);
    }

    [Fact]
    public async Task RunAsyncCancellationStopsAnActiveChildAndDrainsOwnedStreams()
    {
        if (OperatingSystem.IsWindows()) return;

        using var cancellation = new CancellationTokenSource();
        var running = BoundedProcess.RunAsync("/bin/sh", ["-c", "while :; do :; done"], null,
            TimeSpan.FromSeconds(10), 1024, [], cancellation.Token);
        await Task.Yield();
        cancellation.Cancel();

        var failure = await Assert.ThrowsAsync<BoundedProcessException>(() => running);
        Assert.Equal(ProcessStopReason.Cancelled, failure.Reason);
    }

    [Fact]
    public async Task RunAsyncActiveFloodStopsAtOutputLimitAndDrainsBothStreams()
    {
        if (OperatingSystem.IsWindows()) return;

        var failure = await Assert.ThrowsAsync<BoundedProcessException>(() =>
            BoundedProcess.RunAsync("/bin/sh", ["-c", "while :; do printf 1234567890; printf 1234567890 >&2; done"], null,
                TimeSpan.FromSeconds(10), 1024, [], default));

        Assert.Equal(ProcessStopReason.OutputLimit, failure.Reason);
    }

    [Fact]
    public async Task RunToFileBudgetFailureTerminatesActiveChildWithBothPipesOpen()
    {
        if (OperatingSystem.IsWindows()) return;

        var output = NewOutputPath();
        var marker = NewOutputPath() + ".pid";
        var script = "printf '%s' \"$$\" > '" + marker + "'; printf payload; printf diagnostic >&2; while :; do :; done";
        using var budget = TemporaryDiskBudget.BeginInvocation();
        TemporaryDiskBudget.Account(TemporaryDiskBudget.MaximumBytes - 1);

        try
        {
            var failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
                BoundedProcess.RunToFileAsync("/bin/sh", ["-c", script], output, 1024, TimeSpan.FromSeconds(10), default));

            Assert.True(File.Exists(marker));
            var pid = int.Parse(await File.ReadAllTextAsync(marker));
            AssertChildExited(pid);
            Assert.False(File.Exists(output));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(output)!, Path.GetFileName(output) + ".staging-*"));
            Assert.Contains("budget", failure.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryKillMarkerChild(marker);
            File.Delete(marker);
            File.Delete(output);
        }
    }

    [Fact]
    public async Task RunToFileBudgetFailureDoesNotPublishPartialBinaryOutput()
    {
        if (OperatingSystem.IsWindows()) return;

        var output = NewOutputPath();
        var marker = NewOutputPath() + ".pid";
        var script = "printf '%s' \"$$\" > '" + marker + "'; printf '\\000\\377payload'; printf diagnostic >&2; while :; do :; done";
        using var budget = TemporaryDiskBudget.BeginInvocation();
        TemporaryDiskBudget.Account(TemporaryDiskBudget.MaximumBytes - 1);

        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                BoundedProcess.RunToFileAsync("/bin/sh", ["-c", script], output, 1024, TimeSpan.FromSeconds(10), default));
            Assert.False(File.Exists(output));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(output)!, Path.GetFileName(output) + ".staging-*"));
        }
        finally
        {
            TryKillMarkerChild(marker);
            File.Delete(marker);
            File.Delete(output);
        }
    }

    private static void TryKillMarkerChild(string marker)
    {
        if (!File.Exists(marker) || !int.TryParse(File.ReadAllText(marker), out var pid)) return;
        try
        {
            using var child = Process.GetProcessById(pid);
            if (!child.HasExited) child.Kill(entireProcessTree: true);
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
    }

    private static void AssertChildExited(int pid)
    {
        try
        {
            using var child = Process.GetProcessById(pid);
            Assert.True(child.HasExited, $"bounded export child {pid} is still running");
        }
        catch (ArgumentException)
        {
        }
    }

    private static string NewOutputPath() => Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "keycloak-bounded-export-" + Guid.NewGuid().ToString("N"));
}
