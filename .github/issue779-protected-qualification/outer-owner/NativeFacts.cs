using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

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
        var fields = ParseProcStatusFields(status);
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
        var fields = ParseProcStatusFields(ReadBounded("/proc/self/status", MaximumStatusBytes));
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

        var launcher = new List<(int Pid, string[] NsPids, string[] Uids, string[] Gids, string[] Args)>();
        foreach (var pid in pids)
        {
            var proc = $"/proc/{pid.ToString(CultureInfo.InvariantCulture)}";
            var status = ParseProcStatusFields(ReadBounded(proc + "/status", MaximumStatusBytes));
            if (!status.TryGetValue("NSpid", out var nsPids) || !status.TryGetValue("Uid", out var uids)
                || !status.TryGetValue("Gid", out var gids)) throw new IsolationException("unit-process-facts");
            var args = ParseNul(ReadBounded(proc + "/cmdline", 16 * 1024));
            if (nsPids.Length >= 2 && uint.TryParse(nsPids[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var inner)
                && inner == namespacePid) launcher.Add((pid, nsPids, uids, gids, args));
        }

        if (launcher.Count != 1) throw new IsolationException("unit-launcher-ambiguous");
        var target = launcher[0];
        if (target.Uids.Length != 4 || target.Gids.Length != 4
            || !AllEqual(target.Uids, layout.Uid) || !AllEqual(target.Gids, layout.Gid)
            || !MatchesReadonlyDotnet(layout, target.Pid)
            || !target.Args.Any(arg => arg == OwnerPaths.Assembly)
            || !target.Args.Any(arg => arg == "phase") || !target.Args.Any(arg => arg == PhaseName(phase)))
            throw new IsolationException("unit-launcher-facts");

        var nnp = ParseProcStatusFields(ReadBounded($"/proc/{target.Pid.ToString(CultureInfo.InvariantCulture)}/status", MaximumStatusBytes));
        if (!nnp.TryGetValue("NoNewPrivs", out var nnpValue) || nnpValue.Length != 1 || nnpValue[0] != "1"
            || !nnp.TryGetValue("CapEff", out var caps) || caps.Length != 1 || caps[0] != "0000000000000000"
            || !nnp.TryGetValue("Groups", out var groups) || groups.Length != 1
            || groups[0] != layout.Gid.ToString(CultureInfo.InvariantCulture))
            throw new IsolationException("unit-launcher-security");
        var bootstrap = pids.Single(pid => pid != target.Pid);
        var bootstrapProc = $"/proc/{bootstrap.ToString(CultureInfo.InvariantCulture)}";
        var bootstrapStatus = ParseProcStatusFields(ReadBounded(bootstrapProc + "/status", MaximumStatusBytes));
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
        witness.LastSettlementDiagnostic.Begin();
        try
        {
            var launcherExited = witness.LauncherExited;
            witness.LastSettlementDiagnostic.Launcher(launcherExited);
            if (!launcherExited) return false;
            if (IsRemovedCgroup(witness, witness.LastSettlementDiagnostic)) return true;
            if (!witness.CgroupIdentityMatches(witness.LastSettlementDiagnostic))
                return IsRemovedCgroup(witness, witness.LastSettlementDiagnostic);
            var path = witness.HostPath;
            var events = ParseCgroupEvents(witness.ReadEvents());
            var procs = ParsePidList(witness.ReadProcesses());
            procs.AddRange(ReadRecursivePids(path, path, 0, 256));
            var eventEmpty = events.TryGetValue("populated", out var populated) && populated.Length == 1
                && populated[0] == "0";
            witness.LastSettlementDiagnostic.Events(eventEmpty);
            var held = witness.ReadCgroupIdentity();
            witness.LastSettlementDiagnostic.Held(held, witness.ExpectedIdentity);
            var pathIdentity = ReadCgroupPathIdentity(path);
            witness.LastSettlementDiagnostic.Path(pathIdentity, witness.ExpectedIdentity);
            var finalLauncherExited = witness.LauncherExited;
            witness.LastSettlementDiagnostic.Launcher(finalLauncherExited);
            var noProcesses = procs.Count == 0;
            witness.LastSettlementDiagnostic.Processes(noProcesses);
            return DecideCgroupSettlement(held, pathIdentity,
                witness.ExpectedIdentity, finalLauncherExited, eventEmpty, noProcesses);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or IsolationException or InvalidOperationException)
        {
            witness.LastSettlementDiagnostic.Error(error);
            try { return IsRemovedCgroup(witness, witness.LastSettlementDiagnostic); }
            catch (Exception removalError) when (removalError is IOException or UnauthorizedAccessException or IsolationException or InvalidOperationException)
            {
                witness.LastSettlementDiagnostic.Error(removalError);
                return false;
            }
        }
    }

    private static bool IsRemovedCgroup(PhaseWitness witness, SettlementDiagnostic diagnostic)
    {
        var held = witness.ReadCgroupIdentity();
        diagnostic.Held(held, witness.ExpectedIdentity);
        var path = ReadCgroupPathIdentity(witness.HostPath);
        diagnostic.Path(path, witness.ExpectedIdentity);
        var launcherExited = witness.LauncherExited;
        diagnostic.Launcher(launcherExited);
        if (!launcherExited || !held.SameObject(witness.ExpectedIdentity) || !held.IsDirectory || !path.ErrorFree)
            return false;

        // Preserve the original removed-node rule without requiring procfs when the kernel reports zero links.
        if (held.Links == 0)
            return path.Missing;

        // Most checks run while the node is still live; don't read procfs for that ordinary pending state.
        if (!path.Missing)
            return false;

        var firstProcfs = witness.TryReadTrustedDescriptorPath(out var firstDescriptorPath);
        var firstRootMatch = witness.CapturedCgroupMountMatches
            && CurrentCgroupMountMatches(witness.ExpectedIdentity.MountId);
        var secondProcfs = witness.TryReadTrustedDescriptorPath(out var secondDescriptorPath);
        var finalHeld = witness.ReadCgroupIdentity();
        var finalPath = ReadCgroupPathIdentity(witness.HostPath);
        var finalLauncherExited = witness.LauncherExited;
        var finalRootMatch = CurrentCgroupMountMatches(witness.ExpectedIdentity.MountId);
        var first = new CgroupRemovalObservation(held, path, launcherExited, firstDescriptorPath,
            firstProcfs, firstRootMatch);
        var second = new CgroupRemovalObservation(finalHeld, finalPath, finalLauncherExited,
            secondDescriptorPath, secondProcfs, finalRootMatch);
        diagnostic.RemovalEvidence(firstProcfs && secondProcfs,
            witness.InitialDescriptorPath == witness.HostPath,
            firstDescriptorPath == witness.HostPath + " (deleted)"
                && secondDescriptorPath == witness.HostPath + " (deleted)");
        diagnostic.CgroupRoot(firstRootMatch && finalRootMatch);
        return DecideRemovedCgroupSettlement(witness.ExpectedIdentity, witness.HostPath,
            witness.InitialDescriptorPath, first, second);
    }

    /// <summary>One sequential observation for the removed-dentry settlement validation candidate.</summary>
    /// <remarks>
    /// A pair is required because these reads are not an atomic snapshot. Each observation binds the held
    /// directory identity and positive link count to the missing original name, exited launcher, exact deleted
    /// descriptor path, trusted procfs, and visible original cgroup mount. This mechanism check is not native
    /// qualification evidence; the exact-source protected Linux workflow must exercise it before acceptance.
    /// </remarks>
    internal readonly record struct CgroupRemovalObservation(CgroupIdentity Held, CgroupPathIdentity Path,
        bool LauncherExited, string DescriptorPath, bool TrustedProcfs, bool OriginalCgroupMountStillVisible);

    /// <summary>Evaluates the two-observation removed-dentry validation candidate for the captured cgroup.</summary>
    /// <param name="expected">The pinned cgroup device, inode, mount ID, and directory type.</param>
    /// <param name="hostPath">The exact generated systemd cgroup path captured when the phase started.</param>
    /// <param name="capturedDescriptorPath">The descriptor target read at capture; it must equal <paramref name="hostPath"/>.</param>
    /// <param name="first">The first post-dispatch proof observation.</param>
    /// <param name="second">A separate, sequential proof observation within the original phase deadline.</param>
    /// <returns><see langword="true"/> only when both observations satisfy the complete removed-node predicate.</returns>
    /// <remarks>
    /// The check preserves the original zero-link route separately. Missing paths, positive links, or a deleted
    /// marker alone do not establish removal. The new positive-link route remains a validation candidate until
    /// exact-source native CI validates it on the protected runner.
    /// </remarks>
    internal static bool DecideRemovedCgroupSettlement(CgroupIdentity expected, string hostPath,
        string capturedDescriptorPath, CgroupRemovalObservation first, CgroupRemovalObservation second)
    {
        if (!IsGeneratedUnitCgroupPath(hostPath)
            || !string.Equals(capturedDescriptorPath, hostPath, StringComparison.Ordinal)) return false;
        return IsRemovedObservation(first, expected, hostPath)
            && IsRemovedObservation(second, expected, hostPath);
    }

    private static bool IsRemovedObservation(CgroupRemovalObservation observation,
        CgroupIdentity expected, string hostPath) =>
        observation.LauncherExited
        && observation.Held.SameObject(expected) && observation.Held.IsDirectory && observation.Held.Links > 0
        && observation.Path.ErrorFree && observation.Path.Missing
        && observation.TrustedProcfs && observation.OriginalCgroupMountStillVisible
        && string.Equals(observation.DescriptorPath, hostPath + " (deleted)", StringComparison.Ordinal);

    private static bool CurrentCgroupMountMatches(ulong expectedMountId)
    {
        var currentMountId = TryReadMountId("/sys/fs/cgroup");
        return currentMountId is not null && currentMountId == expectedMountId
            && HasVisibleCgroup2Mount(ReadBounded("/proc/self/mountinfo", 256 * 1024), currentMountId.Value);
    }

    private static bool IsGeneratedUnitCgroupPath(string path)
    {
        const string root = "/sys/fs/cgroup/system.slice/appsurface-completed-";
        if (!path.StartsWith(root, StringComparison.Ordinal)) return false;
        var suffix = path[root.Length..];
        foreach (var phase in new[] { "prepare", "replay", "close", "control" })
        {
            var prefix = phase + "-";
            if (!suffix.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var remainder = suffix[prefix.Length..];
            if (!remainder.EndsWith(".service", StringComparison.Ordinal)) return false;
            var guidText = remainder[..^".service".Length];
            return guidText.Length == 32 && Guid.TryParseExact(guidText, "N", out var guid)
                && string.Equals(guid.ToString("N"), guidText, StringComparison.Ordinal);
        }
        return false;
    }

    internal static bool HasStatxMountId(uint mask) => (mask & 0x1000) != 0;

    private static ulong? TryReadMountId(string path)
    {
        if (Statx(-100, path, 0, 0x17ff, out var stat) != 0 || !HasStatxMountId(stat.Mask) || stat.MountId == 0)
            return null;
        return stat.MountId;
    }

    internal static bool HasVisibleCgroup2Mount(byte[] mountInfo, ulong visibleMountId) =>
        visibleMountId != 0 && ParseMountInfo(mountInfo).Any(entry => entry.MountId == visibleMountId
            && entry.MountPoint == "/sys/fs/cgroup" && entry.FileSystem == "cgroup2");

    internal static bool DecideCgroupSettlement(CgroupIdentity held, CgroupPathIdentity path,
        CgroupIdentity expected, bool launcherExited, bool eventEmpty, bool noProcesses)
    {
        if (!launcherExited || !held.SameObject(expected) || !held.IsDirectory || !path.ErrorFree) return false;
        if (held.Links == 0) return path.Missing;
        return held.Links > 0 && path.SameNode(expected) && eventEmpty && noProcesses;
    }

    private static CgroupPathIdentity ReadCgroupPathIdentity(string path)
    {
        if (Statx(-100, path, 0x100, 0x17ff, out var stat) == 0)
        {
            if (!HasStatxMountId(stat.Mask)) return new(false, false, default);
            return new(true, false, new(stat.Device, stat.Inode, stat.MountId, stat.Links,
                (stat.Mode & 0xf000) == 0x4000));
        }
        var error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
        return error == 2
            ? new(true, true, default)
            : new(false, false, default);
    }

    internal readonly record struct CgroupIdentity(ulong Device, ulong Inode, ulong MountId, uint Links, bool IsDirectory)
    {
        internal bool SameObject(CgroupIdentity other) => Device == other.Device && Inode == other.Inode
            && MountId == other.MountId && IsDirectory == other.IsDirectory;
    }

    internal readonly record struct CgroupPathIdentity(bool ErrorFree, bool Missing, CgroupIdentity Identity)
    {
        internal bool SameNode(CgroupIdentity expected) => !Missing && Identity.SameObject(expected);
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
                var status = ParseProcStatusFields(ReadBounded($"/proc/{pid.ToString(CultureInfo.InvariantCulture)}/status", MaximumStatusBytes));
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
        private readonly ulong? _capturedProcMountId;
        internal bool CapturedCgroupMountMatches { get; }
        internal string InitialDescriptorPath { get; }
        internal string HostPath => _hostPath;
        internal int LauncherPid { get; }
        internal SettlementDiagnostic LastSettlementDiagnostic { get; } = new();
        internal bool LauncherExited => PollExited(_launcherHandle);
        internal bool CgroupIdentityVerified => CgroupIdentityMatches();
        internal CgroupIdentity ExpectedIdentity => new(_device, _inode, _mountId, 0, IsDirectory: true);

        internal PhaseWitness(string hostPath, int launcherPid, SafeFileHandle cgroupHandle, SafeFileHandle launcherHandle,
            SafeFileHandle eventsHandle, SafeFileHandle procsHandle, ulong device, ulong inode, ulong mountId,
            ulong? capturedProcMountId, string initialDescriptorPath, bool capturedCgroupMountMatches)
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
            _capturedProcMountId = capturedProcMountId;
            InitialDescriptorPath = initialDescriptorPath;
            CapturedCgroupMountMatches = capturedCgroupMountMatches;
        }

        internal bool CgroupIdentityMatches(SettlementDiagnostic? diagnostic = null)
        {
            if (_cgroupHandle.IsClosed || _cgroupHandle.IsInvalid) return false;
            var held = NativeFacts.ReadIdentity(_cgroupHandle.DangerousGetHandle().ToInt32());
            var path = ReadCgroupPathIdentity(_hostPath);
            diagnostic?.Held(held, ExpectedIdentity);
            diagnostic?.Path(path, ExpectedIdentity);
            return held.SameObject(ExpectedIdentity) && held.Links > 0 && path.ErrorFree && path.SameNode(ExpectedIdentity)
                && !_eventsHandle.IsClosed && !_procsHandle.IsClosed;
        }

        internal CgroupIdentity ReadCgroupIdentity() =>
            NativeFacts.ReadIdentity(_cgroupHandle.DangerousGetHandle().ToInt32());

        internal byte[] ReadEvents() => ReadHandle(_eventsHandle, 4096);
        internal byte[] ReadProcesses() => ReadHandle(_procsHandle, 64 * 1024);

        internal bool TryReadTrustedDescriptorPath(out string path)
        {
            path = string.Empty;
            var procMountId = TryReadMountId("/proc/self/fd");
            if (procMountId is null || procMountId != _capturedProcMountId
                || !HasVisibleProcMount(ReadBounded("/proc/self/mountinfo", 256 * 1024), procMountId.Value))
                return false;
            var descriptorPath = "/proc/self/fd/" + _cgroupHandle.DangerousGetHandle().ToInt32()
                .ToString(CultureInfo.InvariantCulture);
            path = ReadLink(descriptorPath);
            return true;
        }

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
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
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
        var procMountId = TryReadMountId("/proc/self/fd");
        var initialDescriptorPath = string.Empty;
        if (procMountId is not null)
        {
            try
            {
                if (HasVisibleProcMount(ReadBounded("/proc/self/mountinfo", 256 * 1024), procMountId.Value))
                    initialDescriptorPath = ReadLink("/proc/self/fd/" + descriptor.ToString(CultureInfo.InvariantCulture));
                else procMountId = null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or IsolationException
                or System.Text.DecoderFallbackException)
            {
                procMountId = null;
                initialDescriptorPath = string.Empty;
            }
        }
        var capturedCgroupMountMatches = false;
        try
        {
            var cgroupRootMountId = TryReadMountId("/sys/fs/cgroup");
            capturedCgroupMountMatches = cgroupRootMountId is not null && cgroupRootMountId == stat.MountId
                && HasVisibleCgroup2Mount(ReadBounded("/proc/self/mountinfo", 256 * 1024), cgroupRootMountId.Value);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or IsolationException)
        {
            capturedCgroupMountMatches = false;
        }
        var witness = new PhaseWitness(hostPath, launcherPid, handle, new SafeFileHandle((IntPtr)pidfd, ownsHandle: true),
            new SafeFileHandle((IntPtr)eventsFd, ownsHandle: true), new SafeFileHandle((IntPtr)procsFd, ownsHandle: true),
            stat.Device, stat.Inode, stat.MountId, procMountId, initialDescriptorPath, capturedCgroupMountMatches);
        try
        {
            var initialEvents = ParseCgroupEvents(witness.ReadEvents());
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

    private static CgroupIdentity ReadIdentity(int fd)
    {
        var stat = ReadStatx(fd);
        return new(stat.Device, stat.Inode, stat.MountId, stat.Links, (stat.Mode & 0xf000) == 0x4000);
    }

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
        if (Statx(fd, "", 0x1000, 0x17ff, out var buffer) != 0 || !HasStatxMountId(buffer.Mask))
            throw new IsolationException("statx-handle");
        return buffer;
    }

    private static StatxBuffer ReadStatx(int directory, string path, int flags, uint mask)
    {
        if (Statx(directory, path, flags, mask, out var buffer) != 0
            || ((mask & 0x1000) != 0 && !HasStatxMountId(buffer.Mask)))
            throw new IsolationException("statx-path");
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

    internal static Dictionary<string, string[]> ParseProcStatusFields(ReadOnlySpan<byte> bytes)
    {
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var line in Encoding.ASCII.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) throw new IsolationException("proc-fields-format");
            var name = line[..colon];
            if (name.Length == 0 || name.Any(static character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
                throw new IsolationException("proc-fields-format");
            var values = line[(colon + 1)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (!result.TryAdd(name, values)) throw new IsolationException("proc-fields");
        }
        return result;
    }

    internal static Dictionary<string, string[]> ParseCgroupEvents(ReadOnlySpan<byte> bytes)
    {
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var line in Encoding.ASCII.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 2 || fields[0] is not ("populated" or "frozen")
                || fields[1] is not ("0" or "1") || !result.TryAdd(fields[0], [fields[1]]))
                throw new IsolationException("cgroup-events-format");
        }
        if (result.Count != 2) throw new IsolationException("cgroup-events-format");
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
        OwnerPhase.Prepare => "prepare",
        OwnerPhase.Replay => "replay",
        OwnerPhase.Close => "close",
        OwnerPhase.Control => "control",
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
            && (left.Mode & 0xf000) == 0x8000 && (left.Mode & 0x12) == 0 && (left.Mode & 0x1) != 0
            && left.Uid == 0 && left.Links > 0;
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
        var procMount = ReadStatx(-100, proc + "/root/proc", 0, 0x17ff);
        if (rootEntry is null || !rootEntry.Options.Contains("ro", StringComparer.Ordinal)
            || (procMount.Mask & 0x1000) == 0 || !HasVisibleProcMount(entries, procMount.MountId))
            throw new IsolationException("unit-mount-namespace");

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
            && (!VisibleReadonlyMount(proc, entries, OwnerPaths.Source + "/.git")
                || !VisibleReadonlyMount(proc, entries, OwnerPaths.Native + "/.git")))
            throw new IsolationException("unit-git-mount");
        foreach (var path in writable)
            if (!VisibleMountWith(proc, entries, path, "rw")) throw new IsolationException("unit-readwrite-mount");
        if (phase is OwnerPhase.Replay or OwnerPhase.Control or OwnerPhase.Close
            && !VisibleReadonlyMount(proc, entries, OwnerPaths.Preparation))
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

    private static bool VisibleReadonlyMount(string proc, IReadOnlyList<MountEntry> entries, string path) =>
        VisibleMountWith(proc, entries, path, "ro");

    private static bool VisibleMountWith(string proc, IReadOnlyList<MountEntry> entries, string path, string option)
    {
        var visible = ReadStatx(-100, proc + "/root" + path, 0, 0x17ff);
        return (visible.Mask & 0x1000) != 0 && HasVisibleMountWith(entries, path, visible.MountId, option);
    }

    /// <summary>Requires an exact read-only mount for the supplied visible kernel mount ID and namespace path.</summary>
    /// <remarks>A hidden read-only mount or a read-only ancestor cannot authorize a different visible mount.</remarks>
    internal static bool HasVisibleReadonlyMount(byte[] mountInfo, string path, ulong visibleMountId) =>
        HasVisibleMountWith(ParseMountInfo(mountInfo), path, visibleMountId, "ro");

    /// <summary>Requires an exact writable mount for the supplied visible kernel mount ID and namespace path.</summary>
    /// <remarks>A hidden writable mount or writable ancestor cannot satisfy this readiness check.</remarks>
    internal static bool HasVisibleReadwriteMount(byte[] mountInfo, string path, ulong visibleMountId) =>
        HasVisibleMountWith(ParseMountInfo(mountInfo), path, visibleMountId, "rw");

    private static bool HasVisibleMountWith(IReadOnlyList<MountEntry> entries, string path, ulong visibleMountId, string option) =>
        visibleMountId != 0 && entries.Any(entry => entry.MountId == visibleMountId
            && entry.MountPoint == path && entry.Options.Contains(option, StringComparer.Ordinal));

    /// <summary>Matches the proc mount visible at the target's root against its kernel mountinfo identity.</summary>
    /// <remarks>Hidden stacked mounts cannot satisfy the check. The caller must obtain the ID from statx with STATX_MNT_ID.</remarks>
    internal static bool HasVisibleProcMount(byte[] mountInfo, ulong visibleMountId) =>
        HasVisibleProcMount(ParseMountInfo(mountInfo), visibleMountId);

    private static bool HasVisibleProcMount(IReadOnlyList<MountEntry> entries, ulong visibleMountId) =>
        visibleMountId != 0 && entries.Any(entry => entry.MountId == visibleMountId
            && entry.MountPoint == "/proc" && entry.FileSystem == "proc");

    private static List<MountEntry> ParseMountInfo(byte[] bytes)
    {
        var entries = new List<MountEntry>();
        var mountIds = new HashSet<ulong>();
        foreach (var line in Encoding.ASCII.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var separator = Array.IndexOf(fields, "-");
            if (separator < 6 || separator + 2 >= fields.Length
                || !ulong.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var mountId)
                || mountId == 0 || !mountIds.Add(mountId)) throw new IsolationException("mountinfo-format");
            entries.Add(new(mountId, DecodeMount(fields[4]), fields[5].Split(',').ToHashSet(StringComparer.Ordinal), fields[separator + 1]));
        }
        return entries;
    }

    private static string DecodeMount(string value) => value.Replace("\\040", " ", StringComparison.Ordinal)
        .Replace("\\011", "\t", StringComparison.Ordinal).Replace("\\012", "\n", StringComparison.Ordinal)
        .Replace("\\134", "\\", StringComparison.Ordinal);
    private sealed record MountEntry(ulong MountId, string MountPoint, HashSet<string> Options, string FileSystem);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit, Size = 256)]
    private struct StatxBuffer
    {
        [System.Runtime.InteropServices.FieldOffset(0)] internal uint Mask;
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
        OwnerWorkspace.MarkSdkValidationStage("sdk-path-validation");
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
            || text[..^1] is not (OwnerWorkspace.OwnedSdkDirectory or "/usr/share/dotnet" or "/usr/local/share/dotnet"))
            throw new IsolationException("sdk-host-path");
        var sdkPath = text[..^1];
        var directories = new List<string>();
        for (var current = sdkPath; current != "/"; current = Path.GetDirectoryName(current)!) directories.Add(current);
        directories.Add("/");
        OwnerWorkspace.MarkSdkValidationStage("sdk-directory-validation");
        foreach (var directory in directories)
        {
            var metadata = FileSystemInfoStat.ReadNoFollow(directory);
            if (!IsTrustedSdkDirectoryFact(directory, sdkPath, metadata.Uid, metadata.Mode,
                    (metadata.Mode & 0xf000) == 0x4000))
                throw new IsolationException("sdk-host-directory");
        }
        return sdkPath;
    }

    /// <summary>Pure policy for one canonical SDK ancestor fact; write bits, nonroot owners and links fail closed.</summary>
    internal static bool IsTrustedSdkDirectoryFact(string directory, string sdkPath, uint uid, uint mode, bool isDirectory)
    {
        if (!Path.IsPathFullyQualified(directory) || Path.GetFullPath(directory) != directory
            || !Path.IsPathFullyQualified(sdkPath) || Path.GetFullPath(sdkPath) != sdkPath
            || !isDirectory || uid != 0 || (mode & 0x12) != 0) return false;
        return directory == "/" || sdkPath == directory
            || sdkPath.StartsWith(directory.TrimEnd('/') + "/", StringComparison.Ordinal);
    }

    internal static async Task VerifySdkAsync(OwnerLayout layout, CancellationToken cancellationToken)
    {
        var sdk = ReadCanonicalSdkPath(layout);
        OwnerWorkspace.MarkSdkValidationStage("sdk-executable-validation");
        var binaryPath = sdk + "/dotnet";
        var binary = FileSystemInfoStat.Read(binaryPath);
        if ((binary.Mode & 0xf000) != 0x8000 || binary.Uid != 0 || (binary.Mode & 0x12) != 0
            || (binary.Mode & 0x1) == 0)
            throw new IsolationException("sdk-host-binary");
        OwnerWorkspace.MarkSdkValidationStage("sdk-version-validation");
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
