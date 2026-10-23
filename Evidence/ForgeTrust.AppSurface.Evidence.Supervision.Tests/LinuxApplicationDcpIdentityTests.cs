using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Pure DCP identity controls and a conditional native root Linux x64 executable fixture.</summary>
public sealed class LinuxApplicationDcpIdentityTests
{
    private static readonly Guid RunId = Guid.Parse("d46a7df7-3eaf-4bf2-a94e-11129dc4824a");
    private static readonly LinuxUnitName Unit = LinuxUnitName.Create(LinuxUnitRole.Application, RunId);
    private const string RuntimeHost = "/run/root/runtime/dotnet";
    private const string ResourceDll = "/run/root/bundle/resource/NativeHttpResource.dll";
    private const string SocketPath = "/run/root/scratch/http.sock";

    [Fact]
    public void CgroupProcessListIsBoundedCanonicalUniqueAndComplete()
    {
        var maximum = string.Join("\n", Enumerable.Range(1, LinuxApplicationDcpIdentity.MaximumProcesses)) + "\n";
        Assert.Equal(LinuxApplicationDcpIdentity.MaximumProcesses,
            LinuxApplicationDcpIdentity.ParseProcessIds(Encoding.ASCII.GetBytes(maximum)).Length);
        Assert.Equal(new uint[] { 1, int.MaxValue },
            LinuxApplicationDcpIdentity.ParseProcessIds("1\n2147483647\n"u8));

        foreach (var malformed in new[]
        {
            "", "1", "\n", "0\n", "+1\n", "-1\n", "01\n", "01x\n", "1x\n", "2147483648\n",
            "1\n1\n", string.Join("\n", Enumerable.Range(1, LinuxApplicationDcpIdentity.MaximumProcesses + 1)) + "\n",
        })
            Reject(() => LinuxApplicationDcpIdentity.ParseProcessIds(Encoding.ASCII.GetBytes(malformed)));
        Reject(() => LinuxApplicationDcpIdentity.ParseProcessIds(new byte[LinuxApplicationDcpIdentity.MaximumProcessBytes + 1]));
    }

    [Fact]
    public void ExecutableMetadataRequiresExactAuditedRootReadOnlyRegularFileIdentity()
    {
        var expected = new LinuxDcpFileMetadata(8, 1, 42, 0x8000, 0, 0x816d, 8192);
        LinuxApplicationDcpIdentity.RequireAuditedDcp(expected);
        Assert.True(LinuxApplicationDcpIdentity.SameExecutable(expected, expected));
        foreach (var altered in new[]
        {
            expected with { DeviceMajor = 9 }, expected with { DeviceMinor = 2 },
            expected with { Inode = 0 }, expected with { Inode = 43 },
            expected with { Type = 0x4000 }, expected with { Uid = 1 },
            expected with { Mode = 0x81a4 }, expected with { Length = 0 },
            expected with { Length = 8193 },
        })
        {
            Assert.False(LinuxApplicationDcpIdentity.SameExecutable(altered, expected));
            if (altered.Inode == 0 || altered.Type != 0x8000 || altered.Uid != 0
                || altered.Mode != 0x816d || altered.Length == 0)
                Reject(() => LinuxApplicationDcpIdentity.RequireAuditedDcp(altered));
            else LinuxApplicationDcpIdentity.RequireAuditedDcp(altered); // Structurally valid, but not the selected inode/length.
        }
    }

