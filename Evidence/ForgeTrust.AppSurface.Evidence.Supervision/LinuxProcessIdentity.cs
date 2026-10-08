using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using ForgeTrust.AppSurface.Evidence.Contracts;
using Microsoft.Win32.SafeHandles;
using static ForgeTrust.AppSurface.Evidence.Contracts.EvidenceLinuxFileSystem;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Closed sampling roles; selecting a role is data, not owner activation.</summary>
internal enum LinuxProcessSamplingRole
{
    /// <summary>The generated nonroot worker service.</summary>
    Worker,
    /// <summary>The generated root owner service, selected explicitly.</summary>
    Owner,
}

/// <summary>Four sampled Linux IDs. This value cannot authenticate a process.</summary>
/// <param name="Real">Real ID.</param>
/// <param name="Effective">Effective ID.</param>
/// <param name="Saved">Saved-set ID.</param>
/// <param name="FileSystem">Filesystem ID.</param>
internal readonly record struct LinuxProcessIds(uint Real, uint Effective, uint Saved, uint FileSystem)
{
    /// <summary>Requires all four sampled IDs to equal the independently selected ID.</summary>
    internal bool AllEqual(uint expected) =>
        Real == expected && Effective == expected && Saved == expected && FileSystem == expected;
}

/// <summary>Decoded stat facts only, including the kernel start-time tick count.</summary>
/// <param name="Pid">Decoded PID.</param>
/// <param name="StartTimeTicks">Field 22, not a wall-clock timestamp.</param>
/// <param name="State">Field 3; zombie/dead states cannot satisfy a live identity.</param>
internal readonly record struct LinuxProcessStat(uint Pid, ulong StartTimeTicks, char State);

/// <summary>Detached proc data. Constructing or parsing it grants no live identity or admission.</summary>
/// <param name="Stat">Sampled stat facts.</param>
/// <param name="Uids">Complete UID tuple.</param>
/// <param name="Gids">Complete GID tuple.</param>
/// <param name="ControlGroup">One unified cgroup path; it does not establish group emptiness.</param>
internal sealed record LinuxProcessSample(
    LinuxProcessStat Stat, LinuxProcessIds Uids, LinuxProcessIds Gids, string ControlGroup);

/// <summary>
/// Bounded, intentionally portable proc grammar and identity comparison; no native factory is exposed here.
/// </summary>
internal static class LinuxProcessData
{
    /// <summary>Maximum status bytes, charged before decoding.</summary>
    internal const int MaximumStatusBytes = 64 * 1024;
    /// <summary>Maximum stat bytes, including the ignored comm field.</summary>
    internal const int MaximumStatBytes = 16 * 1024;
    /// <summary>Maximum cgroup bytes.</summary>
    internal const int MaximumCgroupBytes = 4096;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    /// <summary>Decodes one canonical Uid and Gid row; unrelated bounded status rows are not retained.</summary>
    internal static (LinuxProcessIds Uids, LinuxProcessIds Gids) ParseStatus(ReadOnlySpan<byte> bytes)
    {
        var text = Decode(bytes, MaximumStatusBytes);
        LinuxProcessIds? uids = null;
        LinuxProcessIds? gids = null;
        var rows = text[..^1].Split('\n');
        if (rows.Length > 512) throw LinuxProcessIdentity.Rejected();
        foreach (var row in rows)
        {
            var colon = row.IndexOf(':');
            if (colon < 1 || row.Length > 8192) throw LinuxProcessIdentity.Rejected();
            var name = row[..colon];
            if (name.Equals("Uid", StringComparison.OrdinalIgnoreCase))
            {
                if (name != "Uid" || uids is not null) throw LinuxProcessIdentity.Rejected();
                uids = ParseIds(row[(colon + 1)..]);
            }
            else if (name.Equals("Gid", StringComparison.OrdinalIgnoreCase))
            {
                if (name != "Gid" || gids is not null) throw LinuxProcessIdentity.Rejected();
                gids = ParseIds(row[(colon + 1)..]);
            }
        }
        return (uids ?? throw LinuxProcessIdentity.Rejected(), gids ?? throw LinuxProcessIdentity.Rejected());
    }

