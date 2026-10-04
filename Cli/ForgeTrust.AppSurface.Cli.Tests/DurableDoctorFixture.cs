using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Cli;
using ForgeTrust.AppSurface.Durable.PostgreSql;
using ForgeTrust.AppSurface.Durable.Provider;
using ForgeTrust.AppSurface.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit.Sdk;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>
/// Owns a disposable schema-11 store configured by the canonical Durable role recipe.
/// </summary>
/// <remarks>
/// Catalog, service, command, and distribution proofs share this fixture so their observations use the same restricted
/// runtime/dispatcher/owner shape as the reviewed PostgreSQL preflight. Mutations run through the disposable server's
/// administrative connection; doctor observations always use <see cref="RuntimeConnectionString"/>. The fixture's
/// reference stale threshold is the provider's existing 15-second default, with no override. Its CLI gate serializes
/// AppSurfaceCliApp entrypoint calls because test hosts share environment variables, exit code, console streams, and
/// CliFx's primary service-provider slot process-wide.
/// </remarks>
internal sealed class DurableDoctorFixture : IAsyncDisposable
{
    internal const string RuntimeWorkerId = "doctor-fixture-801";
    internal const string ConnectionEnvironmentName = "APPSURFACE_DURABLE_CONNECTION";
    internal const string EpochEnvironmentName = "APPSURFACE_DURABLE_RUNTIME_EPOCH";
    internal const string PasswordSentinel = "durable-doctor-password-secret-sentinel";
    internal static readonly TimeSpan ReferenceStaleAfter = TimeSpan.FromSeconds(15);
    internal const int MinimumSupportedServerVersion = 160000;

    private const string PostgreSqlImage = "postgres:16.5@sha256:53f3e608f9475ce120ced2d0f430b89458d7faa28530e0b0977a6af64d294877";
    private const int ContainerStartupProbeMaximumAttempts = 3;
    private static readonly TimeSpan ContainerStartupProbeRetryDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ContainerStartupTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SetupCommandTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ExternalProcessShutdownTimeout = TimeSpan.FromSeconds(5);
    private static readonly SemaphoreSlim SharedServerGate = new(1, 1);
    private static readonly SemaphoreSlim CliEnvironmentGate = new(1, 1);
    private static Task<PostgreSqlTestServer>? _sharedServer;
    private readonly string _databaseName;
    private readonly string _ownerRole;
    private readonly string _dispatcherRole;
    private readonly string _runtimeRole;
    private readonly string _retentionRole;
    private readonly PostgreSqlTestServer _server;
    private readonly string _administrativeConnectionString;
    private readonly NpgsqlDataSource _administrativeDataSource;
    private bool _disposed;

    private DurableDoctorFixture(
        string databaseName,
        string ownerRole,
        string dispatcherRole,
        string runtimeRole,
        string retentionRole,
        PostgreSqlTestServer server,
        string administrativeConnectionString,
        NpgsqlDataSource administrativeDataSource)
    {
        _databaseName = databaseName;
        _ownerRole = ownerRole;
        _dispatcherRole = dispatcherRole;
        _runtimeRole = runtimeRole;
        _retentionRole = retentionRole;
        _server = server;
        _administrativeConnectionString = administrativeConnectionString;
        _administrativeDataSource = administrativeDataSource;
        RuntimeEpoch = Guid.NewGuid();
    }

    internal string OwnerRole => _ownerRole;

    internal string DispatcherRole => _dispatcherRole;

    internal string RuntimeRole => _runtimeRole;

    internal string RetentionRole => _retentionRole;

    internal string AdministrativeConnectionString => _administrativeConnectionString;

    internal string RuntimeConnectionString => ConnectionStringForRole(RuntimeRole);

    internal string DispatcherConnectionString => ConnectionStringForRole(DispatcherRole);

    internal Guid StoreId { get; private set; }

    internal Guid RuntimeEpoch { get; }

