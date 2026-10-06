using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Tmds.DBus.Protocol;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Fixed worker unit data selected by the protected run owner.</summary>
/// <remarks>
/// This recipe issues no admission and performs no filesystem or kernel identity validation.
/// Before starting, the owner must authenticate its own unit, pin the deployment and paths, verify both
/// handles are retained write ends, and reserve pending workload ownership in
/// <see cref="SupervisionWorkRegistry"/>. See the
/// <see href="../../docs/plans/issue-779-csharp-supervision-migration.md">checkpoint plan</see>
/// for native prerequisites.
/// BindsTo can activate its dependency: the owner must have its single-use systemd activation guard armed.
/// Fixed env -i argv clears inherited variables before dotnet executes. Environment metadata alone cannot
/// establish pre-CLR safety; the trusted OS bootstrap must pin env's loader and the actual native runtime.
/// Type=exec initially confirms env execution, not managed worker readiness.
/// </remarks>
internal sealed class LinuxWorkerUnit
{
    /// <summary>Gets the fixed simultaneous task ceiling, including threads.</summary>
    internal const ulong TasksMaximum = 64;

    /// <summary>Gets the fixed one-GiB memory ceiling.</summary>
    internal const ulong MemoryMaximumBytes = 1UL << 30;

    private readonly IReadOnlyList<string> _arguments;
    private readonly string[] _readOnlyPaths;
    private readonly string _outputParent;
    private readonly string _deniedSubjectResults;
    private readonly uint _uid;
    private readonly uint _gid;

    private LinuxWorkerUnit(LinuxUnitName worker, LinuxUnitName owner, string runtimeHost, string managedEntry,
        uint uid, uint gid, string socket, string toolRoot, string subjectRoot, string outputParent,
        string deniedSubjectResults, ulong runtime, ulong stopping, SafeHandle stdout, SafeHandle stderr)
    {
        Unit = worker;
        Owner = owner;
        _uid = uid;
        _gid = gid;
        _arguments = LinuxOwnerFacts.ManagedArguments(EvidenceProcessRole.Worker, runtimeHost, managedEntry, socket);
        _readOnlyPaths = new[] { toolRoot, subjectRoot, runtimeHost, runtimeHost[..runtimeHost.LastIndexOf('/')] }
            .Distinct(StringComparer.Ordinal).ToArray();
        _outputParent = outputParent;
        _deniedSubjectResults = deniedSubjectResults;
        RuntimeMicroseconds = runtime;
        StoppingMicroseconds = stopping;
        StandardOutput = stdout;
        StandardError = stderr;
    }

    /// <summary>Gets the generated worker name; this is not a unit ownership receipt.</summary>
    internal LinuxUnitName Unit { get; }

    /// <summary>Gets the exact generated owner dependency for the same run.</summary>
    internal LinuxUnitName Owner { get; }

    /// <summary>Gets immutable argv including the executable as argv[0], without caller-supplied extra tokens.</summary>
    internal IReadOnlyList<string> Arguments => _arguments;

    /// <summary>Gets remaining job allowance minus the stop reserve, rounded down to whole microseconds.</summary>
    internal ulong RuntimeMicroseconds { get; }

    /// <summary>Gets the stop reserve rounded down to whole microseconds; runtime plus stop fits the job allowance.</summary>
    internal ulong StoppingMicroseconds { get; }

    /// <summary>Gets the borrowed stdout handle; the recipe never closes the caller's handle.</summary>
    internal SafeHandle StandardOutput { get; }

    /// <summary>Gets the borrowed stderr handle; the recipe never closes the caller's handle.</summary>
    internal SafeHandle StandardError { get; }

