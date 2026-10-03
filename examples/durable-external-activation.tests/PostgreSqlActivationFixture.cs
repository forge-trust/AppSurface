using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.PostgreSql;
using ForgeTrust.AppSurface.Durable.PostgreSql.Tests;
using ForgeTrust.AppSurface.Durable.Provider;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace ForgeTrust.AppSurface.Examples.DurableExternalActivation.Tests;

/// <summary>Provisions isolated PostgreSQL state out of band, then runs the actual passive sample host.</summary>
internal sealed class PostgreSqlActivationFixture : IAsyncDisposable
{
    private readonly PostgreSqlIntegrationTestDatabase database;
    private readonly string[] roles;
    private readonly List<NpgsqlDataSource> dataSources = [];
    private WebApplication? app;
    private HttpClient? client;

    private PostgreSqlActivationFixture(PostgreSqlIntegrationTestDatabase database)
    {
        this.database = database;
        var suffix = Guid.NewGuid().ToString("N")[..12];
        roles = [$"activation_owner_{suffix}", $"activation_dispatch_{suffix}", $"activation_runtime_{suffix}", $"activation_retention_{suffix}"];
    }

    internal IServiceProvider Services => app!.Services;
    internal HttpClient Client => client!;
    internal NpgsqlDataSource Administrator => database.DataSource;
    internal Guid Epoch { get; } = Guid.NewGuid();
    internal Guid StoreId { get; private set; }
    internal string DispatcherConnection { get; private set; } = string.Empty;
    internal string RuntimeConnection { get; private set; } = string.Empty;
    internal string CatalogBeforeHost { get; private set; } = string.Empty;

    /// <summary>Uses the existing strict database fixture and the canonical reviewed role recipe.</summary>
    internal static async Task<PostgreSqlActivationFixture> StartAsync(Action<IServiceCollection>? configure = null, bool useKestrel = false)
    {
        var fixture = new PostgreSqlActivationFixture(await PostgreSqlIntegrationTestDatabase.TryCreateAsync());
        try
        {
            using var setup = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var manager = new PostgreSqlDurableRuntimeSchemaManager(fixture.Administrator);
            await manager.ApplyAsync(setup.Token);
            await manager.InitializeRuntimeEpochAsync(fixture.Epoch, "activation-proof", "initial-development", setup.Token);
            fixture.StoreId = (await manager.GetStatusAsync(setup.Token)).StoreId;
            await fixture.CreateRolesAsync(setup.Token);
            await fixture.ApplyRoleRecipeAsync(setup.Token);
            fixture.CatalogBeforeHost = await fixture.ReadCatalogAsync();
            await fixture.StartHostAsync(configure, useKestrel);
            Assert.Equal(fixture.CatalogBeforeHost, await fixture.ReadCatalogAsync());
            return fixture;
        }
        catch (Exception setupFailure)
        {
            try
            {
                await fixture.DisposeAsync();
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("Activation fixture setup and cleanup both failed.", setupFailure, cleanupFailure);
            }
            throw;
        }
    }

