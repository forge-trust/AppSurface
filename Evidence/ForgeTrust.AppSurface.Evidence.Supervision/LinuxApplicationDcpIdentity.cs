using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using ForgeTrust.AppSurface.Evidence.Contracts;
using Microsoft.Win32.SafeHandles;
using static ForgeTrust.AppSurface.Evidence.Contracts.EvidenceLinuxFileSystem;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Native identity fields for one regular executable observed through an open file descriptor.</summary>
/// <remarks>Detached fields are comparison data only; they cannot construct a process identity or lease.</remarks>
internal readonly record struct LinuxDcpFileMetadata(uint DeviceMajor, uint DeviceMinor, ulong Inode,
    ushort Type, uint Uid, ushort Mode, ulong Length);

/// <summary>Retains the one selected application process whose actual executable is the audited DCP file.</summary>
/// <remarks>
/// Capture is available only to root on Linux x64. It reads only the exact generated application cgroup,
/// captures every listed member through <see cref="LinuxProcessIdentity"/>, and accepts exactly one member
/// whose retained <c>/proc/PID/exe</c> descriptor has the audited DCP file identity. Command lines and
/// executable link text are never used as authority. <see cref="Recheck"/> repeats live process, procfs,
/// executable, and named DCP-path checks; callers keep this identity until application stop is joined.
/// </remarks>
internal sealed class LinuxApplicationDcpIdentity : IDisposable
{
    internal const int MaximumProcessBytes = 4096;
    internal const int MaximumProcesses = 128;
    private const string CgroupMount = "/sys/fs/cgroup";
    private const int AtFdcwd = -100;
    private const ulong DirectoryFlags = 0x10000 | 0x80000;
    private const ulong ReadFlags = 0x80000 | 0x800;
    private const ulong NamedFlags = 0x200000 | 0x80000;
    private const ulong NamedDirectoryFlags = 0x200000 | 0x10000 | 0x80000;
    private const ulong MountResolution = 0x02 | 0x04;
    private const ulong DescendantResolution = MountResolution | 0x08 | 0x01;
    private const ulong AuditedPathResolution = MountResolution | 0x08;
    private const long ProcMagic = 0x9fa0;
    private const long Cgroup2Magic = 0x63677270;
    private const ushort RegularFileType = 0x8000;
    private const ushort DirectoryType = 0x4000;
    private const ushort RequiredDcpMode = RegularFileType | 0x16d;
    private const int MaximumResourceParentDepth = 16;

    private readonly object _sync = new();
    private readonly string _dcpRelativePath;
    private readonly SafeFileHandle _filesystemRoot;
    private readonly SafeFileHandle _auditedDcp;
    private readonly LinuxDcpFileMetadata _expectedFile;
    private readonly LinuxProcessIdentity _process;
    private readonly RetainedProcCandidate _proc;
    private bool _closed;
    private bool _rejected;

    private LinuxApplicationDcpIdentity(string dcpRelativePath, SafeFileHandle filesystemRoot,
        SafeFileHandle auditedDcp, LinuxDcpFileMetadata expectedFile, LinuxProcessIdentity process,
        RetainedProcCandidate proc)
    {
        _dcpRelativePath = dcpRelativePath;
        _filesystemRoot = filesystemRoot;
        _auditedDcp = auditedDcp;
        _expectedFile = expectedFile;
        _process = process;
        _proc = proc;
    }

    /// <summary>Gets the PID selected from the exact application cgroup and bound to the audited executable.</summary>
    internal uint Pid => _process.Pid;