    /// <summary>Gets a fresh, closed property projection; arrays cannot mutate the stored recipe.</summary>
    /// <remarks>
    /// ExecStart and the two Unix-FD properties are serialized separately by the backend.
    /// ProtectHome is D-Bus string "yes", rather than a Boolean variant; see the
    /// <see href="https://github.com/systemd/systemd/blob/v255/src/core/dbus-execute.c">v255 execution property contract</see>.
    /// RemainAfterExit retains worker inspection metadata; it cannot establish physical exit.
    /// AddRef retains that metadata on the original authenticated starting connection through both stops and joins.
    /// </remarks>
    internal IReadOnlyDictionary<string, VariantValue> Properties => new ReadOnlyDictionary<string, VariantValue>(
        new Dictionary<string, VariantValue>(StringComparer.Ordinal)
        {
            ["Type"] = "exec",
            ["User"] = _uid.ToString(CultureInfo.InvariantCulture),
            ["Group"] = _gid.ToString(CultureInfo.InvariantCulture),
            ["SupplementaryGroups"] = VariantValue.Array(Array.Empty<string>()),
            ["NoNewPrivileges"] = true,
            ["CapabilityBoundingSet"] = 0UL,
            ["AmbientCapabilities"] = 0UL,
            ["ProtectSystem"] = "strict",
            ["ProtectHome"] = "yes",
            ["PrivateTmp"] = true,
            ["ProtectControlGroups"] = true,
            ["RestrictSUIDSGID"] = false,
            ["TasksMax"] = TasksMaximum,
            ["MemoryMax"] = MemoryMaximumBytes,
            ["KillMode"] = "control-group",
            ["Restart"] = "no",
            ["RemainAfterExit"] = true,
            ["AddRef"] = true,
            ["RuntimeMaxUSec"] = RuntimeMicroseconds,
            ["TimeoutStopUSec"] = StoppingMicroseconds,
            ["LimitCORE"] = 0UL,
            ["After"] = VariantValue.Array(new[] { Owner.Value }),
            ["BindsTo"] = VariantValue.Array(new[] { Owner.Value }),
            ["ReadOnlyPaths"] = VariantValue.Array((string[])_readOnlyPaths.Clone()),
            ["ReadWritePaths"] = VariantValue.Array(new[] { _outputParent }),
            ["InaccessiblePaths"] = VariantValue.Array(new[] { _deniedSubjectResults }),
            ["StandardInput"] = "null",
            ["PassEnvironment"] = VariantValue.Array(Array.Empty<string>()),
            ["UnsetEnvironment"] = VariantValue.Array(new[]
            {
                "DOTNET_STARTUP_HOOKS", "DOTNET_ADDITIONAL_DEPS", "DOTNET_SHARED_STORE", "DOTNET_ROOT",
                "DOTNET_ROOT_X64", "DOTNET_HOST_PATH", "DOTNET_ROLL_FORWARD", "DOTNET_ROLL_FORWARD_TO_PRERELEASE",
                "DOTNET_MULTILEVEL_LOOKUP", "CORECLR_ENABLE_PROFILING", "CORECLR_PROFILER", "CORECLR_PROFILER_PATH",
                "CORECLR_PROFILER_PATH_64", "COR_ENABLE_PROFILING", "COR_PROFILER", "COR_PROFILER_PATH",
                "COMPlus_ReadyToRun", "COMPlus_ZapDisable"
            }),
            ["Environment"] = VariantValue.Array(new[]
            {
                "PATH=/usr/bin:/bin", "HOME=/nonexistent", "LANG=C.UTF-8",
                "DOTNET_CLI_TELEMETRY_OPTOUT=1", "DOTNET_CLI_HOME=/tmp"
            })
        });

