using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using ForgeTrust.AppSurface.Evidence.Contracts;
using Tmds.DBus.Protocol;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Closed classes of units owned by one Evidence run.</summary>
internal enum LinuxUnitRole { Owner, Worker, Producer, Application, AccountUtility }

/// <summary>A generated unit name, not proof that the unit exists or belongs to the caller.</summary>
internal sealed class LinuxUnitName
{
    private LinuxUnitName(string value) => Value = value;

    /// <summary>Gets the generated service name.</summary>
    internal string Value { get; }

    /// <summary>Creates a name from a closed role and a fresh run identifier.</summary>
    internal static LinuxUnitName Create(LinuxUnitRole role, Guid runId)
    {
        var label = role switch
        {
            LinuxUnitRole.Owner => "owner", LinuxUnitRole.Worker => "worker",
            LinuxUnitRole.Producer => "producer", LinuxUnitRole.Application => "application",
            LinuxUnitRole.AccountUtility => "utility",
            _ => throw LinuxSystemdBackend.InvalidControl(),
        };
        if (runId == Guid.Empty) throw LinuxSystemdBackend.InvalidControl();
        return new($"appsurface-evidence-{label}-{runId:N}.service");
    }
}

/// <summary>Sampled systemd data. A PID or an inactive state alone cannot prove physical exit.</summary>
internal sealed record LinuxUnitProperties(
    string Id, string LoadState, string ActiveState, string SubState, string ControlGroup,
    uint MainPid, uint ExecMainPid, int ExecMainCode, int ExecMainStatus,
    string User, string Group, string Type, string KillMode, bool RemainAfterExit,
    ulong RuntimeMaxMicroseconds, ulong TimeoutStopMicroseconds)
{
    /// <summary>Projects required typed properties without inventing missing values.</summary>
    internal static LinuxUnitProperties Parse(IReadOnlyDictionary<string, VariantValue> unit,
        IReadOnlyDictionary<string, VariantValue> service)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(service);
        try
        {
            return new(Text(unit, "Id"), Text(unit, "LoadState"), Text(unit, "ActiveState"),
                Text(unit, "SubState"), Text(service, "ControlGroup"), service["MainPID"].GetUInt32(),
                service["ExecMainPID"].GetUInt32(), service["ExecMainCode"].GetInt32(),
                service["ExecMainStatus"].GetInt32(), Text(service, "User"), Text(service, "Group"),
                Text(service, "Type"), Text(service, "KillMode"), service["RemainAfterExit"].GetBool(),
                service["RuntimeMaxUSec"].GetUInt64(), service["TimeoutStopUSec"].GetUInt64());
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            throw LinuxSystemdBackend.InvalidControl();
        }
    }

    private static string Text(IReadOnlyDictionary<string, VariantValue> values, string name)
    {
        var value = values[name].GetString();
        if (value.Length > 4096 || value.Any(char.IsControl)) throw LinuxSystemdBackend.InvalidControl();
        return value;
    }
}

/// <summary>Joins a client operation even when its caller cancels the wait.</summary>
/// <remarks>
/// Aborting the transport must settle the actual operation. Cancellation does not establish
/// whether a systemd start was accepted; the owning run must retain and stop pending units.
/// </remarks>
internal static class SupervisionOperationJoin
{
    /// <summary>Aborts on caller cancellation and joins the original task before propagating it.</summary>
    internal static async Task<T> RunAsync<T>(Task<T> operation, Action abort, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(abort);
        try
        {
            var result = await operation.WaitAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            var abortFailed = false;
            try { abort(); }
            catch (Exception) { abortFailed = true; }
            try { await operation.ConfigureAwait(false); }
            catch (Exception) { /* The original caller cancellation remains the outcome after join. */ }
            if (abortFailed) throw LinuxSystemdBackend.InvalidControl();
            throw new OperationCanceledException(token);
        }
    }
}