    internal static async Task<DurableDoctorFixture> CreateAsync()
    {
        var configured = Environment.GetEnvironmentVariable("APPSURFACE_POSTGRES_TEST_CONNECTION");
        var server = string.IsNullOrWhiteSpace(configured)
            ? await GetSharedServerAsync()
            : await CreateConfiguredServerAsync(configured);
        NpgsqlDataSource? administrativeDataSource = null;
        var databaseName = $"doctor_{Guid.NewGuid():N}";
        ValidateGeneratedIdentifier(databaseName);
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var ownerRole = $"doctor_owner_{suffix}";
        var dispatcherRole = $"doctor_dispatcher_{suffix}";
        var runtimeRole = $"doctor_runtime_{suffix}";
        var retentionRole = $"doctor_retention_{suffix}";
        foreach (var role in new[] { ownerRole, dispatcherRole, runtimeRole, retentionRole })
        {
            ValidateGeneratedIdentifier(role);
        }

        try
        {
            await CreateDatabaseAsync(server, databaseName);
            var databaseBuilder = new NpgsqlConnectionStringBuilder(server.ConnectionString)
            {
                Database = databaseName,
                Pooling = false,
                Multiplexing = false,
                Enlist = false,
                Timeout = 5,
                CommandTimeout = 15,
            };
            var administrativeConnectionString = databaseBuilder.ConnectionString;
            administrativeDataSource = NpgsqlDataSource.Create(databaseBuilder.ConnectionString);
            var fixture = new DurableDoctorFixture(
                databaseName,
                ownerRole,
                dispatcherRole,
                runtimeRole,
                retentionRole,
                server,
                administrativeConnectionString,
                administrativeDataSource);
            await fixture.InitializeAsync();
            return fixture;
        }
        catch
        {
            if (administrativeDataSource is not null)
            {
                await administrativeDataSource.DisposeAsync();
            }
            try
            {
                await DropDatabaseAndRolesAsync(server,
                    databaseName,
                    ownerRole, dispatcherRole, runtimeRole, retentionRole);
            }
            catch (Exception cleanupFailure)
            {
                throw new InvalidOperationException(
                    $"Durable doctor fixture setup failed and isolated database cleanup also failed ({cleanupFailure.GetType().Name}).");
            }
            throw;
        }
    }

    /// <summary>Builds the same printable request used by service and real-command fixture tests.</summary>
    internal DurableDoctorRequest CreateRequest(
        string? workerId = null,
        TimeSpan? staleAfter = null,
        string connectionEnvironmentName = ConnectionEnvironmentName,
        string epochEnvironmentName = EpochEnvironmentName,
        string format = "json",
        TimeSpan? timeout = null) => new(
            RuntimeEpoch,
            connectionEnvironmentName,
            epochEnvironmentName,
            workerId,
            staleAfter,
            timeout ?? TimeSpan.FromSeconds(30),
            format);

    /// <summary>Runs the concrete read-only operation with the fixture's restricted runtime credential.</summary>
    internal ValueTask<DurableDoctorObservation> InspectAsync(
        DurableDoctorRequest? request = null,
        CancellationToken cancellationToken = default) =>
        new DurableDoctorService().InspectAsync(RuntimeConnectionString, request ?? CreateRequest(), cancellationToken);

