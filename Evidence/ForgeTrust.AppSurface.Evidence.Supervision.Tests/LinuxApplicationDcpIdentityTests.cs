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
            "", "1", "\n", "0\n", "+1\n", "-1\n", "01\n", "01x\n", "2147483648\n",
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
            Encoding.UTF8.GetBytes(RuntimeHost + "\0--socket\0" + SocketPath + "\0" + ResourceDll + "\0--case\0normal\0"),
            RuntimeHost, ResourceDll, SocketPath));
        Reject(() => LinuxApplicationDcpIdentity.RequireResourceCommandBytes([.. valid, (byte)'x'], RuntimeHost, ResourceDll, SocketPath));
        Reject(() => LinuxApplicationDcpIdentity.RequireResourceCommandBytes(valid[..^1], RuntimeHost, ResourceDll, SocketPath));
        Reject(() => LinuxApplicationDcpIdentity.RequireResourceCommandBytes(
            new byte[LinuxApplicationDcpIdentity.MaximumProcessBytes + 1], RuntimeHost, ResourceDll, SocketPath));
        Reject(() => LinuxApplicationDcpIdentity.RequireResourceCommandBytes(valid, RuntimeHost,
            "/run/root/bundle/other/NativeHttpResource.dll", SocketPath));
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

    private static LinuxProcessSample Sample() => new(new(123, 444, 'S'),
        new(65010, 65010, 65010, 65010), new(65011, 65011, 65011, 65011),
        "/system.slice/" + Unit.Value);

    private static byte[] CommandBytes(string runtimeHost, string resourceDll, string socketPath) =>
        [.. new[] { runtimeHost, resourceDll, "--socket", socketPath, "--case", "normal" }
            .SelectMany(static value => Encoding.UTF8.GetBytes(value + "\0"))];

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
}