    /// <summary>Decodes field 22 after the final ')' without interpreting or returning comm text.</summary>
    /// <remarks>Requires the complete 52-field Linux stat shape; signed unrelated fields remain valid.</remarks>
    internal static LinuxProcessStat ParseStat(ReadOnlySpan<byte> bytes)
    {
        var text = Decode(bytes, MaximumStatBytes);
        var firstSpace = text.IndexOf(' ');
        var closing = text.LastIndexOf(')');
        if (firstSpace < 1 || firstSpace + 1 >= text.Length || text[firstSpace + 1] != '('
            || closing <= firstSpace + 1 || closing + 2 >= text.Length || text[closing + 1] != ' ')
            throw LinuxProcessIdentity.Rejected();
        var pid = Unsigned(text[..firstSpace]);
        if (pid is 0 or > int.MaxValue) throw LinuxProcessIdentity.Rejected();
        var fields = text[(closing + 2)..^1].Split(' ');
        if (fields.Length != 50 || fields[0].Length != 1 || !"RSDZTtXxKWPI".Contains(fields[0][0]))
            throw LinuxProcessIdentity.Rejected();
        for (var i = 1; i < fields.Length; i++)
        {
            var value = fields[i];
            if (value.StartsWith('-'))
            {
                if (!Digits(value.AsSpan(1)) || !long.TryParse(value, NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out _)) throw LinuxProcessIdentity.Rejected();
            }
            else if (!Digits(value) || !ulong.TryParse(value, NumberStyles.None,
                CultureInfo.InvariantCulture, out _)) throw LinuxProcessIdentity.Rejected();
        }
        if (!ulong.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out var start) || start == 0)
            throw LinuxProcessIdentity.Rejected();
        return new(pid, start, fields[0][0]);
    }

    /// <summary>Decodes exactly one unified 0:: row, rejecting hybrid, extra and noncanonical paths.</summary>
    internal static string ParseCgroup(ReadOnlySpan<byte> bytes)
    {
        var text = Decode(bytes, MaximumCgroupBytes);
        if (!text.StartsWith("0::/", StringComparison.Ordinal) || text[..^1].Contains('\n'))
            throw LinuxProcessIdentity.Rejected();
        var path = text[3..^1];
        if (path.Any(static c => char.IsControl(c) || c is '\\' or ':')
            || path[1..].Split('/').Any(static part => part is "" or "." or ".."))
            throw LinuxProcessIdentity.Rejected();
        return path;
    }

    /// <summary>Checks selected tuple/unit data without constructing a live object.</summary>
    internal static void RequireSelection(uint pid, uint uid, uint gid, LinuxUnitName unit,
        LinuxProcessSamplingRole role)
    {
        var label = role switch
        {
            LinuxProcessSamplingRole.Worker => "worker",
            LinuxProcessSamplingRole.Owner => "owner",
            _ => throw LinuxProcessIdentity.Rejected(),
        };
        if (unit is null || pid is 0 or > int.MaxValue || uid == uint.MaxValue || gid == uint.MaxValue
            || !unit.Value.StartsWith($"appsurface-evidence-{label}-", StringComparison.Ordinal)
            || (role == LinuxProcessSamplingRole.Owner ? uid != 0 || gid != 0 : uid == 0 || gid == 0))
            throw LinuxProcessIdentity.Rejected();
    }

    /// <summary>Checks all four IDs, live state, PID and exact generated unified cgroup.</summary>
    internal static void RequireExpected(LinuxProcessSample sample, uint pid, uint uid, uint gid,
        LinuxUnitName unit, LinuxProcessSamplingRole role)
    {
        var stage = LinuxControlFailureStage.Unknown;
        RequireExpected(sample, pid, uid, gid, unit, role, ref stage);
    }

    /// <summary>Runs the same expected-sample guards while recording their closed checkpoint.</summary>
    /// <remarks>Detached sample data and checkpoints cannot create a retained process identity.</remarks>
    internal static void RequireExpected(LinuxProcessSample sample, uint pid, uint uid, uint gid,
        LinuxUnitName unit, LinuxProcessSamplingRole role, ref LinuxControlFailureStage stage)
    {
        stage = LinuxControlFailureStage.ProcessSelection;
        RequireSelection(pid, uid, gid, unit, role);
        stage = LinuxControlFailureStage.ProcessExpectedSample;
        if (sample is null) throw LinuxProcessIdentity.Rejected();
        stage = LinuxControlFailureStage.ProcessExpectedPid;
        if (sample.Stat.Pid != pid) throw LinuxProcessIdentity.Rejected();
        stage = LinuxControlFailureStage.ProcessExpectedStartTime;
        if (sample.Stat.StartTimeTicks == 0) throw LinuxProcessIdentity.Rejected();
        stage = LinuxControlFailureStage.ProcessExpectedLiveState;
        if (!"RSDTtKWPI".Contains(sample.Stat.State)) throw LinuxProcessIdentity.Rejected();
        stage = LinuxControlFailureStage.ProcessExpectedUid;
        if (!sample.Uids.AllEqual(uid)) throw LinuxProcessIdentity.Rejected();
        stage = LinuxControlFailureStage.ProcessExpectedGid;
        if (!sample.Gids.AllEqual(gid)) throw LinuxProcessIdentity.Rejected();
        stage = LinuxControlFailureStage.ProcessExpectedCgroup;
        if (sample.ControlGroup != "/system.slice/" + unit.Value) throw LinuxProcessIdentity.Rejected();
    }

    /// <summary>Requires identity continuity, allowing ordinary running/sleeping state transitions only.</summary>
    internal static void RequireSameIdentity(LinuxProcessSample before, LinuxProcessSample after)
    {
        if (before.Stat.Pid != after.Stat.Pid || before.Stat.StartTimeTicks != after.Stat.StartTimeTicks
            || before.Uids != after.Uids || before.Gids != after.Gids || before.ControlGroup != after.ControlGroup)
            throw LinuxProcessIdentity.Rejected();
    }

    /// <summary>Checks detached fstatfs result/type data with the original proc filesystem predicates.</summary>
    /// <remarks>No descriptor, live process or authority is constructed from these values.</remarks>
    internal static void RequireProcFileSystem(int result, long type, ref LinuxControlFailureStage stage)
    {
        stage = LinuxControlFailureStage.ProcessFileSystemInspect;
        if (result != 0) throw LinuxProcessIdentity.Rejected();
        stage = LinuxControlFailureStage.ProcessFileSystemType;
        if (type != 0x9fa0) throw LinuxProcessIdentity.Rejected();
    }

    /// <summary>Checks detached statx inode/mode fields for the retained PID directory.</summary>
    /// <remarks>The native caller still uses the unchanged StatFd required-mask check.</remarks>
    internal static void RequireProcessDirectory(ulong inode, ushort mode, ref LinuxControlFailureStage stage)
    {
        stage = LinuxControlFailureStage.ProcessDirectoryInode;
        if (inode == 0) throw LinuxProcessIdentity.Rejected();
        stage = LinuxControlFailureStage.ProcessDirectoryType;
        if ((mode & 0xf000) != 0x4000) throw LinuxProcessIdentity.Rejected();
    }

    /// <summary>Reports the first unequal field and retains the original complete metadata equality guard.</summary>
    /// <remarks>Both records are detached data; no sampled values appear in the diagnostic.</remarks>
    internal static void RequireRetainedProcessMetadata(LinuxProcessIdentity.ProcNodeMetadata actual,
        LinuxProcessIdentity.ProcNodeMetadata expected, ref LinuxControlFailureStage stage)
    {
        stage = actual.Major != expected.Major ? LinuxControlFailureStage.ProcessRetainedProcessDeviceMajor
            : actual.Minor != expected.Minor ? LinuxControlFailureStage.ProcessRetainedProcessDeviceMinor
            : actual.Inode != expected.Inode ? LinuxControlFailureStage.ProcessRetainedProcessInode
            : actual.Uid != expected.Uid ? LinuxControlFailureStage.ProcessRetainedProcessUid
            : actual.Gid != expected.Gid ? LinuxControlFailureStage.ProcessRetainedProcessGid
            : actual.Mode != expected.Mode ? LinuxControlFailureStage.ProcessRetainedProcessMode
            : LinuxControlFailureStage.ProcessRetainedProcessMetadata;
        if (actual != expected) throw LinuxProcessIdentity.Rejected();
    }

    private static LinuxProcessIds ParseIds(string row)
    {
        var values = row.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (values.Length != 4) throw LinuxProcessIdentity.Rejected();
        return new(Unsigned(values[0]), Unsigned(values[1]), Unsigned(values[2]), Unsigned(values[3]));
    }

    private static uint Unsigned(string value)
    {
        if (!Digits(value) || !uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            throw LinuxProcessIdentity.Rejected();
        return number;
    }

    private static bool Digits(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty) return false;
        foreach (var character in value) if (character is < '0' or > '9') return false;
        return true;
    }

    private static string Decode(ReadOnlySpan<byte> bytes, int maximum)
    {
        if (bytes.IsEmpty || bytes.Length > maximum || bytes[^1] != (byte)'\n')
            throw LinuxProcessIdentity.Rejected();
        try
        {
            var text = Utf8.GetString(bytes);
            if (text.Contains('\0') || text.Contains('\r')) throw LinuxProcessIdentity.Rejected();
            return text;
        }
        catch (DecoderFallbackException) { throw LinuxProcessIdentity.Rejected(); }
    }
}