    [Fact]
    public void CandidateContinuityBindsPidStartTimeEveryUidGidAndExactApplicationUnit()
    {
        var before = Sample();
        var after = before with { Stat = before.Stat with { State = 'R' } };
        LinuxApplicationDcpIdentity.RequireCandidateContinuity(before, after, 123, 65010, 65011, Unit);
        Reject(() => LinuxApplicationDcpIdentity.RequireCandidateContinuity(before,
            after with { Stat = after.Stat with { Pid = 124 } }, 123, 65010, 65011, Unit));
        Reject(() => LinuxApplicationDcpIdentity.RequireCandidateContinuity(before,
            after with { Stat = after.Stat with { StartTimeTicks = 445 } }, 123, 65010, 65011, Unit));
        Reject(() => LinuxApplicationDcpIdentity.RequireCandidateContinuity(before,
            after with { Uids = new(65010, 65010, 65010, 0) }, 123, 65010, 65011, Unit));
        Reject(() => LinuxApplicationDcpIdentity.RequireCandidateContinuity(before,
            after with { Gids = new(65011, 65011, 0, 65011) }, 123, 65010, 65011, Unit));
        Reject(() => LinuxApplicationDcpIdentity.RequireCandidateContinuity(before,
            after with { ControlGroup = "/system.slice/other.service" }, 123, 65010, 65011, Unit));
        Reject(() => LinuxApplicationDcpIdentity.RequireCandidateContinuity(before,
            after with { Stat = after.Stat with { State = 'Z' } }, 123, 65010, 65011, Unit));
    }

    [Fact]
    public void ResourceCommandRequiresTheExactBoundedSixArgumentNulVector()
    {
        var valid = CommandBytes(RuntimeHost, ResourceDll, SocketPath);
        LinuxApplicationDcpIdentity.RequireResourceCommandBytes(valid, RuntimeHost, ResourceDll, SocketPath);
        Reject(() => LinuxApplicationDcpIdentity.RequireResourceCommandBytes(
            CommandBytes("/run/other/runtime/dotnet", ResourceDll, SocketPath), RuntimeHost, ResourceDll, SocketPath));
        Reject(() => LinuxApplicationDcpIdentity.RequireResourceCommandBytes(
            CommandBytes(RuntimeHost, "/run/root/bundle/resource/Other.dll", SocketPath), RuntimeHost, ResourceDll, SocketPath));
        Reject(() => LinuxApplicationDcpIdentity.RequireResourceCommandBytes(
            CommandBytes(RuntimeHost, ResourceDll, "/run/other/http.sock"), RuntimeHost, ResourceDll, SocketPath));
        Reject(() => LinuxApplicationDcpIdentity.RequireResourceCommandBytes(
            Array.Empty<byte>(), RuntimeHost, ResourceDll, SocketPath));
        Reject(() => LinuxApplicationDcpIdentity.RequireResourceCommandBytes(
            new byte[] { 0 }, RuntimeHost, ResourceDll, SocketPath));
        Reject(() => LinuxApplicationDcpIdentity.RequireResourceCommandBytes(
            Encoding.UTF8.GetBytes(RuntimeHost + "\0--socket\0" + SocketPath + "\0" + ResourceDll + "\0--case\0normal\0"),
            RuntimeHost, ResourceDll, SocketPath));
        Reject(() => LinuxApplicationDcpIdentity.RequireResourceCommandBytes([.. valid, (byte)'x'], RuntimeHost, ResourceDll, SocketPath));
        Reject(() => LinuxApplicationDcpIdentity.RequireResourceCommandBytes([.. valid, (byte)0], RuntimeHost, ResourceDll, SocketPath));
        Reject(() => LinuxApplicationDcpIdentity.RequireResourceCommandBytes(valid[..^1], RuntimeHost, ResourceDll, SocketPath));
        Reject(() => LinuxApplicationDcpIdentity.RequireResourceCommandBytes(
            new byte[LinuxApplicationDcpIdentity.MaximumProcessBytes + 1], RuntimeHost, ResourceDll, SocketPath));
        Reject(() => LinuxApplicationDcpIdentity.RequireResourceCommandBytes(valid, RuntimeHost,
            "/run/root/bundle/other/NativeHttpResource.dll", SocketPath));
    }

