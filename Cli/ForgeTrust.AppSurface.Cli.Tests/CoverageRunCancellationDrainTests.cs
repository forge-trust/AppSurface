using System.Diagnostics;
using CliFx;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Cli;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageRunCancellationDrainTests
{
    [Fact]
    public async Task CallerCancellationDrain_KillsAndRetainsLeaseUntilExit()
    {
        using var console = new FakeInMemoryConsole();
        using var caller = new CancellationTokenSource();
        await using var supervisor = CreateSupervisor(console, caller.Token);
        using var operation = supervisor.Start("project");
        var lease = operation.ReserveProcess();
        using var process = Process.Start(CreateLongRunningProcess())!;
        lease.Attach(process);

        caller.Cancel();
        var result = await supervisor.TerminateAndDrainProcessesAsync();

        Assert.True(result.Confirmed);
        Assert.Equal("complete", result.Status);
        Assert.True(process.HasExited);
        Assert.Equal(0, supervisor.RegisteredProcessLeaseCount);
    }

    [Fact]
    public async Task CancellationBeforeAttach_KillsLateProcessAndConfirmsDrain()
    {
        using var console = new FakeInMemoryConsole();
        using var caller = new CancellationTokenSource();
        await using var supervisor = CreateSupervisor(console, caller.Token);
        using var operation = supervisor.Start("project");
        var lease = operation.ReserveProcess();
        caller.Cancel();
        var drain = supervisor.TerminateAndDrainProcessesAsync();
        await Task.Yield();
        using var process = Process.Start(CreateLongRunningProcess())!;
        lease.Attach(process);

        var result = await drain;
        lease.Complete();

        Assert.True(result.Confirmed);
        Assert.True(process.HasExited);
        Assert.Equal(0, supervisor.RegisteredProcessLeaseCount);
    }

    [Fact]
    public async Task AlreadyExitedProcess_ConfirmsDrainWithoutChangingPrimaryCancellation()
    {
        using var console = new FakeInMemoryConsole();
        using var caller = new CancellationTokenSource();
        await using var supervisor = CreateSupervisor(console, caller.Token);
        using var operation = supervisor.Start("project");
        var lease = operation.ReserveProcess();
        using var process = Process.Start(CreateCompletedProcess())!;
        await process.WaitForExitAsync();
        lease.Attach(process);
        caller.Cancel();

        var result = await supervisor.TerminateAndDrainProcessesAsync();

        Assert.True(result.Confirmed);
        Assert.True(caller.IsCancellationRequested);
    }

    [Fact]
    public async Task CleanupTimeout_ReturnsUnconfirmedAndLeavesLeaseRegistered()
    {
        using var console = new FakeInMemoryConsole();
        using var caller = new CancellationTokenSource();
        await using var supervisor = CreateSupervisor(console, caller.Token, TimeSpan.FromMilliseconds(40), _ => { });
        using var operation = supervisor.Start("project");
        var lease = operation.ReserveProcess();
        using var process = Process.Start(CreateLongRunningProcess())!;
        lease.Attach(process);

        var result = await supervisor.TerminateAndDrainProcessesAsync();
        lease.Complete();

        Assert.False(result.Confirmed);
        Assert.Equal("deadline-exceeded", result.Status);
        Assert.Equal(1, supervisor.RegisteredProcessLeaseCount);

        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
        await WaitForLeaseReleaseAsync(supervisor);
        Assert.Equal(0, supervisor.RegisteredProcessLeaseCount);
    }

    [Fact]
    public async Task KillerFailure_ReturnsFailedAndKeepsLeaseRegistered()
    {
        using var console = new FakeInMemoryConsole();
        using var caller = new CancellationTokenSource();
        await using var supervisor = CreateSupervisor(console, caller.Token, processKiller: _ => throw new IOException("test failure"));
        using var operation = supervisor.Start("project");
        var lease = operation.ReserveProcess();
        using var process = Process.Start(CreateLongRunningProcess())!;
        lease.Attach(process);

        var result = await supervisor.TerminateAndDrainProcessesAsync();
        lease.Complete();
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();

        Assert.False(result.Confirmed);
        Assert.Equal("failed", result.Status);
        Assert.Equal(1, supervisor.RegisteredProcessLeaseCount);
    }

    [Fact]
    public async Task ConcurrentDrainCallersShareOneCleanupAndPreserveCancellation()
    {
        using var console = new FakeInMemoryConsole();
        using var caller = new CancellationTokenSource();
        var kills = 0;
        await using var supervisor = CreateSupervisor(console, caller.Token, processKiller: process =>
        {
            Interlocked.Increment(ref kills);
            process.Kill(entireProcessTree: true);
        });
        using var operation = supervisor.Start("project");
        var lease = operation.ReserveProcess();
        using var process = Process.Start(CreateLongRunningProcess())!;
        lease.Attach(process);
        caller.Cancel();

        var first = supervisor.TerminateAndDrainProcessesAsync();
        var second = supervisor.TerminateAndDrainProcessesAsync();
        var results = await Task.WhenAll(first, second);

        Assert.Same(first, second);
        Assert.All(results, result => Assert.True(result.Confirmed));
        Assert.Equal(1, Volatile.Read(ref kills));
        Assert.True(caller.IsCancellationRequested);
    }

    [Fact]
    public async Task CancellationAndDrainPreventNewProcessLeases()
    {
        using var console = new FakeInMemoryConsole();
        using var caller = new CancellationTokenSource();
        await using var supervisor = CreateSupervisor(console, caller.Token);
        using var operation = supervisor.Start("project");

        caller.Cancel();
        var drain = await supervisor.TerminateAndDrainProcessesAsync();

        Assert.True(drain.Confirmed);
        Assert.ThrowsAny<OperationCanceledException>(() => operation.ReserveProcess());
        Assert.ThrowsAny<OperationCanceledException>(() => supervisor.Start("project"));
        Assert.Equal(0, supervisor.RegisteredProcessLeaseCount);
    }

    [Fact]
    public async Task ExplicitDrainPreventsLateProcessLeaseEvenWithoutCancellation()
    {
        using var console = new FakeInMemoryConsole();
        await using var supervisor = CreateSupervisor(console, CancellationToken.None);
        using var operation = supervisor.Start("project");

        var drain = await supervisor.TerminateAndDrainProcessesAsync();

        Assert.True(drain.Confirmed);
        Assert.ThrowsAny<OperationCanceledException>(() => operation.ReserveProcess());
        Assert.ThrowsAny<OperationCanceledException>(() => supervisor.Start("project"));
        Assert.Equal(0, supervisor.RegisteredProcessLeaseCount);
    }

    private static CoverageRunWatchdogSupervisor CreateSupervisor(
        FakeInMemoryConsole console,
        CancellationToken cancellationToken,
        TimeSpan? cleanupTimeout = null,
        Action<Process>? processKiller = null)
        => new(
            CoverageRunWatchdogMode.Off,
            TimeSpan.Zero,
            TimeSpan.FromMinutes(1),
            CoverageTextWriters.Create(console.Output, console.Error),
            TimeProvider.System,
            cancellationToken,
            processKiller: processKiller,
            processCleanupTimeout: cleanupTimeout);

    private static ProcessStartInfo CreateLongRunningProcess()
        => OperatingSystem.IsWindows()
            ? new ProcessStartInfo("ping.exe", "127.0.0.1 -n 30") { UseShellExecute = false, CreateNoWindow = true }
            : new ProcessStartInfo("/bin/sleep", "30") { UseShellExecute = false };

    private static ProcessStartInfo CreateCompletedProcess()
        => OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c exit 0") { UseShellExecute = false, CreateNoWindow = true }
            : new ProcessStartInfo("/bin/sh", "-c \"exit 0\"") { UseShellExecute = false };

    private static async Task WaitForLeaseReleaseAsync(CoverageRunWatchdogSupervisor supervisor)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (supervisor.RegisteredProcessLeaseCount != 0)
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
