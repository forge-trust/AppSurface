using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Tmds.DBus.Protocol;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Closed systemd recipe for one isolated, root-selected coverage test producer.</summary>
/// <remarks>
/// The producer runs the fixed SDK test command as the dedicated subject account. Its writable roots are
/// the copied source, one prepared results-token directory, and private scratch. The audited runtime is
/// read-only; the original source, original result tree, tool tree, worker control socket and worker output
/// tree are inaccessible. This value is recipe data only and creates no process or completion authority.
/// </remarks>
internal sealed class LinuxProducerUnit
{
    internal const ulong TasksMaximum = 64;
    internal const ulong MemoryMaximumBytes = 1UL << 30;
    internal const int MaximumRuntimeSeconds = 1800;

    private readonly string[] _readOnlyPaths;
    private readonly string[] _readWritePaths;
    private readonly string[] _inaccessiblePaths;
    private readonly string[] _arguments;
    private readonly string _workingDirectory;
    private readonly uint _uid;
    private readonly uint _gid;

    private LinuxProducerUnit(LinuxUnitName unit, LinuxUnitName owner, uint uid, uint gid,
        ulong runtimeMicroseconds, ulong stoppingMicroseconds, string[] readOnlyPaths,
        string[] readWritePaths, string[] inaccessiblePaths, string[] arguments, string workingDirectory)
    {
        Unit = unit;
        Owner = owner;
        _uid = uid;
        _gid = gid;
        RuntimeMicroseconds = runtimeMicroseconds;
        StoppingMicroseconds = stoppingMicroseconds;
        _readOnlyPaths = readOnlyPaths;
        _readWritePaths = readWritePaths;
        _inaccessiblePaths = inaccessiblePaths;
        _arguments = arguments;
        _workingDirectory = workingDirectory;
    }

    internal LinuxUnitName Unit { get; }
    internal LinuxUnitName Owner { get; }
    internal IReadOnlyList<string> Arguments => Array.AsReadOnly((string[])_arguments.Clone());
    internal ulong RuntimeMicroseconds { get; }
    internal ulong StoppingMicroseconds { get; }
    internal SafeHandle StandardOutput { get; private init; } = null!;
    internal SafeHandle StandardError { get; private init; } = null!;

    /// <summary>Returns a fresh systemd property projection for the exact selected recipe.</summary>
    internal IReadOnlyDictionary<string, VariantValue> Properties => new ReadOnlyDictionary<string, VariantValue>(
        new Dictionary<string, VariantValue>(StringComparer.Ordinal)
        {
            ["Type"] = "exec",
            ["WorkingDirectory"] = _workingDirectory,
            ["User"] = _uid.ToString(CultureInfo.InvariantCulture),
            ["Group"] = _gid.ToString(CultureInfo.InvariantCulture),
            ["SupplementaryGroups"] = VariantValue.Array(Array.Empty<string>()),
            ["NoNewPrivileges"] = true,
            ["CapabilityBoundingSet"] = 0UL,
            ["AmbientCapabilities"] = 0UL,
            ["ProtectSystem"] = "strict",
            ["ProtectHome"] = "yes",
            ["ProtectControlGroups"] = true,
            ["PrivateTmp"] = true,
            ["RestrictSUIDSGID"] = true,
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
            ["ReadWritePaths"] = VariantValue.Array((string[])_readWritePaths.Clone()),
            ["InaccessiblePaths"] = VariantValue.Array((string[])_inaccessiblePaths.Clone()),
            ["StandardInput"] = "null",
            ["PassEnvironment"] = VariantValue.Array(Array.Empty<string>()),
            ["UnsetEnvironment"] = VariantValue.Array(new[]
            {
                "DOTNET_STARTUP_HOOKS", "DOTNET_ADDITIONAL_DEPS", "DOTNET_SHARED_STORE", "DOTNET_ROOT_X64",
                "DOTNET_HOST_PATH", "DOTNET_ROLL_FORWARD", "DOTNET_ROLL_FORWARD_TO_PRERELEASE",
                "DOTNET_MULTILEVEL_LOOKUP", "CORECLR_ENABLE_PROFILING", "CORECLR_PROFILER",
                "CORECLR_PROFILER_PATH", "CORECLR_PROFILER_PATH_64", "COR_ENABLE_PROFILING",
                "COR_PROFILER", "COR_PROFILER_PATH", "COMPlus_ReadyToRun", "COMPlus_ZapDisable"
            }),
            ["Environment"] = VariantValue.Array(BuildEnvironment())
        });

