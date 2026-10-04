using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Images;
using ForgeTrust.AppSurface.Durable.PostgreSql;
using Npgsql;
using NpgsqlTypes;
using Testcontainers.PostgreSql;

namespace DurableWorkerTemplate.Tests;

internal sealed class PostgreSqlFixture : IAsyncDisposable
{
    private const string RoleRecipeFileName = "configure-postgresql-roles.sql";
    private const int MaximumRoleRecipeBytes = 1024 * 1024;
    private readonly List<NpgsqlDataSource> _dataSources = [];
    private readonly bool _ownsContainer;
    private readonly bool _ownsNativeDatabase;
    private readonly string? _nativePsqlPath;
    private readonly string _databaseName;
    private readonly string _bootstrapConnectionString;
    private string _fixtureAdministratorConnectionString;
    private readonly TimeSpan _setupBudget;
    private readonly long _setupStartedAt;
    private readonly SetupOperationLifetime _setupOperations = new();
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..12];
    private PostgreSqlContainer? _container;
    private NpgsqlDataSource? _administrator;
    private NpgsqlDataSource? _migrationOwner;
    private NpgsqlDataSource? _dispatcher;
    private NpgsqlDataSource? _runtime;
    private int _disposeStarted;
    private int _containerForceRemoved;

    private PostgreSqlFixture(
        string databaseName,
        string bootstrapConnectionString,
        string fixtureAdministratorConnectionString,
        bool ownsContainer,
        bool ownsNativeDatabase,
        string? nativePsqlPath,
        TimeSpan setupBudget)
    {
        _databaseName = databaseName;
        _bootstrapConnectionString = bootstrapConnectionString;
        _fixtureAdministratorConnectionString = fixtureAdministratorConnectionString;
        _ownsContainer = ownsContainer;
        _ownsNativeDatabase = ownsNativeDatabase;
        _nativePsqlPath = nativePsqlPath;
        _setupBudget = setupBudget;
        _setupStartedAt = Stopwatch.GetTimestamp();
        OwnerRole = $"tpl_owner_{_suffix}";
        DispatcherRole = $"tpl_dispatch_{_suffix}";
        RuntimeRole = $"tpl_runtime_{_suffix}";
        RetentionRole = $"tpl_retention_{_suffix}";
        OwnerPassword = NewPassword();
        DispatcherPassword = NewPassword();
        RuntimePassword = NewPassword();
        RetentionPassword = NewPassword();
        Epoch = Guid.NewGuid();
    }

    internal Guid Epoch { get; }

    internal Guid StoreId { get; private set; }

    internal string OwnerRole { get; }

    internal string DispatcherRole { get; }

    internal string RuntimeRole { get; }

    internal string RetentionRole { get; }

    private string OwnerPassword { get; }

    private string DispatcherPassword { get; }

    private string RuntimePassword { get; }

    private string RetentionPassword { get; }

    internal NpgsqlDataSource Administrator => _administrator
        ?? throw new InvalidOperationException("The PostgreSQL fixture is not provisioned.");

    internal NpgsqlDataSource Dispatcher => _dispatcher
        ?? throw new InvalidOperationException("The PostgreSQL fixture is not provisioned.");

    internal NpgsqlDataSource Runtime => _runtime
        ?? throw new InvalidOperationException("The PostgreSQL fixture is not provisioned.");

    internal string DispatcherConnectionString => ConnectionFor(DispatcherRole, DispatcherPassword);

    internal string RuntimeConnectionString => ConnectionFor(RuntimeRole, RuntimePassword);

    internal string NativeConnectionString => ConnectionFor("", "");

    internal string DatabaseIdentitySha256 => Convert.ToHexString(
        SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(_databaseName)));

    internal string RoleCatalogBeforeHost { get; private set; } = string.Empty;

    internal string DurableCatalogBeforeHost { get; private set; } = string.Empty;

    internal static async Task<PostgreSqlFixture> StartDockerAsync()
    {
        await RequireDockerAsync().ConfigureAwait(false);
        await AcquirePinnedImageAsync().ConfigureAwait(false);

        var database = $"appsurface_{Guid.NewGuid():N}";
        var password = NewPassword();
        var bootstrapConnection = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Database = database,
            Username = "appsurface",
            Password = password,
            Pooling = false,
            Timeout = 5,
            CommandTimeout = 5,
        }.ConnectionString;
        var fixture = new PostgreSqlFixture(
            database,
            bootstrapConnection,
            bootstrapConnection,
            ownsContainer: true,
            ownsNativeDatabase: false,
            nativePsqlPath: null,
            FixtureBudgets.DatabaseSetup);
        fixture._container = new PostgreSqlBuilder(PostgreSqlTestContainerImage.Reference)
            .WithDatabase(database)
            .WithUsername("appsurface")
            .WithPassword(password)
            .WithImagePullPolicy(PullPolicy.Never)
            .WithCleanUp(true)
            .Build();

        using var setupBudget = new CancellationTokenSource(FixtureBudgets.DatabaseSetup);
        try
        {
            await fixture._setupOperations.AwaitAsync(
                "container-start",
                () => fixture._container.StartAsync(setupBudget.Token),
                setupBudget.Token).ConfigureAwait(false);
            var actualConnection = fixture._container.GetConnectionString();
            fixture._fixtureAdministratorConnectionString = actualConnection;
            fixture._administrator = fixture.AddDataSource(actualConnection);
            await fixture._setupOperations.AwaitAsync(
                "fixture-provisioning",
                () => fixture.ProvisionAsync(setupBudget.Token),
                setupBudget.Token).ConfigureAwait(false);
            return fixture;
        }
        catch (OperationCanceledException) when (setupBudget.IsCancellationRequested)
        {
            await fixture.DisposeAfterSetupFailureAsync().ConfigureAwait(false);
            throw new TimeoutException("PostgreSQL container readiness and fixture provisioning exceeded the 40-second phase budget.");
        }
        catch (Exception exception)
        {
            await fixture.DisposeAfterSetupFailureAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"PostgreSQL fixture setup failed in its owned phase ({exception.GetType().Name}).");
        }
    }

    internal static async Task<PostgreSqlFixture> StartNativeAsync(
        string adminConnectionString,
        string pgBinDirectory,
        TimeSpan remainingSetupBudget,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adminConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(pgBinDirectory);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(remainingSetupBudget, TimeSpan.Zero);
        var resolvedBinDirectory = Path.GetFullPath(pgBinDirectory);
        if (!Directory.Exists(resolvedBinDirectory))
        {
            throw new InvalidOperationException("The resolved native PostgreSQL bin directory is missing.");
        }

        var resolvedPsql = Path.Combine(resolvedBinDirectory, OperatingSystem.IsWindows() ? "psql.exe" : "psql");
        if (!File.Exists(resolvedPsql))
        {
            throw new InvalidOperationException("The resolved native PostgreSQL psql executable is missing.");
        }

        var database = $"appsurface_{Guid.NewGuid():N}";
        var fixtureConnection = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Database = database,
            Pooling = false,
            Timeout = 5,
            CommandTimeout = 5,
        };
        var fixture = new PostgreSqlFixture(
            database,
            adminConnectionString,
            fixtureConnection.ConnectionString,
            ownsContainer: false,
            ownsNativeDatabase: true,
            resolvedPsql,
            remainingSetupBudget);
        using var setupDeadline = new CancellationTokenSource(remainingSetupBudget);
        using var setupCancellation = CancellationTokenSource.CreateLinkedTokenSource(setupDeadline.Token, cancellationToken);
        try
        {
            await fixture._setupOperations.AwaitAsync(
                "native-database-create",
                () => CreateOwnedNativeDatabaseAsync(adminConnectionString, database, setupCancellation.Token),
                setupCancellation.Token).ConfigureAwait(false);
            fixture._administrator = fixture.AddDataSource(fixtureConnection.ConnectionString);
            await fixture._setupOperations.AwaitAsync(
                "native-fixture-provisioning",
                () => fixture.ProvisionAsync(setupCancellation.Token),
                setupCancellation.Token).ConfigureAwait(false);
            return fixture;
        }
        catch (OperationCanceledException) when (setupDeadline.IsCancellationRequested)
        {
            await fixture.DisposeAfterSetupFailureAsync().ConfigureAwait(false);
            throw new TimeoutException(
                "Native PostgreSQL database creation and fixture provisioning exceeded the supplied remaining setup budget.");
        }
        catch (Exception exception)
        {
            await fixture.DisposeAfterSetupFailureAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"Native PostgreSQL fixture setup failed in its owned phase ({exception.GetType().Name}).");
        }
    }

    private static async Task CreateOwnedNativeDatabaseAsync(
        string adminConnectionString,
        string databaseName,
        CancellationToken cancellationToken)
    {
        await using var bootstrap = NpgsqlDataSource.Create(adminConnectionString);
        await using var command = bootstrap.CreateCommand("SELECT format('CREATE DATABASE %I', @database_name);");
        command.Parameters.AddWithValue("database_name", databaseName);
        var createSql = Assert.IsType<string>(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        await using var create = bootstrap.CreateCommand(createSql);
        await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task AssertDispatcherCannotReadWorkPayloadAsync(CancellationToken cancellationToken = default)
    {
        await using var command = Dispatcher.CreateCommand("SELECT count(*) FROM appsurface_durable.work;");
        try
        {
            _ = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            return;
        }

        throw new InvalidOperationException("The work_only dispatcher credential unexpectedly read the Work payload table.");
    }

    internal async Task<string> ReadDurableCatalogAsync(CancellationToken cancellationToken = default)
    {
        const string query = """
            SELECT md5(COALESCE((
                SELECT jsonb_agg(jsonb_build_array(c.relname,c.relkind,c.relowner,c.relacl,c.relrowsecurity,c.relforcerowsecurity)
                                 ORDER BY c.relname)::text
                FROM pg_catalog.pg_class c
                JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
                WHERE n.nspname='appsurface_durable'), '[]')
                || E'\\n' || COALESCE((
                SELECT jsonb_agg(jsonb_build_array(p.proname,p.proowner,p.proacl,pg_catalog.pg_get_functiondef(p.oid))
                                 ORDER BY p.proname,p.oid)::text
                FROM pg_catalog.pg_proc p
                JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
                WHERE n.nspname='appsurface_durable'), '[]'));
            """;
        await using var command = Administrator.CreateCommand(query);
        return Assert.IsType<string>(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    internal async Task<string> ReadRoleCatalogAsync(CancellationToken cancellationToken = default)
    {
        await using var command = Administrator.CreateCommand("""
            SELECT COALESCE(jsonb_agg(
                jsonb_build_array(r.rolname,r.rolcanlogin,r.rolsuper,r.rolcreatedb,r.rolcreaterole,r.rolreplication,
                                  r.rolbypassrls,r.rolinherit,r.rolconnlimit,r.rolvaliduntil,
                                  (SELECT jsonb_agg(jsonb_build_array(m.roleid,m.member,m.admin_option) ORDER BY m.roleid,m.member)
                                   FROM pg_catalog.pg_auth_members m WHERE m.roleid=r.oid OR m.member=r.oid))
                ORDER BY r.rolname)::text, '[]')
            FROM pg_catalog.pg_roles r
            WHERE r.rolname = ANY(@role_names);
            """);
        command.Parameters.AddWithValue("role_names", NpgsqlDbType.Array | NpgsqlDbType.Text,
            new[] { OwnerRole, DispatcherRole, RuntimeRole, RetentionRole });
        return Assert.IsType<string>(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeWithinBudgetAsync(FixtureBudgets.Cleanup).ConfigureAwait(false);
    }

    internal async Task DisposeWithinBudgetAsync(TimeSpan totalBudget)
    {
        if (totalBudget <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(totalBudget));
        }
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        var failures = new List<string>();
        var startedAt = Stopwatch.GetTimestamp();
        var setupOperationsSettled = await StopAndObservePendingSetupOperationsAsync(startedAt, totalBudget, failures)
            .ConfigureAwait(false);

        foreach (var source in _dataSources.AsEnumerable().Reverse())
        {
            await AttemptCleanupAsync(
                () => source.DisposeAsync().AsTask(),
                "data-source",
                startedAt,
                totalBudget,
                failures).ConfigureAwait(false);
        }

        if (_ownsNativeDatabase && setupOperationsSettled)
        {
            await AttemptCleanupAsync(
                DropOwnedNativeDatabaseAndRolesAsync,
                "native-database-and-roles",
                startedAt,
                totalBudget,
                failures)
                .ConfigureAwait(false);
        }
        else if (_ownsNativeDatabase)
        {
            failures.Add("native-database-retained-while-setup-operation-remains-active");
        }

        if (_ownsContainer && _container is not null && Volatile.Read(ref _containerForceRemoved) == 0)
        {
            await DisposeOwnedContainerAsync(startedAt, totalBudget, failures).ConfigureAwait(false);
        }

        if (failures.Count != 0)
        {
            throw new InvalidOperationException($"Fixture cleanup was incomplete: {string.Join(", ", failures)}.");
        }
    }

    private async Task ProvisionAsync(CancellationToken cancellationToken)
    {
        var ownerPassword = OwnerPassword;
        var dispatcherPassword = DispatcherPassword;
        var runtimePassword = RuntimePassword;
        var retentionPassword = RetentionPassword;
        await using (var connection = await Administrator.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
            await CreateLoginRoleAsync(connection, OwnerRole, ownerPassword, cancellationToken).ConfigureAwait(false);
            await CreateLoginRoleAsync(connection, DispatcherRole, dispatcherPassword, cancellationToken).ConfigureAwait(false);
            await CreateLoginRoleAsync(connection, RuntimeRole, runtimePassword, cancellationToken).ConfigureAwait(false);
            await CreateLoginRoleAsync(connection, RetentionRole, retentionPassword, cancellationToken).ConfigureAwait(false);
            await ExecuteFormattedAsync(connection,
                "GRANT CREATE ON DATABASE %I TO %I",
                _databaseName,
                OwnerRole,
                cancellationToken).ConfigureAwait(false);
        }

        _migrationOwner = AddDataSource(ConnectionFor(OwnerRole, ownerPassword));
        var schemaManager = new PostgreSqlDurableRuntimeSchemaManager(_migrationOwner);
        await schemaManager.ApplyAsync(cancellationToken).ConfigureAwait(false);
        await schemaManager.InitializeRuntimeEpochAsync(
            Epoch,
            actorId: "durable-template-fixture",
            reasonCode: "initial-test-setup",
            cancellationToken).ConfigureAwait(false);
        var status = await schemaManager.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (!status.IsCompatible || status.StoreId == Guid.Empty || status.ActiveRuntimeEpoch != Epoch)
        {
            throw new InvalidOperationException("The published PostgreSQL schema and epoch APIs did not verify the disposable store.");
        }

        StoreId = status.StoreId;
        await ApplyPackagedRoleRecipeAsync(cancellationToken).ConfigureAwait(false);
        _dispatcher = AddDataSource(DispatcherConnectionString);
        _runtime = AddDataSource(RuntimeConnectionString);
        await AssertDispatcherCannotReadWorkPayloadAsync(cancellationToken).ConfigureAwait(false);
        RoleCatalogBeforeHost = await ReadRoleCatalogAsync(cancellationToken).ConfigureAwait(false);
        DurableCatalogBeforeHost = await ReadDurableCatalogAsync(cancellationToken).ConfigureAwait(false);
    }

    private NpgsqlDataSource AddDataSource(string connectionString)
    {
        var source = NpgsqlDataSource.Create(connectionString);
        _dataSources.Add(source);
        return source;
    }

    private string ConnectionFor(string role, string password)
    {
        var builder = new NpgsqlConnectionStringBuilder(_fixtureAdministratorConnectionString)
        {
            Database = _databaseName,
            Pooling = false,
            Timeout = 5,
            CommandTimeout = 5,
        };
        if (!string.IsNullOrEmpty(role))
        {
            builder.Username = role;
            builder.Password = password;
        }

        return builder.ConnectionString;
    }

    private async Task ApplyPackagedRoleRecipeAsync(CancellationToken cancellationToken)
    {
        // This immutable package content file is resolved by name; keep it in its package-provided directory.
        var sqlPath = Path.Combine(AppContext.BaseDirectory, "configure-postgresql-roles.sql");
        var sql = new FileInfo(sqlPath);
        if (!sql.Exists || sql.Length is <= 0 or > MaximumRoleRecipeBytes)
        {
            throw new InvalidOperationException("The directly referenced PostgreSQL package did not copy its bounded role recipe to test output.");
        }

        var manifest = JsonSerializer.Serialize(new
        {
            version = 1,
            pairs = new[]
            {
                new { dispatcher = DispatcherRole, runtime = RuntimeRole, dispatcher_profile = "work_only" },
            },
        });
        string executionPath;
        List<string> arguments;
        string executable;
        IReadOnlyDictionary<string, string?> childEnvironment;
        var connection = new NpgsqlConnectionStringBuilder(_fixtureAdministratorConnectionString) { Database = _databaseName };
        var bootstrapPassword = connection.Password
            ?? throw new InvalidOperationException("The fixture administrator connection has no password for noninteractive psql setup.");
        var redactions = new[]
        {
            bootstrapPassword,
            _bootstrapConnectionString,
            OwnerPassword,
            DispatcherPassword,
            RuntimePassword,
            RetentionPassword,
        };

        if (_container is not null)
        {
            executionPath = $"/tmp/appsurface-template-{_suffix}-{RoleRecipeFileName}";
            var copy = await BoundedProcessRunner.RunAsync(
                "docker",
                ["cp", sqlPath, $"{_container.Id}:{executionPath}"],
                environment: null,
                RemainingSetupBudget(cancellationToken),
                redactions,
                cancellationToken).ConfigureAwait(false);
            if (copy.ExitCode != 0 || copy.StandardOutputTruncated || copy.StandardErrorTruncated)
            {
                throw new InvalidOperationException(
                    $"The exact package role recipe could not be copied to the PostgreSQL fixture (exit {copy.ExitCode}; output-truncated={copy.StandardOutputTruncated || copy.StandardErrorTruncated}).");
            }

            executable = "docker";
            childEnvironment = PsqlEnvironment(connection, bootstrapPassword, insideContainer: true);
            arguments = ["exec"];
            AddDockerEnvironment(arguments, childEnvironment.Keys);
            arguments.Add(_container.Id);
            arguments.Add("psql");
            arguments.AddRange(PsqlArguments(executionPath, manifest, OwnerRole, RetentionRole));
        }
        else
        {
            executionPath = sqlPath;
            executable = _nativePsqlPath
                ?? throw new InvalidOperationException("The native fixture has no resolved psql executable.");
            childEnvironment = PsqlEnvironment(connection, bootstrapPassword, insideContainer: false);
            arguments = PsqlArguments(executionPath, manifest, OwnerRole, RetentionRole);
        }

        var safeRedactions = redactions.Where(static value => !string.IsNullOrEmpty(value))
            .Select(static value => value!)
            .ToArray();
        var result = await BoundedProcessRunner.RunAsync(
            executable,
            arguments,
            childEnvironment,
            RemainingSetupBudget(cancellationToken),
            safeRedactions,
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 || result.StandardOutputTruncated || result.StandardErrorTruncated)
        {
            throw new InvalidOperationException(
                $"The exact package role recipe failed (exit {result.ExitCode}; output-truncated={result.StandardOutputTruncated || result.StandardErrorTruncated}).");
        }
    }

    private TimeSpan RemainingSetupBudget(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var remaining = _setupBudget - Stopwatch.GetElapsedTime(_setupStartedAt);
        if (remaining <= TimeSpan.Zero)
        {
            throw new TimeoutException("PostgreSQL fixture provisioning exhausted its owned setup budget.");
        }

        return remaining;
    }

    private async Task<bool> StopAndObservePendingSetupOperationsAsync(
        long startedAt,
        TimeSpan totalBudget,
        ICollection<string> failures)
    {
        var remaining = totalBudget - Stopwatch.GetElapsedTime(startedAt);
        Func<TimeSpan, Task<bool>>? stopOwnerAsync =
            _ownsContainer && _container is not null && Volatile.Read(ref _containerForceRemoved) == 0
                ? _ => ForceRemoveOwnedContainerAsync(startedAt, totalBudget, failures)
                : null;
        return await _setupOperations.StopAndObservePendingAsync(
            stopOwnerAsync,
            remaining,
            FixtureBudgets.ChildTermination,
            failures).ConfigureAwait(false);
    }

    private async Task<bool> ForceRemoveOwnedContainerAsync(
        long startedAt,
        TimeSpan totalBudget,
        ICollection<string> failures)
    {
        var remaining = totalBudget - Stopwatch.GetElapsedTime(startedAt);
        var processBudget = remaining - FixtureBudgets.ChildTermination;
        if (processBudget <= TimeSpan.Zero)
        {
            failures.Add("container-force-remove-not-attempted-within-cleanup-budget");
            return false;
        }

        if (processBudget > TimeSpan.FromSeconds(2))
        {
            processBudget = TimeSpan.FromSeconds(2);
        }

        try
        {
            var result = await BoundedProcessRunner.RunAsync(
                "docker",
                ["rm", "--force", _container!.Id],
                environment: null,
                processBudget).ConfigureAwait(false);
            if (result.ExitCode != 0 || result.StandardOutputTruncated || result.StandardErrorTruncated)
            {
                failures.Add("container-force-remove-failed");
                return false;
            }

            Volatile.Write(ref _containerForceRemoved, 1);
            return true;
        }
        catch (Exception exception)
        {
            failures.Add($"container-force-remove-{exception.GetType().Name}");
            return false;
        }
    }

    private async Task DisposeOwnedContainerAsync(long startedAt, TimeSpan totalBudget, ICollection<string> failures)
    {
        var remaining = totalBudget - Stopwatch.GetElapsedTime(startedAt);
        if (remaining <= TimeSpan.Zero)
        {
            failures.Add("container-not-disposed-after-total-budget");
            return;
        }

        var disposeTask = _container!.DisposeAsync().AsTask();
        var sdkDisposeBudget = remaining < TimeSpan.FromSeconds(2)
            ? remaining
            : TimeSpan.FromSeconds(2);

        try
        {
            await disposeTask.WaitAsync(sdkDisposeBudget).ConfigureAwait(false);
            return;
        }
        catch (TimeoutException)
        {
            ObserveLateCompletion(disposeTask);
        }
        catch (Exception) when (disposeTask.IsCompleted)
        {
            ObserveLateCompletion(disposeTask);
        }

        var removed = await ForceRemoveOwnedContainerAsync(startedAt, totalBudget, failures).ConfigureAwait(false);
        if (!removed)
        {
            failures.Add("container-dispose-unfinished-after-force-remove-failed");
            ObserveLateCompletion(disposeTask);
            return;
        }

        var afterRemoval = totalBudget - Stopwatch.GetElapsedTime(startedAt);
        if (afterRemoval <= TimeSpan.Zero)
        {
            failures.Add("container-dispose-observation-budget-exhausted");
            ObserveLateCompletion(disposeTask);
            return;
        }

        try
        {
            var observationBudget = afterRemoval < FixtureBudgets.ChildTermination
                ? afterRemoval
                : FixtureBudgets.ChildTermination;
            await disposeTask.WaitAsync(observationBudget).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            failures.Add("container-dispose-task-unfinished-after-force-remove");
            ObserveLateCompletion(disposeTask);
        }
        catch (Exception) when (disposeTask.IsCompleted)
        {
            // Force removal succeeded; the SDK may report the already-removed container as absent.
        }
    }

    private static void ObserveLateCompletion(Task operation)
    {
        _ = operation.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task DropOwnedNativeDatabaseAndRolesAsync()
    {
        await using var bootstrap = NpgsqlDataSource.Create(_bootstrapConnectionString);
        await using (var command = bootstrap.CreateCommand("SELECT format('DROP DATABASE IF EXISTS %I WITH (FORCE)', @database_name);"))
        {
            command.Parameters.AddWithValue("database_name", _databaseName);
            var dropSql = Assert.IsType<string>(await command.ExecuteScalarAsync().ConfigureAwait(false));
            await using var drop = bootstrap.CreateCommand(dropSql);
            await drop.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        foreach (var role in new[] { RuntimeRole, DispatcherRole, RetentionRole, OwnerRole })
        {
            await using var command = bootstrap.CreateCommand("SELECT format('DROP ROLE IF EXISTS %I', @role_name);");
            command.Parameters.AddWithValue("role_name", role);
            var dropSql = Assert.IsType<string>(await command.ExecuteScalarAsync().ConfigureAwait(false));
            await using var drop = bootstrap.CreateCommand(dropSql);
            await drop.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
    }

    private async Task DisposeAfterSetupFailureAsync()
    {
        try
        {
            await DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            throw new AggregateException(
                "PostgreSQL fixture setup failed and owned-resource cleanup was incomplete.",
                new InvalidOperationException(exception.GetType().Name));
        }
    }

    private static async Task CreateLoginRoleAsync(
        NpgsqlConnection connection,
        string role,
        string password,
        CancellationToken cancellationToken)
    {
        await ExecuteFormattedAsync(
            connection,
            "CREATE ROLE %I LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD %L",
            role,
            password,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteFormattedAsync(
        NpgsqlConnection connection,
        string format,
        string firstValue,
        string secondValue,
        CancellationToken cancellationToken)
    {
        await using var formatCommand = new NpgsqlCommand("SELECT format(@format, @first_value, @second_value);", connection);
        formatCommand.Parameters.AddWithValue("format", format);
        formatCommand.Parameters.AddWithValue("first_value", firstValue);
        formatCommand.Parameters.AddWithValue("second_value", secondValue);
        var sql = Assert.IsType<string>(await formatCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyDictionary<string, string?> PsqlEnvironment(
        NpgsqlConnectionStringBuilder connection,
        string? password,
        bool insideContainer) =>
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PGHOST"] = insideContainer ? "localhost" : connection.Host,
            ["PGPORT"] = insideContainer ? "5432" : connection.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["PGDATABASE"] = connection.Database,
            ["PGUSER"] = connection.Username,
            ["PGPASSWORD"] = password,
            ["PGSSLMODE"] = insideContainer ? "disable" : connection.SslMode.ToString().ToLowerInvariant(),
            ["PGCONNECT_TIMEOUT"] = "5",
        };

    private static void AddDockerEnvironment(ICollection<string> arguments, IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            arguments.Add("--env");
            arguments.Add(name);
        }
    }

    private static List<string> PsqlArguments(string sqlPath, string manifest, string ownerRole, string retentionRole) =>
    [
        "-X",
        "-v",
        "ON_ERROR_STOP=1",
        "-v",
        $"migration_owner_role={ownerRole}",
        "-v",
        $"retention_operator_role={retentionRole}",
        "-v",
        $"role_pairs_json={manifest}",
        "-f",
        sqlPath,
    ];

    private static async Task RequireDockerAsync()
    {
        BoundedProcessResult result;
        try
        {
            result = await BoundedProcessRunner.RunAsync(
                "docker",
                ["info", "--format", "{{.ServerVersion}}"],
                environment: null,
                FixtureBudgets.DockerProbe).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException)
        {
            throw DockerPrerequisiteFailure(exception.GetType().Name);
        }

        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput)
            || result.StandardOutputTruncated || result.StandardErrorTruncated)
        {
            throw DockerPrerequisiteFailure("daemon-unavailable");
        }
    }

    private static async Task AcquirePinnedImageAsync()
    {
        var inspect = await BoundedProcessRunner.RunAsync(
            "docker",
            ["image", "inspect", "--format", "{{.Id}}", PostgreSqlTestContainerImage.Reference],
            environment: null,
            FixtureBudgets.DockerProbe).ConfigureAwait(false);
        if (inspect.ExitCode == 0)
        {
            if (inspect.StandardOutputTruncated || inspect.StandardErrorTruncated
                || string.IsNullOrWhiteSpace(inspect.StandardOutput))
            {
                throw new InvalidOperationException("The pinned PostgreSQL image inspection returned incomplete evidence.");
            }

            return;
        }

        var pull = await BoundedProcessRunner.RunAsync(
            "docker",
            ["pull", PostgreSqlTestContainerImage.Reference],
            environment: null,
            FixtureBudgets.ImagePull).ConfigureAwait(false);
        if (pull.ExitCode != 0 || pull.StandardOutputTruncated || pull.StandardErrorTruncated)
        {
            throw new InvalidOperationException("The pinned PostgreSQL 16.5 image could not be acquired within the 300-second pull budget.");
        }
    }

    private static InvalidOperationException DockerPrerequisiteFailure(string cause) => new(
        $"Docker is required for FirstDurableWork; availability is checked for at most five seconds ({cause}). "
        + "Start Docker and rerun: dotnet test --filter FullyQualifiedName~FirstDurableWork. "
        + "Read https://github.com/forge-trust/AppSurface/blob/main/start-here/durable-worker.md#three-command-first-proof.");

    private static string NewPassword() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    private static async Task AttemptCleanupAsync(
        Func<Task> cleanup,
        string phase,
        long startedAt,
        TimeSpan totalBudget,
        ICollection<string> failures)
    {
        var remaining = totalBudget - Stopwatch.GetElapsedTime(startedAt);
        if (remaining <= TimeSpan.Zero)
        {
            failures.Add($"{phase}-not-attempted-after-total-budget");
            return;
        }

        Task operation;
        try
        {
            operation = cleanup();
        }
        catch (Exception exception)
        {
            failures.Add($"{phase}-{exception.GetType().Name}");
            return;
        }

        try
        {
            await operation.WaitAsync(remaining).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            ObserveLateCompletion(operation);
            failures.Add($"{phase}-unfinished");
        }
        catch (Exception exception)
        {
            failures.Add($"{phase}-{exception.GetType().Name}");
            if (!operation.IsCompleted)
            {
                ObserveLateCompletion(operation);
            }
        }
    }
}