    /// <summary>Runs the doctor through command discovery, argument binding, and the configured CLI console.</summary>
    internal async Task<DurableDoctorCliRun> RunCliAsync(
        string[] arguments,
        string connectionEnvironmentName = ConnectionEnvironmentName,
        string epochEnvironmentName = EpochEnvironmentName,
        string? connectionString = null,
        Guid? configuredEpoch = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        await CliEnvironmentGate.WaitAsync();
        var originalConnectionEnvironment = Environment.GetEnvironmentVariable(connectionEnvironmentName);
        var originalEpochEnvironment = Environment.GetEnvironmentVariable(epochEnvironmentName);
        var originalStandardOutput = global::System.Console.Out;
        var originalStandardError = global::System.Console.Error;
        var originalExitCode = Environment.ExitCode;
        try
        {
            using var connectionEnvironment = new EnvironmentVariableScope(connectionEnvironmentName, connectionString ?? RuntimeConnectionString);
            using var epochEnvironment = new EnvironmentVariableScope(epochEnvironmentName, (configuredEpoch ?? RuntimeEpoch).ToString("D"));
            using var console = new FakeInMemoryConsole();
            try
            {
                Environment.ExitCode = 0;
                await AppSurfaceCliApp.RunAsync(arguments, options =>
                {
                    options.CustomRegistrations.Add(services => services.AddSingleton<IConsole>(console));
                });
                return new DurableDoctorCliRun(
                    console.ReadOutputString(),
                    console.ReadErrorString(),
                    Environment.ExitCode);
            }
            finally
            {
                Environment.ExitCode = originalExitCode;
            }
        }
        finally
        {
            try
            {
                if (Environment.GetEnvironmentVariable(connectionEnvironmentName) != originalConnectionEnvironment
                    || Environment.GetEnvironmentVariable(epochEnvironmentName) != originalEpochEnvironment
                    || !ReferenceEquals(global::System.Console.Out, originalStandardOutput)
                    || !ReferenceEquals(global::System.Console.Error, originalStandardError)
                    || Environment.ExitCode != originalExitCode)
                {
                    throw new InvalidOperationException("The serialized Durable doctor CLI fixture did not restore process-global state.");
                }
            }
            finally
            {
                CliEnvironmentGate.Release();
            }
        }
    }

    /// <summary>Applies setup-only catalog/data changes using the fixture administrator.</summary>
    internal async Task MutateAsync(string sql, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        await using var command = _administrativeDataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Seeds one payload-free heartbeat at a chosen relative age.</summary>
    internal async Task SeedHeartbeatAsync(
        string workerId = RuntimeWorkerId,
        TimeSpan? age = null,
        Guid? runtimeEpoch = null,
        bool draining = false,
        CancellationToken cancellationToken = default)
    {
        await using var command = _administrativeDataSource.CreateCommand("""
            INSERT INTO appsurface_durable.runtime_heartbeat
                (worker_id, worker_instance_id, runtime_epoch, hosted_surfaces, started_at, last_heartbeat_at, draining, updated_at)
            VALUES
                (@worker, @instance, @epoch, @surfaces, pg_catalog.clock_timestamp() - @age,
                 pg_catalog.clock_timestamp() - @age, @draining, pg_catalog.clock_timestamp())
            ON CONFLICT (worker_id) DO UPDATE
            SET worker_instance_id = EXCLUDED.worker_instance_id,
                runtime_epoch = EXCLUDED.runtime_epoch,
                hosted_surfaces = EXCLUDED.hosted_surfaces,
                started_at = EXCLUDED.started_at,
                last_heartbeat_at = EXCLUDED.last_heartbeat_at,
                draining = EXCLUDED.draining,
                updated_at = EXCLUDED.updated_at
            """);
        command.Parameters.AddWithValue("worker", workerId);
        command.Parameters.AddWithValue("instance", Guid.Parse("8b66e8fb-b6d4-4454-bec6-19c5d1e50111"));
        command.Parameters.AddWithValue("epoch", runtimeEpoch ?? RuntimeEpoch);
        command.Parameters.AddWithValue("surfaces", (short)DurableRuntimeSurface.Work);
        command.Parameters.AddWithValue("age", age ?? TimeSpan.Zero);
        command.Parameters.AddWithValue("draining", draining);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Reads a bounded hash of Durable table rows, sequences, schema identity and prune-function body.</summary>
    /// <remarks>
    /// The snapshot retains no row payload in its return value. It makes accidental doctor writes, sequence advances,
    /// epoch changes, and retention-function invocation visible to nonmutation proofs.
    /// </remarks>
    internal async Task<string> ReadDurableStateFingerprintAsync(CancellationToken cancellationToken = default)
    {
        var evidence = new StringBuilder();
        await using var connection = await _administrativeDataSource.OpenConnectionAsync(cancellationToken);
        var hasSchema = await ScalarBooleanAsync(
            connection,
            "SELECT pg_catalog.to_regnamespace('appsurface_durable') IS NOT NULL",
            cancellationToken);
        if (!hasSchema)
        {
            evidence.Append("schema:absent\n");
            return HashEvidence(evidence);
        }

        const string tablesSql = """
            SELECT relation.relname
            FROM pg_catalog.pg_class AS relation
            JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = relation.relnamespace
            WHERE namespace.nspname = 'appsurface_durable' AND relation.relkind IN ('r', 'p')
            ORDER BY relation.relname
            """;
        var tables = new List<string>();
        await using (var command = new NpgsqlCommand(tablesSql, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                tables.Add(reader.GetString(0));
            }
        }

        foreach (var table in tables)
        {
            evidence.Append("table:").Append(table).Append('\n');
            var quotedTable = QuoteIdentifier(table);
            var sql = $"SELECT pg_catalog.to_jsonb(row_value)::text FROM appsurface_durable.{quotedTable} AS row_value ORDER BY pg_catalog.to_jsonb(row_value)::text";
            await using var command = new NpgsqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                evidence.Append(reader.GetString(0)).Append('\n');
            }
        }

        await using (var structure = new NpgsqlCommand("""
            SELECT relation.relname,
                   relation.relkind::text,
                   pg_catalog.pg_get_userbyid(relation.relowner),
                   COALESCE(relation.relacl::text, '<default>'),
                   CASE WHEN relation.relkind IN ('i', 'I') THEN pg_catalog.pg_get_indexdef(relation.oid) ELSE '' END
            FROM pg_catalog.pg_class AS relation
            JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = relation.relnamespace
            WHERE namespace.nspname = 'appsurface_durable'
              AND relation.relkind IN ('r', 'p', 'i', 'I', 'S')
            ORDER BY relation.relname, relation.relkind
            """, connection))
        await using (var reader = await structure.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                evidence.Append("relation:").Append(reader.GetString(0)).Append(':')
                    .Append(reader.GetString(1)).Append(':').Append(reader.GetString(2)).Append(':')
                    .Append(reader.GetString(3)).Append(':').Append(reader.GetString(4)).Append('\n');
            }
        }

        const string sequencesSql = """
            SELECT relation.relname
            FROM pg_catalog.pg_class AS relation
            JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = relation.relnamespace
            WHERE namespace.nspname = 'appsurface_durable' AND relation.relkind = 'S'
            ORDER BY relation.relname
            """;
        var sequences = new List<string>();
        await using (var command = new NpgsqlCommand(sequencesSql, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                sequences.Add(reader.GetString(0));
            }
        }

        foreach (var sequence in sequences)
        {
            var quotedSequence = QuoteIdentifier(sequence);
            var sql = $"SELECT last_value::text, is_called FROM appsurface_durable.{quotedSequence}";
            await using var command = new NpgsqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                evidence.Append("sequence:").Append(sequence).Append(':')
                    .Append(reader.GetString(0)).Append(':').Append(reader.GetBoolean(1)).Append('\n');
            }
        }