    /// <summary>
    /// Selects exactly one live application member whose actual executable file is the audited DCP file.
    /// </summary>
    /// <param name="unit">The closed generated application unit to inspect.</param>
    /// <param name="uid">The expected nonroot application UID from retained account state.</param>
    /// <param name="gid">The expected nonroot application GID from retained account state.</param>
    /// <param name="auditedDcpPath">The absolute, canonical path independently audited by the application workspace.</param>
    /// <param name="token">The original owner deadline or stop token.</param>
    /// <returns>A retained actual process and executable identity; dispose it only after application stop joins.</returns>
    /// <exception cref="EvidenceAdmissionException">The native platform, cgroup, process set, or executable identity is unavailable or changed.</exception>
    internal static LinuxApplicationDcpIdentity Capture(LinuxUnitName unit, uint uid, uint gid,
        string auditedDcpPath, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(auditedDcpPath);
        var cgroupHandles = new List<SafeFileHandle>();
        SafeFileHandle? filesystemRoot = null;
        SafeFileHandle? auditedDcp = null;
        LinuxProcessIdentity? selectedProcess = null;
        RetainedProcCandidate? selectedProc = null;
        LinuxApplicationDcpIdentity? result = null;
        Exception? failure = null;

        try
        {
            token.ThrowIfCancellationRequested();
            RequirePlatform();
            LinuxProcessData.RequireSelection(1, uid, gid, unit, LinuxProcessSamplingRole.Application);
            var relativeDcpPath = RelativeDcpPath(auditedDcpPath);

            var cgroupRoot = OpenCgroupNode(null, CgroupMount, true, cgroupHandles, token);
            var systemSlice = OpenCgroupNode(cgroupRoot, "system.slice", true, cgroupHandles, token);
            var leaf = OpenCgroupNode(systemSlice, unit.Value, true, cgroupHandles, token);
            var processes = OpenCgroupNode(leaf, "cgroup.procs", false, cgroupHandles, token);
            var pids = ParseProcessIds(ReadBounded(processes.Handle, MaximumProcessBytes, token));

            filesystemRoot = OpenAt2(AtFdcwd, "/", DirectoryFlags, 0, MountResolution);
            RequireDirectory(filesystemRoot);
            auditedDcp = OpenAt2(Fd(filesystemRoot), relativeDcpPath, NamedFlags, 0, AuditedPathResolution);
            var expectedFile = ToFileMetadata(StatFd(auditedDcp));
            RequireAuditedDcp(expectedFile);
            RequireCgroupNode(processes, token);

            foreach (var pid in pids)
            {
                token.ThrowIfCancellationRequested();
                using var before = LinuxProcessIdentity.CaptureApplication(pid, uid, gid, unit, token);
                LinuxProcessIdentity? after = null;
                RetainedProcCandidate? candidate = null;
                try
                {
                    candidate = OpenProcCandidate(pid, token);
                    var actualFile = ToFileMetadata(StatFd(candidate.Executable));
                    after = LinuxProcessIdentity.CaptureApplication(pid, uid, gid, unit, token);
                    RequireCandidateContinuity(before.SampledFacts, after.SampledFacts, pid, uid, gid, unit);
                    RequireProcCandidate(candidate, token);

                    if (!SameExecutable(actualFile, expectedFile)) continue;
                    if (selectedProcess is not null) throw Rejected();
                    RequireAuditedDcp(ToFileMetadata(StatFd(auditedDcp)));
                    if (!SameExecutable(ToFileMetadata(StatFd(auditedDcp)), expectedFile)) throw Rejected();
                    selectedProcess = after;
                    after = null;
                    selectedProc = candidate;
                    candidate = null;
                }
                finally
                {
                    after?.Dispose();
                    candidate?.Dispose();
                }
            }

            if (selectedProcess is null || selectedProc is null) throw Rejected();
            var secondPids = ParseProcessIds(ReadBounded(processes.Handle, MaximumProcessBytes, token));
            if (!pids.AsSpan().SequenceEqual(secondPids)) throw Rejected();
            RequireCgroupNode(processes, token);
            RequireProcCandidate(selectedProc, token);
            selectedProcess.Recheck(token);
            RequireNamedDcpPath(filesystemRoot, relativeDcpPath, auditedDcp, expectedFile, token);
            RequireCurrentExecutable(selectedProc, expectedFile, token);
            selectedProcess.Recheck(token);
            token.ThrowIfCancellationRequested();

            result = new LinuxApplicationDcpIdentity(relativeDcpPath, filesystemRoot, auditedDcp,
                expectedFile, selectedProcess, selectedProc);
            filesystemRoot = null;
            auditedDcp = null;
            selectedProcess = null;
            selectedProc = null;
        }
        catch (OperationCanceledException error) when (token.IsCancellationRequested) { failure = error; }
        catch (Exception) { failure = Rejected(); }
        finally
        {
            if (!CloseHandles(cgroupHandles)) failure ??= Rejected();
            var cleanupFailure = DisposeCaptured(selectedProc);
            failure ??= cleanupFailure;
            cleanupFailure = DisposeCaptured(selectedProcess);
            failure ??= cleanupFailure;
            cleanupFailure = DisposeCaptured(auditedDcp);
            failure ??= cleanupFailure;
            cleanupFailure = DisposeCaptured(filesystemRoot);
            failure ??= cleanupFailure;
        }

        if (failure is OperationCanceledException) { DisposeCaptured(result); throw new OperationCanceledException(token); }
        if (failure is not null) { DisposeCaptured(result); throw Rejected(); }
        return result ?? throw Rejected();
    }