/// <summary>Retained real proc handles for one root-selected live process; not admission or group-exit proof.</summary>
/// <remarks>
/// Only root Linux x64 factories construct this object. Proc virtual-file length/timestamps are not regular-file
/// snapshot bounds: reads count actual bytes and repeated kernel facts. The owner supplies its original deadline
/// token; cancellation is checked between synchronous bounded reads, not a promise to preempt a kernel syscall.
/// Readers and disposal are serialized. A failed/cancelled recheck permanently rejects reuse. Cached facts are
/// sampled data only; call Recheck immediately before identity-sensitive operations. Cgroup emptiness, retained
/// cgroup ownership, owner activation and protected bootstrap are separate prerequisites.
/// </remarks>
internal sealed class LinuxProcessIdentity : IDisposable
{
    private const ulong DirectoryFlags = 0x10000 | 0x80000;
    private const ulong ReadFlags = 0x80000 | 0x800;
    private const ulong NamedFlags = 0x200000 | 0x80000;
    private const ulong ChildResolution = 0x01 | 0x02 | 0x04 | 0x08;
    private readonly object _sync = new();
    private readonly SafeFileHandle[] _handles;
    private readonly LinuxProcessSample _initial;
    private readonly ProcNodeMetadata[] _metadata;
    private bool _closed;
    private bool _rejected;
    private readonly LinuxControlFailureLatch _failures = new();

