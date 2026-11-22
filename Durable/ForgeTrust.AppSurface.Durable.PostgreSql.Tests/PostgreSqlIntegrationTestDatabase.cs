using ForgeTrust.AppSurface.PostgreSql.Testing;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit.Sdk;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

internal sealed partial class PostgreSqlIntegrationTestDatabase : IAsyncDisposable
{
    /// <summary>
    /// The inclusive PostgreSQL <c>server_version_num</c> floor accepted by integration tests.
    /// </summary>
    internal const int MinimumSupportedServerVersion = 160000;

    /// <summary>
    /// Human-readable form of <see cref="MinimumSupportedServerVersion"/>.
    /// </summary>
    internal const string MinimumSupportedServerVersionText = "PostgreSQL 16+";

    /// <summary>
    /// Tests whether a PostgreSQL numeric server version is supported.
    /// </summary>
    /// <remarks>
    /// PostgreSQL encodes major version 16 as <c>160000</c>. Later versions are accepted so the test boundary does not
    /// reject forward-compatible servers.
    /// </remarks>
    internal static Func<int, bool> IsSupportedServerVersion { get; } =
        static version => version >= MinimumSupportedServerVersion;
    private const int ContainerStartupProbeMaximumAttempts = 3;
    private static readonly TimeSpan ContainerStartupProbeRetryDelay = TimeSpan.FromMilliseconds(250);
    private static readonly SemaphoreSlim SharedContainerServerGate = new(1, 1);
    private readonly List<NpgsqlDataSource> _additionalDataSources = [];
    private readonly PostgreSqlDatabaseLease _lease;
    private readonly string _maintenanceConnectionString;
    private string? _executionClockReaderRole;
    private static Task<PostgreSqlTestServer>? _sharedContainerServer;

    private static readonly TimeSpan LifecycleBudget = TimeSpan.FromSeconds(60);
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly bool _isTemplate;
    private uint? _clockRoleOid;
    private uint? _clockFunctionOid;
    private bool _disposed;
    private bool _closing;
    private Task? _closeSources;

    /// <summary>Receives elapsed teardown time once, after the first attempt that acquired the lifecycle gate.</summary>
    internal Action<TimeSpan>? ObserveTeardown { get; set; }

    /// <summary>Releases the class fixture's case lease after owned-resource cleanup succeeds.
    /// A timed-out or failed cleanup leaves the callback registered for a safe retry.</summary>
    internal Action? AfterDispose { get; set; }

    private PostgreSqlIntegrationTestDatabase(
        PostgreSqlDatabaseLease lease,
        string maintenanceConnectionString,
        bool isTemplate)
    {
        _lease = lease;
        _isTemplate = isTemplate;
        _maintenanceConnectionString = maintenanceConnectionString;
        var settings = new NpgsqlConnectionStringBuilder(lease.ConnectionString);
        if (isTemplate)
        {
            settings.Pooling = false;
        }

        ConnectionString = settings.ConnectionString;
        DataSource = NpgsqlDataSource.Create(ConnectionString);
    }

    internal NpgsqlDataSource DataSource { get; }

    internal string ConnectionString { get; }

    private string _databaseName => _lease.Name;

    private uint _databaseOid => _lease.Oid;