/// <summary>Explicit system-bus connection pinned to the root systemd manager.</summary>
/// <remarks>
/// Uses no environment-selected address, auto-reconnect, shell, or activatable manager name.
/// This backend issues no admission. Its unit facts must be bound to kernel identity by the run owner.
/// Workload creation and the single-use owner bootstrap are composed separately.
/// </remarks>
internal sealed partial class LinuxSystemdBackend : IDisposable
{
    private const string BusAddress = "unix:path=/run/dbus/system_bus_socket";
    private const string BusName = "org.freedesktop.DBus";
    private const string BusPath = "/org/freedesktop/DBus";
    private const string ManagerPath = "/org/freedesktop/systemd1";
    private const string ManagerInterface = "org.freedesktop.systemd1.Manager";
    private const string UnitInterface = "org.freedesktop.systemd1.Unit";
    private const string ServiceInterface = "org.freedesktop.systemd1.Service";
    private readonly DBusConnection _connection;
    private readonly string _manager;
    private int _closed;

    private LinuxSystemdBackend(DBusConnection connection, string manager)
    {
        _connection = connection;
        _manager = manager;
    }

    /// <summary>Connects only on root Linux x64 and authenticates systemd's unique bus owner as PID 1/UID 0.</summary>
    internal static async Task<LinuxSystemdBackend> ConnectAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64 || GetEffectiveUid() != 0)
            throw new EvidenceAdmissionException("ASEVD402", "The C# supervisor requires root on supported Linux x64.");
        var connection = new DBusConnection(new DBusConnectionOptions(BusAddress) { AutoConnect = false });
        try
        {
            await SupervisionOperationJoin.RunAsync(Connect(connection), connection.Dispose, token).ConfigureAwait(false);
            var owner = await SupervisionOperationJoin.RunAsync(connection.CallMethodAsync(
                BusRequest(connection, "GetNameOwner", "org.freedesktop.systemd1"),
                static (message, _) => { RequireReply(message, BusName, "s"); return message.GetBodyReader().ReadString(); }),
                connection.Dispose, token).ConfigureAwait(false);
            ValidateManagerName(owner);
            foreach (var query in new[] { "GetConnectionUnixProcessID", "GetConnectionUnixUser" })
            {
                var value = await SupervisionOperationJoin.RunAsync(connection.CallMethodAsync(
                    BusRequest(connection, query, owner),
                    static (message, _) => { RequireReply(message, BusName, "u"); return message.GetBodyReader().ReadUInt32(); }),
                    connection.Dispose, token).ConfigureAwait(false);
                if (value != (query == "GetConnectionUnixProcessID" ? 1u : 0u)) throw InvalidControl();
            }
            return new(connection, owner);
        }
        catch (OperationCanceledException) { connection.Dispose(); throw; }
        catch (Exception) { connection.Dispose(); throw InvalidControl(); }
    }

    /// <summary>Reads the current process's service properties; callers must verify the owner bootstrap.</summary>
    internal async Task<LinuxUnitProperties> ReadCurrentUnitAsync(CancellationToken token)
    {
        var path = await CallAsync(ManagerRequest("GetUnitByPID", "u", processId: (uint)Environment.ProcessId),
            "o", static message => message.GetBodyReader().ReadObjectPath().ToString(), token).ConfigureAwait(false);
        return await ReadPropertiesAsync(path, token).ConfigureAwait(false);
    }

    /// <summary>Reads the exact generated unit without using a shell or reusing a collected cgroup path.</summary>
    internal async Task<LinuxUnitProperties> ReadUnitAsync(LinuxUnitName unit, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(unit);
        var path = await CallAsync(ManagerRequest("GetUnit", "s", unit.Value), "o",
            static message => message.GetBodyReader().ReadObjectPath().ToString(), token).ConfigureAwait(false);
        var result = await ReadPropertiesAsync(path, token).ConfigureAwait(false);
        if (result.Id != unit.Value) throw InvalidControl();
        return result;
    }

    /// <summary>Requests stop for one retained generated unit. A returned job path is not an exit receipt.</summary>
    internal async Task StopUnitAsync(LinuxUnitName unit, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(unit);
        _ = await CallAsync(ManagerRequest("StopUnit", "ss", unit.Value, "replace"), "o",
            static message => message.GetBodyReader().ReadObjectPath().ToString(), token).ConfigureAwait(false);
    }

    /// <summary>Forces SIGKILL for all processes of a retained generated unit; kernel group join is still required.</summary>
    internal async Task KillUnitAsync(LinuxUnitName unit, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(unit);
        _ = await CallAsync(ManagerRequest("KillUnit", "ssi", unit.Value, "all", signal: 9), string.Empty,
            static _ => true, token).ConfigureAwait(false);
    }

    /// <summary>Closes this connection irrevocably; a fresh independent stop connection can be authenticated separately.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0) _connection.Dispose();
    }

    private async Task<LinuxUnitProperties> ReadPropertiesAsync(string path, CancellationToken token)
    {
        if (!path.StartsWith(ManagerPath + "/unit/", StringComparison.Ordinal)
            || path.Length > 4096 || path.Any(char.IsControl)) throw InvalidControl();
        var unit = await ReadInterfaceAsync(path, UnitInterface, token).ConfigureAwait(false);
        var service = await ReadInterfaceAsync(path, ServiceInterface, token).ConfigureAwait(false);
        return LinuxUnitProperties.Parse(unit, service);
    }

    private Task<IReadOnlyDictionary<string, VariantValue>> ReadInterfaceAsync(string path, string @interface, CancellationToken token)
    {
        using var writer = _connection.GetMessageWriter();
        writer.WriteMethodCallHeader(_manager, path, "org.freedesktop.DBus.Properties", "GetAll", "s");
        writer.WriteString(@interface);
        return CallAsync<IReadOnlyDictionary<string, VariantValue>>(writer.CreateMessage(), "a{sv}", static message =>
        {
            var reader = message.GetBodyReader();
            var end = reader.ReadDictionaryStart();
            var properties = new Dictionary<string, VariantValue>(StringComparer.Ordinal);
            while (reader.HasNext(end))
            {
                var name = reader.ReadString();
                if (properties.Count >= 512 || name.Length is < 1 or > 128 || name.Any(char.IsControl)
                    || !properties.TryAdd(name, reader.ReadVariantValue())) throw InvalidControl();
            }
            return new ReadOnlyDictionary<string, VariantValue>(properties);
        }, token);
    }

    private async Task<T> CallAsync<T>(MessageBuffer request, string signature, Func<Message, T> read, CancellationToken token)
    {
        if (Volatile.Read(ref _closed) != 0) throw InvalidControl();
        try
        {
            return await SupervisionOperationJoin.RunAsync(_connection.CallMethodAsync(request, (message, _) =>
            {
                RequireReply(message, _manager, signature);
                return read(message);
            }), Dispose, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { Dispose(); throw InvalidControl(); }
    }

    private MessageBuffer ManagerRequest(string member, string signature, string? first = null,
        string? second = null, uint? processId = null, int? signal = null)
    {
        using var writer = _connection.GetMessageWriter();
        writer.WriteMethodCallHeader(_manager, ManagerPath, ManagerInterface, member, signature);
        if (first is not null) writer.WriteString(first);
        if (second is not null) writer.WriteString(second);
        if (processId is { } pid) writer.WriteUInt32(pid);
        if (signal is { } number) writer.WriteInt32(number);
        return writer.CreateMessage();
    }

    private static MessageBuffer BusRequest(DBusConnection connection, string member, string argument)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(BusName, BusPath, BusName, member, "s");
        writer.WriteString(argument);
        return writer.CreateMessage();
    }

    private static async Task<bool> Connect(DBusConnection connection)
    {
        await connection.ConnectAsync().ConfigureAwait(false);
        return true;
    }

    /// <summary>Checks a unique bus name as data; the caller must also authenticate its UID and PID.</summary>
    internal static void ValidateManagerName(string name)
    {
        if (name.Length is < 4 or > 255 || name[0] != ':'
            || name[1..].Split('.').Length < 2
            || name[1..].Split('.').Any(static part => part.Length == 0 || part.Any(static c => c is < '0' or > '9')))
            throw InvalidControl();
    }

    private static void RequireReply(Message message, string sender, string signature)
    {
        if (message.SenderAsString != sender || message.SignatureAsString != signature) throw InvalidControl();
    }

    /// <summary>Creates a fixed backend failure without raw wire values or dependency exceptions.</summary>
    internal static EvidenceAdmissionException InvalidControl() =>
        new("ASEVD410", "The protected systemd operation or its response is invalid.");

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEffectiveUid();
}
