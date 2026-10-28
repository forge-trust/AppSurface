using System.Globalization;
using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using System.Text;

namespace EvidenceHost.CompletedIsolation;

/// <summary>Small fail-closed Linux process observations for the private phase boundary.</summary>
/// <remarks>These observations are deliberately local to this private executable and do not grant product admission.</remarks>
[SupportedOSPlatform("linux")]
internal static class NativeFacts
{
    private const int MaximumStatusBytes = 64 * 1024;
    private const int MaximumEnvironmentBytes = 16 * 1024;

    internal static void RequireSelfEnvironment(IReadOnlyCollection<string> allowed)
    {
        var expected = new HashSet<string>(allowed, StringComparer.Ordinal);
        var actual = Environment.GetEnvironmentVariables();
        if (actual.Count != expected.Count) throw new IsolationException("launcher-environment");
        foreach (System.Collections.DictionaryEntry entry in actual)
        {
            if (entry.Key is not string key || !expected.Contains(key) || entry.Value is not string value
                || value.Any(char.IsControl)) throw new IsolationException("launcher-environment");
        }
        RequireEnv("PATH", "/usr/bin:/bin");
        RequireEnv("HOME", "/nonexistent");
        RequireEnv("LANG", "C.UTF-8");
        RequireEnv("LC_ALL", "C");
    }

    internal static void RequireSelfIdentity()
    {
        if (!OperatingSystem.IsLinux() || Environment.ProcessId != 1 || GetUid() == 0 || GetEuid() == 0
            || GetGid() != GetEgid()) throw new IsolationException("launcher-identity");

        var status = ReadBounded("/proc/self/status", MaximumStatusBytes);
        var fields = ParseFields(status);
        if (!fields.TryGetValue("Uid", out var uid) || !fields.TryGetValue("Gid", out var gid)
            || !fields.TryGetValue("Groups", out var groups) || !fields.TryGetValue("NSpid", out var nsPid)
            || uid.Length != 4 || gid.Length != 4 || groups.Length != 1 || nsPid.Length < 1
            || !ParseUInt(uid[0], out var realUid) || !ParseUInt(uid[1], out var effectiveUid)
            || !ParseUInt(uid[2], out var savedUid) || !ParseUInt(uid[3], out var fsUid)
            || !ParseUInt(gid[0], out var realGid) || !ParseUInt(gid[1], out var effectiveGid)
            || !ParseUInt(gid[2], out var savedGid) || !ParseUInt(gid[3], out var fsGid)
            || !ParseUInt(groups[0], out var group) || !ParseUInt(nsPid[^1], out var nsPidValue)
            || realUid == 0 || realUid != effectiveUid || realUid != savedUid || realUid != fsUid
            || realGid != effectiveGid || realGid != savedGid || realGid != fsGid || group != realGid
            || nsPidValue != 1)
            throw new IsolationException("launcher-identity");
    }

    internal static void RequireSelfCapabilitiesAndDescriptors()
    {
        var fields = ParseFields(ReadBounded("/proc/self/status", MaximumStatusBytes));
        foreach (var name in new[] { "CapInh", "CapPrm", "CapEff", "CapBnd", "CapAmb" })
            if (!fields.TryGetValue(name, out var value) || value.Length != 1
                || !ulong.TryParse(value[0], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var bits)
                || bits != 0) throw new IsolationException("launcher-capabilities");
        if (!fields.TryGetValue("NoNewPrivs", out var nnp) || nnp.Length != 1 || nnp[0] != "1")
            throw new IsolationException("launcher-nnp");

        foreach (var path in Directory.EnumerateFileSystemEntries("/proc/self/fd"))
        {
            if (!int.TryParse(Path.GetFileName(path), NumberStyles.None, CultureInfo.InvariantCulture, out var fd))
                throw new IsolationException("launcher-descriptor");
            if (fd <= 2) continue;
            var flags = Fcntl(fd, 1); // F_GETFD
            if (flags < 0)
            {
                if (System.Runtime.InteropServices.Marshal.GetLastPInvokeError() == 9) continue; // transiently closed enumerator fd
                throw new IsolationException("launcher-descriptor");
            }
            if ((flags & 1) == 0) throw new IsolationException("launcher-descriptor");
        }
    }

