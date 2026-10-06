using Tmds.DBus.Protocol;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

internal sealed partial class LinuxSystemdBackend
{
    /// <summary>Serializes and requests one fixed transient worker unit; returns only its job object path.</summary>
    /// <param name="worker">Protected-owner-selected recipe; construction alone creates no authority.</param>
    /// <param name="token">Original run deadline/cancellation, not a renewed dispatch budget.</param>
    /// <returns>The systemd job path, not worker readiness, ownership, successful execution, or physical exit.</returns>
    /// <remarks>
    /// The caller MUST reserve the pending unit in its work ledger before invocation, authenticate the live owner,
    /// validate the single-use activation guard and deployment, and retain both write handles and pumps.
    /// Cancellation aborts this transport and joins the actual call. An ambiguous/canceled start
    /// may already have been accepted: retain the pending registration until an independent stop and kernel join.
    /// This method neither closes the caller's handles nor marks workload completion.
    /// Wire contract: <see href="https://github.com/systemd/systemd/blob/v255/man/org.freedesktop.systemd1.xml">systemd v255</see>
    /// StartTransientUnit(ssa(sv)a(sa(sv))), mode fail, no auxiliary units. ExecStart is a(sasb); descriptors are h via
    /// <see href="https://github.com/tmds/Tmds.DBus/blob/rel/0.95.1/src/Tmds.DBus.Protocol/MessageWriter.Handle.cs">WriteVariantHandle</see>.
    /// </remarks>
    internal async Task<string> StartWorkerAsync(LinuxWorkerUnit worker, CancellationToken token)
    {
        if (worker is null) throw InvalidControl();
        return await StartOwnedUnitAsync(worker.Unit, worker.Arguments, worker.Properties,
            worker.StandardOutput, worker.StandardError, token).ConfigureAwait(false);
    }

    /// <summary>Requests one fixed root account utility after pending ownership is retained.</summary>
    /// <remarks>Job reply alone proves no execution, account identity, cgroup exit or output EOF.</remarks>
    internal Task<string> StartAccountUtilityAsync(LinuxAccountUnit unit, CancellationToken token)
    {
        if (unit is null) throw InvalidControl();
        return StartOwnedUnitAsync(unit.Unit, unit.Arguments, unit.Properties,
            unit.StandardOutput, unit.StandardError, token);
    }

    private async Task<string> StartOwnedUnitAsync(LinuxUnitName unit, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, VariantValue> properties, System.Runtime.InteropServices.SafeHandle stdout,
        System.Runtime.InteropServices.SafeHandle stderr, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _closed) != 0) throw InvalidControl();
        try
        {
            return await SupervisionOperationJoin.RunAsync(_connection.CallMethodAsync(
                OwnedStartRequest(unit, arguments, properties, stdout, stderr), (message, _) =>
            {
                RequireReply(message, _manager, "o");
                var path = message.GetBodyReader().ReadObjectPath().ToString();
                if (!path.StartsWith(ManagerPath + "/job/", StringComparison.Ordinal)
                    || path.Length > 4096 || path.Any(char.IsControl)) throw InvalidControl();
                return path;
            }), Dispose, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { Dispose(); throw InvalidControl(); }
    }

    private MessageBuffer OwnedStartRequest(LinuxUnitName unit, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, VariantValue> unitProperties, System.Runtime.InteropServices.SafeHandle stdout,
        System.Runtime.InteropServices.SafeHandle stderr)
    {
        if (stdout.IsClosed || stdout.IsInvalid
            || stderr.IsClosed || stderr.IsInvalid) throw InvalidControl();
        using var writer = _connection.GetMessageWriter();
        writer.WriteMethodCallHeader(_manager, ManagerPath, ManagerInterface, "StartTransientUnit", "ssa(sv)a(sa(sv))");
        writer.WriteString(unit.Value);
        writer.WriteString("fail");
        var properties = writer.WriteArrayStart(DBusType.Struct);
        foreach (var property in unitProperties)
        {
            writer.WriteStructureStart();
            writer.WriteString(property.Key);
            writer.WriteVariant(property.Value);
        }

        writer.WriteStructureStart();
        writer.WriteString("ExecStart");
        writer.WriteSignature("a(sasb)");
        var commands = writer.WriteArrayStart(DBusType.Struct);
        writer.WriteStructureStart();
        writer.WriteString(arguments[0]);
        writer.WriteArray(arguments.ToArray());
        writer.WriteBool(false); // Never ignore an owned execution failure.
        writer.WriteArrayEnd(commands);

        writer.WriteStructureStart();
        writer.WriteString("StandardOutputFileDescriptor");
        writer.WriteVariantHandle(stdout);
        writer.WriteStructureStart();
        writer.WriteString("StandardErrorFileDescriptor");
        writer.WriteVariantHandle(stderr);
        writer.WriteArrayEnd(properties);

        var auxiliary = writer.WriteArrayStart(DBusType.Struct);
        writer.WriteArrayEnd(auxiliary);
        return writer.CreateMessage();
    }
}