    /// <summary>
    /// Sets this isolated database's authoritative execution time for integration tests.
    /// </summary>
    /// <remarks>
    /// The first call installs a private clock row and replaces the migration's production clock function with a
    /// fixture-only <c>SECURITY DEFINER</c> reader owned by a unique <c>NOLOGIN</c> role. That role can only read the
    /// clock row; the fixture's administrative data source alone can change it. Production processes and other test
    /// databases do not share this clock.
    /// </remarks>
    /// <param name="executionTimeUtc">The exact instant returned by the fixture clock.</param>
    internal async ValueTask SetExecutionTimeAsync(DateTimeOffset executionTimeUtc)
    {
        var instant = executionTimeUtc.ToUniversalTime();
        await _lifecycleGate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            await using var connection = await DataSource.OpenConnectionAsync();
            if (_executionClockReaderRole is null)
            {
                var roleName = $"appsurface_clock_reader_{Guid.NewGuid():N}";
                await using var transaction = await connection.BeginTransactionAsync();
                await VerifyConnectedIdentityAsync(connection, transaction, CancellationToken.None);
                await ExecuteAsync($"CREATE ROLE \"{roleName}\" NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;", transaction);
                await ExecuteAsync("CREATE SCHEMA appsurface_test_clock;", transaction);
                await ExecuteAsync($"GRANT USAGE, CREATE ON SCHEMA appsurface_durable TO {roleName};", transaction);
                await ExecuteAsync($"GRANT {roleName} TO CURRENT_USER;", transaction);
                await ExecuteAsync("REVOKE ALL ON SCHEMA appsurface_test_clock FROM PUBLIC;", transaction);
                await ExecuteAsync(
                    "CREATE TABLE appsurface_test_clock.execution_clock (singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton), clock_value timestamp with time zone NOT NULL);",
                    transaction);
                await using (var insert = new NpgsqlCommand(
                    "INSERT INTO appsurface_test_clock.execution_clock (singleton, clock_value) VALUES (true, @clock_value);",
                    connection,
                    transaction))
                {
                    insert.Parameters.AddWithValue("clock_value", instant);
                    await insert.ExecuteNonQueryAsync();
                }

                await ExecuteAsync($"GRANT USAGE ON SCHEMA appsurface_test_clock TO \"{roleName}\";", transaction);
                await ExecuteAsync($"GRANT SELECT ON appsurface_test_clock.execution_clock TO \"{roleName}\";", transaction);
                await ExecuteAsync(
                    "CREATE OR REPLACE FUNCTION appsurface_durable.work_execution_now() " +
                    "RETURNS timestamp with time zone LANGUAGE sql VOLATILE SECURITY DEFINER " +
                    "SET search_path = pg_catalog AS " +
                    "$$ SELECT clock_value FROM appsurface_test_clock.execution_clock WHERE singleton $$;",
                    transaction);
                await ExecuteAsync(
                    $"ALTER FUNCTION appsurface_durable.work_execution_now() OWNER TO \"{roleName}\";",
                    transaction);
                await ExecuteAsync("REVOKE ALL ON FUNCTION appsurface_durable.work_execution_now() FROM PUBLIC;", transaction);
                await ExecuteAsync($"REVOKE CREATE ON SCHEMA appsurface_durable FROM {roleName};", transaction);
                await ExecuteAsync($"REVOKE {roleName} FROM CURRENT_USER;", transaction);
                await using (var identity = new NpgsqlCommand(
                    "SELECT proowner, oid FROM pg_catalog.pg_proc WHERE oid = 'appsurface_durable.work_execution_now()'::regprocedure",
                    connection, transaction))
                await using (var reader = await identity.ExecuteReaderAsync())
                {
                    if (!await reader.ReadAsync())
                    {
                        throw new PostgreSqlFixtureException("Clock role and function identity capture failed.");
                    }

                    _clockRoleOid = reader.GetFieldValue<uint>(0);
                    _clockFunctionOid = reader.GetFieldValue<uint>(1);
                }
                _executionClockReaderRole = roleName;
                // Record the transactional role OID before COMMIT, whose acknowledgement can be lost.
                await transaction.CommitAsync();
            }
            else
            {
                await using var transaction = await connection.BeginTransactionAsync();
                await VerifyConnectedIdentityAsync(connection, transaction, CancellationToken.None);
                await using var update = new NpgsqlCommand(
                    "UPDATE appsurface_test_clock.execution_clock SET clock_value = @clock_value WHERE singleton;",
                    connection, transaction);
                update.Parameters.AddWithValue("clock_value", instant);
                if (await update.ExecuteNonQueryAsync() != 1)
                {
                    throw new InvalidOperationException("The isolated PostgreSQL execution clock row is missing.");
                }

                await transaction.CommitAsync();
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }

        async ValueTask ExecuteAsync(string sql, NpgsqlTransaction transaction)
        {
            await using var command = new NpgsqlCommand(sql, transaction.Connection, transaction);
            await command.ExecuteNonQueryAsync();
        }
    }