    private async Task CreateRolesAsync(CancellationToken cancellationToken)
    {
        foreach (var role in roles)
        {
            await using var command = Administrator.CreateCommand($"CREATE ROLE {role} LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '{role}';");
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        var databaseName = new NpgsqlConnectionStringBuilder(database.ConnectionString).Database
            ?? throw new InvalidOperationException("The isolated fixture database name is required.");
        await using (var grant = Administrator.CreateCommand($"GRANT CREATE ON DATABASE \"{databaseName}\" TO {roles[0]};"))
        {
            await grant.ExecuteNonQueryAsync(cancellationToken);
        }
        DispatcherConnection = ConnectionFor(roles[1]);
        RuntimeConnection = ConnectionFor(roles[2]);
    }

    private string ConnectionFor(string role) => new NpgsqlConnectionStringBuilder(database.ConnectionString)
    {
        Username = role,
        Password = role,
    }.ConnectionString;

    /// <summary>Uses psql locally or a pinned PostgreSQL client container; credentials travel only as environment data.</summary>
    private async Task ApplyRoleRecipeAsync(CancellationToken cancellationToken)
    {
        var recipe = await File.ReadAllTextAsync(Path.Join(AppContext.BaseDirectory, "configure-postgresql-roles.sql"), cancellationToken);
        var manifest = JsonSerializer.Serialize(new
        {
            version = 1,
            pairs = new[] { new { dispatcher = roles[1], runtime = roles[2], dispatcher_profile = "work_only" } },
        });
        var connection = new NpgsqlConnectionStringBuilder(database.ConnectionString);
        var start = new ProcessStartInfo("psql")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        SetConnectionEnvironment(start, connection);
        AddRecipeArguments(start, manifest);
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the PostgreSQL role recipe client.");
        }
        catch (Win32Exception)
        {
            start.FileName = "docker";
            start.ArgumentList.Clear();
            foreach (var argument in new[] { "run", "--rm", "-i", "--add-host", "host.docker.internal:host-gateway", "-e", "PGHOST", "-e", "PGPORT", "-e", "PGDATABASE", "-e", "PGUSER", "-e", "PGPASSWORD", "-e", "PGSSLMODE", PostgreSqlTestContainerImage.Reference, "psql" })
            {
                start.ArgumentList.Add(argument);
            }
            if (connection.Host is "localhost" or "127.0.0.1" or "::1")
            {
                start.Environment["PGHOST"] = "host.docker.internal";
            }
            AddRecipeArguments(start, manifest);
            process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the pinned PostgreSQL role recipe client.");
        }

        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.StandardInput.WriteAsync(recipe.AsMemory(), cancellationToken);
                process.StandardInput.Close();
                await process.WaitForExitAsync(cancellationToken);
                await Task.WhenAll(output, error);
                Assert.True(process.ExitCode == 0, $"Canonical role recipe failed with exit {process.ExitCode}: {SafeDiagnostics(output.Result + error.Result)}");
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await process.WaitForExitAsync(cleanup.Token);
                }
            }
        }
    }

    private void AddRecipeArguments(ProcessStartInfo start, string manifest)
    {
        foreach (var argument in new[] { "-X", "-v", "ON_ERROR_STOP=1", "-v", $"migration_owner_role={roles[0]}", "-v", $"retention_operator_role={roles[3]}", "-v", $"role_pairs_json={manifest}", "-f", "-" })
        {
            start.ArgumentList.Add(argument);
        }
    }

    /// <summary>Retains useful setup failure evidence while removing all fixture connection and password values.</summary>
    private string SafeDiagnostics(string diagnostics)
    {
        diagnostics = diagnostics.Replace(database.ConnectionString, "<postgres-connection>", StringComparison.Ordinal);
        var password = new NpgsqlConnectionStringBuilder(database.ConnectionString).Password;
        if (!string.IsNullOrEmpty(password))
        {
            diagnostics = diagnostics.Replace(password, "<postgres-password>", StringComparison.Ordinal);
        }
        foreach (var role in roles)
        {
            diagnostics = diagnostics.Replace(role, "<fixture-role>", StringComparison.Ordinal);
        }
        return diagnostics.Length <= 4096 ? diagnostics : diagnostics[..4096];
    }

    private static void SetConnectionEnvironment(ProcessStartInfo start, NpgsqlConnectionStringBuilder connection)
    {
        start.Environment["PGHOST"] = connection.Host;
        start.Environment["PGPORT"] = connection.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["PGDATABASE"] = connection.Database;
        start.Environment["PGUSER"] = connection.Username;
        start.Environment["PGPASSWORD"] = connection.Password;
        start.Environment["PGSSLMODE"] = connection.SslMode switch
        {
            SslMode.VerifyCA => "verify-ca",
            SslMode.VerifyFull => "verify-full",
            _ => connection.SslMode.ToString().ToLowerInvariant(),
        };
    }

    private async Task StartHostAsync(Action<IServiceCollection>? configure, bool useKestrel)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        if (useKestrel)
        {
            builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        }
        else
        {
            builder.WebHost.UseTestServer();
        }
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DurableActivation:DevelopmentBearerToken"] = ActivationTestHost.ValidToken,
            ["AppSurfaceObservability:ExporterMode"] = "Never",
        });
        var dispatcher = NpgsqlDataSource.Create(DispatcherConnection);
        var runtime = NpgsqlDataSource.Create(RuntimeConnection);
        dataSources.Add(dispatcher);
        dataSources.Add(runtime);
        DurableExternalActivationProgram.ConfigurePostgreSqlServices(builder.Services, builder.Configuration, [], dispatcher, runtime, StoreId, Epoch, roles[2]);
        app = DurableExternalActivationProgram.BuildApplication(builder, configure);
        await app.StartAsync();
        client = useKestrel
            ? new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()), Timeout = TimeSpan.FromSeconds(30) }
            : app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ActivationTestHost.ValidToken);
    }

    internal async Task<DurableWorkAcceptance> AcceptAsync(string value)
    {
        var result = await Services.GetRequiredService<IDurableWorkClient>().EnqueueAsync(DemoWorkContract.Definition.CreateRequest(
            new DurableScopeId(DemoWorkContract.Scope), DurableCommandId.New(), Guid.NewGuid().ToString("N"),
            new DemoWork(value)));
        Assert.True(result.IsSuccess, result.Problem?.Code);
        return Assert.IsType<DurableWorkAcceptance>(result.Value);
    }

    internal async Task<DurableWorkSnapshot> InspectAsync(DurableWorkAcceptance receipt)
    {
        var result = await Services.GetRequiredService<IDurableWorkControlClient>().GetAsync(new DurableWorkGetRequest(
            new DurableScopeId(DemoWorkContract.Scope), receipt.WorkId));
        Assert.True(result.IsSuccess, result.Problem?.Code);
        return Assert.IsType<DurableWorkSnapshot>(result.Value);
    }

    /// <summary>Reads the durable catalog, including ownership, ACLs, RLS policies and function definitions.</summary>
    internal async Task<string> ReadCatalogAsync()
    {
        await using var command = Administrator.CreateCommand("""
            SELECT md5(string_agg(item, E'\n' ORDER BY item)) FROM (
              SELECT json_build_array(c.relname,c.relkind,c.relowner,c.relacl,c.relrowsecurity,c.relforcerowsecurity,c.reloptions)::text AS item FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='appsurface_durable'
              UNION ALL SELECT row_to_json(a)::text FROM pg_attribute a JOIN pg_class c ON c.oid=a.attrelid JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='appsurface_durable'
              UNION ALL SELECT row_to_json(p)::text FROM pg_policy p JOIN pg_class c ON c.oid=p.polrelid JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='appsurface_durable'
              UNION ALL SELECT row_to_json(p)::text FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='appsurface_durable'
              UNION ALL SELECT row_to_json(n)::text FROM pg_namespace n WHERE n.nspname='appsurface_durable'
              UNION ALL SELECT row_to_json(a)::text FROM pg_default_acl a
              UNION ALL SELECT json_build_array(d.datname,d.datdba,d.datacl)::text FROM pg_database d WHERE d.datname=current_database()
            ) catalog;
            """);
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    /// <summary>Closes restricted-role connections to this database without taking down the shared PostgreSQL server.</summary>
    internal async Task SetStoreAvailableAsync(bool available)
    {
        var connection = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Database = "postgres", Pooling = false };
        var databaseName = new NpgsqlConnectionStringBuilder(database.ConnectionString).Database
            ?? throw new InvalidOperationException("The isolated fixture database name is required.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var source = NpgsqlDataSource.Create(connection.ConnectionString);
        // Connection exhaustion is an existing provider availability classification. The superuser maintenance
        // identity can restore this owned database while runtime/dispatcher logins cannot acquire a connection.
        await using var alter = source.CreateCommand($"ALTER DATABASE \"{databaseName}\" CONNECTION LIMIT {(available ? -1 : 0)};");
        await alter.ExecuteNonQueryAsync(deadline.Token);
        if (!available)
        {
            await using var terminate = source.CreateCommand("SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @database;");
            terminate.Parameters.AddWithValue("database", databaseName);
            await terminate.ExecuteNonQueryAsync(deadline.Token);
        }
    }

    /// <summary>Attempts every independently owned cleanup, then reports any cleanup failures together.</summary>
    public async ValueTask DisposeAsync()
    {
        var maintenance = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Database = "postgres", Pooling = false };
        var failures = new List<Exception>();
        await AttemptAsync(async () => await SetStoreAvailableAsync(true));
        await AttemptAsync(() =>
        {
            client?.Dispose();
            return ValueTask.CompletedTask;
        });
        if (app is not null)
        {
            await AttemptAsync(app.DisposeAsync);
        }
        foreach (var source in dataSources)
        {
            await AttemptAsync(source.DisposeAsync);
        }
        await AttemptAsync(database.DisposeAsync);
        await AttemptAsync(async () =>
        {
            await using var cleanupSource = NpgsqlDataSource.Create(maintenance.ConnectionString);
            foreach (var role in roles)
            {
                await AttemptAsync(async () =>
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await using var command = cleanupSource.CreateCommand($"DROP ROLE IF EXISTS {role};");
                    await command.ExecuteNonQueryAsync(cleanup.Token);
                });
            }
        });
        if (failures.Count > 0)
        {
            throw new AggregateException("Activation fixture cleanup failed.", failures);
        }

        async ValueTask AttemptAsync(Func<ValueTask> cleanup)
        {
            try
            {
                await cleanup();
            }
            catch (Exception failure)
            {
                failures.Add(failure);
            }
        }
    }
}
