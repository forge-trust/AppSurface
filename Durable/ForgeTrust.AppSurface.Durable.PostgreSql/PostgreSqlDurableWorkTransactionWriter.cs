using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>
/// Writes durable work directly into a caller-owned Npgsql transaction.
/// </summary>
public sealed class PostgreSqlDurableWorkTransactionWriter : IDurableWorkTransactionWriter
{
    private readonly Guid _runtimeEpoch;
    private readonly bool _sendWakeNotification;
    private readonly IDurableWorkRegistry _workRegistry;
    private readonly PostgreSqlDurableStoreTarget _storeTarget;

    /// <summary>
    /// Initializes a transaction writer for one externally managed runtime recovery epoch.
    /// </summary>
    /// <param name="dataSource">
    /// Authoritative runtime data source whose PostgreSQL host, port, and database must exactly match the caller
    /// transaction. Host aliases are intentionally rejected; use the same canonical connection target for both.
    /// </param>
    /// <param name="runtimeEpoch">
    /// Non-empty epoch stored outside the durable database and rotated after point-in-time restore.
    /// </param>
    /// <param name="workRegistry">Immutable work registrations used to validate payload policy and provider safety.</param>
    /// <param name="sendWakeNotification">
    /// Whether acceptance should emit a metadata-only PostgreSQL notification at commit. Notifications are hints; the
    /// runtime sweep remains authoritative.
    /// </param>
    public PostgreSqlDurableWorkTransactionWriter(
        NpgsqlDataSource dataSource,
        Guid runtimeEpoch,
        IDurableWorkRegistry workRegistry,
        bool sendWakeNotification = true)
    {
        if (runtimeEpoch == Guid.Empty)
        {
            throw new ArgumentException("The durable runtime epoch must not be empty.", nameof(runtimeEpoch));
        }

        ArgumentNullException.ThrowIfNull(dataSource);
        _storeTarget = PostgreSqlDurableStoreTarget.Create(dataSource.ConnectionString);
        _runtimeEpoch = runtimeEpoch;
        _workRegistry = workRegistry ?? throw new ArgumentNullException(nameof(workRegistry));
        _sendWakeNotification = sendWakeNotification;
    }

    /// <inheritdoc />
    public async ValueTask<DurableOperationResult<DurableWorkAcceptance>> EnqueueAsync(
        NpgsqlTransaction transaction,
        DurableWorkRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(request);
        if (transaction.Connection is not { } connection)
        {
            throw new InvalidOperationException(
                "The supplied Npgsql transaction is no longer active. Pass the exact uncommitted domain transaction.");
        }

        if (!_storeTarget.Matches(connection))
        {
            throw new InvalidOperationException(
                $"{DurableProblemCodes.StoreIdentityMismatch}: The caller transaction does not target the configured durable PostgreSQL host, port, and database.");
        }

        var registration = _workRegistry.GetRequired(request.WorkName, request.WorkVersion);
        if (registration.ProviderSafety != request.ProviderSafety)
        {
            throw new InvalidOperationException(
                "The durable request provider-safety mode does not match its immutable work registration.");
        }

        _ = registration.WorkCodec.DecodeObject(request.Payload);

        return await PostgreSqlDurableWorkStore.AcceptAsync(
            transaction,
            request,
            _runtimeEpoch,
            expectedStoreId: null,
            _sendWakeNotification,
            cancellationToken).ConfigureAwait(false);
    }
}

internal sealed record PostgreSqlDurableStoreTarget(string Host, int Port, string Database)
{
    internal static PostgreSqlDurableStoreTarget Create(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var host = NormalizeHost(builder.Host);
        var database = string.IsNullOrWhiteSpace(builder.Database)
            ? builder.Username
            : builder.Database;
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(database))
        {
            throw new ArgumentException(
                "The durable PostgreSQL connection must explicitly identify a host and database.",
                nameof(connectionString));
        }

        return new PostgreSqlDurableStoreTarget(host, builder.Port, database);
    }

    internal bool Matches(NpgsqlConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var actual = Create(connection.ConnectionString);
        return Port == actual.Port
            && string.Equals(Host, actual.Host, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Database, connection.Database, StringComparison.Ordinal);
    }

    private static string NormalizeHost(string? host) => string.Join(
        ',',
        (host ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static item => item.ToLowerInvariant()));
}
