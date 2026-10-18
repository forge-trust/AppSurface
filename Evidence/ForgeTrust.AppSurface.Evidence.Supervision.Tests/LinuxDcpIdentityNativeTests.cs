using System.Runtime.Versioning;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Actual root Linux executable/proc/cgroup selection, not an Aspire DCP readiness or accepted-proof fixture.</summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxDcpIdentityNativeTests
{
    [LinuxKernelIdentityNativeTests.LinuxKernelIdentityFact]
    public async Task RetainedExecutableSelectionRejectsWrongCredentialsUnrelatedFilesAndNameSubstitution()
    {
        var directory = "/run/issue779-dcp-root-native-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(directory);
        LinuxKernelIdentityNativeTests.TestUnit? helper = null;
        try
        {
            // The executable is an actual copy of sleep, not a claimed SDK DCP payload.
            // Native selection checks file identity; the separate C3 consumers qualify real SDK DCP execution.
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            var executable = Path.Join(directory, "dcp");
            var replacement = Path.Join(directory, "replacement");
            CopyExecutable(executable);
            CopyExecutable(replacement);
            helper = LinuxKernelIdentityNativeTests.TestUnit.ForApplicationExecutable(executable);
            var pid = await helper.StartAsync();
            using var original = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var identity = LinuxApplicationDcpIdentity.Capture(helper.Unit, 65534, 65534, executable, original.Token);
            Assert.Equal(pid, identity.Pid);
            identity.Recheck(original.Token);

            Reject(() => { using var unexpected = LinuxApplicationDcpIdentity.Capture(helper.Unit, 65533, 65534, executable, original.Token); });
            Reject(() => { using var unexpected = LinuxApplicationDcpIdentity.Capture(helper.Unit, 65534, 65533, executable, original.Token); });
            LinuxDcpCaptureFailure? diagnostic = null;
            Reject(() => { using var unexpected = LinuxApplicationDcpIdentity.Capture(helper.Unit, 65534, 65534, replacement, original.Token, out diagnostic); });
            Assert.NotNull(diagnostic);
            Assert.Equal(0, diagnostic.MatchingExecutables);
            Assert.True(diagnostic.ObservedProcesses > 0);

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var cancellation = Assert.Throws<OperationCanceledException>(() => identity.Recheck(cancelled.Token));
            Assert.Equal(cancelled.Token, cancellation.CancellationToken);
            identity.Recheck(original.Token); // Cancellation alone does not latch identity rejection.

            // Isolate disposal from the substitution latch while the independent unit owner remains live.
            using (var disposed = LinuxApplicationDcpIdentity.Capture(helper.Unit, 65534, 65534, executable, original.Token))
            {
                Assert.Equal(pid, disposed.Pid);
                disposed.Recheck(original.Token);
                disposed.Dispose();
                disposed.Dispose();
                Reject(() => disposed.Recheck(original.Token));
            }

            var backup = Path.Join(directory, "original");
            File.Move(executable, backup);
            File.Move(replacement, executable);
            Reject(() => identity.Recheck(original.Token));
            File.Move(backup, executable, overwrite: true);
            Reject(() => identity.Recheck(original.Token)); // Restoring the original inode cannot clear the latch.
            await helper.StopKillAndJoinAsync();
            Assert.True(LinuxAccountUtility.GroupEmpty(LinuxCgroupProbe.Read(helper.Unit, default)));
        }
        finally
        {
            // Pending ownership was retained before StartAsync. Failed teardown preserves the owned directory.
            if (helper is not null) await helper.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void CopyExecutable(string path)
    {
        File.Copy("/usr/bin/sleep", path);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    private static void Reject(Action action)
    {
        var error = Assert.Throws<EvidenceAdmissionException>(action);
        Assert.Equal("ASEVD410", error.Code);
        Assert.Null(error.InnerException);
    }
}
