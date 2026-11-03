using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using Tmds.DBus.Protocol;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Closed root account-utility unit data, never a process, account owner or admission.</summary>
/// <remarks>
/// Only fixed generated account commands can populate argv. The root executor validates the actual
/// owner and reserves a pending start before D-Bus I/O. This is a short child unit, not a persistent service.
/// Root account-db/log grants apply only to these utilities; worker/subject grants are unaffected.
/// </remarks>
internal sealed class LinuxAccountUnit
{
    private LinuxAccountUnit(LinuxUnitName unit, LinuxUnitName owner, LinuxRunAccountCommand command,
        ulong runtime, ulong stopping, SafeHandle stdout, SafeHandle stderr)
    {
        Unit = unit; Owner = owner; Command = command; RuntimeMicroseconds = runtime; StoppingMicroseconds = stopping;
        Arguments = Array.AsReadOnly(new[] { command.Executable }.Concat(command.Arguments).ToArray());
        StandardOutput = stdout; StandardError = stderr;
    }

    /// <summary>Gets this root-generated utility name, not a unit-existence fact.</summary>
    internal LinuxUnitName Unit { get; }
    /// <summary>Gets the exact single-use owner dependency.</summary>
    internal LinuxUnitName Owner { get; }
    /// <summary>Gets the fixed account command data.</summary>
    internal LinuxRunAccountCommand Command { get; }
    /// <summary>Gets immutable complete argv, including argv[0].</summary>
    internal IReadOnlyList<string> Arguments { get; }
    /// <summary>Gets the remaining runtime capped at ten seconds, inside the original run.</summary>
    internal ulong RuntimeMicroseconds { get; }
    /// <summary>Gets the original stop reserve lowered to at most five seconds.</summary>
    internal ulong StoppingMicroseconds { get; }
    /// <summary>Gets the borrowed stdout write descriptor, retained by the pipe owner.</summary>
    internal SafeHandle StandardOutput { get; }
    /// <summary>Gets the borrowed stderr write descriptor.</summary>
    internal SafeHandle StandardError { get; }

    /// <summary>Gets copied typed properties for the fixed root utility policy.</summary>
    internal IReadOnlyDictionary<string, VariantValue> Properties => new ReadOnlyDictionary<string, VariantValue>(
        new Dictionary<string, VariantValue>(StringComparer.Ordinal)
        {
            ["Type"] = "exec", ["User"] = "0", ["Group"] = "0",
            ["SupplementaryGroups"] = VariantValue.Array(Array.Empty<string>()),
            ["NoNewPrivileges"] = true,
            // Account database ownership preservation needs only CHOWN, DAC_OVERRIDE and FOWNER.
            // SYS_ADMIN/SETUID/SETGID are absent; these fixed utilities cannot remount/move their cgroup.
            ["CapabilityBoundingSet"] = 11UL, ["AmbientCapabilities"] = 0UL,
            ["ProtectSystem"] = "strict", ["ProtectHome"] = "yes", ["PrivateTmp"] = true,
            ["ProtectControlGroups"] = true, ["RestrictSUIDSGID"] = true,
            ["TasksMax"] = 64UL, ["MemoryMax"] = 1UL << 30,
            ["KillMode"] = "control-group", ["Restart"] = "no", ["RemainAfterExit"] = true,
            // Retain terminal metadata through both stops and kernel/pump joins. Closing the original
            // authenticated starting connection releases this reference; it does not stop the service.
            ["AddRef"] = true,
            ["RuntimeMaxUSec"] = RuntimeMicroseconds, ["TimeoutStopUSec"] = StoppingMicroseconds,
            ["SendSIGKILL"] = true, ["FinalKillSignal"] = 9, ["LimitCORE"] = 0UL,
            ["After"] = VariantValue.Array(new[] { Owner.Value }), ["BindsTo"] = VariantValue.Array(new[] { Owner.Value }),
            ["ReadWritePaths"] = VariantValue.Array(new[] { "/etc", "/var/log" }),
            ["StandardInput"] = "null", ["PassEnvironment"] = VariantValue.Array(Array.Empty<string>()),
            ["UnsetEnvironment"] = VariantValue.Array(LinuxOwnerFacts.UnsafeEnvironment.ToArray()),
            ["Environment"] = VariantValue.Array(LinuxRunAccountCommand.Environment.ToArray())
        });

    /// <summary>Creates bounded policy data for a fixed account operation; it issues no execution authority.</summary>
    internal static LinuxAccountUnit Create(LinuxUnitName unit, LinuxUnitName owner, LinuxRunAccountCommand command,
        TimeSpan remaining, TimeSpan stopping, SafeHandle stdout, SafeHandle stderr)
    {
        if (unit is null || owner is null || command is null
            || !unit.Value.StartsWith("appsurface-evidence-utility-", StringComparison.Ordinal)
            || !owner.Value.StartsWith("appsurface-evidence-owner-", StringComparison.Ordinal)
            || remaining <= stopping || remaining > TimeSpan.FromHours(1)
            || stopping < TimeSpan.FromTicks(10) || stopping > TimeSpan.FromSeconds(5)
            || stdout is null || stderr is null || stdout.IsClosed || stdout.IsInvalid || stderr.IsClosed || stderr.IsInvalid)
            throw LinuxSystemdBackend.InvalidControl();
        var runtime = Math.Min(remaining.Ticks - stopping.Ticks, TimeSpan.FromSeconds(10).Ticks) / 10;
        if (runtime < 1) throw LinuxSystemdBackend.InvalidControl();
        return new(unit, owner, command, (ulong)runtime, (ulong)(stopping.Ticks / 10), stdout, stderr);
    }

    /// <summary>Checks observed terminal service data; this alone proves no group or pump join.</summary>
    internal bool HasFinished(LinuxUnitProperties value)
    {
        if (value.Id != Unit.Value || value.LoadState != "loaded" || value.User != "0" || value.Group != "0"
            || value.Type != "exec" || value.KillMode != "control-group" || !value.RemainAfterExit
            || value.RuntimeMaxMicroseconds != RuntimeMicroseconds || value.TimeoutStopMicroseconds != StoppingMicroseconds
            || value.ControlGroup != string.Empty && value.ControlGroup != "/system.slice/" + Unit.Value)
            throw LinuxSystemdBackend.InvalidControl();
        if (value.MainPid != 0) return false;
        if (value.ExecMainPid == 0 || value.ExecMainCode == 0) return false; // Uninitialized is never success.
        if (value.ExecMainCode != 1 || value.ExecMainStatus != 0) throw LinuxSystemdBackend.InvalidControl();
        return value.ActiveState == "active" && value.SubState == "exited";
    }
}
