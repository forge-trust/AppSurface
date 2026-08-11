using Npgsql;
using Testcontainers.PostgreSql;
using Xunit.Sdk;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

internal sealed class PostgreSqlIntegrationTestDatabase : IAsyncDisposable
{
    private const int RequiredServerVersion = 170005;
    private const int ContainerStartupProbeMaximumAttempts = 3;
    private static readonly TimeSpan ContainerStartupProbeRetryDelay = TimeSpan.FromMilliseconds(250);
    private readonly List<NpgsqlDataSource> _additionalDataSources = [];
    private readonly string _databaseName;
    private readonly string? _maintenanceConnectionString;
    private readonly PostgreSqlContainer? _container;

    private PostgreSqlIntegrationTestDatabase(
        string databaseName,
        string? maintenanceConnectionString,
        string connectionString,
        NpgsqlDataSource dataSource,
        PostgreSqlContainer? container = null)
    {
        _databaseName = databaseName;
        _maintenanceConnectionString = maintenanceConnectionString;
        _container = container;
        ConnectionString = connectionString;
        DataSource = dataSource;
    }

    internal NpgsqlDataSource DataSource { get; }

    internal string ConnectionString { get; }

    /// <summary>Creates a separately owned data source for testing configuration that requires distinct instances.</summary>
    internal NpgsqlDataSource CreateDataSource()
    {
        var dataSource = NpgsqlDataSource.Create(ConnectionString);
        _additionalDataSources.Add(dataSource);
        return dataSource;
    }

    internal static async ValueTask<PostgreSqlIntegrationTestDatabase> TryCreateAsync()
    {
        var configured = Environment.GetEnvironmentVariable("APPSURFACE_POSTGRES_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(configured))
        {
            var container = new PostgreSqlBuilder(PostgreSqlTestContainerImage.Reference)
                .WithDatabase("appsurface_durable")
                .WithUsername("appsurface")
                .WithPassword("appsurface-test-password")
                .Build();
            try
            {
                await container.StartAsync();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await container.DisposeAsync();
                var prerequisite =
                    $"Real PostgreSQL tests require APPSURFACE_POSTGRES_TEST_CONNECTION or an available Docker daemon: {exception.Message}";
                var skipRequested = string.Equals(
                        Environment.GetEnvironmentVariable("APPSURFACE_POSTGRES_TEST_ALLOW_SKIP"),
                        "true",
                        StringComparison.OrdinalIgnoreCase);
                var runningInCi = string.Equals(
                    Environment.GetEnvironmentVariable("CI"),
                    "true",
                    StringComparison.OrdinalIgnoreCase);
                if (skipRequested && !runningInCi)
                {
                    throw SkipException.ForSkip(prerequisite);
                }

                throw new InvalidOperationException(
                    $"{prerequisite} Set APPSURFACE_POSTGRES_TEST_ALLOW_SKIP=true only for an intentional local opt-out.",
                    exception);
            }

            var containerConnectionString = container.GetConnectionString();
            var containerDataSource = NpgsqlDataSource.Create(containerConnectionString);
            try
            {
                await ExecuteContainerStartupProbeAsync(
                    _ => EnsureRequiredServerVersionAsync(containerDataSource));
            }
            catch
            {
                await containerDataSource.DisposeAsync();
                await container.DisposeAsync();
                throw;
            }

            return new PostgreSqlIntegrationTestDatabase(
                "appsurface_durable",
                maintenanceConnectionString: null,
                containerConnectionString,
                containerDataSource,
                container);
        }

        var sourceBuilder = new NpgsqlConnectionStringBuilder(configured);
        var maintenanceBuilder = new NpgsqlConnectionStringBuilder(configured)
        {
            Database = "postgres",
            Pooling = false,
        };
        var databaseName = $"appsurface_durable_{Guid.NewGuid():N}";
        await using (var maintenance = new NpgsqlConnection(maintenanceBuilder.ConnectionString))
        {
            await maintenance.OpenAsync();
            await EnsureRequiredServerVersionAsync(maintenance);
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\";", maintenance);
            await create.ExecuteNonQueryAsync();
        }

        sourceBuilder.Database = databaseName;
        var dataSource = NpgsqlDataSource.Create(sourceBuilder.ConnectionString);
        return new PostgreSqlIntegrationTestDatabase(
            databaseName,
            maintenanceBuilder.ConnectionString,
            sourceBuilder.ConnectionString,
            dataSource);
    }

    /// <summary>
    /// Executes an idempotent container-startup probe, retrying only an Npgsql connection timeout that can occur after
    /// the container-local readiness probe succeeds but before Docker Desktop exposes the published port to the host.
    /// </summary>
    /// <param name="probe">The idempotent probe to execute.</param>
    /// <param name="delayAsync">Optional delay seam for deterministic retry tests.</param>
    /// <param name="cancellationToken">Token that cancels a pending retry delay.</param>
    internal static async ValueTask ExecuteContainerStartupProbeAsync(
        Func<CancellationToken, ValueTask> probe,
        Func<TimeSpan, CancellationToken, ValueTask>? delayAsync = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(probe);

        var delay = delayAsync ?? (static (duration, token) => new ValueTask(Task.Delay(duration, token)));
        for (var attempt = 1; attempt <= ContainerStartupProbeMaximumAttempts; attempt++)
        {
            try
            {
                await probe(cancellationToken);
                return;
            }
            catch (NpgsqlException exception) when (
                exception.InnerException is TimeoutException
                && attempt < ContainerStartupProbeMaximumAttempts)
            {
                await delay(ContainerStartupProbeRetryDelay, cancellationToken);
            }
        }
    }

    private static async ValueTask EnsureRequiredServerVersionAsync(NpgsqlDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await EnsureRequiredServerVersionAsync(connection);
    }

    private static async ValueTask EnsureRequiredServerVersionAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand("SHOW server_version_num;", connection);
        var value = (string?)await command.ExecuteScalarAsync();
        if (!int.TryParse(value, out var version) || version != RequiredServerVersion)
        {
            throw new InvalidOperationException(
                $"Durable PostgreSQL integration tests require server_version_num {RequiredServerVersion} (PostgreSQL 17.5), but the server reported '{value ?? "<null>"}'.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var additionalDataSource in _additionalDataSources)
        {
            await additionalDataSource.DisposeAsync();
        }

        await DataSource.DisposeAsync();
        if (_container is not null)
        {
            await _container.DisposeAsync();
            return;
        }

        await using var maintenance = new NpgsqlConnection(_maintenanceConnectionString!);
        await maintenance.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE \"{_databaseName}\" WITH (FORCE);", maintenance);
        await drop.ExecuteNonQueryAsync();
    }
}