    /// <summary>Creates a separately owned data source for testing configuration that requires distinct instances.</summary>
    internal NpgsqlDataSource CreateDataSource()
    {
        lock (_additionalDataSources)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            var dataSource = NpgsqlDataSource.Create(ConnectionString);
            _additionalDataSources.Add(dataSource);
            return dataSource;
        }
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
    /// PostgreSQL server (16 or later) whose database can be changed to <c>postgres</c>, and its credentials must be
    /// allowed to create and drop databases.
    /// </remarks>
    internal static async ValueTask<PostgreSqlIntegrationTestDatabase> CreateFromConnectionStringAsync(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return await CreateDatabaseAsync(await CreateConfiguredServerAsync(connectionString));
    }

    private static async Task<PostgreSqlIntegrationTestDatabase> CreateDatabaseAsync(PostgreSqlTestServer server)
    {
        try
        {
            using var deadline = new CancellationTokenSource(LifecycleBudget);
            return await CreateRegisteredAsync(server, template: null, isTemplate: false, afterCreate: null,
                CancellationToken.None, deadline.Token);
        }
        catch (PostgreSqlFixtureException) when (server.Container is not null)
        {
            await InvalidateSharedContainerServerAsync(server);
            throw;
        }
    }

    private static async ValueTask<PostgreSqlIntegrationTestDatabase> CreateRegisteredAsync(
        PostgreSqlTestServer server,
        PostgreSqlIntegrationTestDatabase? template,
        bool isTemplate,
        Func<string, CancellationToken, ValueTask>? afterCreate,
        CancellationToken cancellationToken,
        CancellationToken deadline)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline);
        PostgreSqlDatabaseLease lease;
        try
        {
            if (template is not null)
            {
                await using var maintenance = new NpgsqlConnection(server.MaintenanceConnectionString);
                await maintenance.OpenAsync(operation.Token);
                await using var activeSessions = new NpgsqlCommand(
                    "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_stat_activity WHERE datid = @database_oid AND pid <> pg_backend_pid())",
                    maintenance);
                activeSessions.Parameters.AddWithValue("database_oid", NpgsqlTypes.NpgsqlDbType.Oid, template._lease.Oid);
                if ((bool)(await activeSessions.ExecuteScalarAsync(operation.Token))!)
                {
                    throw new PostgreSqlFixtureException(
                        $"Fixture server rejected CREATE DATABASE {template._databaseName} " +
                        $"(SQLSTATE {PostgresErrorCodes.ObjectInUse}). No ownership was registered. Check the template before retrying.");
                }
            }

            lease = template is null
                ? await PostgreSqlDatabaseLease.CreateAsync(
                    server.ConnectionString,
                    "appsurface_durable",
                    afterCreate,
                    operation.Token)
                : await template._lease.CloneAsync(
                    "appsurface_durable",
                    afterCreate,
                    operation.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            throw new PostgreSqlFixtureException("Fixture creation timed out within the lifecycle budget. Check the dedicated server before retrying.");
        }
        catch (PostgreSqlDatabaseCleanupException)
        {
            throw;
        }
        catch (PostgreSqlDatabaseOwnershipException ownershipError)
        {
            throw new PostgreSqlFixtureException(ownershipError.Message);
        }
        catch (PostgresException error) when (error.SqlState is
            PostgresErrorCodes.InsufficientPrivilege or PostgresErrorCodes.DuplicateDatabase or
            PostgresErrorCodes.ObjectInUse or PostgresErrorCodes.InvalidCatalogName)
        {
            throw new PostgreSqlFixtureException(
                $"Fixture server rejected database creation (SQLSTATE {error.SqlState}). No ownership was registered. " +
                "Check the server and template before retrying.");
        }
        catch (PostgreSqlFixtureException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new PostgreSqlFixtureException(
                $"Database creation failed ({PostgreSqlFixtureException.Describe(error)}). Check the fixture server.");
        }

        try
        {
            return new PostgreSqlIntegrationTestDatabase(lease, server.MaintenanceConnectionString, isTemplate);
        }
        catch (Exception setupError)
        {
            try
            {
                await lease.DisposeAsync();
            }
            catch (Exception cleanupError)
            {
                throw new PostgreSqlDatabaseCleanupException(lease, cleanupError, setupError);
            }

            throw new PostgreSqlFixtureException(
                $"Fixture data source creation failed ({PostgreSqlFixtureException.Describe(setupError)}). The owned database was removed.");
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
        PostgreSqlContainer? container,
        CancellationToken cancellationToken = default)
    {
        var maintenanceBuilder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = "postgres",
            Pooling = false,
        };
        if (container is null)
        {
            await VerifyServerAsync(maintenanceBuilder.ConnectionString, cancellationToken);
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
        await EnsureRequiredServerVersionAsync(maintenance, cancellationToken);
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
    /// <remarks>
    /// The probe runs at most three times. Each retry waits 250 milliseconds by default, and <paramref name="delayAsync"/>
    /// runs only between eligible failed attempts. An eligible failure is an <see cref="NpgsqlException"/> with a direct
    /// <see cref="TimeoutException"/> inner exception; every other exception propagates immediately. Cancellation from
    /// either <paramref name="probe"/> or the retry delay also propagates. Use this only for the initial, idempotent
    /// host-side probe of a newly started Testcontainers server, never for configured-server or database-creation work.
    /// </remarks>
    /// <param name="probe">The idempotent probe to execute with the supplied cancellation token.</param>
    /// <param name="delayAsync">Optional replacement for the default 250 millisecond retry delay, primarily for deterministic tests.</param>
    /// <param name="cancellationToken">Token supplied to the probe and delay that cancels pending retry work.</param>
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

    private static async ValueTask EnsureRequiredServerVersionAsync(NpgsqlConnection connection, CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand("SHOW server_version_num;", connection);
        var value = (string?)await command.ExecuteScalarAsync(cancellationToken);
        EnsureRequiredServerVersion(value);
    }

    /// <summary>
    /// Validates a PostgreSQL <c>server_version_num</c> value against the supported floor.
    /// </summary>
    /// <param name="value">The numeric version returned by PostgreSQL.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="value"/> is missing, malformed, or below PostgreSQL 16. Higher future versions are
    /// accepted.
    /// </exception>
    internal static void EnsureRequiredServerVersion(string? value)
    {
        if (!int.TryParse(value, out var version))
        {
            throw new InvalidOperationException(
                "Durable PostgreSQL integration tests require server_version_num >= "
                + $"{MinimumSupportedServerVersion} ({MinimumSupportedServerVersionText}) "
                + "for PostgreSQL 16 or newer. "
                + $"The server reported '{value ?? "<null>"}'.");
        }

        if (!IsSupportedServerVersion(version))
        {
            throw new InvalidOperationException(
                "Durable PostgreSQL integration tests require PostgreSQL 16 or newer, "
                + $"server_version_num >= {MinimumSupportedServerVersion}, but the server reported {version}. "
                + "Set APPSURFACE_POSTGRES_TEST_CONNECTION to PostgreSQL 16+ (server_version_num >= 160000) "
                + "or clear that variable to use the pinned Testcontainers image.");
        }
    }

    // The dedicated server has no independent administrators renaming resources between this check and the DDL.
    // Look up both directions so a registered OID surviving under another name is never classified as absent.
    private async ValueTask<bool> VerifyClockRoleAsync(NpgsqlConnection connection, CancellationToken token)
    {
        var name = _executionClockReaderRole!;
        var oid = _clockRoleOid!.Value;
        await using var cmd = new NpgsqlCommand(
            "SELECT (SELECT oid FROM pg_catalog.pg_roles WHERE rolname = @name), EXISTS(SELECT 1 FROM pg_catalog.pg_roles WHERE oid = @oid)", connection);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("oid", NpgsqlTypes.NpgsqlDbType.Oid, oid);
        await using var reader = await cmd.ExecuteReaderAsync(token);
        await reader.ReadAsync(token);
        var actual = reader.IsDBNull(0) ? (uint?)null : reader.GetFieldValue<uint>(0);
        if (actual == oid)
        {
            return true;
        }

        if (actual is null && !reader.GetBoolean(1))
        {
            return false;
        }

        throw IdentityError("role", name, oid, actual);
    }

    private static PostgreSqlFixtureException IdentityError(string kind, string name, uint? expected, uint? actual) =>
        new($"Fixture server {kind} {name} ownership mismatch (expected OID {expected}, observed {actual?.ToString() ?? "absent"}). " +
            "Cleanup incomplete. Preserve this resource and its dependencies for the disposable-server owner.");

    private async ValueTask VerifyConnectedIdentityAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await _lease.VerifyConnectedIdentityAsync(connection, transaction, cancellationToken);
    }

    public ValueTask DisposeAsync() => DisposeRegisteredAsync(null);

    /// <summary>Replaces the drop command for failure tests. Verified survivors can be retried with a fresh connection.</summary>
    internal ValueTask DisposeAsync(Func<NpgsqlConnection, ValueTask> dropDatabaseAsync)
    {
        ArgumentNullException.ThrowIfNull(dropDatabaseAsync);
        return DisposeRegisteredAsync(dropDatabaseAsync);
    }

    private async ValueTask DisposeRegisteredAsync(Func<NpgsqlConnection, ValueTask>? dropDatabaseAsync)
    {
        using var deadline = new CancellationTokenSource(LifecycleBudget);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            await _lifecycleGate.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            throw new PostgreSqlFixtureException($"Fixture database {_databaseName} expected OID {_databaseOid}: timed out waiting for active setup. " +
                "Cleanup incomplete. Reconcile the disposable server before retrying.");
        }

        Exception? cleanupFailure = null;
        try
        {
            if (_disposed)
            {
                return;
            }

            if (_lease.HasRegisteredClones)
            {
                throw new PostgreSqlFixtureException($"Template {_databaseName} still has registered clones. Release them before template teardown.");
            }

            lock (_additionalDataSources)
            {
                _closing = true;
                _closeSources ??= CloseSourcesAsync();
            }

            await _closeSources.WaitAsync(deadline.Token);
            if (_executionClockReaderRole is null)
            {
                await _lease.DropAsync(dropDatabaseAsync, deadline.Token);
            }
            else
            {
                await using var maintenance = new NpgsqlConnection(_maintenanceConnectionString);
                await maintenance.OpenAsync(deadline.Token);
                await _lease.ExistsAsync(maintenance, deadline.Token);
                await VerifyClockRoleAsync(maintenance, deadline.Token);

                Exception? dropError = null;
                try
                {
                    Func<NpgsqlConnection, ValueTask>? dropOnRoleConnection = dropDatabaseAsync is null
                        ? null
                        : _ => dropDatabaseAsync(maintenance);
                    await _lease.DropAsync(dropOnRoleConnection, deadline.Token);
                }
                catch (Exception error)
                {
                    dropError = error;
                    throw;
                }
                finally
                {
                    try
                    {
                        await ReleaseClockRoleAsync(maintenance, deadline.Token);
                    }
                    catch (Exception cleanupError) when (dropError is not null)
                    {
                        dropError.Data["ExecutionClockCleanupException"] = cleanupError;
                    }
                }
            }

            _disposed = true;
        }
        catch (Exception error)
        {
            cleanupFailure = error;
            var secondary = error.Data["ExecutionClockCleanupException"] is Exception roleError
                ? $" Role cleanup failed ({PostgreSqlFixtureException.Describe(roleError)})." : string.Empty;
            if (dropDatabaseAsync is not null || error is PostgreSqlDatabaseCleanupException ||
                error is PostgreSqlFixtureException && secondary.Length == 0)
            {
                throw;
            }

            if (error is PostgreSqlDatabaseOwnershipException && secondary.Length == 0)
            {
                cleanupFailure = new PostgreSqlFixtureException(
                    $"Fixture server database {_databaseName} expected OID {_databaseOid}: cleanup incomplete " +
                    $"({PostgreSqlFixtureException.Describe(error)}). Reconcile the dedicated server before retrying.");
                throw cleanupFailure;
            }

            cleanupFailure = new PostgreSqlFixtureException($"Fixture server database {_databaseName} expected OID {_databaseOid}: cleanup incomplete " +
                $"({PostgreSqlFixtureException.Describe(error)}).{secondary} Reconcile the dedicated server before retrying.");
            throw cleanupFailure;
        }
        finally
        {
            var timing = ObserveTeardown;
            var release = _disposed ? AfterDispose : null;
            ObserveTeardown = null;
            if (_disposed)
            {
                AfterDispose = null;
            }

            _lifecycleGate.Release();
            try
            {
                timing?.Invoke(System.Diagnostics.Stopwatch.GetElapsedTime(started));
            }
            catch (Exception timingError) when (cleanupFailure is not null)
            {
                throw new PostgreSqlFixtureException($"{PostgreSqlFixtureException.Describe(cleanupFailure)} " +
                    $"Timing output also failed ({PostgreSqlFixtureException.Describe(timingError)}). Retain the invalid run.");
            }
            finally
            {
                release?.Invoke();
            }
        }
    }

    private async ValueTask ReleaseClockRoleAsync(NpgsqlConnection maintenance, CancellationToken token)
    {
        // Recheck the target after DROP. Release only the recorded clock function and its case-local grants.
        var databaseExists = await _lease.ExistsAsync(maintenance, token);
        if (await VerifyClockRoleAsync(maintenance, token))
        {
            if (databaseExists && _clockFunctionOid is not null)
            {
                var settings = new NpgsqlConnectionStringBuilder(ConnectionString) { Pooling = false };
                await using var survivor = new NpgsqlConnection(settings.ConnectionString);
                await survivor.OpenAsync(token);
                await using var transaction = await survivor.BeginTransactionAsync(token);
                await VerifyConnectedIdentityAsync(survivor, transaction, token);
                await using var check = new NpgsqlCommand(
                    "SELECT oid FROM pg_catalog.pg_proc WHERE oid = pg_catalog.to_regprocedure('appsurface_durable.work_execution_now()') AND proowner = @role",
                    survivor, transaction);
                check.Parameters.AddWithValue("role", NpgsqlTypes.NpgsqlDbType.Oid, _clockRoleOid!.Value);
                var observedFunction = (uint?)await check.ExecuteScalarAsync(token);
                if (observedFunction != _clockFunctionOid)
                {
                    throw IdentityError("clock function", _databaseName, _clockFunctionOid, observedFunction);
                }

                await using var release = new NpgsqlCommand(
                    $"""
                    DROP FUNCTION appsurface_durable.work_execution_now();
                    REVOKE USAGE ON SCHEMA appsurface_durable, appsurface_test_clock FROM "{_executionClockReaderRole}";
                    REVOKE SELECT ON appsurface_test_clock.execution_clock FROM "{_executionClockReaderRole}";
                    """, survivor, transaction);
                await release.ExecuteNonQueryAsync(token);
                await transaction.CommitAsync(token);
                _clockFunctionOid = null;
            }

            await using var drop = new NpgsqlCommand($"DROP ROLE \"{_executionClockReaderRole}\"", maintenance);
            await drop.ExecuteNonQueryAsync(token);
            if (await VerifyClockRoleAsync(maintenance, token))
            {
                throw new PostgreSqlFixtureException("Clock role cleanup incomplete. Reconcile the dedicated server.");
            }
        }

        _executionClockReaderRole = null;
        _clockRoleOid = null;
        _clockFunctionOid = null;
    }

    private async Task CloseSourcesAsync()
    {
        foreach (var source in _additionalDataSources)
        {
            await source.DisposeAsync();
        }

        await DataSource.DisposeAsync();
    }

    private sealed record PostgreSqlTestServer(
        string ConnectionString,
        string MaintenanceConnectionString,
        // Retain the container instance until the test process and its resource-reaper session exit.
        PostgreSqlContainer? Container);
}

/// <summary>Safe test-fixture failure text. Raw connection strings, credentials and server detail stay out of TRX.</summary>
internal sealed class PostgreSqlFixtureException(string safeMessage) : InvalidOperationException(safeMessage)
{
    internal static string Describe(Exception error) => error switch
    {
        PostgreSqlFixtureException fixture => fixture.Message,
        PostgreSqlDatabaseOwnershipException ownership => ownership.Message,
        PostgresException postgres => $"PostgreSQL SQLSTATE {postgres.SqlState}",
        AggregateException aggregate => string.Join(", ", aggregate.InnerExceptions.Select(Describe)),
        _ => error.GetType().Name,
    };
}
