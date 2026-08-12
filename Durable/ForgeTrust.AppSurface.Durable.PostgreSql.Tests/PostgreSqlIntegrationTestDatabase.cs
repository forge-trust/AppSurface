using Npgsql;
using Testcontainers.PostgreSql;
using Xunit.Sdk;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

internal sealed class PostgreSqlIntegrationTestDatabase : IAsyncDisposable
{
    private const int RequiredServerVersion = 170005;
    private const int ContainerStartupProbeMaximumAttempts = 3;
    private static readonly TimeSpan ContainerStartupProbeRetryDelay = TimeSpan.FromMilliseconds(250);
    private static readonly SemaphoreSlim SharedContainerServerGate = new(1, 1);
    private readonly List<NpgsqlDataSource> _additionalDataSources = [];
    private readonly string _databaseName;
    private readonly string _maintenanceConnectionString;
    private static Task<PostgreSqlTestServer>? _sharedContainerServer;

    private PostgreSqlIntegrationTestDatabase(
        string databaseName,
        string maintenanceConnectionString,
        string connectionString,
        NpgsqlDataSource dataSource)
    {
        _databaseName = databaseName;
        _maintenanceConnectionString = maintenanceConnectionString;
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

    /// <summary>
    /// Creates an isolated disposable database on a configured server or a process-shared Testcontainers server.
    /// </summary>
    /// <remarks>
    /// Each caller receives a unique database that <see cref="DisposeAsync"/> drops, so xUnit cases can run concurrently
    /// without sharing application state. The default Testcontainers server is retained for the test-host lifetime and
    /// cleaned up by Testcontainers' resource reaper when that host exits.
    /// </remarks>
    internal static async ValueTask<PostgreSqlIntegrationTestDatabase> TryCreateAsync()
    {
        var configured = Environment.GetEnvironmentVariable("APPSURFACE_POSTGRES_TEST_CONNECTION");
        return string.IsNullOrWhiteSpace(configured)
            ? await CreateDatabaseAsync(await GetSharedContainerServerAsync())
            : await CreateFromConnectionStringAsync(configured);
    }

    /// <summary>
    /// Creates an isolated disposable database from an explicitly configured PostgreSQL server.
    /// </summary>
    /// <remarks>
    /// This is the same path that <see cref="TryCreateAsync"/> uses when
    /// <c>APPSURFACE_POSTGRES_TEST_CONNECTION</c> is set. It is exposed internally so fixture tests can verify
    /// configured-server isolation without mutating process-wide environment state. The connection must target a
    /// PostgreSQL 17.5 server whose database can be changed to <c>postgres</c>, and its credentials must be allowed
    /// to create and drop databases.
    /// </remarks>
    internal static async ValueTask<PostgreSqlIntegrationTestDatabase> CreateFromConnectionStringAsync(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return await CreateDatabaseAsync(await CreateConfiguredServerAsync(connectionString));
    }

    private static async Task<PostgreSqlIntegrationTestDatabase> CreateDatabaseAsync(PostgreSqlTestServer server)
    {
        var databaseName = $"appsurface_durable_{Guid.NewGuid():N}";
        try
        {
            await using (var maintenance = new NpgsqlConnection(server.MaintenanceConnectionString))
            {
                await maintenance.OpenAsync();
                await using var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\";", maintenance);
                await create.ExecuteNonQueryAsync();
            }

            var sourceBuilder = new NpgsqlConnectionStringBuilder(server.ConnectionString)
            {
                Database = databaseName,
            };
            var dataSource = NpgsqlDataSource.Create(sourceBuilder.ConnectionString);
            return new PostgreSqlIntegrationTestDatabase(
                databaseName,
                server.MaintenanceConnectionString,
                sourceBuilder.ConnectionString,
                dataSource);
        }
        catch (NpgsqlException) when (server.Container is not null)
        {
            await InvalidateSharedContainerServerAsync(server);
            throw;
        }
    }

