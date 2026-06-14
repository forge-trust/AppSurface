using Npgsql;

namespace ProductReadinessLab;

/// <summary>
/// Product subscription state persisted by the lab.
/// </summary>
/// <param name="Id">Subscription identifier.</param>
/// <param name="AccountName">Account display name.</param>
/// <param name="PlanName">Plan name.</param>
/// <param name="Status">Current subscription status.</param>
internal sealed record ProductSubscription(Guid Id, string AccountName, string PlanName, string Status);

/// <summary>
/// Safe product-state probe result.
/// </summary>
/// <param name="Succeeded">Whether the probe succeeded.</param>
/// <param name="IsPostgresBacked">Whether the probe used Postgres.</param>
/// <param name="SafeDiagnostic">A diagnostic that excludes connection values and local paths.</param>
internal sealed record ProductStateProbe(bool Succeeded, bool IsPostgresBacked, string SafeDiagnostic);

/// <summary>
/// Product state persistence abstraction for the lab.
/// </summary>
internal interface IProductStateStore
{
    /// <summary>
    /// Gets a value indicating whether this store is backed by Postgres.
    /// </summary>
    bool IsPostgresBacked { get; }

    /// <summary>
    /// Creates or replaces a subscription.
    /// </summary>
    /// <param name="subscription">Subscription to store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored subscription.</returns>
    Task<ProductSubscription> SaveAsync(ProductSubscription subscription, CancellationToken cancellationToken = default);

    /// <summary>
    /// Probes the store without exposing connection details.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A safe probe result.</returns>
    Task<ProductStateProbe> ProbeAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// In-process fallback store used when Postgres is not configured.
/// </summary>
internal sealed class InMemoryProductStateStore : IProductStateStore
{
    private readonly Dictionary<Guid, ProductSubscription> _subscriptions = new();

    /// <inheritdoc />
    public bool IsPostgresBacked => false;

    /// <inheritdoc />
    public Task<ProductSubscription> SaveAsync(ProductSubscription subscription, CancellationToken cancellationToken = default)
    {
        _subscriptions[subscription.Id] = subscription;
        return Task.FromResult(subscription);
    }

    /// <inheritdoc />
    public Task<ProductStateProbe> ProbeAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new ProductStateProbe(true, false, "Postgres connection not configured."));
}

/// <summary>
/// Postgres-backed product state store for local product/domain state.
/// </summary>
internal sealed class PostgresProductStateStore : IProductStateStore, IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private bool _schemaReady;

    /// <summary>
    /// Creates a Postgres product-state store.
    /// </summary>
    /// <param name="connectionString">Postgres connection string.</param>
    public PostgresProductStateStore(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("Connection string is required.", nameof(connectionString));
        }

        _dataSource = NpgsqlDataSource.Create(connectionString);
    }

    /// <inheritdoc />
    public bool IsPostgresBacked => true;

    /// <inheritdoc />
    public async Task<ProductSubscription> SaveAsync(
        ProductSubscription subscription,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);

        const string sql = """
            insert into product_readiness_subscriptions (id, account_name, plan_name, status)
            values ($1, $2, $3, $4)
            on conflict (id) do update
            set account_name = excluded.account_name,
                plan_name = excluded.plan_name,
                status = excluded.status,
                updated_at = now();
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(subscription.Id);
        command.Parameters.AddWithValue(subscription.AccountName);
        command.Parameters.AddWithValue(subscription.PlanName);
        command.Parameters.AddWithValue(subscription.Status);
        await command.ExecuteNonQueryAsync(cancellationToken);

        return subscription;
    }

    /// <inheritdoc />
    public async Task<ProductStateProbe> ProbeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var probe = new ProductSubscription(Guid.NewGuid(), "readiness-lab", "team", "probe");
            await SaveAsync(probe, cancellationToken);
            return new ProductStateProbe(true, true, "Postgres product-state probe succeeded.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new ProductStateProbe(false, true, $"Postgres probe failed with {exception.GetType().Name}.");
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _dataSource.DisposeAsync();
        _schemaGate.Dispose();
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        await _schemaGate.WaitAsync(cancellationToken);
        try
        {
            if (_schemaReady)
            {
                return;
            }

            await EnsureSchemaCoreAsync(cancellationToken);
            _schemaReady = true;
        }
        finally
        {
            _schemaGate.Release();
        }
    }

    private async Task EnsureSchemaCoreAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            create table if not exists product_readiness_subscriptions (
                id uuid primary key,
                account_name text not null,
                plan_name text not null,
                status text not null,
                updated_at timestamptz not null default now()
            );
            """;

        await using var command = _dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
