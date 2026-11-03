using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Tmds.DBus.Protocol;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Copied finite unit limits; constructing this data never selects an application.</summary>
internal sealed record LinuxApplicationLimits(long ScratchBytes, long MemoryBytes, int MaximumTasks,
    long MaximumOutputBytes, int StartSeconds, int StoppingSeconds);

/// <summary>Closed AppHost/DCP recipe selected from an immutable registration, never caller argv.</summary>
/// <remarks>Private networking, empty capabilities, no supplementary groups, strict read-only system,
/// bounded task/memory/scratch/output and owner binding apply to all application descendants.</remarks>
internal sealed class LinuxApplicationUnit
{
    private readonly Dictionary<string, VariantValue> _properties;
    private readonly string[] _arguments;
    private readonly uint _uid;
    private readonly uint _gid;
    private LinuxApplicationUnit(LinuxUnitName unit, uint uid, uint gid, string[] args,
        Dictionary<string, VariantValue> properties, SafeHandle stdout, SafeHandle stderr)
    { Unit = unit; _uid = uid; _gid = gid; _arguments = args; _properties = properties; StandardOutput = stdout; StandardError = stderr; }
    /// <summary>Gets the generated unit name, as comparison data.</summary>
    internal LinuxUnitName Unit { get; }
    /// <summary>Gets a detached fixed command snapshot.</summary>
    internal IReadOnlyList<string> Arguments => Array.AsReadOnly((string[])_arguments.Clone());
    /// <summary>Gets a fresh property projection; no mutable source map escapes.</summary>
    internal IReadOnlyDictionary<string, VariantValue> Properties => new ReadOnlyDictionary<string, VariantValue>(new Dictionary<string, VariantValue>(_properties, StringComparer.Ordinal));
    /// <summary>Gets the retained actual stdout write handle.</summary>
    internal SafeHandle StandardOutput { get; }
    /// <summary>Gets the retained actual stderr write handle.</summary>
    internal SafeHandle StandardError { get; }

    /// <summary>Builds one non-injectable recipe from independently selected paths and capabilities.</summary>
    internal static LinuxApplicationUnit Create(LinuxUnitName unit, LinuxUnitName owner,
        string runtimeHost, string runtimeRoot, string toolRoot, string subjectRoot, string outputRoot,
        string controlRoot, string producerRoot, string bundleRoot, string appHostPath, string scratchRoot,
        string protectedOutputRoot, string inputPath, uint uid, uint gid,
        LinuxApplicationLimits capabilities, TimeSpan remaining,
        SafeHandle stdout, SafeHandle stderr)
    {
        if (unit is null || owner is null || !unit.Value.StartsWith("appsurface-evidence-application-", StringComparison.Ordinal)
            || unit.Value["appsurface-evidence-application-".Length..] != owner.Value["appsurface-evidence-owner-".Length..] || uid is 0 or uint.MaxValue || gid is 0 or uint.MaxValue
            || capabilities is null || capabilities.MaximumTasks is < 1 or > 128
            || capabilities.MemoryBytes is < 1 or > 1L << 30 || capabilities.ScratchBytes is < 1 or > 1L << 30
            || capabilities.MaximumOutputBytes is < 1 or > 1048576 || capabilities.StartSeconds is < 1 or > 120
            || capabilities.StoppingSeconds is < 1 or > 30 || remaining <= TimeSpan.FromSeconds(capabilities.StoppingSeconds)
            || remaining > TimeSpan.FromHours(1) || stdout is null || stderr is null
            || stdout.IsClosed || stdout.IsInvalid || stderr.IsClosed || stderr.IsInvalid) throw Invalid();
        foreach (var path in new[] { runtimeHost, runtimeRoot, toolRoot, subjectRoot, outputRoot, controlRoot,
            producerRoot, bundleRoot, appHostPath, scratchRoot, protectedOutputRoot, inputPath }) ValidatePath(path);
        if (!runtimeHost.StartsWith(runtimeRoot + "/", StringComparison.Ordinal)
            || !appHostPath.StartsWith(bundleRoot + "/", StringComparison.Ordinal)
            || appHostPath[(bundleRoot.Length + 1)..].Contains('/') || !inputPath.StartsWith(bundleRoot + "/", StringComparison.Ordinal)) throw Invalid();
        var denied = new[] { toolRoot, subjectRoot, outputRoot, controlRoot, producerRoot, protectedOutputRoot };
        foreach (var deny in denied)
            if (Overlaps(deny, bundleRoot) || Overlaps(deny, scratchRoot) || Overlaps(deny, runtimeRoot)) throw Invalid();
        if (Overlaps(bundleRoot, scratchRoot) || Overlaps(runtimeRoot, scratchRoot) || Overlaps(runtimeRoot, bundleRoot)) throw Invalid();
        var env = new[] { "PATH=/usr/bin:/bin", "HOME=" + scratchRoot, "TMPDIR=" + scratchRoot,
            "TMP=" + scratchRoot, "TEMP=" + scratchRoot, "DOTNET_CLI_HOME=" + scratchRoot,
            "DOTNET_ROOT=" + runtimeRoot, "DOTNET_PROCESSOR_COUNT=1", "DOTNET_CLI_TELEMETRY_OPTOUT=1",
            "DOTNET_EnableDiagnostics_IPC=0", "DOTNET_EnableDiagnostics_Debugger=0", "LANG=C.UTF-8",
            "ASPIRE__STORE__PATH=" + scratchRoot + "/.aspire-store", "PROOF_PROTECTED_TOOLS=" + toolRoot,
            "PROOF_PROTECTED_OUTPUT=" + protectedOutputRoot, "PROOF_ALLOWED_INPUT=" + inputPath };
        var args = new[] { "/usr/bin/env", "-i" }.Concat(env).Concat(new[] { runtimeHost, appHostPath,
            "--scratch", scratchRoot, "--case", "normal" }).ToArray();
        var properties = new Dictionary<string, VariantValue>(StringComparer.Ordinal)
        {
            ["Type"] = "exec",
            ["WorkingDirectory"] = scratchRoot,
            ["User"] = uid.ToString(CultureInfo.InvariantCulture),
            ["Group"] = gid.ToString(CultureInfo.InvariantCulture),
            ["SupplementaryGroups"] = VariantValue.Array(Array.Empty<string>()),
            ["NoNewPrivileges"] = true,
            ["CapabilityBoundingSet"] = 0UL,
            ["AmbientCapabilities"] = 0UL,
            ["ProtectSystem"] = "strict",
            ["ProtectHome"] = "yes",
            ["ProtectControlGroups"] = true,
            ["PrivateTmp"] = true,
            ["PrivateNetwork"] = true,
            ["RestrictSUIDSGID"] = true,
            ["TasksMax"] = (ulong)capabilities.MaximumTasks,
            ["MemoryMax"] = (ulong)capabilities.MemoryBytes,
            ["KillMode"] = "control-group",
            ["Restart"] = "no",
            ["RemainAfterExit"] = true,
            ["AddRef"] = true,
            ["RuntimeMaxUSec"] = (ulong)((remaining - TimeSpan.FromSeconds(capabilities.StoppingSeconds)).Ticks / 10),
            ["TimeoutStopUSec"] = (ulong)capabilities.StoppingSeconds * 1000000,
            ["LimitCORE"] = 0UL,
            ["After"] = VariantValue.Array(new[] { owner.Value }),
            ["BindsTo"] = VariantValue.Array(new[] { owner.Value }),
            ["ReadOnlyPaths"] = VariantValue.Array(new[] { runtimeRoot, bundleRoot }),
            ["ReadWritePaths"] = VariantValue.Array(new[] { scratchRoot }),
            ["InaccessiblePaths"] = VariantValue.Array(denied),
            ["StandardInput"] = "null",
            ["PassEnvironment"] = VariantValue.Array(Array.Empty<string>()),
            ["Environment"] = VariantValue.Array(env)
        };
        return new(unit, uid, gid, args, properties, stdout, stderr);
    }

