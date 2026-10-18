using ForgeTrust.AppSurface.Evidence.Contracts;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>
/// Root Linux x64 integration controls for real proc descriptors and systemd cgroup-v2 sampling.
/// These tests exercise sampling only; they do not establish owner custody or application admission.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxKernelIdentityNativeTests
{
    private const uint Nobody = 65534;

    [LinuxKernelIdentityFact]
    public async Task CaptureRetainsActualHelperFactsRechecksAndRejectsAfterDispose()
    {
        await using var helper = new TestUnit();
        var pid = await helper.StartAsync();
        using var identity = LinuxProcessIdentity.Capture(pid, Nobody, Nobody, helper.Unit, default);

        Assert.Equal(pid, identity.Pid);
        Assert.Equal(Nobody, identity.Uid);
        Assert.Equal(Nobody, identity.Gid);
        Assert.NotEqual(0UL, identity.StartTimeTicks);
        Assert.Same(helper.Unit, identity.Unit);
        Assert.Equal("/system.slice/" + helper.Unit.Value, identity.ControlGroup);
        Assert.Equal(Nobody, identity.SampledFacts.Uids.Real);
        Assert.Equal(Nobody, identity.SampledFacts.Uids.Effective);
        Assert.Equal(Nobody, identity.SampledFacts.Uids.Saved);
        Assert.Equal(Nobody, identity.SampledFacts.Uids.FileSystem);
        Assert.Equal(Nobody, identity.SampledFacts.Gids.Real);
        Assert.Equal(Nobody, identity.SampledFacts.Gids.Effective);
        Assert.Equal(Nobody, identity.SampledFacts.Gids.Saved);
        Assert.Equal(Nobody, identity.SampledFacts.Gids.FileSystem);

        identity.Recheck(default);
        identity.Dispose();
        var rejected = Assert.Throws<EvidenceAdmissionException>(() => identity.Recheck(default));
        Assert.Equal("ASEVD402", rejected.Code);
        Assert.Null(rejected.InnerException);
    }

    [LinuxKernelIdentityFact]
    public async Task CaptureRejectsActualHelperWithWrongUidGidOrGeneratedUnit()
    {
        await using var helper = new TestUnit();
        var pid = await helper.StartAsync();

        AssertCaptureRejected(pid, Nobody - 1, Nobody, helper.Unit);
        AssertCaptureRejected(pid, Nobody, Nobody - 1, helper.Unit);
        AssertCaptureRejected(pid, Nobody, Nobody,
            LinuxUnitName.Create(LinuxUnitRole.Worker, Guid.NewGuid()));
    }

    [LinuxKernelIdentityFact]
    public async Task CancelledCapturePreservesCancellationAndDoesNotPreventLaterCapture()
    {
        await using var helper = new TestUnit();
        var pid = await helper.StartAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var cancelled = Assert.Throws<OperationCanceledException>(() =>
            LinuxProcessIdentity.Capture(pid, Nobody, Nobody, helper.Unit, cancellation.Token));
        Assert.Equal(cancellation.Token, cancelled.CancellationToken);

        using var identity = LinuxProcessIdentity.Capture(pid, Nobody, Nobody, helper.Unit, default);
        identity.Recheck(default);
    }

    [LinuxKernelIdentityFact]
    public async Task CancelledRecheckPreservesCancellationWithoutLatchingIdentityFailure()
    {
        await using var helper = new TestUnit();
        var pid = await helper.StartAsync();
        using var identity = LinuxProcessIdentity.Capture(pid, Nobody, Nobody, helper.Unit, default);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var cancelled = Assert.Throws<OperationCanceledException>(() => identity.Recheck(cancellation.Token));
        Assert.Equal(cancellation.Token, cancelled.CancellationToken);
        Assert.Null(identity.FirstFailure);
        identity.Recheck(default);
        Assert.Null(identity.FirstFailure);
    }

    [LinuxKernelIdentityFact]
    public async Task RecheckRejectsActualHelperAfterItsOwnedUnitStops()
    {
        await using var helper = new TestUnit();
        var pid = await helper.StartAsync();
        using var identity = LinuxProcessIdentity.Capture(pid, Nobody, Nobody, helper.Unit, default);

        await helper.StopKillAndJoinAsync();

        var rejected = Assert.Throws<EvidenceAdmissionException>(() => identity.Recheck(default));
        Assert.Equal("ASEVD402", rejected.Code);
        Assert.NotNull(identity.FirstFailure);
    }

    [LinuxKernelIdentityFact]
    public async Task CgroupReadObservesOwnedHelperThenEmptyOrAbsentLeafAfterStop()
    {
        await using var helper = new TestUnit();
        _ = await helper.StartAsync();
        var generatedPath = "/system.slice/" + helper.Unit.Value;

        var live = LinuxCgroupProbe.Read(helper.Unit, default);
        Assert.True(live.Exists);
        Assert.True(live.Populated);
        Assert.Equal(generatedPath, helper.GeneratedControlGroupPath);
        Assert.NotNull(live.DeviceMajor);
        Assert.NotNull(live.DeviceMinor);
        Assert.NotEqual(0UL, live.KernelInode);

        await helper.StopKillAndJoinAsync();

        var stopped = LinuxCgroupProbe.Read(helper.Unit, default);
        Assert.Equal(generatedPath, helper.GeneratedControlGroupPath);
        if (stopped.Exists)
        {
            Assert.False(stopped.Populated);
            Assert.NotNull(stopped.DeviceMajor);
            Assert.NotNull(stopped.DeviceMinor);
            Assert.NotEqual(0UL, stopped.KernelInode);
        }
        else
        {
            Assert.Null(stopped.Populated);
            Assert.Null(stopped.Frozen);
            Assert.Null(stopped.DeviceMajor);
            Assert.Null(stopped.DeviceMinor);
            Assert.Null(stopped.KernelInode);
        }
    }

    [LinuxKernelIdentityFact]
    public void CgroupReadReportsAbsentFreshGeneratedLeafWithoutCreatingIt()
    {
        var unit = LinuxUnitName.Create(LinuxUnitRole.Worker, Guid.NewGuid());

        var absent = LinuxCgroupProbe.Read(unit, default);

        Assert.False(absent.Exists);
        Assert.Null(absent.Populated);
        Assert.Null(absent.Frozen);
        Assert.Null(absent.DeviceMajor);
        Assert.Null(absent.DeviceMinor);
        Assert.Null(absent.KernelInode);
    }

    private static void AssertCaptureRejected(uint pid, uint uid, uint gid, LinuxUnitName unit)
    {
        var rejected = Assert.Throws<EvidenceAdmissionException>(() =>
            LinuxProcessIdentity.Capture(pid, uid, gid, unit, default));
        Assert.Equal("ASEVD402", rejected.Code);
        Assert.Null(rejected.InnerException);
    }

    private sealed class TestUnit : IAsyncDisposable
    {
        private static readonly TimeSpan CommandLimit = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan StartLimit = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan JoinLimit = TimeSpan.FromSeconds(15);
        private Task? _stop;
        private readonly Stopwatch _startClock = new();

        internal TestUnit() => Unit = LinuxUnitName.Create(LinuxUnitRole.Worker, Guid.NewGuid());

        internal LinuxUnitName Unit { get; }
        internal string GeneratedControlGroupPath => "/system.slice/" + Unit.Value;

        internal async Task<uint> StartAsync()
        {
            _startClock.Start();
            var result = await RunCommandAsync("/usr/bin/systemd-run",
            [
                "--system", "--service-type=exec", "--unit=" + Unit.Value,
                "--property=User=" + Nobody,
                "--property=Group=" + Nobody,
                "--property=NoNewPrivileges=yes",
                "--property=CapabilityBoundingSet=",
                "--property=AmbientCapabilities=",
                "--property=PrivateTmp=yes",
                "--property=RuntimeMaxSec=30s",
                "--property=TimeoutStopSec=2s",
                "--property=SendSIGKILL=yes",
                "--property=KillMode=control-group",
                "--property=RemainAfterExit=yes",
                "--property=Restart=no",
                "/usr/bin/sleep", "30",
            ], StartLimit).ConfigureAwait(false);
            RequireSuccess(result);

            while (_startClock.Elapsed < StartLimit)
            {
                var remaining = StartLimit - _startClock.Elapsed;
                var show = await RunCommandAsync("/usr/bin/systemctl",
                    ["--system", "show", "--property=MainPID", "--value", Unit.Value], remaining)
                    .ConfigureAwait(false);
                if (show.ExitCode == 0 && TryReadPid(show.StandardOutput, out var pid) && pid != 0)
                    return pid;
                remaining = StartLimit - _startClock.Elapsed;
                if (remaining > TimeSpan.Zero)
                    await Task.Delay(remaining < TimeSpan.FromMilliseconds(20)
                        ? remaining : TimeSpan.FromMilliseconds(20)).ConfigureAwait(false);
            }

            throw new TimeoutException("The owned systemd helper did not acquire a MainPID within five seconds.");
        }

        internal Task StopKillAndJoinAsync() => _stop ??= StopCoreAsync();

        private async Task StopCoreAsync()
        {
            // One shared teardown: do not query collected unit metadata or restart an elapsed budget.
            var clock = Stopwatch.StartNew();
            Exception? failure = null;
            try
            {
                var stop = await RunCommandAsync("/usr/bin/systemctl", ["--system", "stop", Unit.Value],
                    Remaining()).ConfigureAwait(false);
                RequireSuccess(stop);
            }
            catch (Exception error) { failure = error; }
            try
            {
                // KILL still runs after stop timeout/failure. Inactive/collected units may reject KILL.
                _ = await RunCommandAsync("/usr/bin/systemctl",
                    ["--system", "kill", "--kill-whom=all", "--signal=KILL", Unit.Value],
                    Remaining()).ConfigureAwait(false);
            }
            catch (Exception error) { failure ??= error; }
            while (clock.Elapsed < JoinLimit)
            {
                try
                {
                    if (LinuxAccountUtility.GroupEmpty(LinuxCgroupProbe.Read(Unit, default)))
                    {
                        if (failure is not null) throw new InvalidOperationException("The owned helper stop command did not settle cleanly.", failure);
                        return;
                    }
                }
                catch (EvidenceAdmissionException) { } // Pruning during a sample requires another fresh sample.
                var remaining = JoinLimit - clock.Elapsed;
                if (remaining > TimeSpan.Zero)
                    await Task.Delay(remaining < TimeSpan.FromMilliseconds(20)
                        ? remaining : TimeSpan.FromMilliseconds(20)).ConfigureAwait(false);
            }
            throw new TimeoutException("The owned systemd helper could not be joined within its teardown allowance.");

            TimeSpan Remaining()
            {
                var remaining = JoinLimit - clock.Elapsed;
                if (remaining <= TimeSpan.Zero) throw new TimeoutException("The owned helper teardown allowance expired.");
                return remaining < CommandLimit ? remaining : CommandLimit;
            }
        }

        public async ValueTask DisposeAsync() => await StopKillAndJoinAsync().ConfigureAwait(false);

        private static async Task<CommandResult> RunCommandAsync(string executable, string[] arguments, TimeSpan timeout)
        {
            if (timeout <= TimeSpan.Zero) throw new TimeoutException("The fixed helper command allowance expired.");
            using var deadline = new CancellationTokenSource(timeout < CommandLimit ? timeout : CommandLimit);
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                }
            };
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            if (!process.Start()) throw new InvalidOperationException("Could not start a fixed systemd helper command.");
            var stdout = DrainAsync(process.StandardOutput.BaseStream, deadline.Token);
            var stderr = DrainAsync(process.StandardError.BaseStream, deadline.Token);
            Exception? failure = null;
            BoundedOutput[]? outputs = null;
            try
            {
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                outputs = await Task.WhenAll(stdout, stderr).WaitAsync(deadline.Token).ConfigureAwait(false);
                if (outputs.Any(output => output.Exceeded))
                    throw new InvalidOperationException("A fixed helper command exceeded its output limit.");
            }
            catch (Exception error) { failure = error; }
            finally
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception error) { failure ??= error; }
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (Exception error) { failure ??= error; }
                if (!stdout.IsCompleted || !stderr.IsCompleted)
                {
                    try { process.StandardOutput.Dispose(); }
                    catch (Exception error) { failure ??= error; }
                    try { process.StandardError.Dispose(); }
                    catch (Exception error) { failure ??= error; }
                }
                try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (Exception error) { failure ??= error; }
            }
            if (failure is not null)
                throw new InvalidOperationException("The fixed helper command or its original output tasks did not finish.", failure);
            return new(process.ExitCode, outputs![0].Bytes);
        }

        private static async Task<BoundedOutput> DrainAsync(Stream stream, CancellationToken token)
        {
            const int maximum = 16 * 1024;
            var buffer = new byte[4096];
            using var captured = new MemoryStream(maximum);
            var exceeded = false;
            while (true)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                if (count == 0) return new(captured.ToArray(), exceeded);
                var allowed = Math.Min(count, maximum - (int)captured.Length);
                if (allowed > 0) captured.Write(buffer, 0, allowed);
                if (allowed != count) exceeded = true;
            }
        }

        private static bool TryReadPid(byte[] bytes, out uint pid)
        {
            var text = Encoding.ASCII.GetString(bytes).Trim();
            return uint.TryParse(text, out pid);
        }

        private static void RequireSuccess(CommandResult result)
        {
            if (result.ExitCode != 0)
                throw new InvalidOperationException("The fixed systemd-run command failed.");
        }

        private sealed record CommandResult(int ExitCode, byte[] StandardOutput);
        private sealed record BoundedOutput(byte[] Bytes, bool Exceeded);
    }

    private sealed class LinuxKernelIdentityFactAttribute : FactAttribute
    {
        public LinuxKernelIdentityFactAttribute()
        {
            if (!OperatingSystem.IsLinux()
                || RuntimeInformation.ProcessArchitecture != Architecture.X64
                || RuntimeInformation.OSArchitecture != Architecture.X64)
            {
                Skip = "Requires Linux x64 for native procfs and cgroup-v2 sampling.";
                return;
            }

            if (GetUid() != 0 || GetEffectiveUid() != 0 || !Directory.Exists("/run/systemd/system"))
                Skip = "Requires a root test process and the booted host systemd manager.";
        }

        [DllImport("libc", EntryPoint = "getuid")]
        private static extern uint GetUid();

        [DllImport("libc", EntryPoint = "geteuid")]
        private static extern uint GetEffectiveUid();
    }
}