    /// <summary>Builds the closed command from retained request facts and a once-prepared token directory.</summary>
    /// <param name="unit">The generated producer unit for this run.</param>
    /// <param name="owner">The matching generated root owner.</param>
    /// <param name="runtimeHost">The audited full-SDK dotnet host selected by the retained request.</param>
    /// <param name="runtimeRoot">The separate audited SDK/runtime root, readable and never writable.</param>
    /// <param name="toolRoot">The protected worker/report-generator tool root, denied to subject execution.</param>
    /// <param name="originalSubjectRoot">The original read-only descriptor source, denied to the producer.</param>
    /// <param name="originalRawResultsRoot">The worker's original raw-results tree, denied to the producer.</param>
    /// <param name="controlSocket">The worker's private control socket, denied to the producer.</param>
    /// <param name="workerOutputRoot">The worker's protected output tree, denied to the producer.</param>
    /// <param name="subjectRoot">The fresh mapped writable source copy.</param>
    /// <param name="solutionPath">The solution path mapped beneath <paramref name="subjectRoot"/>.</param>
    /// <param name="resultsRoot">The root-owned results parent.</param>
    /// <param name="resultsTokenRoot">The once-prepared subject-owned results token directory.</param>
    /// <param name="scratchRoot">The subject-owned HOME, temporary, CLI and copied NuGet package root.</param>
    /// <param name="packageRoot">The copied audited package cache beneath scratch.</param>
    /// <param name="uid">The independently retained subject UID.</param>
    /// <param name="gid">The independently retained subject GID; no supplementary results group is used.</param>
    /// <param name="remainingAllowance">The original remaining allowance after retaining cleanup reserve.</param>
    /// <param name="stopping">The existing stop reserve; no timer or allowance is renewed here.</param>
    /// <param name="stdout">The retained stdout write handle.</param>
    /// <param name="stderr">The retained stderr write handle.</param>
    internal static LinuxProducerUnit Create(LinuxUnitName unit, LinuxUnitName owner, string runtimeHost,
        string runtimeRoot, string toolRoot, string originalSubjectRoot, string originalRawResultsRoot,
        string controlSocket, string workerOutputRoot, string subjectRoot, string solutionPath,
        string resultsRoot, string resultsTokenRoot, string scratchRoot, string packageRoot,
        uint uid, uint gid, TimeSpan remainingAllowance, TimeSpan stopping,
        SafeHandle stdout, SafeHandle stderr)
    {
        const string producerPrefix = "appsurface-evidence-producer-";
        const string ownerPrefix = "appsurface-evidence-owner-";
        if (unit is null || owner is null || !unit.Value.StartsWith(producerPrefix, StringComparison.Ordinal)
            || !owner.Value.StartsWith(ownerPrefix, StringComparison.Ordinal)
            || unit.Value[producerPrefix.Length..] != owner.Value[ownerPrefix.Length..]
            || uid is 0 or uint.MaxValue || gid is 0 or uint.MaxValue
            || stopping <= TimeSpan.Zero || stopping > TimeSpan.FromSeconds(30)
            || remainingAllowance <= stopping || remainingAllowance > TimeSpan.FromSeconds(MaximumRuntimeSeconds + 30)
            || stdout is null || stdout.IsClosed || stdout.IsInvalid || stderr is null || stderr.IsClosed || stderr.IsInvalid)
            throw LinuxSystemdBackend.InvalidControl();

        var paths = new[] { runtimeHost, runtimeRoot, toolRoot, originalSubjectRoot, originalRawResultsRoot,
            controlSocket, workerOutputRoot, subjectRoot, solutionPath, resultsRoot, resultsTokenRoot,
            scratchRoot, packageRoot };
        foreach (var path in paths) ValidatePath(path, path == controlSocket ? 100 : 4096);
        if (!solutionPath.StartsWith(subjectRoot + "/", StringComparison.Ordinal)
            || !resultsTokenRoot.StartsWith(resultsRoot + "/", StringComparison.Ordinal)
            || !packageRoot.StartsWith(scratchRoot + "/", StringComparison.Ordinal)
            || !runtimeHost.StartsWith(runtimeRoot + "/", StringComparison.Ordinal)
            || Overlaps(runtimeRoot, toolRoot)
            || Overlaps(subjectRoot, originalSubjectRoot) || Overlaps(subjectRoot, originalRawResultsRoot)
            || Overlaps(subjectRoot, toolRoot) || Overlaps(subjectRoot, runtimeRoot)
            || Overlaps(resultsRoot, subjectRoot) || Overlaps(resultsRoot, scratchRoot)
            || Overlaps(scratchRoot, toolRoot) || Overlaps(scratchRoot, runtimeRoot)
            || Overlaps(resultsRoot, originalRawResultsRoot)
            || Overlaps(scratchRoot, originalRawResultsRoot))
            throw LinuxSystemdBackend.InvalidControl();

        var stopUs = Microseconds(stopping);
        var runtimeUs = Microseconds(remainingAllowance - stopping);
        if (runtimeUs == 0 || runtimeUs > (ulong)MaximumRuntimeSeconds * 1_000_000UL)
            throw LinuxSystemdBackend.InvalidControl();

        var tmp = scratchRoot + "/tmp";
        var cliHome = scratchRoot + "/dotnet-home";
        var args = new[]
        {
            LinuxOwnerFacts.BootstrapExecutable, "-i", "PATH=/usr/bin:/bin", "HOME=" + scratchRoot,
            "TMPDIR=" + tmp, "TMP=" + tmp, "TEMP=" + tmp, "NUGET_PACKAGES=" + packageRoot,
            "DOTNET_ROOT=" + runtimeRoot, "DOTNET_CLI_HOME=" + cliHome,
            "DOTNET_CLI_TELEMETRY_OPTOUT=1", "LANG=C.UTF-8", "DOTNET_PROCESSOR_COUNT=1",
            "DOTNET_EnableDiagnostics_IPC=0", "DOTNET_EnableDiagnostics_Debugger=0", runtimeHost,
            "test", solutionPath, "--configuration", "Debug", "--collect:XPlat Code Coverage",
            "--results-directory", resultsTokenRoot, "--verbosity", "quiet", "-m:1",
            "-p:UseSharedCompilation=false", "-nodeReuse:false"
        };
        var readOnly = new[] { runtimeRoot };
        var readWrite = new[] { subjectRoot, resultsTokenRoot, scratchRoot };
        var denied = new[] { toolRoot, originalSubjectRoot, originalRawResultsRoot, controlSocket, workerOutputRoot };
        if (HasOverlaps(readWrite, readOnly) || HasOverlaps(readWrite, denied)
            || HasOverlaps(readOnly, denied) || HasOverlaps(denied, denied, allowSame: true))
            throw LinuxSystemdBackend.InvalidControl();

        return new LinuxProducerUnit(unit, owner, uid, gid, runtimeUs, stopUs, readOnly, readWrite, denied, args, subjectRoot)
        { StandardOutput = stdout, StandardError = stderr };
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

    /// <summary>Allows only same-execution exit transitions to remain pending under the original monitor deadline.</summary>
    /// <remarks>A transitional sample is never a finished or stopped receipt. Foreign PIDs and signal exits reject.</remarks>
    internal bool IsNormalExitTransition(LinuxUnitProperties value, uint capturedPid)
    {
        RequireObservedPolicy(value);
        if (capturedPid == 0 || value.ExecMainPid != capturedPid
            || value.MainPid != 0 && value.MainPid != capturedPid || value.ExecMainCode is not (0 or 1)) return false;
        return value.ActiveState == "deactivating" && value.SubState is "stop" or "stop-sigterm" or "stop-post" or "final-sigterm"
            || value.ActiveState == "active" && value.SubState == "running" && value.MainPid == 0
            || value.ActiveState is "inactive" or "failed" && value.SubState is "dead" or "failed" && value.ExecMainCode == 0;
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

    private static string[] BuildEnvironment() => new[]
    {
        "PATH=/usr/bin:/bin", "LANG=C.UTF-8", "DOTNET_CLI_TELEMETRY_OPTOUT=1"
    };

    private static ulong Microseconds(TimeSpan value) => value.Ticks < 10 ? 0UL : (ulong)(value.Ticks / 10);

    private static void ValidatePath(string path, int maximumUtf8Bytes)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || path[0] != '/' || path.Length > maximumUtf8Bytes
                || path.Any(char.IsControl) || path.Contains('%') || path.Contains('$')
                || new UTF8Encoding(false, true).GetByteCount(path) > maximumUtf8Bytes
                || path.Split('/').Skip(1).Any(static part => part.Length == 0 || part is "." or ".."))
                throw LinuxSystemdBackend.InvalidControl();
        }
        catch (EncoderFallbackException) { throw LinuxSystemdBackend.InvalidControl(); }
    }

    private static bool Overlaps(string first, string second) => first == second
        || first.StartsWith(second + "/", StringComparison.Ordinal)
        || second.StartsWith(first + "/", StringComparison.Ordinal);

    private static bool HasOverlaps(string[] paths, string[] other, bool allowSame = false)
    {
        for (var i = 0; i < paths.Length; i++)
        for (var j = 0; j < other.Length; j++)
        {
            if (allowSame && i == j && paths[i] == other[j]) continue;
            if (Overlaps(paths[i], other[j])) return true;
        }
        return false;
    }
}