    private LinuxProcessIdentity(SafeFileHandle[] handles, ProcNodeMetadata[] metadata, LinuxProcessSample initial,
        uint uid, uint gid, LinuxUnitName unit, LinuxProcessSamplingRole role)
    {
        _handles = handles; _metadata = metadata; _initial = initial;
        Uid = uid; Gid = gid; Unit = unit; Role = role;
    }

    /// <summary>Gets the originally selected PID; this value alone does not prove it remains live.</summary>
    internal uint Pid => _initial.Stat.Pid;
    /// <summary>Gets the independently selected complete UID tuple value.</summary>
    internal uint Uid { get; }
    /// <summary>Gets the independently selected complete GID tuple value.</summary>
    internal uint Gid { get; }
    /// <summary>Gets the initial kernel start-time ticks used to reject PID reuse.</summary>
    internal ulong StartTimeTicks => _initial.Stat.StartTimeTicks;
    /// <summary>Gets the root-selected generated unit name.</summary>
    internal LinuxUnitName Unit { get; }
    /// <summary>Gets the closed selected sampling role.</summary>
    internal LinuxProcessSamplingRole Role { get; }
    /// <summary>Gets the initial unified group path; it is not proof that that group is empty.</summary>
    internal string ControlGroup => _initial.ControlGroup;
    /// <summary>Gets detached initial data; it cannot be converted back into this live object.</summary>
    internal LinuxProcessSample SampledFacts => _initial;
    /// <summary>Gets first closed recheck-fault data; null establishes no successful inspection.</summary>
    /// <remarks>Caller cancellation remains unlatched; no exception or sampled kernel tuple is retained.</remarks>
    internal LinuxControlFailure? FirstFailure => _failures.First;