    /// <summary>Creates only the fixed worker recipe, rejecting malformed or conflicting data.</summary>
    /// <param name="worker">Generated worker unit for this run.</param>
    /// <param name="owner">Generated owner unit for the same run, with protected activation already validated separately.</param>
    /// <param name="runtimeHost">Absolute, independently pinned runtime executable.</param>
    /// <param name="managedEntry">Absolute, independently pinned entry beneath toolRoot.</param>
    /// <param name="workerUid">Non-root numeric worker UID; the owner creates and validates the account separately.</param>
    /// <param name="workerGid">Non-root numeric worker GID.</param>
    /// <param name="controlSocket">Absolute normalized socket path, at most 100 UTF-8 bytes.</param>
    /// <param name="toolRoot">Read-only protected tool directory.</param>
    /// <param name="preparedSubjectRoot">Read-only prepared subject directory.</param>
    /// <param name="outputParent">The sole explicit writable host directory.</param>
    /// <param name="deniedSubjectResults">Exact raw subject-results directory to hide.</param>
    /// <param name="remainingJobAllowance">Actual positive original job-deadline remainder, at most one hour; no renewed timer is created here.</param>
    /// <param name="stopping">Positive stop reserve at most 30 seconds and strictly smaller than remainingJobAllowance.</param>
    /// <param name="stdout">Caller-retained valid write-end SafeHandle; metadata validation cannot prove descriptor access mode.</param>
    /// <param name="stderr">Caller-retained valid write-end SafeHandle.</param>
    /// <returns>Immutable policy data without a lease, physical-exit fact, or admission.</returns>
    internal static LinuxWorkerUnit Create(LinuxUnitName worker, LinuxUnitName owner, string runtimeHost,
        string managedEntry, uint workerUid, uint workerGid, string controlSocket, string toolRoot,
        string preparedSubjectRoot, string outputParent, string deniedSubjectResults,
        TimeSpan remainingJobAllowance, TimeSpan stopping, SafeHandle stdout, SafeHandle stderr)
    {
        const string workerPrefix = "appsurface-evidence-worker-";
        const string ownerPrefix = "appsurface-evidence-owner-";
        if (worker is null || owner is null
            || !worker.Value.StartsWith(workerPrefix, StringComparison.Ordinal)
            || !owner.Value.StartsWith(ownerPrefix, StringComparison.Ordinal)
            || worker.Value[workerPrefix.Length..] != owner.Value[ownerPrefix.Length..]
            || workerUid is 0 or uint.MaxValue || workerGid is 0 or uint.MaxValue)
            throw LinuxSystemdBackend.InvalidControl();

        foreach (var path in new[] { runtimeHost, managedEntry, toolRoot, preparedSubjectRoot, outputParent, deniedSubjectResults })
            ValidatePath(path, 4096);
        ValidatePath(controlSocket, 100);
        var runtimeParent = runtimeHost[..runtimeHost.LastIndexOf('/')];
        if (runtimeParent.Length == 0 || !managedEntry.StartsWith(toolRoot + "/", StringComparison.Ordinal)
            || Overlaps(outputParent, controlSocket)
            || new[] { toolRoot, preparedSubjectRoot, runtimeParent, deniedSubjectResults }
                .Any(path => Overlaps(outputParent, path)))
            throw LinuxSystemdBackend.InvalidControl();

        if (remainingJobAllowance > TimeSpan.FromHours(1) || stopping > TimeSpan.FromSeconds(30))
            throw LinuxSystemdBackend.InvalidControl();
        var stop = Microseconds(stopping);
        if (remainingJobAllowance <= stopping) throw LinuxSystemdBackend.InvalidControl();
        var runtime = Microseconds(remainingJobAllowance - stopping);
        if (stdout is null || stderr is null
            || stdout.IsClosed || stdout.IsInvalid || stderr.IsClosed || stderr.IsInvalid)
            throw LinuxSystemdBackend.InvalidControl();
        return new(worker, owner, runtimeHost, managedEntry, workerUid, workerGid, controlSocket,
            toolRoot, preparedSubjectRoot, outputParent, deniedSubjectResults, runtime, stop, stdout, stderr);
    }

    /// <summary>Matches running-main metadata against this exact recipe; it issues no process or admission authority.</summary>
    /// <param name="value">Actual typed systemd sample, or detached data when testing this predicate.</param>
    /// <returns>True only for active/running, exact cgroup, matching positive main PIDs and no recorded exit.</returns>
    /// <remarks>
    /// Wrong unit or sampled policy rejects with fixed ASEVD410. Pending/transitional metadata returns false.
    /// The holder must separately acquire/recheck kernel process identity before authenticating its worker.
    /// </remarks>
    internal bool HasRunningMain(LinuxUnitProperties value)
    {
        RequireObservedPolicy(value);
        return value.ActiveState == "active" && value.SubState == "running"
            && value.ControlGroup == "/system.slice/" + Unit.Value
            && value.MainPid > 0 && value.MainPid == value.ExecMainPid
            && value.ExecMainCode == 0 && value.ExecMainStatus == 0;
    }

