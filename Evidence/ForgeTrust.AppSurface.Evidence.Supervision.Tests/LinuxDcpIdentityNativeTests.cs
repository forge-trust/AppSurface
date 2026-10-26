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

    [LinuxKernelIdentityNativeTests.LinuxKernelIdentityFact]
    public async Task ActualAuditedFileRejectionsAndCancellationNeverPoisonAnIndependentValidCapture()
    {
        var directory = "/run/issue779-dcp-audit-native-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(directory);
        LinuxKernelIdentityNativeTests.TestUnit? helper = null;
        try
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            var executable = Path.Join(directory, "dcp");
            CopyExecutable(executable);
            helper = LinuxKernelIdentityNativeTests.TestUnit.ForApplicationExecutable(executable);
            var pid = await helper.StartAsync();
            using var original = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var empty = Path.Join(directory, "empty");
            File.WriteAllBytes(empty, []);
            File.SetUnixFileMode(empty, UnixFileMode.UserRead | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            var writable = Path.Join(directory, "writable");
            CopyExecutable(writable);
            File.SetUnixFileMode(writable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            var nonExecutable = Path.Join(directory, "non-executable");
            File.WriteAllText(nonExecutable, "small actual non-executable bytes");
            File.SetUnixFileMode(nonExecutable, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            var link = Path.Join(directory, "link");
            File.CreateSymbolicLink(link, executable);
            foreach (var path in new[] { empty, writable, nonExecutable, link, directory, Path.Join(directory, "missing") })
            {
                LinuxDcpCaptureFailure? diagnostic = null;
                Reject(() => { using var unexpected = LinuxApplicationDcpIdentity.Capture(helper.Unit,
                    65534, 65534, path, original.Token, out diagnostic); });
                Assert.NotNull(diagnostic);
                Assert.Equal(LinuxDcpCaptureStage.AuditedExecutable, diagnostic.Stage);
                Assert.True(diagnostic.ObservedProcesses > 0);
                Assert.Equal(0, diagnostic.MatchingExecutables);
                Assert.DoesNotContain(directory, System.Text.Json.JsonSerializer.Serialize(diagnostic));
            }
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            LinuxDcpCaptureFailure? cancellationData = null;
            var error = Assert.Throws<OperationCanceledException>(() =>
            {
                using var unexpected = LinuxApplicationDcpIdentity.Capture(helper.Unit, 65534, 65534,
                    executable, cancelled.Token, out cancellationData);
            });
            Assert.Equal(cancelled.Token, error.CancellationToken);
            Assert.Equal(LinuxDcpCaptureStage.Selection, cancellationData!.Stage);
            Assert.Equal("Cancelled", cancellationData.ErrorKind);
            using var actual = LinuxApplicationDcpIdentity.Capture(helper.Unit, 65534, 65534, executable, original.Token);
            Assert.Equal(pid, actual.Pid);
            actual.Recheck(original.Token);
            await helper.StopKillAndJoinAsync();
            Reject(() => actual.Recheck(original.Token));
            Assert.True(LinuxAccountUtility.GroupEmpty(LinuxCgroupProbe.Read(helper.Unit, default)));
        }
        finally
        {
            if (helper is not null) await helper.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [LinuxKernelIdentityNativeTests.LinuxKernelIdentityFact]
    public async Task RealApplicationPeerRequiresTheSelectedExecutableAndExactResourceCommand()
    {
        var directory = "/run/issue779-dcp-resource-native-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(directory);
        LinuxKernelIdentityNativeTests.TestUnit? helper = null;
        try
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            var executable = Path.Join(directory, "dcp");
            CopyExecutable(executable);
            var unrelatedRuntime = Path.Join(directory, "unrelated-runtime");
            CopyExecutable(unrelatedRuntime); // Equally valid 0555 metadata; only the actual executable inode differs.
            helper = LinuxKernelIdentityNativeTests.TestUnit.ForApplicationExecutable(executable);
            var pid = await helper.StartAsync();
            using var original = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var resource = LinuxProcessIdentity.CaptureApplication(pid, 65534, 65534, helper.Unit, original.Token);
            // Both negative peers are genuine retained kernel identities. A worker role or another
            // application generation must never select an ancestor in this application's cgroup.
            await RejectForeignResourceAsync(LinuxKernelIdentityNativeTests.TestUnit.ForApplicationExecutable(executable), application: true);
            await RejectForeignResourceAsync(new LinuxKernelIdentityNativeTests.TestUnit(), application: false);
            resource.Recheck(original.Token);

            async Task RejectForeignResourceAsync(LinuxKernelIdentityNativeTests.TestUnit foreignUnit, bool application)
            {
                await using (foreignUnit)
                {
                    var foreignPid = await foreignUnit.StartAsync();
                    using var foreignPeer = application
                        ? LinuxProcessIdentity.CaptureApplication(foreignPid, 65534, 65534, foreignUnit.Unit, original.Token)
                        : LinuxProcessIdentity.Capture(foreignPid, 65534, 65534, foreignUnit.Unit, original.Token);
                    Assert.NotEqual(helper.Unit, foreignPeer.Unit);
                    LinuxDcpCaptureFailure? foreignFailure = null;
                    Reject(() =>
                    {
                        using var unexpected = LinuxApplicationDcpIdentity.CaptureForResource(helper.Unit,
                            65534, 65534, executable, foreignPeer, original.Token, out foreignFailure);
                    });
                    Assert.Equal(LinuxDcpCaptureStage.ParentSelection, foreignFailure!.Stage);
                    Assert.Equal(1, foreignFailure.MatchingExecutables);
                    foreignPeer.Recheck(original.Token);
                    await foreignUnit.StopKillAndJoinAsync();
                    Assert.True(LinuxAccountUtility.GroupEmpty(LinuxCgroupProbe.Read(foreignUnit.Unit, default)));
                }
            }

            // These are actual retained process identities, but sleep's two arguments are not the resource's six.
            Reject(() => LinuxApplicationDcpIdentity.RequireResourceCommand(resource, executable,
                directory + "/resource/NativeHttpResource.dll", directory + "/http.sock", original.Token));
            resource.Recheck(original.Token);
            using (var cancelled = new CancellationTokenSource())
            using (var identity = LinuxApplicationDcpIdentity.Capture(helper.Unit, 65534, 65534, executable, original.Token))
            {
                cancelled.Cancel();
                Assert.Equal(cancelled.Token, Assert.Throws<OperationCanceledException>(() =>
                    identity.RequireResourcePeer(resource, executable, cancelled.Token)).CancellationToken);
                identity.Recheck(original.Token); // Cancellation has not latched an integrity rejection.
                // The low-level peer identity check permits the same retained process. The separate resource
                // argv check above still rejects sleep, and no HTTP/application readiness is claimed here.
                identity.RequireResourcePeer(resource, executable, original.Token);
                Reject(() => identity.RequireResourcePeer(resource, unrelatedRuntime, original.Token));
                Reject(() => identity.RequireResourcePeer(resource, executable, original.Token));
                Reject(() => identity.Recheck(original.Token)); // Restoring the argument cannot clear rejection.
            }
            LinuxDcpCaptureFailure? diagnostic = null;
            Reject(() => { using var unexpected = LinuxApplicationDcpIdentity.CaptureForResource(helper.Unit,
                65534, 65534, executable, resource, original.Token, out diagnostic); });
            Assert.Equal(LinuxDcpCaptureStage.ParentSelection, diagnostic!.Stage);
            Assert.Equal(1, diagnostic.MatchingExecutables);
            resource.Recheck(original.Token);
            using (var independent = LinuxApplicationDcpIdentity.Capture(helper.Unit, 65534, 65534, executable, original.Token))
                independent.Recheck(original.Token);
            await helper.StopKillAndJoinAsync();
            Assert.True(LinuxAccountUtility.GroupEmpty(LinuxCgroupProbe.Read(helper.Unit, default)));
        }
        finally
        {
            if (helper is not null) await helper.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [LinuxKernelIdentityNativeTests.LinuxKernelIdentityFact]
    public async Task ActualSameUnitChildSelectsItsAuditedAncestorAndRetainsBothKernelWalks()
    {
        var directory = "/run/issue779-dcp-tree-native-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(directory);
        LinuxKernelIdentityNativeTests.TestUnit? helper = null;
        try
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            var executable = Path.Join(directory, "dcp");
            var runtime = Path.Join(directory, "runtime");
            File.Copy("/usr/bin/dash", executable);
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            CopyExecutable(runtime);
            helper = LinuxKernelIdentityNativeTests.TestUnit.ForApplicationProcessTree(executable, runtime);
            var parentPid = await helper.StartAsync();
            using var original = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            uint childPid;
            while (true)
            {
                original.Token.ThrowIfCancellationRequested();
                var pids = LinuxApplicationDcpIdentity.ParseProcessIds(File.ReadAllBytes(
                    "/sys/fs/cgroup" + helper.GeneratedControlGroupPath + "/cgroup.procs"));
                var children = pids.Where(pid => pid != parentPid).ToArray();
                if (children.Length == 1) { childPid = children[0]; break; }
                Assert.Empty(children);
                await Task.Delay(20, original.Token);
            }
            using var child = LinuxProcessIdentity.CaptureApplication(childPid, 65534, 65534, helper.Unit, original.Token);
            using var identity = LinuxApplicationDcpIdentity.CaptureForResource(helper.Unit, 65534, 65534,
                executable, child, original.Token, out var diagnostic);
            Assert.Null(diagnostic);
            Assert.Equal(parentPid, identity.Pid);
            Assert.NotEqual(parentPid, child.Pid);
            identity.RequireResourcePeer(child, runtime, original.Token);
            identity.Recheck(original.Token);
            child.Recheck(original.Token);
            // The real parent/child/executable relation alone still cannot stand in for the fixed resource argv.
            Reject(() => LinuxApplicationDcpIdentity.RequireResourceCommand(child, runtime,
                directory + "/resource/NativeHttpResource.dll", directory + "/http.sock", original.Token));
            identity.RequireResourcePeer(child, runtime, original.Token);
            await helper.StopKillAndJoinAsync();
            Reject(() => identity.Recheck(original.Token));
            Assert.True(LinuxAccountUtility.GroupEmpty(LinuxCgroupProbe.Read(helper.Unit, default)));
        }
        finally
        {
            if (helper is not null) await helper.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
        await RequireActualTwoHopResourceAncestryAsync();
        await RequireRepeatedExecutableAncestorSelectionAsync();
    }

    // The distinct intermediate executable forces both native walks past the resource's immediate
    // parent. All three PIDs, credentials, parent links and executable identities come from this unit.
    private static async Task RequireActualTwoHopResourceAncestryAsync()
    {
        var directory = "/run/issue779-dcp-tree-native-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(directory);
        LinuxKernelIdentityNativeTests.TestUnit? helper = null;
        try
        {
            var executableMode = UnixFileMode.UserRead | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
            File.SetUnixFileMode(directory, executableMode | UnixFileMode.UserWrite);
            var executable = Path.Join(directory, "dcp");
            var bridge = Path.Join(directory, "bridge");
            var runtime = Path.Join(directory, "runtime");
            File.Copy("/usr/bin/dash", executable);
            File.SetUnixFileMode(executable, executableMode);
            File.Copy("/usr/bin/bash", bridge);
            File.SetUnixFileMode(bridge, executableMode);
            CopyExecutable(runtime);
            helper = LinuxKernelIdentityNativeTests.TestUnit.ForApplicationGrandchildTree(executable, bridge, runtime);
            var rootPid = await helper.StartAsync();
            using var original = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            uint intermediatePid;
            uint resourcePid;
            while (true)
            {
                original.Token.ThrowIfCancellationRequested();
                var pids = LinuxApplicationDcpIdentity.ParseProcessIds(File.ReadAllBytes(
                    "/sys/fs/cgroup" + helper.GeneratedControlGroupPath + "/cgroup.procs"));
                Assert.Contains(rootPid, pids);
                Assert.InRange(pids.Length, 1, 3);
                if (pids.Length == 3)
                {
                    var links = pids.Where(pid => pid != rootPid).Select(pid =>
                        LinuxApplicationDcpIdentity.ParseParentLink(File.ReadAllBytes($"/proc/{pid}/stat"))).ToArray();
                    var intermediate = Assert.Single(links, link => link.ParentPid == rootPid);
                    var resource = Assert.Single(links, link => link.ParentPid == intermediate.Pid);
                    intermediatePid = intermediate.Pid;
                    resourcePid = resource.Pid;
                    Assert.NotEqual(rootPid, resourcePid);
                    Assert.NotEqual(intermediatePid, resourcePid);
                    break;
                }
                await Task.Delay(20, original.Token);
            }
            using var intermediateIdentity = LinuxProcessIdentity.CaptureApplication(intermediatePid,
                65534, 65534, helper.Unit, original.Token);
            using var resourceIdentity = LinuxProcessIdentity.CaptureApplication(resourcePid,
                65534, 65534, helper.Unit, original.Token);
            using var identity = LinuxApplicationDcpIdentity.CaptureForResource(helper.Unit, 65534, 65534,
                executable, resourceIdentity, original.Token, out var diagnostic);
            Assert.Null(diagnostic);
            Assert.Equal(rootPid, identity.Pid);
            Assert.NotEqual(intermediateIdentity.Pid, identity.Pid);
            identity.RequireResourcePeer(resourceIdentity, runtime, original.Token);
            identity.Recheck(original.Token);
            intermediateIdentity.Recheck(original.Token);
            resourceIdentity.Recheck(original.Token);
            await helper.StopKillAndJoinAsync();
            Reject(() => identity.Recheck(original.Token));
            Assert.True(LinuxAccountUtility.GroupEmpty(LinuxCgroupProbe.Read(helper.Unit, default)));
        }
        finally
        {
            if (helper is not null) await helper.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    // Two live processes share the exact audited executable inode. Capture stays ambiguous without a
    // resource; a genuine runtime leaf selects the nearest matching executable through fresh parent walks.
    private static async Task RequireRepeatedExecutableAncestorSelectionAsync()
    {
        var directory = "/run/issue779-dcp-tree-native-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(directory);
        LinuxKernelIdentityNativeTests.TestUnit? helper = null;
        try
        {
            var executableMode = UnixFileMode.UserRead | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
            File.SetUnixFileMode(directory, executableMode | UnixFileMode.UserWrite);
            var executable = Path.Join(directory, "dcp");
            var runtime = Path.Join(directory, "runtime");
            File.Copy("/usr/bin/dash", executable);
            File.SetUnixFileMode(executable, executableMode);
            CopyExecutable(runtime);
            helper = LinuxKernelIdentityNativeTests.TestUnit.ForApplicationRepeatedExecutableTree(executable, runtime);
            var rootPid = await helper.StartAsync();
            using var original = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            uint childPid;
            uint resourcePid;
            while (true)
            {
                original.Token.ThrowIfCancellationRequested();
                var pids = LinuxApplicationDcpIdentity.ParseProcessIds(File.ReadAllBytes(
                    "/sys/fs/cgroup" + helper.GeneratedControlGroupPath + "/cgroup.procs"));
                Assert.Contains(rootPid, pids);
                Assert.InRange(pids.Length, 1, 3);
                if (pids.Length == 3)
                {
                    var links = pids.Where(pid => pid != rootPid).Select(pid =>
                        LinuxApplicationDcpIdentity.ParseParentLink(File.ReadAllBytes($"/proc/{pid}/stat"))).ToArray();
                    var childLink = Assert.Single(links, link => link.ParentPid == rootPid);
                    var resourceLink = Assert.Single(links, link => link.ParentPid == childLink.Pid);
                    childPid = childLink.Pid;
                    resourcePid = resourceLink.Pid;
                    Assert.NotEqual(rootPid, resourcePid);
                    break;
                }
                await Task.Delay(20, original.Token);
            }

            using var root = LinuxProcessIdentity.CaptureApplication(rootPid, 65534, 65534, helper.Unit, original.Token);
            using var child = LinuxProcessIdentity.CaptureApplication(childPid, 65534, 65534, helper.Unit, original.Token);
            using var resource = LinuxProcessIdentity.CaptureApplication(resourcePid, 65534, 65534, helper.Unit, original.Token);
            LinuxDcpCaptureFailure? ambiguous = null;
            Reject(() =>
            {
                using var unexpected = LinuxApplicationDcpIdentity.Capture(helper.Unit, 65534, 65534,
                    executable, original.Token, out ambiguous);
            });
            Assert.NotNull(ambiguous);
            Assert.Equal(LinuxDcpCaptureStage.SelectedExecutable, ambiguous.Stage);
            Assert.Equal(3, ambiguous.ObservedProcesses);
            Assert.Equal(2, ambiguous.MatchingExecutables);

            using var identity = LinuxApplicationDcpIdentity.CaptureForResource(helper.Unit, 65534, 65534,
                executable, resource, original.Token, out var diagnostic);
            Assert.Null(diagnostic);
            Assert.Equal(childPid, identity.Pid);
            Assert.NotEqual(rootPid, identity.Pid);
            Assert.NotEqual(resourcePid, identity.Pid);
            identity.RequireResourcePeer(resource, runtime, original.Token);
            identity.Recheck(original.Token);
            root.Recheck(original.Token);
            child.Recheck(original.Token);
            resource.Recheck(original.Token);

            await helper.StopKillAndJoinAsync();
            Reject(() => identity.Recheck(original.Token));
            Assert.True(LinuxAccountUtility.GroupEmpty(LinuxCgroupProbe.Read(helper.Unit, default)));
        }
        finally
        {
            if (helper is not null)
            {
                await helper.DisposeAsync();
                Assert.True(LinuxAccountUtility.GroupEmpty(LinuxCgroupProbe.Read(helper.Unit, default)));
            }
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