    internal static async Task<PhaseWitness> VerifyReadyProcessAsync(OwnerLayout layout, string unit, uint namespacePid,
        OwnerPhase phase, CancellationToken cancellationToken)
    {
        if (namespacePid != 1) throw new IsolationException("ready-pid");
        var cgroup = await RunTextAsync("/usr/bin/systemctl", ["show", "--property=ControlGroup", "--value", unit],
            cancellationToken).ConfigureAwait(false);
        if (cgroup.ExitCode != 0 || !IsSafeUnitCgroup(cgroup.Text, unit)) throw new IsolationException("unit-cgroup");
        var procsPath = "/sys/fs/cgroup" + cgroup.Text.TrimEnd('\n', '\r') + "/cgroup.procs";
        var pids = ParsePidList(ReadBounded(procsPath, 4096));
        if (pids.Count != 2) throw new IsolationException("unit-process-count");

        var launcher = new List<(int Pid, string[] NsPids, string[] Uids, string[] Gids, string Exe, string[] Args)>();
        foreach (var pid in pids)
        {
            var proc = $"/proc/{pid.ToString(CultureInfo.InvariantCulture)}";
            var status = ParseFields(ReadBounded(proc + "/status", MaximumStatusBytes));
            if (!status.TryGetValue("NSpid", out var nsPids) || !status.TryGetValue("Uid", out var uids)
                || !status.TryGetValue("Gid", out var gids)) throw new IsolationException("unit-process-facts");
            var exe = new FileInfo(proc + "/exe").ResolveLinkTarget(returnFinalTarget: true)?.FullName
                ?? throw new IsolationException("unit-process-exe");
            var args = ParseNul(ReadBounded(proc + "/cmdline", 16 * 1024));
            if (nsPids.Length >= 2 && uint.TryParse(nsPids[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var inner)
                && inner == namespacePid) launcher.Add((pid, nsPids, uids, gids, exe, args));
        }

        if (launcher.Count != 1) throw new IsolationException("unit-launcher-ambiguous");
        var target = launcher[0];
        if (target.Uids.Length != 4 || target.Gids.Length != 4
            || !AllEqual(target.Uids, layout.Uid) || !AllEqual(target.Gids, layout.Gid)
            || !MatchesReadonlyDotnet(layout, target.Pid)
            || !target.Args.Any(arg => arg == OwnerPaths.Assembly)
            || !target.Args.Any(arg => arg == "phase") || !target.Args.Any(arg => arg == PhaseName(phase)))
            throw new IsolationException("unit-launcher-facts");

        var nnp = ParseFields(ReadBounded($"/proc/{target.Pid.ToString(CultureInfo.InvariantCulture)}/status", MaximumStatusBytes));
        if (!nnp.TryGetValue("NoNewPrivs", out var nnpValue) || nnpValue.Length != 1 || nnpValue[0] != "1"
            || !nnp.TryGetValue("CapEff", out var caps) || caps.Length != 1 || caps[0] != "0000000000000000"
            || !nnp.TryGetValue("Groups", out var groups) || groups.Length != 1
            || groups[0] != layout.Gid.ToString(CultureInfo.InvariantCulture))
            throw new IsolationException("unit-launcher-security");
        var bootstrap = pids.Single(pid => pid != target.Pid);
        var bootstrapProc = $"/proc/{bootstrap.ToString(CultureInfo.InvariantCulture)}";
        var bootstrapStatus = ParseFields(ReadBounded(bootstrapProc + "/status", MaximumStatusBytes));
        if (!bootstrapStatus.TryGetValue("Uid", out var bootstrapUids) || !AllEqual(bootstrapUids, 0)
            || !bootstrapStatus.TryGetValue("CapEff", out var bootstrapCaps) || bootstrapCaps.Length != 1
            || !ulong.TryParse(bootstrapCaps[0], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var effectiveCaps)
            || (effectiveCaps & ~0x002401c0UL) != 0 || (effectiveCaps & 0x002401c0UL) != 0x002401c0UL
            || !bootstrapStatus.TryGetValue("NSpid", out var bootstrapNsPids) || bootstrapNsPids.Length != 1
            || !bootstrapStatus.TryGetValue("Groups", out var bootstrapGroups)
            || bootstrapGroups.Any(group => group != "0")
            || !MatchesReadonlyBinary(layout, bootstrap, "/usr/bin/unshare"))
            throw new IsolationException("unit-bootstrap-security");
        var bootstrapArgs = ParseNul(ReadBounded(bootstrapProc + "/cmdline", 16 * 1024));
        if (!HasAdjacent(bootstrapArgs, "--pid", "--fork") || !bootstrapArgs.Contains("--mount-proc", StringComparer.Ordinal)
            || !bootstrapArgs.Contains("--kill-child=KILL", StringComparer.Ordinal)
            || !bootstrapArgs.Contains("/usr/bin/setpriv", StringComparer.Ordinal))
            throw new IsolationException("unit-bootstrap-argv");
        VerifyNamespaceAndMounts(layout, target.Pid, bootstrap, phase);
        return CapturePhaseWitness(unit, cgroup.Text, target.Pid);
    }

    internal static bool IsCgroupEmpty(PhaseWitness witness)
    {
        try
        {
            if (!witness.LauncherExited || !CgroupIdentityMatches(witness)) return false;
            var path = witness.HostPath;
            var events = ParseFields(witness.ReadEvents());
            var procs = ParsePidList(witness.ReadProcesses());
            if (Directory.Exists(path)) procs.AddRange(ReadRecursivePids(path, path, 0, 256));
            var stillSame = CgroupIdentityMatches(witness);
            return events.TryGetValue("populated", out var populated) && populated.Length == 1
                && populated[0] == "0" && procs.Count == 0 && stillSame;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or IsolationException or InvalidOperationException)
        {
            return false;
        }
    }

    internal static bool VerifySpawnEscapeMarker(OwnerLayout layout, PhaseWitness witness)
    {
        var markerPath = layout.Host(OwnerPaths.Output + "/escaped-child");
        using var handle = OpenOwnedMarker(markerPath, layout.Uid);
        using var stream = new FileStream(handle, FileAccess.Read, 64, isAsync: false);
        Span<byte> marker = stackalloc byte[16];
        var read = stream.Read(marker);
        if (read is < 2 or > 10) return false;
        var text = Encoding.ASCII.GetString(marker[..read]);
        if (!uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var namespacePid)
            || namespacePid <= 1) return false;
        foreach (var pid in ReadRecursivePids(witness.HostPath, witness.HostPath, 0, 256))
        {
            if (pid == witness.LauncherPid) continue;
            try
            {
                var status = ParseFields(ReadBounded($"/proc/{pid.ToString(CultureInfo.InvariantCulture)}/status", MaximumStatusBytes));
                if (status.TryGetValue("NSpid", out var nsPids) && nsPids.Length >= 2
                    && uint.TryParse(nsPids[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var inner)
                    && inner == namespacePid) return true;
            }
            catch (IOException) { }
        }
        return false;
    }

    internal sealed class PhaseWitness : IDisposable
    {
        private readonly SafeFileHandle _cgroupHandle;
        private readonly SafeFileHandle _launcherHandle;
        private readonly SafeFileHandle _eventsHandle;
        private readonly SafeFileHandle _procsHandle;
        private readonly string _hostPath;
        private readonly ulong _device;
        private readonly ulong _inode;
        private readonly ulong _mountId;
        internal string HostPath => _hostPath;
        internal int LauncherPid { get; }
        internal bool LauncherExited => PollExited(_launcherHandle);
        internal bool CgroupIdentityVerified => CgroupIdentityMatches();

        internal PhaseWitness(string hostPath, int launcherPid, SafeFileHandle cgroupHandle, SafeFileHandle launcherHandle,
            SafeFileHandle eventsHandle, SafeFileHandle procsHandle, ulong device, ulong inode, ulong mountId)
        {
            _hostPath = hostPath;
            LauncherPid = launcherPid;
            _cgroupHandle = cgroupHandle;
            _launcherHandle = launcherHandle;
            _eventsHandle = eventsHandle;
            _procsHandle = procsHandle;
            _device = device;
            _inode = inode;
            _mountId = mountId;
        }

        internal bool CgroupIdentityMatches()
        {
            if (_cgroupHandle.IsClosed || _cgroupHandle.IsInvalid) return false;
            var held = ReadStatx(_cgroupHandle.DangerousGetHandle().ToInt32());
            var pathMatches = !Directory.Exists(_hostPath);
            if (!pathMatches)
            {
                var path = ReadStatx(-100, _hostPath, 0x100, 0x17ff);
                pathMatches = path.Device == _device && path.Inode == _inode && path.MountId == _mountId;
            }
            return held.Device == _device && held.Inode == _inode && held.MountId == _mountId && pathMatches
                && !_eventsHandle.IsClosed && !_procsHandle.IsClosed;
        }

        internal byte[] ReadEvents() => ReadHandle(_eventsHandle, 4096);
        internal byte[] ReadProcesses() => ReadHandle(_procsHandle, 64 * 1024);

        public void Dispose()
        {
            _launcherHandle.Dispose();
            _eventsHandle.Dispose();
            _procsHandle.Dispose();
            _cgroupHandle.Dispose();
        }
    }

    internal static async Task<(int ExitCode, string Text)> RunTextAsync(string executable, IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment.Clear();
        start.Environment["PATH"] = "/usr/sbin:/usr/bin:/sbin:/bin";
        start.Environment["LANG"] = "C";
        using var process = Process.Start(start) ?? throw new IsolationException("native-command-start");
        try
        {
            var output = ReadBoundedAsync(process.StandardOutput.BaseStream, 4096, cancellationToken);
            var error = ReadBoundedAsync(process.StandardError.BaseStream, 4096, cancellationToken);
            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(4), cancellationToken).ConfigureAwait(false);
            var outBytes = await output.ConfigureAwait(false);
            _ = await error.ConfigureAwait(false);
            return (process.ExitCode, Encoding.UTF8.GetString(outBytes));
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
    }

    private static bool IsSafeUnitCgroup(string value, string unit) =>
        value == $"/system.slice/{unit}\n" || value == $"/system.slice/{unit}\r\n";

    private static PhaseWitness CapturePhaseWitness(string unit, string controlGroup, int launcherPid)
    {
        if (!IsSafeUnitCgroup(controlGroup, unit)) throw new IsolationException("unit-cgroup");
        var hostPath = "/sys/fs/cgroup" + controlGroup.TrimEnd('\n', '\r');
        var descriptor = Open(hostPath, 0x10000 | 0x80000 | 0x200000 | 0x20000); // O_DIRECTORY|O_CLOEXEC|O_PATH|O_NOFOLLOW
        if (descriptor < 0) throw new IsolationException("unit-cgroup-open");
        var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        var stat = ReadStatx(handle.DangerousGetHandle().ToInt32());
        if ((stat.Mode & 0xf000) != 0x4000) { handle.Dispose(); throw new IsolationException("unit-cgroup-type"); }
        var eventsFd = OpenAt(descriptor, "cgroup.events", 0x80000 | 0x20000 | 0x800);
        var procsFd = OpenAt(descriptor, "cgroup.procs", 0x80000 | 0x20000 | 0x800);
        if (eventsFd < 0 || procsFd < 0)
        {
            if (eventsFd >= 0) CloseFd(eventsFd);
            if (procsFd >= 0) CloseFd(procsFd);
            handle.Dispose();
            throw new IsolationException("unit-cgroup-files");
        }
        var pidfd = PidfdOpen(launcherPid, 0);
        if (pidfd < 0) { handle.Dispose(); CloseFd(eventsFd); CloseFd(procsFd); throw new IsolationException("launcher-pidfd"); }
        var witness = new PhaseWitness(hostPath, launcherPid, handle, new SafeFileHandle((IntPtr)pidfd, ownsHandle: true),
            new SafeFileHandle((IntPtr)eventsFd, ownsHandle: true), new SafeFileHandle((IntPtr)procsFd, ownsHandle: true),
            stat.Device, stat.Inode, stat.MountId);
        try
        {
            var initialEvents = ParseFields(witness.ReadEvents());
            var initialPids = ReadRecursivePids(hostPath, hostPath, 0, 256);
            if (witness.LauncherExited || !witness.CgroupIdentityVerified || !initialPids.Contains(launcherPid)
                || Directory.EnumerateDirectories(hostPath).Any()
                || !initialEvents.TryGetValue("populated", out var populated) || populated.Length != 1 || populated[0] != "1")
                throw new IsolationException("unit-cgroup-initial");
            return witness;
        }
        catch { witness.Dispose(); throw; }
    }

    private static bool CgroupIdentityMatches(PhaseWitness witness) => witness.CgroupIdentityVerified;

    private static List<int> ReadRecursivePids(string root, string directory, int depth, int maximum)
    {
        if (depth > 8) throw new IsolationException("cgroup-depth");
        var result = ParsePidList(ReadBounded(directory + "/cgroup.procs", 64 * 1024));
        foreach (var subdirectory in Directory.EnumerateDirectories(directory))
        {
            if (!subdirectory.StartsWith(root.TrimEnd('/') + "/", StringComparison.Ordinal)
                || !string.Equals(Path.GetDirectoryName(subdirectory), directory, StringComparison.Ordinal))
                throw new IsolationException("cgroup-path");
            result.AddRange(ReadRecursivePids(root, subdirectory, depth + 1, maximum));
            if (result.Count > maximum) throw new IsolationException("cgroup-process-limit");
        }
        if (result.Count > maximum || result.Distinct().Count() != result.Count) throw new IsolationException("cgroup-proc-duplicate");
        return result;
    }

    private static SafeFileHandle OpenOwnedMarker(string path, uint owner)
    {
        var fd = Open(path, 0x80000 | 0x20000 | 0x800 | 0x100); // O_CLOEXEC|O_NOFOLLOW|O_NONBLOCK|O_NOCTTY
        if (fd < 0) throw new IsolationException("escape-marker-open");
        var handle = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
        try
        {
            var stat = ReadStatx(fd);
            if ((stat.Mode & 0xf000) != 0x8000 || stat.Uid != owner || stat.Links != 1 || stat.Size > 16)
                throw new IsolationException("escape-marker-identity");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    private static byte[] ReadHandle(SafeFileHandle handle, int maximum)
    {
        var fd = handle.DangerousGetHandle().ToInt32();
        if (Seek(fd, 0, 0) < 0) throw new IsolationException("cgroup-seek");
        using var output = new MemoryStream(256);
        var chunk = new byte[1024];
        while (true)
        {
            var count = ReadFd(fd, chunk, (nuint)chunk.Length);
            if (count < 0) throw new IsolationException("cgroup-read");
            if (count == 0) return output.ToArray();
            if (output.Length + count > maximum) throw new IsolationException("cgroup-size");
            output.Write(chunk, 0, (int)count);
        }
    }

    private static int OpenAt(int directory, string path, int flags) => OpenAtCall(directory, path, flags);
    private static void CloseFd(int descriptor) => _ = Close(descriptor);

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAtCall(int directory, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string path, int flags);
    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "lseek", SetLastError = true)]
    private static extern long Seek(int fd, long offset, int origin);
    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "read", SetLastError = true)]
    private static extern nint ReadFd(int fd, [System.Runtime.InteropServices.Out] byte[] buffer, nuint count);
    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int fd);

    private static bool PollExited(SafeFileHandle handle)
    {
        var pollfd = new PollFd { Fd = handle.DangerousGetHandle().ToInt32(), Events = 1 };
        var result = Poll(ref pollfd, 1, 0);
        if (result < 0 || (pollfd.Revents & 0x20) != 0) throw new IsolationException("launcher-pidfd-poll");
        return result > 0 && (pollfd.Revents & (0x1 | 0x10)) != 0;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct PollFd { internal int Fd; internal short Events; internal short Revents; }
    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string path, int flags);
    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "pidfd_open", SetLastError = true)]
    private static extern int PidfdOpen(int pid, uint flags);
    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "poll", SetLastError = true)]
    private static extern int Poll(ref PollFd descriptors, nuint count, int timeout);

    private static StatxBuffer ReadStatx(int fd)
    {
        if (Statx(fd, "", 0x1000, 0x17ff, out var buffer) != 0) throw new IsolationException("statx-handle");
        return buffer;
    }

    private static StatxBuffer ReadStatx(int directory, string path, int flags, uint mask)
    {
        if (Statx(directory, path, flags, mask, out var buffer) != 0) throw new IsolationException("statx-path");
        return buffer;
    }

    private static List<int> ParsePidList(byte[] bytes)
    {
        var text = Encoding.ASCII.GetString(bytes);
        var result = new List<int>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(line, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0
                || result.Contains(pid)) throw new IsolationException("cgroup-procs");
            result.Add(pid);
        }
        return result;
    }

    private static Dictionary<string, string[]> ParseFields(byte[] bytes)
    {
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var line in Encoding.ASCII.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon];
            var values = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (!result.TryAdd(name, values)) throw new IsolationException("proc-fields");
        }
        return result;
    }

    private static string[] ParseNul(byte[] bytes) => Encoding.UTF8.GetString(bytes).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    private static bool AllEqual(string[] values, uint expected) => values.Length == 4 && values.All(value =>
        uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed == expected);
    private static bool ParseUInt(string value, out uint result) => uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result);
    private static void RequireEnv(string key, string expected)
    {
        if (Environment.GetEnvironmentVariable(key) != expected) throw new IsolationException("launcher-environment");
    }
    private static string PhaseName(OwnerPhase phase) => phase switch
    {
        OwnerPhase.Prepare => "prepare", OwnerPhase.Replay => "replay", OwnerPhase.Close => "close", OwnerPhase.Control => "control",
        _ => throw new IsolationException("phase")
    };

    private static byte[] ReadBounded(string path, int maximum)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096,
            FileOptions.SequentialScan);
        return ReadBoundedAsync(stream, maximum, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static string ReadLink(string path)
    {
        var bytes = new byte[4096];
        var count = ReadLinkCall(path, bytes, (nuint)bytes.Length);
        if (count < 0 || count == bytes.Length) throw new IsolationException("proc-link");
        return new UTF8Encoding(false, true).GetString(bytes, 0, checked((int)count));
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "readlink", SetLastError = true)]
    private static extern nint ReadLinkCall([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string path,
        [System.Runtime.InteropServices.Out] byte[] buffer, nuint size);

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximum, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream(Math.Min(maximum, 4096));
        var chunk = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) return output.ToArray();
            if (output.Length + read > maximum) throw new IsolationException("native-input-size");
            output.Write(chunk, 0, read);
        }
    }

    private static bool MatchesReadonlyDotnet(OwnerLayout layout, int pid) => MatchesReadonlyBinary(layout, pid, OwnerPaths.Dotnet);

    private static bool MatchesReadonlyBinary(OwnerLayout layout, int pid, string insidePath)
    {
        var expectedPath = insidePath == OwnerPaths.Dotnet
            ? ReadCanonicalSdkPath(layout) + "/dotnet"
            : layout.Host(insidePath);
        var left = FileSystemInfoStat.Read(expectedPath);
        var right = FileSystemInfoStat.Read($"/proc/{pid.ToString(CultureInfo.InvariantCulture)}/exe");
        return left.Device == right.Device && left.Inode == right.Inode && left.Mode == right.Mode
            && (left.Mode & 0xf000) == 0x8000 && (left.Mode & 0x92) == 0 && left.Uid == 0 && left.Links > 0;
    }

    private static void VerifyNamespaceAndMounts(OwnerLayout layout, int launcher, int bootstrap, OwnerPhase phase)
    {
        var proc = $"/proc/{launcher.ToString(CultureInfo.InvariantCulture)}";
        var root = FileSystemInfoStat.Read(proc + "/root");
        var expectedRoot = FileSystemInfoStat.Read(layout.RootFileSystem);
        if (root.Device != expectedRoot.Device || root.Inode != expectedRoot.Inode)
            throw new IsolationException("unit-root-identity");
        var launcherPidNs = ReadLink(proc + "/ns/pid");
        var bootstrapPidNs = ReadLink($"/proc/{bootstrap.ToString(CultureInfo.InvariantCulture)}/ns/pid");
        var launcherMountNs = ReadLink(proc + "/ns/mnt");
        var bootstrapMountNs = ReadLink($"/proc/{bootstrap.ToString(CultureInfo.InvariantCulture)}/ns/mnt");
        var hostMountNs = ReadLink("/proc/self/ns/mnt");
        if (launcherPidNs is null || launcherPidNs == bootstrapPidNs || launcherMountNs is null
            || launcherMountNs != bootstrapMountNs || launcherMountNs == hostMountNs)
            throw new IsolationException("unit-namespace-identity");

        var entries = ParseMountInfo(ReadBounded(proc + "/mountinfo", 256 * 1024));
        var rootEntry = entries.Where(entry => entry.MountPoint == "/").OrderByDescending(entry => entry.MountPoint.Length).FirstOrDefault();
        var procEntry = entries.SingleOrDefault(entry => entry.MountPoint == "/proc");
        if (rootEntry is null || !rootEntry.Options.Contains("ro", StringComparer.Ordinal)
            || procEntry is null || procEntry.FileSystem != "proc") throw new IsolationException("unit-mount-namespace");

        var writable = new HashSet<string>(StringComparer.Ordinal);
        writable.Add("/tmp");
        if (phase is OwnerPhase.Prepare or OwnerPhase.Replay or OwnerPhase.Control)
        {
            writable.Add(OwnerPaths.Source); writable.Add(OwnerPaths.Native); writable.Add(OwnerPaths.Output); writable.Add(OwnerPaths.Handoff);
        }
        if (phase == OwnerPhase.Prepare) writable.Add(OwnerPaths.Stage);
        if (phase == OwnerPhase.Close) writable.Add(OwnerPaths.Report);
        var allPaths = new[] { OwnerPaths.Validator, OwnerPaths.Input, OwnerPaths.Source, OwnerPaths.Native,
            OwnerPaths.Stage, OwnerPaths.Handoff, OwnerPaths.Output, OwnerPaths.Report, "/tmp" };
        foreach (var path in allPaths)
            if (!writable.Contains(path) && !PathIsMountedWith(entries, path, "ro"))
                throw new IsolationException("unit-readonly-mount");
        if (phase is OwnerPhase.Replay or OwnerPhase.Control
            && (!PathIsMountedWith(entries, OwnerPaths.Source + "/.git", "ro")
                || !PathIsMountedWith(entries, OwnerPaths.Native + "/.git", "ro")))
            throw new IsolationException("unit-git-mount");
        foreach (var path in writable)
            if (!PathIsExactMountedWith(entries, path, "rw")) throw new IsolationException("unit-readwrite-mount");
        if (phase is OwnerPhase.Replay or OwnerPhase.Control or OwnerPhase.Close
            && !PathIsMountedWith(entries, OwnerPaths.Preparation, "ro"))
            throw new IsolationException("unit-preparation-mount");
        if (!PathIsExactMountedWith(entries, "/usr/share/dotnet", "ro"))
            throw new IsolationException("unit-sdk-mount");
    }

    private static bool PathIsMountedWith(IReadOnlyList<MountEntry> entries, string path, string option)
    {
        var mount = entries.Where(entry => path == entry.MountPoint
            || path.StartsWith(entry.MountPoint.TrimEnd('/') + "/", StringComparison.Ordinal))
            .OrderByDescending(entry => entry.MountPoint.Length).FirstOrDefault();
        return mount is not null && mount.Options.Contains(option, StringComparer.Ordinal);
    }

    private static bool PathIsExactMountedWith(IReadOnlyList<MountEntry> entries, string path, string option) =>
        entries.Any(entry => entry.MountPoint == path && entry.Options.Contains(option, StringComparer.Ordinal));

    private static List<MountEntry> ParseMountInfo(byte[] bytes)
    {
        var entries = new List<MountEntry>();
        foreach (var line in Encoding.ASCII.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var separator = Array.IndexOf(fields, "-");
            if (separator < 6 || separator + 2 >= fields.Length) throw new IsolationException("mountinfo-format");
            entries.Add(new(DecodeMount(fields[4]), fields[5].Split(',').ToHashSet(StringComparer.Ordinal), fields[separator + 1]));
        }
        return entries;
    }

    private static string DecodeMount(string value) => value.Replace("\\040", " ", StringComparison.Ordinal)
        .Replace("\\011", "\t", StringComparison.Ordinal).Replace("\\012", "\n", StringComparison.Ordinal)
        .Replace("\\134", "\\", StringComparison.Ordinal);
    private sealed record MountEntry(string MountPoint, HashSet<string> Options, string FileSystem);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit, Size = 256)]
    private struct StatxBuffer
    {
        [System.Runtime.InteropServices.FieldOffset(16)] internal uint Links;
        [System.Runtime.InteropServices.FieldOffset(20)] internal uint Uid;
        [System.Runtime.InteropServices.FieldOffset(24)] internal uint Gid;
        [System.Runtime.InteropServices.FieldOffset(28)] internal ushort Mode;
        [System.Runtime.InteropServices.FieldOffset(32)] internal ulong Inode;
        [System.Runtime.InteropServices.FieldOffset(40)] internal ulong Size;
        [System.Runtime.InteropServices.FieldOffset(136)] internal uint DevMajor;
        [System.Runtime.InteropServices.FieldOffset(140)] internal uint DevMinor;
        [System.Runtime.InteropServices.FieldOffset(144)] internal ulong MountId;
        internal ulong Device => ((ulong)DevMajor << 32) | DevMinor;
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int dirfd, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string path,
        int flags, uint mask, out StatxBuffer buffer);

    private static bool HasAdjacent(string[] args, string first, string second)
    {
        for (var index = 0; index + 1 < args.Length; index++)
            if (args[index] == first && args[index + 1] == second) return true;
        return false;
    }

    internal static string ReadCanonicalSdkPath(OwnerLayout layout)
    {
        var pathFile = Path.Combine(layout.HostRoot, "sdk.hostpath");
        var fd = Open(pathFile, 0x80000 | 0x20000 | 0x800); // O_CLOEXEC|O_NOFOLLOW|O_NONBLOCK
        if (fd < 0) throw new IsolationException("sdk-host-path");
        byte[] bytes;
        try
        {
            var metadata = ReadStatx(fd);
            if ((metadata.Mode & 0xf000) != 0x8000 || metadata.Uid != 0 || metadata.Links != 1
                || metadata.Size is < 2 or > 4096 || (metadata.Mode & 0x1ff) != 0x180)
                throw new IsolationException("sdk-host-path");
            using var borrowedHandle = new SafeFileHandle((IntPtr)fd, ownsHandle: false);
            bytes = ReadHandle(borrowedHandle, 4096);
        }
        finally { CloseFd(fd); }
        var text = new UTF8Encoding(false, true).GetString(bytes);
        if (!text.EndsWith('\n') || text[..^1].Contains('\n') || text.Contains('\r')
            || !Path.IsPathFullyQualified(text[..^1]) || Path.GetFullPath(text[..^1]) != text[..^1]
            || text[..^1] is not ("/usr/share/dotnet" or "/usr/local/share/dotnet"))
            throw new IsolationException("sdk-host-path");
        var sdkPath = text[..^1];
        var directories = new List<string>();
        for (var current = sdkPath; current != "/"; current = Path.GetDirectoryName(current)!) directories.Add(current);
        directories.Add("/");
        foreach (var directory in directories)
        {
            var metadata = FileSystemInfoStat.ReadNoFollow(directory);
            if ((metadata.Mode & 0xf000) != 0x4000 || metadata.Uid != 0 || (metadata.Mode & 0x12) != 0)
                throw new IsolationException("sdk-host-directory");
        }
        return sdkPath;
    }

    internal static async Task VerifySdkAsync(OwnerLayout layout, CancellationToken cancellationToken)
    {
        var sdk = ReadCanonicalSdkPath(layout);
        var binaryPath = sdk + "/dotnet";
        var binary = FileSystemInfoStat.Read(binaryPath);
        if ((binary.Mode & 0xf000) != 0x8000 || binary.Uid != 0 || (binary.Mode & 0x92) != 0)
            throw new IsolationException("sdk-host-binary");
        var result = await RunTextAsync(binaryPath, ["--version"], cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 || result.Text.Trim() != "10.0.401") throw new IsolationException("sdk-version");
    }

    // Linux statx-backed identity is used only to compare the immutable SDK executable image.
    private static class FileSystemInfoStat
    {
        internal static (ulong Device, ulong Inode, uint Mode, uint Uid, uint Links) Read(string path)
        {
            if (Statx(-100, path, 0, 0x7ff, out var value) != 0) throw new IsolationException("sdk-identity");
            return (((ulong)value.DevMajor << 32) | value.DevMinor, value.Inode, value.Mode, value.Uid, value.Links);
        }

        internal static (ulong Device, ulong Inode, uint Mode, uint Uid, uint Links) ReadNoFollow(string path)
        {
            if (Statx(-100, path, 0x100, 0x7ff, out var value) != 0) throw new IsolationException("sdk-path-identity");
            return (((ulong)value.DevMajor << 32) | value.DevMinor, value.Inode, value.Mode, value.Uid, value.Links);
        }

        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "statx", SetLastError = true)]
        private static extern int Statx(int dirfd, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string path,
            int flags, uint mask, out StatxBuffer buffer);
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "getuid")]
    private static extern uint GetUid();
    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEuid();
    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "getgid")]
    private static extern uint GetGid();
    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "getegid")]
    private static extern uint GetEgid();
    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int Fcntl(int fd, int command);
}