    /// <summary>Captures a generated nonroot worker, or explicitly the generated owner with UID/GID zero.</summary>
    /// <remarks>The closed generated unit role selects the tuple policy; zero IDs never select a worker.</remarks>
    internal static LinuxProcessIdentity Capture(uint pid, uint uid, uint gid, LinuxUnitName unit,
        CancellationToken token) => CaptureCore(pid, uid, gid, unit,
            unit is not null && unit.Value.StartsWith("appsurface-evidence-owner-", StringComparison.Ordinal)
                ? LinuxProcessSamplingRole.Owner : LinuxProcessSamplingRole.Worker, token);

    /// <summary>Explicitly captures a root generated owner, never a root worker.</summary>
    internal static LinuxProcessIdentity CaptureOwner(uint pid, LinuxUnitName unit, CancellationToken token) =>
        CaptureCore(pid, 0, 0, unit, LinuxProcessSamplingRole.Owner, token);

    private static LinuxProcessIdentity CaptureCore(uint pid, uint uid, uint gid, LinuxUnitName unit,
        LinuxProcessSamplingRole role, CancellationToken token)
    {
        var handles = new List<SafeFileHandle>();
        try
        {
            token.ThrowIfCancellationRequested();
            LinuxProcessData.RequireSelection(pid, uid, gid, unit, role);
            if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64
                || RuntimeInformation.OSArchitecture != Architecture.X64 || GetUid() != 0 || GetEffectiveUid() != 0)
                throw Rejected();
            handles.Add(OpenAt2(-100, "/proc", DirectoryFlags, 0, 0x02 | 0x04));
            RequireProc(handles[0]);
            token.ThrowIfCancellationRequested();
            handles.Add(OpenAt2(Fd(handles[0]), pid.ToString(CultureInfo.InvariantCulture),
                DirectoryFlags, 0, ChildResolution));
            foreach (var name in new[] { "status", "stat", "cgroup" })
            {
                token.ThrowIfCancellationRequested();
                handles.Add(OpenAt2(Fd(handles[1]), name, ReadFlags, 0, ChildResolution));
            }
            var retained = handles.ToArray();
            var metadata = retained.Select((handle, index) => Inspect(handle, index < 2)).ToArray();
            var initial = ReadRound(retained, token);
            LinuxProcessData.RequireExpected(initial, pid, uid, gid, unit, role);
            var result = new LinuxProcessIdentity(retained, metadata, initial, uid, gid, unit, role);
            result.Recheck(token);
            handles.Clear(); // Native ownership transfers only after repeated live facts and named bindings.
            return result;
        }
        catch (OperationCanceledException) { Close(handles); throw; }
        catch (Exception error) when (Recoverable(error)) { Close(handles); throw Rejected(); }
    }

    /// <summary>Rereads proc facts and named bindings; absence, mutation or reuse rejects permanently.</summary>
    /// <remarks>
    /// Caller cancellation returns no successful inspection but does not turn the retained kernel
    /// identity into an integrity failure. A later cleanup inspection must repeat every native check.
    /// A native rejection stays latched even when cancellation happens concurrently.
    /// </remarks>
    internal void Recheck(CancellationToken token)
    {
        lock (_sync)
        {
            var stage = LinuxControlFailureStage.ProcessState;
            try
            {
                token.ThrowIfCancellationRequested();
                if (_closed || _rejected) throw Rejected();
                CheckBindings(token, ref stage);
                var before = ReadRound(_handles, token, ref stage);
                LinuxProcessData.RequireExpected(before, Pid, Uid, Gid, Unit, Role, ref stage);
                stage = LinuxControlFailureStage.ProcessInitialContinuity;
                LinuxProcessData.RequireSameIdentity(_initial, before);
                var after = ReadRound(_handles, token, ref stage);
                LinuxProcessData.RequireExpected(after, Pid, Uid, Gid, Unit, Role, ref stage);
                stage = LinuxControlFailureStage.ProcessRepeatedContinuity;
                LinuxProcessData.RequireSameIdentity(before, after);
                CheckBindings(token, ref stage);
                token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (Recoverable(error))
            { _failures.Capture(stage, null, error); _rejected = true; throw Rejected(); }
        }
    }

    /// <summary>Closes every retained descriptor under the reader lock, attempting all closes after failure.</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_closed) return;
            _closed = true;
            if (!Close(_handles)) throw Rejected();
        }
    }

    private void CheckBindings(CancellationToken token, ref LinuxControlFailureStage stage)
    {
        for (var i = 0; i < _handles.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            stage = i switch
            {
                0 => LinuxControlFailureStage.ProcessRetainedProcRoot,
                1 => LinuxControlFailureStage.ProcessRetainedProcess,
                2 => LinuxControlFailureStage.ProcessRetainedStatus,
                3 => LinuxControlFailureStage.ProcessRetainedStat,
                _ => LinuxControlFailureStage.ProcessRetainedCgroup,
            };
            if (i == 1)
            {
                var actual = InspectRetainedProcess(_handles[i], ref stage);
                LinuxProcessData.RequireRetainedProcessMetadata(actual, _metadata[i], ref stage);
            }
            else if (Inspect(_handles[i], i < 2) != _metadata[i]) throw Rejected();
            var parent = i == 0 ? -100 : Fd(_handles[i == 1 ? 0 : 1]);
            var name = i switch { 0 => "/proc", 1 => Pid.ToString(CultureInfo.InvariantCulture),
                2 => "status", 3 => "stat", _ => "cgroup" };
            stage = i switch
            {
                0 => LinuxControlFailureStage.ProcessNamedProcRoot,
                1 => LinuxControlFailureStage.ProcessNamedProcess,
                2 => LinuxControlFailureStage.ProcessNamedStatus,
                3 => LinuxControlFailureStage.ProcessNamedStat,
                _ => LinuxControlFailureStage.ProcessNamedCgroup,
            };
            using var named = OpenAt2(parent, name, NamedFlags, 0, i == 0 ? 0x02 | 0x04 : ChildResolution);
            if (Inspect(named, i < 2) != _metadata[i]) throw Rejected();
            token.ThrowIfCancellationRequested();
        }
    }

    private static LinuxProcessSample ReadRound(SafeFileHandle[] handles, CancellationToken token)
    {
        var stage = LinuxControlFailureStage.Unknown;
        return ReadRound(handles, token, ref stage);
    }

    private static LinuxProcessSample ReadRound(SafeFileHandle[] handles, CancellationToken token,
        ref LinuxControlFailureStage stage)
    {
        stage = LinuxControlFailureStage.ProcessFirstStatRead;
        var firstBytes = ReadBounded(handles[3], LinuxProcessData.MaximumStatBytes, token);
        stage = LinuxControlFailureStage.ProcessFirstStatParse;
        var first = LinuxProcessData.ParseStat(firstBytes);
        stage = LinuxControlFailureStage.ProcessStatusRead;
        var statusBytes = ReadBounded(handles[2], LinuxProcessData.MaximumStatusBytes, token);
        stage = LinuxControlFailureStage.ProcessStatusParse;
        var ids = LinuxProcessData.ParseStatus(statusBytes);
        stage = LinuxControlFailureStage.ProcessCgroupRead;
        var cgroupBytes = ReadBounded(handles[4], LinuxProcessData.MaximumCgroupBytes, token);
        stage = LinuxControlFailureStage.ProcessCgroupParse;
        var group = LinuxProcessData.ParseCgroup(cgroupBytes);
        stage = LinuxControlFailureStage.ProcessLastStatRead;
        var lastBytes = ReadBounded(handles[3], LinuxProcessData.MaximumStatBytes, token);
        stage = LinuxControlFailureStage.ProcessLastStatParse;
        var last = LinuxProcessData.ParseStat(lastBytes);
        stage = LinuxControlFailureStage.ProcessReadContinuity;
        if (first.Pid != last.Pid || first.StartTimeTicks != last.StartTimeTicks) throw Rejected();
        return new(last, ids.Uids, ids.Gids, group);
    }

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

    private static ProcNodeMetadata InspectRetainedProcess(SafeFileHandle handle, ref LinuxControlFailureStage stage)
    {
        stage = LinuxControlFailureStage.ProcessFileSystemInspect;
        var result = FstatFs(handle, out var data);
        LinuxProcessData.RequireProcFileSystem(result, data.Type, ref stage);
        stage = LinuxControlFailureStage.ProcessDirectoryStat;
        var stat = StatFd(handle);
        LinuxProcessData.RequireProcessDirectory(stat.Inode, stat.Mode, ref stage);
        return new(stat.DeviceMajor, stat.DeviceMinor, stat.Inode, stat.Uid, stat.Gid, stat.Mode);
    }

    private static ProcNodeMetadata Inspect(SafeFileHandle handle, bool directory)
    {
        RequireProc(handle);
        var stat = StatFd(handle);
        if (stat.Inode == 0 || (stat.Mode & 0xf000) != (directory ? 0x4000 : 0x8000)) throw Rejected();
        return new(stat.DeviceMajor, stat.DeviceMinor, stat.Inode, stat.Uid, stat.Gid, stat.Mode);
    }

    private static void RequireProc(SafeFileHandle handle)
    {
        if (FstatFs(handle, out var data) != 0 || data.Type != 0x9fa0) throw Rejected();
    }

    private static bool Close(IEnumerable<SafeFileHandle> handles)
    {
        var success = true;
        foreach (var handle in handles.Reverse())
        {
            try { handle.Dispose(); }
            catch (Exception error) when (Recoverable(error)) { success = false; }
        }
        return success;
    }

    private static bool Recoverable(Exception error) =>
        error is not (OutOfMemoryException or StackOverflowException or AccessViolationException);

    /// <summary>Creates a fixed rejection without comm, proc bytes, native details or inner exception.</summary>
    internal static EvidenceAdmissionException Rejected() =>
        new("ASEVD402", "The selected Linux process identity is unavailable or changed.");

    /// <summary>Detached proc node fields; construction grants no descriptor or live process identity.</summary>
    /// <param name="Major">Sampled device major number.</param>
    /// <param name="Minor">Sampled device minor number.</param>
    /// <param name="Inode">Sampled inode number.</param>
    /// <param name="Uid">Sampled owner ID.</param>
    /// <param name="Gid">Sampled group ID.</param>
    /// <param name="Mode">Sampled complete type and permission bits.</param>
    internal readonly record struct ProcNodeMetadata(
        uint Major, uint Minor, ulong Inode, uint Uid, uint Gid, ushort Mode);
    [StructLayout(LayoutKind.Explicit, Size = 120)]
    private struct ProcFileSystem { [FieldOffset(0)] internal long Type; }
    [DllImport("libc", EntryPoint = "fstatfs", SetLastError = true)]
    private static extern int FstatFs(SafeFileHandle handle, out ProcFileSystem value);
    [DllImport("libc", EntryPoint = "getuid")] private static extern uint GetUid();
    [DllImport("libc", EntryPoint = "geteuid")] private static extern uint GetEffectiveUid();
}