    [Theory]
    [InlineData(0, "Io", 13)]
    [InlineData(1, "Unsupported", 38)]
    [InlineData(2, "Other", null)]
    [InlineData(3, "AccessDenied", null)]
    public void CaptureDiagnosticsKeepOnlyClosedCheckpointCountsAndKnownErrno(int variant, string kind, int? errno)
    {
        Exception error = variant switch
        {
            0 => new IOException("CANARY-io", new System.ComponentModel.Win32Exception(13)),
            1 => new PlatformNotSupportedException("CANARY-unsupported", new System.ComponentModel.Win32Exception(38)),
            3 => new UnauthorizedAccessException("CANARY-access-denied"),
            _ => new InvalidOperationException("CANARY-private-exception"),
        };
        var data = LinuxApplicationDcpIdentity.CaptureFailure(LinuxDcpCaptureStage.CandidateExecutable, error, 4, 0);
        Assert.Equal(LinuxDcpCaptureStage.CandidateExecutable, data.Stage);
        Assert.Equal(kind, data.ErrorKind);
        Assert.Equal(errno, data.NativeErrno);
        Assert.Equal(4, data.ObservedProcesses);
        Assert.Equal(0, data.MatchingExecutables);
        var bytes = EvidenceCanonicalJson.Serialize(data);
        Assert.True(bytes.Length <= 1024);
        Assert.DoesNotContain("CANARY", System.Text.Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void PreCanceledCapturePropagatesCancellationWithOnlyClosedSelectionDiagnostics()
    {
        var token = new CancellationToken(canceled: true);
        LinuxDcpCaptureFailure? diagnostic = null;
        var error = Assert.Throws<OperationCanceledException>(() =>
            LinuxApplicationDcpIdentity.Capture(Unit, 65010, 65011, "/run/CANARY/dcp", token, out diagnostic));

        Assert.Equal(token, error.CancellationToken);
        Assert.Equal(LinuxDcpCaptureStage.Selection, diagnostic?.Stage);
        Assert.Equal("Cancelled", diagnostic?.ErrorKind);
        Assert.Null(diagnostic?.NativeErrno);
        Assert.Equal(0, diagnostic?.ObservedProcesses);
        Assert.Equal(0, diagnostic?.MatchingExecutables);
        Assert.DoesNotContain("CANARY", Encoding.UTF8.GetString(EvidenceCanonicalJson.Serialize(diagnostic!)));

        var commandError = Assert.Throws<OperationCanceledException>(() =>
            LinuxApplicationDcpIdentity.RequireResourceCommand(null!, RuntimeHost, ResourceDll, SocketPath, token));
        Assert.Equal(token, commandError.CancellationToken);
    }

    [Fact]
    public void ResourceCaptureRejectsMissingAuthenticatedPeerBeforeNativeCapture()
    {
        var error = Assert.Throws<EvidenceAdmissionException>(() => LinuxApplicationDcpIdentity.CaptureForResource(
            Unit, 65010, 65011, "/run/CANARY/dcp", null!, CancellationToken.None, out _));

        Assert.Equal("ASEVD410", error.Code);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("CANARY", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeHostMetadataRequiresRootOwnedReadonlyExecutableAndExactIdentity()
    {
        var expected = new LinuxDcpFileMetadata(8, 1, 42, 0x8000, 0, 0x816d, 8192);
        LinuxApplicationDcpIdentity.RequireAuditedRuntimeHost(expected);
        Assert.True(LinuxApplicationDcpIdentity.SameExecutable(expected, expected));
        Assert.False(LinuxApplicationDcpIdentity.SameExecutable(expected with { DeviceMajor = 9 }, expected));
        Assert.False(LinuxApplicationDcpIdentity.SameExecutable(expected with { Inode = 43 }, expected));
        foreach (var invalid in new[]
        {
            expected with { Inode = 0 }, expected with { Type = 0x4000 },
            expected with { Uid = 1 }, expected with { Mode = 0x81ed },
            expected with { Mode = 0x8124 }, expected with { Length = 0 },
        })
            Reject(() => LinuxApplicationDcpIdentity.RequireAuditedRuntimeHost(invalid));
    }

    [Fact]
    public void MultipleAuditedDcpCandidatesSelectTheNearestKernelAncestorRatherThanArrayOrder()
    {
        var resource = Sample(123, 444);
        var near = Sample(121, 442);
        var far = Sample(120, 441);
        LinuxApplicationDcpIdentity.LinuxApplicationParentFact[] chain =
        [new(123, 122, resource), new(122, 121, Sample(122, 443))];
        Assert.Equal(121u, LinuxApplicationDcpIdentity.FindNearestDcpAncestor(chain, resource,
            [far, near], 65010, 65011, Unit));
        Assert.Equal(121u, LinuxApplicationDcpIdentity.FindNearestDcpAncestor(chain, resource,
            [near, far], 65010, 65011, Unit));
    }

    [Fact]
    public void CandidateSetAcceptsItsBoundAndRejectsEmptyOrOversizedInputs()
    {
        var resource = Sample(123, 444);
        LinuxApplicationDcpIdentity.LinuxApplicationParentFact[] chain = [new(123, 121, resource)];
        var candidates = Enumerable.Range(0, LinuxApplicationDcpIdentity.MaximumProcesses)
            .Select(index => Sample((uint)(121 + index + (index >= 2 ? 1 : 0)), (ulong)(500 + index))).ToArray();
        Assert.Equal(121u, LinuxApplicationDcpIdentity.FindNearestDcpAncestor(chain, resource,
            candidates, 65010, 65011, Unit));
        Reject(() => LinuxApplicationDcpIdentity.FindNearestDcpAncestor(chain, resource,
            [], 65010, 65011, Unit));
        Reject(() => LinuxApplicationDcpIdentity.FindNearestDcpAncestor(chain, resource,
            [.. candidates, Sample(999, 999)], 65010, 65011, Unit));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void MultipleCandidateMetadataCannotInventOrExtendTheNearestAncestry(int variant)
    {
        var resource = Sample(123, 444);
        LinuxProcessSample[] candidates = [Sample(121, 442), Sample(120, 441)];
        LinuxApplicationDcpIdentity.LinuxApplicationParentFact[] chain = [new(123, 121, resource)];
        switch (variant)
        {
            case 0: candidates = [candidates[0], candidates[0]]; break;
            case 1: candidates = [resource, candidates[0]]; break;
            case 2: candidates = [candidates[0] with { Gids = new(65011, 1, 65011, 65011) }]; break;
            case 3: chain = [chain[0], new(121, 120, candidates[0])]; break;
            case 4: chain = [new(123, 999, resource)]; break;
            case 5: chain = [new(123, 121, resource with { Stat = new(123, 999, 'S') })]; break;
            case 6: chain = [new(124, 500, Sample(124, 445))]; break;
            case 7: chain = [new(123, 124, resource), new(124, 123, Sample(124, 445)), new(123, 121, resource)]; break;
            case 8: chain = [new(123, 0, resource)]; break;
        }
        // Detached comparisons only: no fake process owner, supervisor or admission is issued.
        Reject(() => LinuxApplicationDcpIdentity.FindNearestDcpAncestor(chain, resource, candidates, 65010, 65011, Unit));
    }

    [Fact]
    public void ParentWalkRequiresStableSameUnitKernelAncestryAndBoundsDepth()
    {
        var resource = Sample(123, 444);
        var dcp = Sample(121, 442);
        var valid = new[]
        {
            new LinuxApplicationDcpIdentity.LinuxApplicationParentFact(123, 124, resource),
            new LinuxApplicationDcpIdentity.LinuxApplicationParentFact(124, 121, Sample(124, 445)),
        };
        LinuxApplicationDcpIdentity.RequireParentChain(valid, resource, dcp, 65010, 65011, Unit);
        LinuxApplicationDcpIdentity.RequireSameParentWalk(valid, valid);
        Reject(() => LinuxApplicationDcpIdentity.RequireParentChain(
            [valid[0], valid[1] with { Process = Sample(124, 445) with { Uids = new(65010, 1, 65010, 65010) } }],
            resource, dcp, 65010, 65011, Unit));
        Reject(() => LinuxApplicationDcpIdentity.RequireParentChain(
            [valid[0], valid[1] with { ParentPid = 123 }], resource, dcp, 65010, 65011, Unit));
        Reject(() => LinuxApplicationDcpIdentity.RequireSameParentWalk(valid,
            [valid[0], valid[1] with { Process = Sample(124, 999) }]));

        var depth16 = Enumerable.Range(0, 16).Select(index =>
        {
            var pid = (uint)(300 + index);
            var parent = index == 15 ? 500u : pid + 1;
            return new LinuxApplicationDcpIdentity.LinuxApplicationParentFact(pid, parent, Sample(pid, (ulong)(600 + index)));
        }).ToArray();
        LinuxApplicationDcpIdentity.RequireParentChain(depth16, Sample(300, 600), Sample(500, 800), 65010, 65011, Unit);
        var depth17 = Enumerable.Range(0, 17).Select(index =>
        {
            var pid = (uint)(300 + index);
            var parent = index == 16 ? 500u : pid + 1;
            return new LinuxApplicationDcpIdentity.LinuxApplicationParentFact(pid, parent, Sample(pid, (ulong)(600 + index)));
        }).ToArray();
        Reject(() => LinuxApplicationDcpIdentity.RequireParentChain(
            depth17, Sample(300, 600), Sample(500, 800), 65010, 65011, Unit));
    }

    [Fact]
    public void ParentLinkParsesPpidAfterFinalCommandDelimiterAndSameProcessRequiresNoLinks()
    {
        var link = LinuxApplicationDcpIdentity.ParseParentLink(Stat(comm: "private ) nested ) command", parent: "2147483647"));
        Assert.Equal(new LinuxApplicationDcpIdentity.LinuxApplicationParentLink(123, int.MaxValue, 987654), link);

        var sample = Sample();
        LinuxApplicationDcpIdentity.RequireParentChain([], sample, sample, 65010, 65011, Unit);
        Reject(() => LinuxApplicationDcpIdentity.RequireParentChain([], sample,
            sample with { Stat = sample.Stat with { StartTimeTicks = sample.Stat.StartTimeTicks + 1 } },
            65010, 65011, Unit));
        Reject(() => LinuxApplicationDcpIdentity.RequireParentChain(
            [new(123, 124, sample)], sample, sample, 65010, 65011, Unit));
        Reject(() => LinuxApplicationDcpIdentity.ParseParentLink(Stat(parent: "2147483648")));
        Reject(() => LinuxApplicationDcpIdentity.ParseParentLink(Stat(parent: "4294967296")));
        Assert.Equal(0u, LinuxApplicationDcpIdentity.ParseParentLink(Stat(parent: "0")).ParentPid);
        Reject(() => LinuxApplicationDcpIdentity.RequireParentChain(
            [new(123, 0, sample)], sample, Sample(121, 442), 65010, 65011, Unit));
        RejectStat(() => LinuxApplicationDcpIdentity.ParseParentLink(Stat(parent: "+124")));
    }

    [Fact]
    public void RootLinuxX64FixtureAuthenticatesTheActualDcpExecutableAndRejectsPathReplacement()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64
            || RuntimeInformation.OSArchitecture != Architecture.X64 || GetEffectiveUid() != 0) return;
        var sleep = new[] { "/usr/bin/sleep", "/bin/sleep" }.FirstOrDefault(File.Exists);
        var setpriv = new[] { "/usr/bin/setpriv", "/bin/setpriv" }.FirstOrDefault(File.Exists);
        if (sleep is null || setpriv is null) return;

        var nativeUnit = LinuxUnitName.Create(LinuxUnitRole.Application, Guid.NewGuid());
        var cgroup = "/sys/fs/cgroup/system.slice/" + nativeUnit.Value;
        var fixtureRoot = Path.Combine(Path.GetTempPath(), "evidence-dcp-" + Guid.NewGuid().ToString("N"));
        var dcpPath = Path.Combine(fixtureRoot, "dcp");
        Process? dcpProcess = null;
        Process? unrelatedProcess = null;
        LinuxApplicationDcpIdentity? identity = null;
        try
        {
            try
            {
                Directory.CreateDirectory(cgroup);
                Directory.CreateDirectory(fixtureRoot);
                File.SetUnixFileMode(fixtureRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                File.Copy(sleep, dcpPath);
                SetExecutableMode(dcpPath);
                dcpProcess = StartAsApplication(setpriv, dcpPath);
                MoveToCgroup(cgroup, dcpProcess.Id);
                unrelatedProcess = StartAsApplication(setpriv, sleep);
                MoveToCgroup(cgroup, unrelatedProcess.Id);
                if (dcpProcess.HasExited || unrelatedProcess.HasExited) return;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
            {
                return; // This host does not delegate writable cgroup v2 or executable temp storage to the fixture.
            }

            identity = CaptureUntilSelected(dcpProcess!, nativeUnit, dcpPath);
            Assert.Equal(checked((uint)dcpProcess!.Id), identity.Pid);
            identity.Recheck(CancellationToken.None);

            var replacement = Path.Combine(fixtureRoot, "replacement");
            File.Copy(sleep, replacement);
            SetExecutableMode(replacement);
            File.Move(replacement, dcpPath, overwrite: true);
            Reject(() => identity.Recheck(CancellationToken.None));
        }
        finally
        {
            try { identity?.Dispose(); } catch (EvidenceAdmissionException) { }
            Stop(dcpProcess);
            Stop(unrelatedProcess);
            TryDeleteDirectory(cgroup, recursive: false);
            TryDeleteDirectory(fixtureRoot, recursive: true);
        }
    }

    private static LinuxProcessSample Sample(uint pid = 123, ulong start = 444) => new(new(pid, start, 'S'),
        new(65010, 65010, 65010, 65010), new(65011, 65011, 65011, 65011),
        "/system.slice/" + Unit.Value);

    private static byte[] CommandBytes(string runtimeHost, string resourceDll, string socketPath) =>
        [.. new[] { runtimeHost, resourceDll, "--socket", socketPath, "--case", "normal" }
            .SelectMany(static value => Encoding.UTF8.GetBytes(value + "\0"))];

    private static byte[] Stat(string comm = "dotnet", string parent = "1")
    {
        var fields = Enumerable.Repeat("0", 50).ToArray();
        fields[0] = "S";
        fields[1] = parent;
        fields[19] = "987654";
        return Encoding.UTF8.GetBytes("123 (" + comm + ") " + string.Join(' ', fields) + "\n");
    }

    private static Process StartAsApplication(string setpriv, string executable)
    {
        var start = new ProcessStartInfo(setpriv) { UseShellExecute = false };
        foreach (var argument in new[] { "--reuid=65534", "--regid=65534", "--clear-groups", "--", executable, "120" })
            start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new IOException("Native DCP fixture process did not start.");
    }

    private static void MoveToCgroup(string cgroup, int pid) =>
        File.WriteAllText(Path.Combine(cgroup, "cgroup.procs"), pid.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");

    private static LinuxApplicationDcpIdentity CaptureUntilSelected(Process process, LinuxUnitName unit, string dcpPath)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(3))
        {
            if (process.HasExited) throw new InvalidOperationException("Native DCP fixture process exited before authentication.");
            try
            {
                return LinuxApplicationDcpIdentity.Capture(unit, 65534, 65534, dcpPath, CancellationToken.None);
            }
            catch (EvidenceAdmissionException error) when (error.Code == "ASEVD410")
            {
                Thread.Sleep(20); // Each attempt remains fail-closed while setpriv completes its exec transition.
            }
        }
        throw new InvalidOperationException("Native DCP fixture did not produce one authenticated executable sample.");
    }

    private static void SetExecutableMode(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead
                | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        else throw new PlatformNotSupportedException();
    }

    private static void Stop(Process? process)
    {
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        try { process.WaitForExit(2000); } catch (InvalidOperationException) { }
        process.Dispose();
    }

    private static void TryDeleteDirectory(string path, bool recursive)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUid();

    private static void Reject(Action action)
    {
        var error = Assert.Throws<EvidenceAdmissionException>(action);
        Assert.Equal("ASEVD410", error.Code);
        Assert.Null(error.InnerException);
    }

    private static void RejectStat(Action action)
    {
        var error = Assert.Throws<EvidenceAdmissionException>(action);
        Assert.Equal("ASEVD402", error.Code);
        Assert.Null(error.InnerException);
    }
}
