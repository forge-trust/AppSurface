using Tmds.DBus.Protocol;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Closed categories of an actual D-Bus error reply; no raw name/message or authority is retained.</summary>
internal enum LinuxSystemdStartError
{
    /// <summary>Unrecognized error name, including aliases and caller canaries.</summary>
    Other,
    /// <summary>The bus rejected access.</summary>
    AccessDenied,
    /// <summary>The bus rejected arguments.</summary>
    InvalidArgs,
    /// <summary>The bus did not receive a reply.</summary>
    NoReply,
    /// <summary>The selected service was unavailable.</summary>
    ServiceUnknown,
    /// <summary>The method was unknown.</summary>
    UnknownMethod,
    /// <summary>The selected unit already existed.</summary>
    UnitExists,
    /// <summary>The selected unit could not load.</summary>
    LoadFailed,
    /// <summary>The selected unit was absent.</summary>
    NoSuchUnit,
}

/// <summary>Detached first start fault, not a D-Bus reply, pending-start outcome or kernel fact.</summary>
/// <param name="ErrorKind">Closed actual exception family; no exception object is retained.</param>
/// <param name="DBusCategory">Closed category only for an actual D-Bus error reply, otherwise null.</param>
internal sealed record LinuxSystemdStartFailure(EvidenceNativeObservationErrorKind ErrorKind,
    LinuxSystemdStartError? DBusCategory);

internal sealed partial class LinuxSystemdBackend
{
    private LinuxSystemdStartFailure? _firstStartFailure;
    /// <summary>Gets only the first actual start exception's closed projection, including after transport disposal.</summary>
    internal LinuxSystemdStartFailure? FirstStartFailure => Volatile.Read(ref _firstStartFailure);

    /// <summary>Maps exact error-name data only; unknown names become Other and no raw string is stored.</summary>
    internal static LinuxSystemdStartError ClassifyStartError(string? name) => name switch
    {
        "org.freedesktop.DBus.Error.AccessDenied" => LinuxSystemdStartError.AccessDenied,
        "org.freedesktop.DBus.Error.InvalidArgs" => LinuxSystemdStartError.InvalidArgs,
        "org.freedesktop.DBus.Error.NoReply" => LinuxSystemdStartError.NoReply,
        "org.freedesktop.DBus.Error.ServiceUnknown" => LinuxSystemdStartError.ServiceUnknown,
        "org.freedesktop.DBus.Error.UnknownMethod" => LinuxSystemdStartError.UnknownMethod,
        "org.freedesktop.systemd1.UnitExists" => LinuxSystemdStartError.UnitExists,
        "org.freedesktop.systemd1.LoadFailed" => LinuxSystemdStartError.LoadFailed,
        "org.freedesktop.systemd1.NoSuchUnit" => LinuxSystemdStartError.NoSuchUnit,
        _ => LinuxSystemdStartError.Other,
    };

    private void CaptureStartFailure(Exception error)
    {
        try
        {
            Interlocked.CompareExchange(ref _firstStartFailure, new(
                EvidenceNativeObservationFailure.Capture(EvidenceNativeObservationPhase.AccountCreate, error).ErrorKind,
                error is DBusErrorReplyException reply ? ClassifyStartError(reply.ErrorName) : null), null);
        }
        catch (Exception) { /* Data capture never replaces the existing dispatch/cleanup failure. */ }
    }
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
        var jobPath = await StartOwnedUnitAsync(worker.Unit, worker.Arguments, worker.Properties,
            worker.StandardOutput, worker.StandardError, token).ConfigureAwait(false);
#if EVIDENCE_PRIVATE_N15
        await WaitAtN15PendingStartBarrierAsync(worker.Unit, jobPath, token).ConfigureAwait(false);
#endif
        return jobPath;
    }

#if EVIDENCE_PRIVATE_N10
    /// <summary>Runs the same reserved worker start with one compile-owned post-reply observation barrier.</summary>
    /// <remarks>The barrier emits only after the original validated systemd reply and waits on the same startup token.</remarks>
    internal async Task<string> StartWorkerForN10Async(LinuxWorkerUnit worker,
        LinuxN10PendingStartCheckpoint checkpoint, CancellationToken token)
    {
        if (worker is null) throw InvalidControl();
        var jobPath = await StartOwnedUnitAsync(worker.Unit, worker.Arguments, worker.Properties,
            worker.StandardOutput, worker.StandardError, token).ConfigureAwait(false);
        await checkpoint.WaitAfterStartReplyAsync(worker.Unit, jobPath, token).ConfigureAwait(false);
        return jobPath;
    }
#endif

#if EVIDENCE_PRIVATE_N15
    /// <summary>Publishes a bounded trigger after the real start reply, then holds the original start task.</summary>
    /// <remarks>The frame grants no admission or join status; the original token remains the only release bound.</remarks>
    private static async Task WaitAtN15PendingStartBarrierAsync(LinuxUnitName unit, string jobPath,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var frame = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            case_name = "N15",
            phase = "start-transient-unit-reply",
            unit = unit.Value,
            job_path = jobPath,
        });
        if (frame.Length is 0 or > 8192) throw InvalidControl();

        using var error = Console.OpenStandardError();
        await error.WriteAsync(frame.AsMemory(), token).ConfigureAwait(false);
        await error.WriteAsync(new byte[] { (byte)'\n' }.AsMemory(), token).ConfigureAwait(false);
        await error.FlushAsync(token).ConfigureAwait(false);
        await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
    }
#endif

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
        catch (OperationCanceledException error) { CaptureStartFailure(error); throw; }
        catch (Exception error) { CaptureStartFailure(error); Dispose(); throw InvalidControl(); }
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