    private static async Task<PostgreSqlTestServer> GetSharedContainerServerAsync()
    {
        Task<PostgreSqlTestServer> serverTask;
        await SharedContainerServerGate.WaitAsync();
        try
        {
            serverTask = _sharedContainerServer ??= CreateSharedContainerServerAsync();
        }
        finally
        {
            SharedContainerServerGate.Release();
        }

        try
        {
            return await serverTask;
        }
        catch (SkipException)
        {
            // A caller explicitly opted out of Docker-backed integration tests; retain that result for this test host.
            throw;
        }
        catch
        {
            await SharedContainerServerGate.WaitAsync();
            try
            {
                if (ReferenceEquals(_sharedContainerServer, serverTask))
                {
                    _sharedContainerServer = null;
                }
            }
            finally
            {
                SharedContainerServerGate.Release();
            }

            throw;
        }
    }

    private static async Task InvalidateSharedContainerServerAsync(PostgreSqlTestServer server)
    {
        await SharedContainerServerGate.WaitAsync();
        try
        {
            if (_sharedContainerServer is { IsCompletedSuccessfully: true } sharedServer
                && ReferenceEquals(sharedServer.Result, server))
            {
                // Do not stop this container: other concurrently running databases may still use it.
                // A later caller instead starts a replacement server; the resource reaper cleans the old server at exit.
                _sharedContainerServer = null;
            }
        }
        finally
        {
            SharedContainerServerGate.Release();
        }
    }

    private static async Task<PostgreSqlTestServer> CreateSharedContainerServerAsync()
    {
        var container = new PostgreSqlBuilder(PostgreSqlTestContainerImage.Reference)
            .WithDatabase("appsurface_durable")
            .WithUsername("appsurface")
            .WithPassword("appsurface-test-password")
            // The Testcontainers resource reaper owns the process-lifetime server cleanup.
            .WithCleanUp(true)
            .Build();
        try
        {
            await container.StartAsync();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await container.DisposeAsync();
            throw CreateContainerPrerequisiteException(exception);
        }

        try
        {
            return await CreateServerAsync(container.GetConnectionString(), container);
        }
        catch
        {
            await container.DisposeAsync();
            throw;
        }
    }

    private static Task<PostgreSqlTestServer> CreateConfiguredServerAsync(string connectionString) =>
        CreateServerAsync(connectionString, container: null);

    private static async Task<PostgreSqlTestServer> CreateServerAsync(
        string connectionString,
        PostgreSqlContainer? container)
    {
        var maintenanceBuilder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = "postgres",
            Pooling = false,
        };
        if (container is null)
        {
            await VerifyServerAsync(maintenanceBuilder.ConnectionString, CancellationToken.None);
        }
        else
        {
            await ExecuteContainerStartupProbeAsync(
                cancellationToken => VerifyServerAsync(maintenanceBuilder.ConnectionString, cancellationToken));
        }

        return new PostgreSqlTestServer(connectionString, maintenanceBuilder.ConnectionString, container);
    }

    private static async ValueTask VerifyServerAsync(string maintenanceConnectionString, CancellationToken cancellationToken)
    {
        await using var maintenance = new NpgsqlConnection(maintenanceConnectionString);
        await maintenance.OpenAsync(cancellationToken);
        await EnsureRequiredServerVersionAsync(maintenance);
    }

    private static Exception CreateContainerPrerequisiteException(Exception exception)
    {
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
            return SkipException.ForSkip(prerequisite);
        }

        return new InvalidOperationException(
            $"{prerequisite} Set APPSURFACE_POSTGRES_TEST_ALLOW_SKIP=true only for an intentional local opt-out.",
            exception);
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
        await using var maintenance = new NpgsqlConnection(_maintenanceConnectionString);
        await maintenance.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE \"{_databaseName}\" WITH (FORCE);", maintenance);
        await drop.ExecuteNonQueryAsync();
    }

    private sealed record PostgreSqlTestServer(
        string ConnectionString,
        string MaintenanceConnectionString,
        // Retain the container instance until the test process and its resource-reaper session exit.
        PostgreSqlContainer? Container);
}