    /// <summary>Rechecks the retained process, its current <c>exe</c> magic link, and the named audited DCP path.</summary>
    /// <param name="token">The original owner deadline or stop token.</param>
    /// <exception cref="EvidenceAdmissionException">The process exited, changed identity, or no longer runs the audited DCP file.</exception>
    /// <remarks>Cancellation is propagated without latching an integrity failure; every other rejection is latched.</remarks>
    internal void Recheck(CancellationToken token)
    {
        lock (_sync)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                if (_closed || _rejected) throw Rejected();
                _process.Recheck(token);
                RequireProcCandidate(_proc, token);
                RequireNamedDcpPath(_filesystemRoot, _dcpRelativePath, _auditedDcp, _expectedFile, token);
                RequireCurrentExecutable(_proc, _expectedFile, token);
                RequireNamedDcpPath(_filesystemRoot, _dcpRelativePath, _auditedDcp, _expectedFile, token);
                _process.Recheck(token);
                token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) { _rejected = true; throw Rejected(); }
        }
    }

    /// <summary>Requires a resource peer to run the root-audited host and descend from this retained DCP process.</summary>
    /// <param name="resource">The retained authenticated application socket peer.</param>
    /// <param name="auditedRuntimeHost">The canonical root-selected runtime host path.</param>
    /// <param name="token">The original owner/resource deadline.</param>
    /// <remarks>Executable file identity and a bounded actual PPid chain are sampled twice; proc command text is not authority.</remarks>
    internal void RequireResourcePeer(LinuxProcessIdentity resource, string auditedRuntimeHost, CancellationToken token)
    {
        lock (_sync)
        {
            var ownedProcesses = new List<LinuxProcessIdentity>();
            var procCandidates = new List<RetainedProcCandidate>();
            SafeFileHandle? runtimeHost = null;
            RetainedProcCandidate? resourceProc = null;
            try
            {
                token.ThrowIfCancellationRequested();
                if (_closed || _rejected || resource is null
                    || resource.Role != LinuxProcessSamplingRole.Application
                    || resource.Unit != _process.Unit || resource.Uid != _process.Uid || resource.Gid != _process.Gid)
                    throw Rejected();

                var relativeHost = RelativeDcpPath(auditedRuntimeHost);
                resource.Recheck(token);
                _process.Recheck(token);
                runtimeHost = OpenAt2(Fd(_filesystemRoot), relativeHost, NamedFlags, 0, AuditedPathResolution);
                var expectedHost = ToFileMetadata(StatFd(runtimeHost));
                RequireAuditedRuntimeHost(expectedHost);
                RequireNamedRuntimeHostPath(_filesystemRoot, relativeHost, runtimeHost, expectedHost, token);

                resourceProc = OpenProcCandidate(resource.Pid, token);
                RequireCurrentExecutable(resourceProc, expectedHost, token);
                var first = CaptureParentWalk(resource, ownedProcesses, procCandidates, token);
                RequireParentChain(first, resource.SampledFacts, _process.SampledFacts,
                    resource.Uid, resource.Gid, resource.Unit);
                var second = CaptureParentWalk(resource, ownedProcesses, procCandidates, token);
                RequireParentChain(second, resource.SampledFacts, _process.SampledFacts,
                    resource.Uid, resource.Gid, resource.Unit);
                RequireSameParentWalk(first, second);

                resource.Recheck(token);
                foreach (var process in ownedProcesses) process.Recheck(token);
                foreach (var candidate in procCandidates) RequireProcCandidate(candidate, token);
                RequireProcCandidate(resourceProc, token);
                RequireCurrentExecutable(resourceProc, expectedHost, token);
                RequireNamedRuntimeHostPath(_filesystemRoot, relativeHost, runtimeHost, expectedHost, token);
                _process.Recheck(token);
                RequireProcCandidate(_proc, token);
                RequireCurrentExecutable(_proc, _expectedFile, token);
                RequireNamedDcpPath(_filesystemRoot, _dcpRelativePath, _auditedDcp, _expectedFile, token);
                token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) { _rejected = true; throw Rejected(); }
            finally
            {
                var failed = false;
                foreach (var candidate in procCandidates.AsEnumerable().Reverse())
                    failed |= DisposeCaptured(candidate) is not null;
                foreach (var process in ownedProcesses.AsEnumerable().Reverse())
                    failed |= DisposeCaptured(process) is not null;
                failed |= DisposeCaptured(resourceProc) is not null;
                failed |= DisposeCaptured(runtimeHost) is not null;
                if (failed) { _rejected = true; throw Rejected(); }
            }
        }
    }

    /// <summary>Requires an authenticated application process to retain the exact native resource argv.</summary>
    /// <param name="process">The retained authenticated application process identity.</param>
    /// <param name="runtimeHost">The independently selected canonical runtime host path.</param>
    /// <param name="resourceDll">The root selected bundle resource DLL path.</param>
    /// <param name="socketPath">The root selected native HTTP socket path.</param>
    /// <param name="token">The original owner deadline or stop token.</param>
    /// <remarks>The process is rechecked around two bounded reads from its authenticated procfs cmdline file.</remarks>
    /// <exception cref="EvidenceAdmissionException">The process, procfs binding, or exact argument vector changed.</exception>
    internal static void RequireResourceCommand(LinuxProcessIdentity process, string runtimeHost,
        string resourceDll, string socketPath, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            if (process is null || process.Role != LinuxProcessSamplingRole.Application) throw Rejected();
            RequireResourcePaths(runtimeHost, resourceDll, socketPath);
            process.Recheck(token);
            using var proc = OpenProcCandidate(process.Pid, token);
            RequireProcCandidate(proc, token);
            using var commandLine = OpenAt2(Fd(proc.Directory), "cmdline", ReadFlags, 0, DescendantResolution);
            RequireProcRegularFile(commandLine);
            var first = ReadBounded(commandLine, MaximumProcessBytes, token);
            RequireResourceCommandBytes(first, runtimeHost, resourceDll, socketPath);
            process.Recheck(token);
            RequireProcCandidate(proc, token);
            var second = ReadBounded(commandLine, MaximumProcessBytes, token);
            RequireResourceCommandBytes(second, runtimeHost, resourceDll, socketPath);
            if (!first.AsSpan().SequenceEqual(second)) throw Rejected();
            RequireProcCandidate(proc, token);
            process.Recheck(token);
            token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { throw Rejected(); }
    }

    /// <summary>Checks the complete bounded Linux NUL-separated vector against the selected six arguments.</summary>
    /// <param name="bytes">Raw bytes read from the authenticated procfs cmdline file.</param>
    /// <param name="runtimeHost">Expected canonical runtime host path.</param>
    /// <param name="resourceDll">Expected root selected resource DLL path.</param>
    /// <param name="socketPath">Expected root selected socket path.</param>
    /// <remarks>Exactly six nonempty UTF-8 arguments and one terminating NUL per argument are required.</remarks>
    internal static void RequireResourceCommandBytes(ReadOnlySpan<byte> bytes, string runtimeHost,
        string resourceDll, string socketPath)
    {
        RequireResourcePaths(runtimeHost, resourceDll, socketPath);
        if (bytes.IsEmpty || bytes.Length > MaximumProcessBytes || bytes[^1] != 0) throw Rejected();
        var expected = new[] { runtimeHost, resourceDll, "--socket", socketPath, "--case", "normal" };
        var offset = 0;
        foreach (var argument in expected)
        {
            var end = bytes[offset..].IndexOf((byte)0);
            if (end < 1) throw Rejected();
            byte[] encoded;
            try { encoded = new UTF8Encoding(false, true).GetBytes(argument); }
            catch (EncoderFallbackException) { throw Rejected(); }
            if (!bytes.Slice(offset, end).SequenceEqual(encoded)) throw Rejected();
            offset += end + 1;
        }
        if (offset != bytes.Length) throw Rejected();
    }

    /// <summary>Closes the retained process, procfs, executable, and audited-path descriptors exactly once.</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_closed) return;
            _closed = true;
            var closed = true;
            try { _process.Dispose(); } catch (Exception) { closed = false; }
            try { _proc.Dispose(); } catch (Exception) { closed = false; }
            try { _auditedDcp.Dispose(); } catch (Exception) { closed = false; }
            try { _filesystemRoot.Dispose(); } catch (Exception) { closed = false; }
            if (!closed) throw Rejected();
        }
    }

    /// <summary>Parses the complete bounded cgroup.procs record and rejects duplicate, malformed, or excess PIDs.</summary>
    /// <param name="bytes">Bytes read from the exact retained cgroup.procs descriptor.</param>
    /// <returns>Detached process IDs in kernel record order.</returns>
    internal static uint[] ParseProcessIds(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > MaximumProcessBytes || bytes[^1] != (byte)'\n') throw Rejected();
        var pids = new List<uint>(Math.Min(MaximumProcesses, 16));
        var unique = new HashSet<uint>();
        while (!bytes.IsEmpty)
        {
            var newline = bytes.IndexOf((byte)'\n');
            if (newline < 1 || newline > 10 || (newline > 1 && bytes[0] == (byte)'0')
                || pids.Count == MaximumProcesses) throw Rejected();
            uint pid = 0;
            foreach (var value in bytes[..newline])
            {
                if (value is < (byte)'0' or > (byte)'9') throw Rejected();
                var digit = (uint)(value - (byte)'0');
                if (pid > (uint.MaxValue - digit) / 10) throw Rejected();
                pid = pid * 10 + digit;
            }
            if (pid is 0 or > int.MaxValue || !unique.Add(pid)) throw Rejected();
            pids.Add(pid);
            bytes = bytes[(newline + 1)..];
        }
        return pids.ToArray();
    }

    /// <summary>Compares the exact executable identity fields required by the audited DCP contract.</summary>
    /// <param name="actual">Fields from the retained <c>/proc/PID/exe</c> file descriptor.</param>
    /// <param name="audited">Fields from the retained independently audited DCP path descriptor.</param>
    /// <returns>Whether device, inode, type, UID, full mode, and length all match.</returns>
    internal static bool SameExecutable(LinuxDcpFileMetadata actual, LinuxDcpFileMetadata audited) =>
        actual == audited;

    /// <summary>Detached PPid link; it grants no live process identity or resource admission.</summary>
    internal readonly record struct LinuxApplicationParentLink(uint Pid, uint ParentPid, ulong StartTimeTicks);

    /// <summary>One actual parent-walk node paired with its independently captured process sample.</summary>
    internal readonly record struct LinuxApplicationParentFact(uint Pid, uint ParentPid, LinuxProcessSample Process);

    /// <summary>Parses one bounded proc stat record's PID, PPid, and start time after validating the full stat grammar.</summary>
    internal static LinuxApplicationParentLink ParseParentLink(ReadOnlySpan<byte> bytes)
    {
        var process = LinuxProcessData.ParseStat(bytes);
        var text = new UTF8Encoding(false, true).GetString(bytes);
        var closing = text.LastIndexOf(')');
        var fields = text[(closing + 2)..^1].Split(' ');
        if (fields.Length != 50 || !uint.TryParse(fields[1], NumberStyles.None,
            CultureInfo.InvariantCulture, out var parentPid) || parentPid > int.MaxValue)
            throw Rejected();
        return new(process.Pid, parentPid, process.StartTimeTicks);
    }

    /// <summary>Checks that actual PPid links reach the retained DCP through at most sixteen same-unit app processes.</summary>
    internal static void RequireParentChain(IReadOnlyList<LinuxApplicationParentFact> chain,
        LinuxProcessSample resource, LinuxProcessSample dcp, uint uid, uint gid, LinuxUnitName unit)
    {
        try
        {
            LinuxProcessData.RequireExpected(resource, resource.Stat.Pid, uid, gid, unit, LinuxProcessSamplingRole.Application);
            LinuxProcessData.RequireExpected(dcp, dcp.Stat.Pid, uid, gid, unit, LinuxProcessSamplingRole.Application);
            if (resource.Stat.Pid == dcp.Stat.Pid)
            {
                if (resource.Stat.StartTimeTicks != dcp.Stat.StartTimeTicks || chain.Count != 0) throw Rejected();
                return;
            }
            if (chain.Count is 0 or > MaximumResourceParentDepth) throw Rejected();

            var visited = new HashSet<uint>();
            var expectedPid = resource.Stat.Pid;
            var reachedDcp = false;
            for (var index = 0; index < chain.Count; index++)
            {
                var fact = chain[index];
                LinuxProcessData.RequireExpected(fact.Process, fact.Pid, uid, gid, unit,
                    LinuxProcessSamplingRole.Application);
                if (fact.Pid != expectedPid || fact.Process.Stat.Pid != fact.Pid || !visited.Add(fact.Pid)
                    || fact.ParentPid is 0 or > int.MaxValue) throw Rejected();
                if (index == 0) LinuxProcessData.RequireSameIdentity(resource, fact.Process);
                if (fact.ParentPid == dcp.Stat.Pid)
                {
                    reachedDcp = index == chain.Count - 1;
                    break;
                }
                if (visited.Contains(fact.ParentPid)) throw Rejected();
                expectedPid = fact.ParentPid;
            }
            if (!reachedDcp) throw Rejected();
        }
        catch (Exception) { throw Rejected(); }
    }

    /// <summary>Requires two bounded parent walks to preserve PPids and every process start-time/credential identity.</summary>
    internal static void RequireSameParentWalk(IReadOnlyList<LinuxApplicationParentFact> first,
        IReadOnlyList<LinuxApplicationParentFact> second)
    {
        if (first.Count != second.Count) throw Rejected();
        for (var index = 0; index < first.Count; index++)
        {
            if (first[index].Pid != second[index].Pid || first[index].ParentPid != second[index].ParentPid)
                throw Rejected();
            try { LinuxProcessData.RequireSameIdentity(first[index].Process, second[index].Process); }
            catch (Exception) { throw Rejected(); }
        }
    }

    /// <summary>Requires root ownership, a regular executable, no write/special bits, and nonempty runtime bytes.</summary>
    internal static void RequireAuditedRuntimeHost(LinuxDcpFileMetadata value)
    {
        if (value.Inode == 0 || value.Type != RegularFileType || value.Uid != 0
            || (value.Mode & 0x0e00) != 0 || (value.Mode & 0x92) != 0
            || (value.Mode & 0x49) == 0 || value.Length == 0) throw Rejected();
    }

    /// <summary>Requires before/after retained process samples to identify the same live application member.</summary>
    /// <param name="before">The authenticated sample captured before inspecting its executable descriptor.</param>
    /// <param name="after">The independently captured sample after executable inspection.</param>
    /// <param name="pid">The cgroup.procs candidate PID.</param>
    /// <param name="uid">Expected application UID.</param>
    /// <param name="gid">Expected application GID.</param>
    /// <param name="unit">Exact generated application unit.</param>
    internal static void RequireCandidateContinuity(LinuxProcessSample before, LinuxProcessSample after,
        uint pid, uint uid, uint gid, LinuxUnitName unit)
    {
        try
        {
            LinuxProcessData.RequireExpected(before, pid, uid, gid, unit, LinuxProcessSamplingRole.Application);
            LinuxProcessData.RequireExpected(after, pid, uid, gid, unit, LinuxProcessSamplingRole.Application);
            LinuxProcessData.RequireSameIdentity(before, after);
        }
        catch (Exception) { throw Rejected(); }
    }

    private static string RelativeDcpPath(string path)
    {
        if (path.Length is < 2 or > 4096 || path[0] != '/' || path.Contains('\0')) throw Rejected();
        var relative = path[1..];
        if (relative.Split('/').Any(static part => part is "" or "." or "..")) throw Rejected();
        return relative;
    }

    private static void RequireResourcePaths(string runtimeHost, string resourceDll, string socketPath)
    {
        _ = RelativeDcpPath(runtimeHost);
        _ = RelativeDcpPath(resourceDll);
        _ = RelativeDcpPath(socketPath);
        if (!resourceDll.EndsWith("/resource/NativeHttpResource.dll", StringComparison.Ordinal)
            || !socketPath.EndsWith("/http.sock", StringComparison.Ordinal)) throw Rejected();
    }

    private static void RequirePlatform()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64
            || RuntimeInformation.OSArchitecture != Architecture.X64 || GetUid() != 0 || GetEffectiveUid() != 0)
            throw Rejected();
    }

    private static CgroupNode OpenCgroupNode(CgroupNode? parent, string name, bool directory,
        List<SafeFileHandle> retained, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (parent is not null) RequireCgroupNode(parent, token);
        var handle = OpenAt2(parent is null ? AtFdcwd : Fd(parent.Handle), name,
            directory ? DirectoryFlags : ReadFlags, 0,
            parent is null ? MountResolution : DescendantResolution);
        retained.Add(handle);
        token.ThrowIfCancellationRequested();
        var metadata = InspectCgroupNode(handle, directory);
        var node = new CgroupNode(handle, parent, name, directory, metadata);
        RequireCgroupNode(node, token);
        return node;
    }

    private static void RequireCgroupNode(CgroupNode node, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (node.Parent is not null) RequireCgroupNode(node.Parent, token);
        if (!SameKernelNode(node.Metadata, InspectCgroupNode(node.Handle, node.Directory))) throw Rejected();
        using var named = OpenAt2(node.Parent is null ? AtFdcwd : Fd(node.Parent.Handle), node.Name,
            node.Directory ? NamedDirectoryFlags : NamedFlags, 0,
            node.Parent is null ? MountResolution : DescendantResolution);
        if (!SameKernelNode(node.Metadata, InspectCgroupNode(named, node.Directory))) throw Rejected();
        token.ThrowIfCancellationRequested();
    }

    private static KernelMetadata InspectCgroupNode(SafeFileHandle handle, bool directory)
    {
        RequireFileSystem(handle, Cgroup2Magic);
        var stat = StatFd(handle);
        var metadata = ToKernelMetadata(stat);
        if (metadata.Inode == 0 || metadata.Uid != 0 || metadata.Gid != 0
            || (metadata.Mode & 0xf000) != (directory ? DirectoryType : RegularFileType)
            || (metadata.Mode & 0x0c12) != 0
            || (directory ? (metadata.Mode & 0x140) != 0x140 || metadata.LinkCount < 2
                : (metadata.Mode & 0x100) == 0 || metadata.LinkCount != 1))
            throw Rejected();
        return metadata;
    }

    private static bool SameKernelNode(KernelMetadata left, KernelMetadata right) =>
        left.DeviceMajor == right.DeviceMajor && left.DeviceMinor == right.DeviceMinor
        && left.Inode == right.Inode && left.Uid == right.Uid && left.Gid == right.Gid
        && left.Mode == right.Mode;

    private static byte[] ReadBounded(SafeFileHandle handle, int maximum, CancellationToken token)
    {
        var bytes = new byte[maximum + 1];
        var offset = 0;
        while (offset < bytes.Length)
        {
            token.ThrowIfCancellationRequested();
            var count = RandomAccess.Read(handle, bytes.AsSpan(offset, Math.Min(4096, bytes.Length - offset)), offset);
            token.ThrowIfCancellationRequested();
            if (count == 0) return bytes[..offset];
            offset += count;
            if (offset > maximum) throw Rejected();
        }
        throw Rejected();
    }

    private static RetainedProcCandidate OpenProcCandidate(uint pid, CancellationToken token)
    {
        SafeFileHandle? root = null;
        SafeFileHandle? directory = null;
        SafeFileHandle? executable = null;
        RetainedProcCandidate? candidate = null;
        try
        {
            token.ThrowIfCancellationRequested();
            root = OpenAt2(AtFdcwd, "/proc", DirectoryFlags, 0, MountResolution);
            var rootMetadata = InspectProcDirectory(root);
            directory = OpenAt2(Fd(root), pid.ToString(CultureInfo.InvariantCulture),
                DirectoryFlags, 0, DescendantResolution);
            var directoryMetadata = InspectProcDirectory(directory);
            var pidName = pid.ToString(CultureInfo.InvariantCulture);
            executable = OpenExecutable(directory);
            candidate = new RetainedProcCandidate(root, directory, executable, pidName, rootMetadata, directoryMetadata);
            root = null;
            directory = null;
            executable = null;
            RequireProcCandidate(candidate, token);
            var result = candidate;
            candidate = null;
            return result;
        }
        catch
        {
            DisposeCaptured(candidate);
            DisposeCaptured(executable);
            DisposeCaptured(directory);
            DisposeCaptured(root);
            throw;
        }
    }

    private static void RequireProcCandidate(RetainedProcCandidate candidate, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (InspectProcDirectory(candidate.Root) != candidate.RootMetadata
            || InspectProcDirectory(candidate.Directory) != candidate.DirectoryMetadata) throw Rejected();
        using var namedRoot = OpenAt2(AtFdcwd, "/proc", NamedDirectoryFlags, 0, MountResolution);
        if (InspectProcDirectory(namedRoot) != candidate.RootMetadata) throw Rejected();
        using var namedPid = OpenAt2(Fd(candidate.Root), candidate.PidName,
            NamedDirectoryFlags, 0, DescendantResolution);
        if (InspectProcDirectory(namedPid) != candidate.DirectoryMetadata) throw Rejected();
        token.ThrowIfCancellationRequested();
    }

    private static ProcDirectoryMetadata InspectProcDirectory(SafeFileHandle handle)
    {
        RequireFileSystem(handle, ProcMagic);
        var stat = StatFd(handle);
        if (stat.Inode == 0 || (stat.Mode & 0xf000) != DirectoryType) throw Rejected();
        return new(stat.DeviceMajor, stat.DeviceMinor, stat.Inode, stat.Uid, stat.Gid, stat.Mode);
    }

    private static void RequireProcRegularFile(SafeFileHandle handle)
    {
        RequireFileSystem(handle, ProcMagic);
        var stat = StatFd(handle);
        if (stat.Inode == 0 || (stat.Mode & 0xf000) != RegularFileType) throw Rejected();
    }

    private static SafeFileHandle OpenExecutable(SafeFileHandle processDirectory)
    {
        // "exe" is the one fixed procfs magic-link intentionally followed; no caller path is accepted here.
        var fd = OpenAt(Fd(processDirectory), "exe", 0x200000 | 0x80000);
        if (fd < 0)
            throw new IOException("Could not open the selected proc executable.", new Win32Exception(Marshal.GetLastPInvokeError()));
        return new SafeFileHandle((IntPtr)fd, ownsHandle: true);
    }

    private static void RequireCurrentExecutable(RetainedProcCandidate candidate,
        LinuxDcpFileMetadata expected, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var retained = ToFileMetadata(StatFd(candidate.Executable));
        if (!SameExecutable(retained, expected)) throw Rejected();
        using var current = OpenExecutable(candidate.Directory);
        if (!SameExecutable(ToFileMetadata(StatFd(current)), expected)) throw Rejected();
        token.ThrowIfCancellationRequested();
    }

    private List<LinuxApplicationParentFact> CaptureParentWalk(LinuxProcessIdentity resource,
        List<LinuxProcessIdentity> owned, List<RetainedProcCandidate> candidates, CancellationToken token)
    {
        if (resource.Pid == _process.Pid) return [];
        var facts = new List<LinuxApplicationParentFact>();
        var visited = new HashSet<uint> { resource.Pid };
        var current = resource;
        for (var depth = 0; depth < MaximumResourceParentDepth; depth++)
        {
            token.ThrowIfCancellationRequested();
            current.Recheck(token);
            var proc = OpenProcCandidate(current.Pid, token);
            candidates.Add(proc);
            using var stat = OpenAt2(Fd(proc.Directory), "stat", ReadFlags, 0, DescendantResolution);
            RequireProcRegularFile(stat);
            var link = ParseParentLink(ReadBounded(stat, LinuxProcessData.MaximumStatBytes, token));
            if (link.Pid != current.Pid || link.StartTimeTicks != current.StartTimeTicks) throw Rejected();
            RequireProcCandidate(proc, token);
            current.Recheck(token);
            facts.Add(new(link.Pid, link.ParentPid, current.SampledFacts));
            if (link.ParentPid == _process.Pid) return facts;
            if (link.ParentPid is 0 or > int.MaxValue || !visited.Add(link.ParentPid)) throw Rejected();
            current = LinuxProcessIdentity.CaptureApplication(link.ParentPid, resource.Uid, resource.Gid,
                resource.Unit, token);
            owned.Add(current);
        }
        throw Rejected();
    }

    private static void RequireNamedRuntimeHostPath(SafeFileHandle filesystemRoot, string relativePath,
        SafeFileHandle retainedHost, LinuxDcpFileMetadata expected, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var retained = ToFileMetadata(StatFd(retainedHost));
        RequireAuditedRuntimeHost(retained);
        if (!SameExecutable(retained, expected)) throw Rejected();
        using var named = OpenAt2(Fd(filesystemRoot), relativePath, NamedFlags, 0, AuditedPathResolution);
        var current = ToFileMetadata(StatFd(named));
        RequireAuditedRuntimeHost(current);
        if (!SameExecutable(current, expected)) throw Rejected();
        token.ThrowIfCancellationRequested();
    }

    private static void RequireNamedDcpPath(SafeFileHandle filesystemRoot, string relativePath,
        SafeFileHandle retainedDcp, LinuxDcpFileMetadata expected, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var retained = ToFileMetadata(StatFd(retainedDcp));
        RequireAuditedDcp(retained);
        if (!SameExecutable(retained, expected)) throw Rejected();
        using var named = OpenAt2(Fd(filesystemRoot), relativePath, NamedFlags, 0, AuditedPathResolution);
        var current = ToFileMetadata(StatFd(named));
        RequireAuditedDcp(current);
        if (!SameExecutable(current, expected)) throw Rejected();
        token.ThrowIfCancellationRequested();
    }

    internal static void RequireAuditedDcp(LinuxDcpFileMetadata value)
    {
        if (value.Inode == 0 || value.Type != RegularFileType || value.Uid != 0
            || value.Mode != RequiredDcpMode || value.Length == 0) throw Rejected();
    }

    private static LinuxDcpFileMetadata ToFileMetadata(Statx stat) =>
        new(stat.DeviceMajor, stat.DeviceMinor, stat.Inode, (ushort)(stat.Mode & 0xf000),
            stat.Uid, stat.Mode, stat.Size);

    private static KernelMetadata ToKernelMetadata(Statx stat) =>
        new(stat.DeviceMajor, stat.DeviceMinor, stat.Inode, stat.Uid, stat.Gid,
            stat.Mode, stat.Nlink, stat.Size);

    private static void RequireDirectory(SafeFileHandle handle)
    {
        var stat = StatFd(handle);
        if (stat.Inode == 0 || (stat.Mode & 0xf000) != DirectoryType) throw Rejected();
    }

    private static void RequireFileSystem(SafeFileHandle handle, long expectedType)
    {
        if (FstatFs(handle, out var filesystem) != 0 || filesystem.Type != expectedType) throw Rejected();
    }

    private static Exception? DisposeCaptured(IDisposable? value)
    {
        if (value is null) return null;
        try { value.Dispose(); return null; }
        catch (Exception) { return Rejected(); }
    }

    private static bool CloseHandles(List<SafeFileHandle> handles)
    {
        var success = true;
        for (var index = handles.Count - 1; index >= 0; index--)
        {
            try { handles[index].Dispose(); }
            catch (Exception) { success = false; }
        }
        handles.Clear();
        return success;
    }

    private static EvidenceAdmissionException Rejected() =>
        new("ASEVD410", "The selected application DCP identity is unavailable or changed.");

    private sealed record CgroupNode(SafeFileHandle Handle, CgroupNode? Parent, string Name,
        bool Directory, KernelMetadata Metadata);

    private sealed record RetainedProcCandidate(SafeFileHandle Root, SafeFileHandle Directory,
        SafeFileHandle Executable, string PidName, ProcDirectoryMetadata RootMetadata,
        ProcDirectoryMetadata DirectoryMetadata) : IDisposable
    {
        public void Dispose()
        {
            var success = true;
            try { Executable.Dispose(); } catch (Exception) { success = false; }
            try { Directory.Dispose(); } catch (Exception) { success = false; }
            try { Root.Dispose(); } catch (Exception) { success = false; }
            if (!success) throw Rejected();
        }
    }

    private readonly record struct KernelMetadata(uint DeviceMajor, uint DeviceMinor, ulong Inode,
        uint Uid, uint Gid, ushort Mode, uint LinkCount, ulong Length);

    private readonly record struct ProcDirectoryMetadata(uint DeviceMajor, uint DeviceMinor, ulong Inode,
        uint Uid, uint Gid, ushort Mode);

    [StructLayout(LayoutKind.Explicit, Size = 120)]
    private struct FileSystem { [FieldOffset(0)] internal long Type; }

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAt(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "fstatfs", SetLastError = true)]
    private static extern int FstatFs(SafeFileHandle handle, out FileSystem filesystem);

    [DllImport("libc", EntryPoint = "getuid")]
    private static extern uint GetUid();

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUid();
}