    /// <summary>Checks the sampled recipe before considering any running/exit state.</summary>
    internal void RequirePolicy(LinuxUnitProperties value)
    {
        if (value is null || value.Id != Unit.Value || value.LoadState != "loaded" || value.Type != "exec"
            || value.User != _uid.ToString(CultureInfo.InvariantCulture) || value.Group != _gid.ToString(CultureInfo.InvariantCulture)
            || value.KillMode != "control-group" || !value.RemainAfterExit
            || value.RuntimeMaxMicroseconds != _properties["RuntimeMaxUSec"].GetUInt64()
            || value.TimeoutStopMicroseconds != _properties["TimeoutStopUSec"].GetUInt64()
            || value.ControlGroup != "" && value.ControlGroup != "/system.slice/" + Unit.Value) throw Invalid();
    }
    /// <summary>Requires a live positive main PID; inactive zero-PID startup samples remain pending only.</summary>
    internal bool IsRunning(LinuxUnitProperties value)
    {
        RequirePolicy(value);
        return value.ActiveState == "active" && value.SubState == "running" && value.MainPid > 0
            && value.MainPid == value.ExecMainPid && value.ExecMainCode == 0 && value.ExecMainStatus == 0
            && value.ControlGroup == "/system.slice/" + Unit.Value;
    }
    /// <summary>Requires an authenticated stopped terminal tuple; group and pumps must separately join.</summary>
    internal bool IsStopped(LinuxUnitProperties value)
    {
        RequirePolicy(value);
        return value.MainPid == 0 && (value.ActiveState == "inactive" && value.SubState == "dead"
            || value.ActiveState == "failed" && value.SubState == "failed");
    }
    private static void ValidatePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 4095 || !path.StartsWith('/') || path.Contains('\\')
            || path.Contains('%') || path.Contains('$') || path.Any(char.IsControl)
            || path[1..].Split('/').Any(p => p is "" or "." or "..")) throw Invalid();
    }
    private static bool Overlaps(string a, string b) => a == b || a.StartsWith(b + "/", StringComparison.Ordinal) || b.StartsWith(a + "/", StringComparison.Ordinal);
    private static Exception Invalid() => LinuxSystemdBackend.InvalidControl();
}
