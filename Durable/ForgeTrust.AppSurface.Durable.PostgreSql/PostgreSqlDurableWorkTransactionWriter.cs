using System.Data;
using ForgeTrust.AppSurface.Durable;
using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>Writes durable Work directly into a caller-owned Npgsql transaction.</summary>
public sealed class PostgreSqlDurableWorkTransactionWriter : IDurableWorkTransactionWriter
{
    private readonly IDurableWorkRegistry _workRegistry;
    private readonly PostgreSqlDurableWorkOptions _options;
    private readonly PostgreSqlDurableStoreTarget _storeTarget;

    /// <summary>Initializes a transaction writer for one validated store and runtime epoch.</summary>
    /// <param name="dataSource">Authoritative runtime data source matching the caller transaction target.</param>
    /// <param name="workRegistry">Immutable registrations used to validate payload and safety policy.</param>
    /// <param name="options">Validated StoreId, epoch, and notification behavior.</param>
    public PostgreSqlDurableWorkTransactionWriter(
        NpgsqlDataSource dataSource,
        IDurableWorkRegistry workRegistry,
        PostgreSqlDurableWorkOptions options)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _storeTarget = PostgreSqlDurableStoreTarget.Create(dataSource.ConnectionString);
        _workRegistry = workRegistry ?? throw new ArgumentNullException(nameof(workRegistry));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public async ValueTask<DurableOperationResult<DurableWorkAcceptance>> EnqueueAsync(
        NpgsqlTransaction transaction,
        DurableWorkRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(request);
        if (transaction.Connection is not { State: ConnectionState.Open } connection)
        {
            throw new InvalidOperationException(
                "The supplied Npgsql transaction is not active on an open connection. Pass the exact uncommitted domain transaction.");
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
                "The durable request provider-safety mode does not match its immutable Work registration.");
        }

        _ = registration.WorkCodec.DecodeObject(request.Payload);
        return await PostgreSqlDurableWorkStore.AcceptAsync(
            transaction,
            request,
            _options.RuntimeEpoch,
            _options.ExpectedStoreId,
            _options.WakeNotificationMode == PostgreSqlDurableWakeNotificationMode.Enabled,
            cancellationToken).ConfigureAwait(false);
    }
}

internal sealed record PostgreSqlDurableStoreTarget(string Host, int Port, string Database)
{
    internal static PostgreSqlDurableStoreTarget Create(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var host = NormalizeHost(builder.Host);
        var database = string.IsNullOrWhiteSpace(builder.Database) ? builder.Username : builder.Database;
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