    /// <summary>Matches a normal main-process exit record, including nonzero failure status, as detached data only.</summary>
    /// <param name="value">Actual typed systemd sample; MainPid zero alone never establishes completion.</param>
    /// <returns>True for a recorded CLD_EXITED main with active/exited, inactive/dead or failed/failed state.</returns>
    /// <remarks>
    /// ExecMainPid must remain positive, MainPid must be zero and ExecMainCode must be 1. ExecMainStatus is the
    /// actual normal exit byte (0 through 255); nonzero is a failed workload outcome, not successful execution.
    /// An empty pruned cgroup is permitted as metadata only. The holder must still stop/join the retained unit,
    /// check the actual kernel group and join both output pumps. Loaded/inactive/dead with zero code/status
    /// remains pending, and a signal-exit record is not a normal main-exit match.
    /// </remarks>
    internal bool HasFinished(LinuxUnitProperties value)
    {
        RequireObservedPolicy(value);
        if (value.MainPid != 0 || value.ExecMainPid == 0 || value.ExecMainCode != 1) return false;
        return value.ActiveState == "active" && value.SubState == "exited"
            || value.ActiveState == "inactive" && value.SubState == "dead"
            || value.ActiveState == "failed" && value.SubState == "failed";
    }

    /// <summary>Matches stopped-unit metadata after the original stop sequence; this is not physical settlement.</summary>
    /// <param name="value">Actual retained unit sample, or detached metadata when testing this predicate.</param>
    /// <returns>True only for inactive/dead or failed/failed, zero main PID and a recorded positive execution PID.</returns>
    /// <remarks>
    /// Policy validation is identical to the other predicates. Normal termination uses code 1/status 0 through
    /// 255; killed or dumped termination uses code 2 or 3/signal 1 through 64. Invalid termination status rejects
    /// with fixed ASEVD410. Active/exited and uninitialized zero-code records never match. An empty pruned cgroup
    /// is allowed as data only. The holder must separately join actual start/monitor tasks, perform the original
    /// stop sequence, check the kernel group and join both pumps before issuing any physical-exit fact.
    /// </remarks>
    internal bool HasStopped(LinuxUnitProperties value)
    {
        RequireObservedPolicy(value);
        var signalled = value.ExecMainCode is 2 or 3;
        if (signalled && value.ExecMainStatus is < 1 or > 64) throw LinuxSystemdBackend.InvalidControl();
        return (value.ActiveState == "inactive" && value.SubState == "dead"
            || value.ActiveState == "failed" && value.SubState == "failed")
            && value.MainPid == 0 && value.ExecMainPid > 0
            && (value.ExecMainCode == 1 || signalled);
    }

    private void RequireObservedPolicy(LinuxUnitProperties value)
    {
        if (value is null || value.Id != Unit.Value || value.LoadState != "loaded"
            || value.User != _uid.ToString(CultureInfo.InvariantCulture)
            || value.Group != _gid.ToString(CultureInfo.InvariantCulture)
            || value.Type != "exec" || value.KillMode != "control-group" || !value.RemainAfterExit
            || value.RuntimeMaxMicroseconds != RuntimeMicroseconds
            || value.TimeoutStopMicroseconds != StoppingMicroseconds
            || value.ControlGroup != string.Empty && value.ControlGroup != "/system.slice/" + Unit.Value
            || value.MainPid > int.MaxValue || value.ExecMainPid > int.MaxValue
            || value.ExecMainStatus < 0 || value.ExecMainCode == 1 && value.ExecMainStatus > byte.MaxValue)
            throw LinuxSystemdBackend.InvalidControl();
    }

    private static ulong Microseconds(TimeSpan value)
    {
        if (value.Ticks < 10) throw LinuxSystemdBackend.InvalidControl();
        return (ulong)(value.Ticks / 10);
    }

    private static void ValidatePath(string path, int maximumBytes)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || path.Length > maximumBytes || path[0] != '/' || path.Any(char.IsControl)
                || path.Contains('%') || path.Contains('$')
                || new UTF8Encoding(false, true).GetByteCount(path) > maximumBytes
                || path.Split('/').Skip(1).Any(part => part.Length == 0 || part is "." or ".."))
                throw LinuxSystemdBackend.InvalidControl();
        }
        catch (EncoderFallbackException) { throw LinuxSystemdBackend.InvalidControl(); }
    }

    private static bool Overlaps(string first, string second) => first == second
        || first.StartsWith(second + "/", StringComparison.Ordinal)
        || second.StartsWith(first + "/", StringComparison.Ordinal);
}