        await using (var function = new NpgsqlCommand("""
            SELECT pg_catalog.pg_get_functiondef(routine.oid), COALESCE(routine.proacl::text, '<default>')
            FROM pg_catalog.pg_proc AS routine
            JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = routine.pronamespace
            WHERE namespace.nspname = 'appsurface_durable'
              AND routine.proname = 'prune_runtime_heartbeats'
            ORDER BY routine.oid
            """, connection))
        await using (var reader = await function.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                evidence.Append("function:").Append(reader.GetString(0)).Append(':').Append(reader.GetString(1)).Append('\n');
            }
        }

        var epoch = await ScalarTextAsync(
            connection,
            "SELECT store_id::text || ':' || COALESCE(active_runtime_epoch::text, 'null') FROM appsurface_durable.store_metadata WHERE singleton",
            cancellationToken);
        evidence.Append("identity:").Append(epoch ?? "missing").Append('\n');
        return HashEvidence(evidence);
    }

    /// <summary>Replaces prune's body with a trap that fails if a doctor executes it.</summary>
    internal Task TrapPruneFunctionBodyAsync(CancellationToken cancellationToken = default) => MutateAsync("""
        CREATE OR REPLACE FUNCTION appsurface_durable.prune_runtime_heartbeats(
            p_retention interval,
            p_maximum_rows integer,
            p_current_worker_id text,
            p_current_worker_instance_id uuid)
        RETURNS integer
        LANGUAGE plpgsql
        SECURITY DEFINER
        SET search_path = pg_catalog, appsurface_durable, pg_temp
        AS $$
        BEGIN
            RAISE EXCEPTION 'durable-doctor-prune-invocation-sentinel';
        END;
        $$;
        """, cancellationToken);

    /// <summary>Returns the selected worker row as bounded facts for captured-clock assertions.</summary>
    internal async Task<DurableDoctorHeartbeat?> ReadHeartbeatAsync(
        string workerId = RuntimeWorkerId,
        CancellationToken cancellationToken = default)
    {
        await using var command = _administrativeDataSource.CreateCommand("""
            SELECT runtime_epoch, last_heartbeat_at, draining
            FROM appsurface_durable.runtime_heartbeat
            WHERE worker_id = @worker
            """);
        command.Parameters.AddWithValue("worker", workerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        return new DurableDoctorHeartbeat(true, reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetBoolean(2));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        await _administrativeDataSource.DisposeAsync();
        await DropDatabaseAndRolesAsync(
            _server,
            _databaseName,
            _ownerRole,
            _dispatcherRole,
            _runtimeRole,
            _retentionRole);
    }

    private async Task InitializeAsync()
    {
        var schemaManager = new PostgreSqlDurableRuntimeSchemaManager(_administrativeDataSource);
        await schemaManager.ApplyAsync();
        var roleNames = new[] { _ownerRole, _retentionRole, _dispatcherRole, _runtimeRole };
        var passwordLiteral = "'" + PasswordSentinel.Replace("'", "''", StringComparison.Ordinal) + "'";
        var roleSql = string.Join(';', roleNames.Select(role =>
            $"CREATE ROLE {QuoteIdentifier(role)} LOGIN PASSWORD {passwordLiteral} NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS"));
        await using (var command = _administrativeDataSource.CreateCommand(roleSql))
        {
            await command.ExecuteNonQueryAsync();
        }

        var manifest = JsonSerializer.Serialize(new
        {
            version = 1,
            pairs = new[]
            {
                new { dispatcher = _dispatcherRole, runtime = _runtimeRole, dispatcher_profile = "full" },
            },
        });
        var recipeArguments = new[]
        {
            "-v", $"migration_owner_role={_ownerRole}",
            "-v", $"retention_operator_role={_retentionRole}",
            "-v", $"role_pairs_json={manifest}",
        };
        if (_server.Container is { } container)
        {
            await ExecuteContainerRoleRecipeAsync(container, _databaseName, recipeArguments);
        }
        else
        {
            var repositoryRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
            var recipePath = TestPathUtils.PathUnder(repositoryRoot, "Durable", "configure-postgresql-roles.sql");
            await ExecuteHostRoleRecipeAsync(AdministrativeConnectionString, recipePath, recipeArguments);
        }

        await schemaManager.InitializeRuntimeEpochAsync(RuntimeEpoch, RuntimeWorkerId, "integration-test");
        var status = await schemaManager.GetStatusAsync();
        if (!status.IsCompatible || status.StoreId == Guid.Empty || status.ActiveRuntimeEpoch != RuntimeEpoch)
        {
            throw new InvalidOperationException("The canonical doctor store did not reach its expected schema and epoch state.");
        }
        StoreId = status.StoreId;
    }

    private string ConnectionStringForRole(string role) => new NpgsqlConnectionStringBuilder(AdministrativeConnectionString)
    {
        Username = role,
        Password = PasswordSentinel,
        Pooling = false,
        Multiplexing = false,
        Enlist = false,
        MaxPoolSize = 1,
        Timeout = 5,
        CommandTimeout = 10,
    }.ConnectionString;

    private static async Task<bool> ScalarBooleanAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<string?> ScalarTextAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }

    private static string HashEvidence(StringBuilder evidence) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(evidence.ToString()))).ToLowerInvariant();

    private static async Task<PostgreSqlTestServer> GetSharedServerAsync()
    {
        Task<PostgreSqlTestServer> serverTask;
        await SharedServerGate.WaitAsync();
        try
        {
            serverTask = _sharedServer ??= CreateSharedServerAsync();
        }
        finally
        {
            SharedServerGate.Release();
        }

        try
        {
            return await serverTask;
        }
        catch (SkipException)
        {
            throw;
        }
        catch
        {
            await SharedServerGate.WaitAsync();
            try
            {
                if (ReferenceEquals(_sharedServer, serverTask))
                {
                    _sharedServer = null;
                }
            }
            finally
            {
                SharedServerGate.Release();
            }

            throw;
        }
    }

    private static async Task<PostgreSqlTestServer> CreateSharedServerAsync()
    {
        var repositoryRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var recipePath = TestPathUtils.PathUnder(repositoryRoot, "Durable", "configure-postgresql-roles.sql");
        var container = new PostgreSqlBuilder(PostgreSqlImage)
            .WithDatabase("appsurface_durable")
            .WithUsername("appsurface")
            .WithPassword("appsurface-test-password")
            .WithResourceMapping(recipePath, "/tmp")
            .WithCleanUp(true)
            .Build();

        try
        {
            using var startup = new CancellationTokenSource(ContainerStartupTimeout);
            await container.StartAsync(startup.Token);
            var connectionString = container.GetConnectionString();
            var server = BuildTestServer(connectionString, container);
            await ExecuteContainerStartupProbeAsync(
                token => VerifyServerAsync(server.MaintenanceConnectionString, token));
            return server;
        }
        catch (Exception exception)
        {
            try
            {
                await container.DisposeAsync();
            }
            catch
            {
                // Keep the bounded prerequisite or setup failure as the useful error.
            }

            if (exception is SkipException)
            {
                throw;
            }

            if (IsLocalSkipAllowed())
            {
                throw SkipException.ForSkip(
                    "Real PostgreSQL doctor integration tests need an available Docker daemon or APPSURFACE_POSTGRES_TEST_CONNECTION.");
            }

            throw new InvalidOperationException(
                $"Unable to prepare the pinned PostgreSQL 16 doctor test server ({exception.GetType().Name}).");
        }
    }

    private static async Task<PostgreSqlTestServer> CreateConfiguredServerAsync(string connectionString)
    {
        var server = BuildTestServer(connectionString, container: null);
        await VerifyServerAsync(server.MaintenanceConnectionString, CancellationToken.None);
        return server;
    }

    private static PostgreSqlTestServer BuildTestServer(string connectionString, PostgreSqlContainer? container)
    {
        var connectionBuilder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            Multiplexing = false,
            Enlist = false,
            Timeout = 5,
            CommandTimeout = 10,
        };
        var maintenanceBuilder = new NpgsqlConnectionStringBuilder(connectionBuilder.ConnectionString)
        {
            Database = "postgres",
        };
        return new PostgreSqlTestServer(connectionBuilder.ConnectionString, maintenanceBuilder.ConnectionString, container);
    }

    private static async Task CreateDatabaseAsync(PostgreSqlTestServer server, string databaseName)
    {
        ValidateGeneratedIdentifier(databaseName);
        try
        {
            await using var maintenance = new NpgsqlConnection(server.MaintenanceConnectionString);
            await maintenance.OpenAsync();
            await using var command = new NpgsqlCommand(
                $"CREATE DATABASE {QuoteIdentifier(databaseName)}",
                maintenance);
            await command.ExecuteNonQueryAsync();
        }
        catch (NpgsqlException) when (server.Container is not null)
        {
            await InvalidateSharedServerAsync(server);
            throw;
        }
    }

    private static async Task InvalidateSharedServerAsync(PostgreSqlTestServer server)
    {
        await SharedServerGate.WaitAsync();
        try
        {
            if (_sharedServer is { IsCompletedSuccessfully: true } sharedTask
                && ReferenceEquals(sharedTask.Result, server))
            {
                // Another fixture may still own a database here; let Testcontainers reap the old shared server at exit.
                _sharedServer = null;
            }
        }
        finally
        {
            SharedServerGate.Release();
        }
    }

    private static async Task VerifyServerAsync(string maintenanceConnectionString, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(maintenanceConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SHOW server_version_num", connection);
        var value = (string?)await command.ExecuteScalarAsync(cancellationToken);
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            || version < MinimumSupportedServerVersion)
        {
            throw new InvalidOperationException("Durable doctor integration tests require PostgreSQL 16 or newer (server_version_num >= 160000).");
        }
    }

    private static async Task ExecuteContainerStartupProbeAsync(
        Func<CancellationToken, Task> probe,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; attempt <= ContainerStartupProbeMaximumAttempts; attempt++)
        {
            try
            {
                await probe(cancellationToken);
                return;
            }
            catch (NpgsqlException exception) when (
                exception.InnerException is TimeoutException && attempt < ContainerStartupProbeMaximumAttempts)
            {
                await Task.Delay(ContainerStartupProbeRetryDelay, cancellationToken);
            }
        }
    }

    private static async Task ExecuteContainerRoleRecipeAsync(
        PostgreSqlContainer container,
        string databaseName,
        IReadOnlyList<string> recipeArguments)
    {
        using var deadline = new CancellationTokenSource(SetupCommandTimeout);
        var arguments = new List<string> { "psql", "-X", "-w", "-v", "ON_ERROR_STOP=1", "-U", "appsurface", "-d", databaseName };
        arguments.AddRange(recipeArguments);
        arguments.AddRange(["-f", "/tmp/configure-postgresql-roles.sql"]);
        var result = await container.ExecAsync(arguments, deadline.Token);
        if (result.ExitCode != 0)
        {
            await CaptureRoleRecipeFailureAsync(container, databaseName, result);
            throw new InvalidOperationException("The canonical Durable PostgreSQL role recipe failed.");
        }
    }

    private static async Task CaptureRoleRecipeFailureAsync(
        PostgreSqlContainer container,
        string databaseName,
        DotNet.Testcontainers.Containers.ExecResult result)
    {
        var diagnosticPath = Environment.GetEnvironmentVariable("APPSURFACE_DOCTOR_RECIPE_DIAGNOSTICS_PATH");
        if (string.IsNullOrWhiteSpace(diagnosticPath))
        {
            return;
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var expectedPath = await container.ExecAsync(
            ["stat", "-c", "%F|%a|%u:%g|%n", "/tmp/configure-postgresql-roles.sql"],
            deadline.Token);
        var nestedPath = await container.ExecAsync(
            ["stat", "-c", "%F|%a|%u:%g|%n", "/tmp/configure-postgresql-roles.sql/configure-postgresql-roles.sql"],
            deadline.Token);
        var output = new StringBuilder()
            .AppendLine("Durable doctor role-recipe diagnostic (synthetic fixture only)")
            .Append("database=").AppendLine(databaseName)
            .Append("recipe_exit_code=").AppendLine($"{result.ExitCode}")
            .AppendLine("expected_path_stat:")
            .AppendLine(SanitizeRecipeDiagnostic(expectedPath.Stdout))
            .AppendLine(SanitizeRecipeDiagnostic(expectedPath.Stderr))
            .AppendLine("nested_path_stat:")
            .AppendLine(SanitizeRecipeDiagnostic(nestedPath.Stdout))
            .AppendLine(SanitizeRecipeDiagnostic(nestedPath.Stderr))
            .AppendLine("psql_stdout:")
            .AppendLine(SanitizeRecipeDiagnostic(result.Stdout))
            .AppendLine("psql_stderr:")
            .AppendLine(SanitizeRecipeDiagnostic(result.Stderr))
            .ToString();
        var fullPath = Path.GetFullPath(diagnosticPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, output, Encoding.UTF8, deadline.Token);
    }

    private static string SanitizeRecipeDiagnostic(string? value) =>
        (value ?? string.Empty)
            .Replace(PasswordSentinel, "<redacted>", StringComparison.Ordinal)
            .Replace("appsurface-test-password", "<redacted>", StringComparison.Ordinal);

    private static async Task ExecuteHostRoleRecipeAsync(
        string connectionString,
        string recipePath,
        IReadOnlyList<string> recipeArguments)
    {
        var settings = new NpgsqlConnectionStringBuilder(connectionString);
        var start = new ProcessStartInfo
        {
            FileName = "psql",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "-X", "-w", "-v", "ON_ERROR_STOP=1" })
        {
            start.ArgumentList.Add(argument);
        }
        foreach (var argument in recipeArguments)
        {
            start.ArgumentList.Add(argument);
        }
        start.ArgumentList.Add("--file");
        start.ArgumentList.Add(recipePath);
        start.Environment["PGHOST"] = settings.Host;
        start.Environment["PGPORT"] = settings.Port.ToString(CultureInfo.InvariantCulture);
        start.Environment["PGUSER"] = settings.Username;
        start.Environment["PGDATABASE"] = settings.Database;
        start.Environment["PGPASSWORD"] = settings.Password;
        start.Environment["PGCONNECT_TIMEOUT"] = Math.Max(1, settings.Timeout).ToString(CultureInfo.InvariantCulture);
        start.Environment["PGSSLMODE"] = settings.SslMode switch
        {
            SslMode.Disable => "disable",
            SslMode.Allow => "allow",
            SslMode.Prefer => "prefer",
            SslMode.Require => "require",
            SslMode.VerifyCA => "verify-ca",
            SslMode.VerifyFull => "verify-full",
            _ => "prefer",
        };
        using var deadline = new CancellationTokenSource(SetupCommandTimeout);
        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("The local psql process did not start.");
            }

            var drainOutput = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
            var drainError = process.StandardError.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            await Task.WhenAll(drainOutput, drainError).WaitAsync(ExternalProcessShutdownTimeout);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException("The canonical Durable PostgreSQL role recipe failed.");
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(ExternalProcessShutdownTimeout);
                }
            }
            catch
            {
                // Report the bounded command timeout below; no child is allowed to outlive fixture setup.
            }
            throw new InvalidOperationException("The canonical Durable PostgreSQL role recipe exceeded its setup deadline.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException("A local psql executable is required when APPSURFACE_POSTGRES_TEST_CONNECTION is configured.");
        }
    }

    private static async Task DropDatabaseAndRolesAsync(
        PostgreSqlTestServer server,
        string databaseName,
        string ownerRole,
        string dispatcherRole,
        string runtimeRole,
        string retentionRole)
    {
        ValidateGeneratedIdentifier(databaseName);
        var roleNames = new[] { ownerRole, dispatcherRole, runtimeRole, retentionRole };
        foreach (var role in roleNames)
        {
            ValidateGeneratedIdentifier(role);
        }

        await using var maintenance = new NpgsqlConnection(server.MaintenanceConnectionString);
        await maintenance.OpenAsync();
        var database = QuoteIdentifier(databaseName);
        await using (var dropDatabase = new NpgsqlCommand($"DROP DATABASE IF EXISTS {database} WITH (FORCE)", maintenance))
        {
            await dropDatabase.ExecuteNonQueryAsync();
        }
        var roles = string.Join(", ", roleNames
            .Select(QuoteIdentifier));
        await using var dropRoles = new NpgsqlCommand($"DROP ROLE IF EXISTS {roles}", maintenance);
        await dropRoles.ExecuteNonQueryAsync();
    }

    private static bool IsLocalSkipAllowed() =>
        string.Equals(Environment.GetEnvironmentVariable("APPSURFACE_POSTGRES_TEST_ALLOW_SKIP"), "true", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase);

    private static void ValidateGeneratedIdentifier(string value)
    {
        if (value.Length is < 1 or > 63 || value.Any(character =>
                character is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '_'))
        {
            throw new InvalidOperationException("The generated PostgreSQL fixture identifier is outside its fixed grammar.");
        }
    }

    private static string QuoteIdentifier(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new NpgsqlCommandBuilder().QuoteIdentifier(value);
    }
}

internal sealed record PostgreSqlTestServer(
    string ConnectionString,
    string MaintenanceConnectionString,
    PostgreSqlContainer? Container);

internal sealed record DurableDoctorCliRun(string StandardOutput, string StandardError, int ExitCode);
