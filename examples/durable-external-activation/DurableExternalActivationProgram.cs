using System.Globalization;
using System.Text.Json;
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Core.Defaults;
using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.PostgreSql;
using ForgeTrust.AppSurface.Durable.Provider;
using ForgeTrust.AppSurface.Observability;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace ForgeTrust.AppSurface.Examples.DurableExternalActivation;

/// <summary>Runs the sample's separate out-of-band, serving, and persisted-inspection commands.</summary>
internal static class DurableExternalActivationProgram
{
    /// <summary>Gets the migration-owner PostgreSQL connection setting name.</summary>
    internal const string MigrationConnectionKey = "APPSURFACE_DURABLE_MIGRATION_CONNECTION";

    /// <summary>Gets the dispatcher PostgreSQL connection setting name.</summary>
    internal const string DispatcherConnectionKey = "APPSURFACE_DURABLE_DISPATCHER_CONNECTION";

    /// <summary>Gets the runtime PostgreSQL connection setting name.</summary>
    internal const string RuntimeConnectionKey = "APPSURFACE_DURABLE_RUNTIME_CONNECTION";

    /// <summary>Gets the explicitly provisioned active runtime epoch setting name.</summary>
    internal const string RuntimeEpochKey = "APPSURFACE_DURABLE_RUNTIME_EPOCH";

    /// <summary>Gets the development-only bearer-token configuration key.</summary>
    internal const string DevelopmentTokenKey = "DurableActivation:DevelopmentBearerToken";

    /// <summary>Executes one sample CLI command and returns its process exit code.</summary>
    /// <param name="args">Command line after the executable name.</param>
    /// <param name="shutdownToken">Requests graceful shutdown of <c>serve</c> after startup; other commands finish independently.</param>
    internal static async Task<int> RunAsync(string[] args, CancellationToken shutdownToken = default)
    {
        if (args is ["help" or "--help" or "-h"] || args.Length == 0)
        {
            PrintUsage(Console.Out);
            return 0;
        }

        try
        {
            return args[0] switch
            {
                "schema-apply-dev" when args.Length == 1 => await ApplySchemaAsync(),
                "epoch-bootstrap-dev" when args.Length == 1 => await BootstrapEpochAsync(),
                "accept-demo-work" => await AcceptDemoWorkAsync(args),
                "serve" when args.Length == 1 => await ServeAsync(args, shutdownToken),
                "inspect-demo-work" => await InspectDemoWorkAsync(args),
                _ => UnknownCommand(args[0]),
            };
        }
        catch (Exception exception) when (IsNonfatal(exception))
        {
            Console.Error.WriteLine($"[{args[0]}] failed; verify the configured PostgreSQL roles, schema, epoch, and local bearer-token settings.");
            return 1;
        }
    }

    /// <summary>Builds the testable HTTP application and eagerly validates its activation dependency graph.</summary>
    /// <param name="builder">Configured ASP.NET Core builder.</param>
    /// <param name="configureServices">Optional sample-test service seam applied after host defaults.</param>
    /// <param name="configureExternalAuthentication">Required non-Development scheme composition.</param>
    /// <param name="configureExternalAuthorization">Required non-Development policy composition.</param>
    /// <returns>A built but not yet listening application.</returns>
    internal static WebApplication BuildApplication(
        WebApplicationBuilder builder,
        Action<IServiceCollection>? configureServices = null,
        Action<AuthenticationBuilder>? configureExternalAuthentication = null,
        Action<AuthorizationOptions>? configureExternalAuthorization = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateOnBuild = true;
            options.ValidateScopes = true;
        });

        if (builder.Environment.IsDevelopment())
        {
            var token = builder.Configuration[DevelopmentTokenKey];
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException($"{DevelopmentTokenKey} is required in Development.");
            }

            builder.Services
                .AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = DevelopmentBearerHandler.SchemeName;
                    options.DefaultChallengeScheme = DevelopmentBearerHandler.SchemeName;
                    options.DefaultForbidScheme = DevelopmentBearerHandler.SchemeName;
                })
                .AddScheme<DevelopmentBearerOptions, DevelopmentBearerHandler>(
                    DevelopmentBearerHandler.SchemeName,
                    options => options.Token = token);
            builder.Services.AddAuthorization(options => AddActivationPolicy(options));
        }
        else
        {
            if (configureExternalAuthentication is null || configureExternalAuthorization is null)
            {
                throw new InvalidOperationException(
                    "Non-Development startup requires explicit authentication-scheme and authorization-policy composition.");
            }

            configureExternalAuthentication(builder.Services.AddAuthentication());
            builder.Services.AddAuthorization(configureExternalAuthorization);
        }

        builder.Services.AddDurableExternalActivation();
        configureServices?.Invoke(builder.Services);
        var settings = ReadActivationSettings(builder.Configuration);
        var app = builder.Build();
        try
        {
            using (var scope = app.Services.CreateScope())
            {
                _ = scope.ServiceProvider.GetRequiredService<IDurableExternalActivationService>();
                var authorizationProvider = scope.ServiceProvider.GetRequiredService<IAuthorizationPolicyProvider>();
                if (authorizationProvider.GetPolicyAsync(ActivationHttpEndpoints.AuthorizationPolicy).GetAwaiter().GetResult() is null)
                {
                    throw new InvalidOperationException("The DurableActivation authorization policy must exist before listening.");
                }
            }

            app.UseAuthentication();
            app.UseAuthorization();
            ActivationHttpEndpoints.Map(app, settings);
            return app;
        }
        catch
        {
            app.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    /// <summary>Applies forward migrations using only the configured migration-owner data source.</summary>
    private static async Task<int> ApplySchemaAsync()
    {
        RequireDevelopmentEnvironment();
        await using var migrationDataSource = NpgsqlDataSource.Create(RequireEnvironment(MigrationConnectionKey));
        var result = await new PostgreSqlDurableRuntimeSchemaManager(migrationDataSource).ApplyAsync().ConfigureAwait(false);
        Console.WriteLine($"[schema-apply-dev] schema compatible at version {result.CurrentVersion}");
        return 0;
    }

    /// <summary>Initializes the configured nonempty runtime epoch once, after out-of-band schema application.</summary>
    private static async Task<int> BootstrapEpochAsync()
    {
        RequireDevelopmentEnvironment();
        var runtimeEpoch = RequireRuntimeEpoch();
        await using var migrationDataSource = NpgsqlDataSource.Create(RequireEnvironment(MigrationConnectionKey));
        var manager = new PostgreSqlDurableRuntimeSchemaManager(migrationDataSource);
        var status = await manager.GetStatusAsync().ConfigureAwait(false);
        if (!status.IsCompatible)
        {
            throw new InvalidOperationException("The schema is not compatible; run schema-apply-dev first.");
        }

        if (status.ActiveRuntimeEpoch is not null)
        {
            throw new InvalidOperationException("The store already has an active epoch; this command initializes only an empty epoch.");
        }

        await manager.InitializeRuntimeEpochAsync(
            runtimeEpoch,
            actorId: "external-activation-example",
            reasonCode: "initial-development").ConfigureAwait(false);
        Console.WriteLine("[epoch-bootstrap-dev] active epoch initialized");
        return 0;
    }

    /// <summary>Composes a passive authenticated runtime after read-only schema and epoch verification.</summary>
    /// <param name="args">Original command line passed to startup context.</param>
    /// <param name="shutdownToken">Requests graceful host shutdown after the startup checks and listener have completed.</param>
    private static async Task<int> ServeAsync(string[] args, CancellationToken shutdownToken)
    {
        RequireDevelopmentEnvironment();
        var dispatcherConnection = RequireEnvironment(DispatcherConnectionKey);
        var runtimeConnection = RequireEnvironment(RuntimeConnectionKey);
        var runtimeEpoch = RequireRuntimeEpoch();
        var token = Environment.GetEnvironmentVariable("DurableActivation__DevelopmentBearerToken");
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException($"{DevelopmentTokenKey} is required in Development.");
        }

        await using (var checkDataSource = NpgsqlDataSource.Create(runtimeConnection))
        {
            var status = await new PostgreSqlDurableRuntimeSchemaManager(checkDataSource).GetStatusAsync().ConfigureAwait(false);
            if (!status.IsCompatible || status.StoreId == Guid.Empty || status.ActiveRuntimeEpoch != runtimeEpoch)
            {
                throw new InvalidOperationException("Runtime schema or active epoch does not match the configured process identity.");
            }
        }

        var builder = CreateWebApplicationBuilder([]);
        var dispatcherDataSource = NpgsqlDataSource.Create(dispatcherConnection);
        NpgsqlDataSource? runtimeDataSource = null;
        WebApplication? app = null;
        try
        {
            runtimeDataSource = NpgsqlDataSource.Create(runtimeConnection);
            var runtimeRole = RequireRuntimeRole(runtimeConnection);
            var runtimeSchema = new PostgreSqlDurableRuntimeSchemaManager(runtimeDataSource);
            var runtimeStatus = await runtimeSchema.GetStatusAsync().ConfigureAwait(false);
            if (!runtimeStatus.IsCompatible || runtimeStatus.StoreId == Guid.Empty || runtimeStatus.ActiveRuntimeEpoch != runtimeEpoch)
            {
                throw new InvalidOperationException("Runtime schema or active epoch changed during startup validation.");
            }

            ConfigurePostgreSqlServices(builder.Services, builder.Configuration, args, dispatcherDataSource, runtimeDataSource, runtimeStatus.StoreId, runtimeEpoch, runtimeRole);
            app = BuildApplication(builder);
            await app.StartAsync().ConfigureAwait(false);
            var address = app.Urls.Order(StringComparer.Ordinal).FirstOrDefault() ?? "configured by host";
            Console.WriteLine($"[serve] host ready at {address}; /live /compatibility /ready");
            await app.WaitForShutdownAsync(shutdownToken).ConfigureAwait(false);
            return 0;
        }
        finally
        {
            try
            {
                if (app is not null)
                {
                    await app.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                try
                {
                    await dispatcherDataSource.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    if (runtimeDataSource is not null)
                    {
                        await runtimeDataSource.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
        }
    }

    /// <summary>Accepts typed demo Work through the public client and prints its receipt before an HTTP wake.</summary>
    /// <param name="args">Command line containing <c>accept-demo-work --value &lt;value&gt;</c>.</param>
    private static async Task<int> AcceptDemoWorkAsync(string[] args)
    {
        RequireDevelopmentEnvironment();
        if (args.Length != 3 || args[1] != "--value" || string.IsNullOrWhiteSpace(args[2]) || args[2].Length > 200)
        {
            Console.Error.WriteLine("Usage: accept-demo-work --value <value>");
            return 2;
        }

        var dispatcherConnection = RequireEnvironment(DispatcherConnectionKey);
        var runtimeConnection = RequireEnvironment(RuntimeConnectionKey);
        var runtimeEpoch = RequireRuntimeEpoch();
        await using var dispatcherDataSource = NpgsqlDataSource.Create(dispatcherConnection);
        await using var runtimeDataSource = NpgsqlDataSource.Create(runtimeConnection);
        var status = await new PostgreSqlDurableRuntimeSchemaManager(runtimeDataSource).GetStatusAsync().ConfigureAwait(false);
        if (!status.IsCompatible || status.StoreId == Guid.Empty || status.ActiveRuntimeEpoch != runtimeEpoch)
        {
            throw new InvalidOperationException("Runtime schema or active epoch does not match the configured process identity.");
        }

        var services = new ServiceCollection();
        ConfigureDemoWorkServices(services);
        _ = services.AddAppSurfaceDurablePostgreSql(
            dispatcherDataSource,
            runtimeDataSource,
            new PostgreSqlDurableWorkOptions(runtimeEpoch, status.StoreId),
            new PostgreSqlDurableScheduleOptions(RequireRuntimeRole(runtimeConnection)));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        var acceptance = await provider.GetRequiredService<IDurableWorkClient>().EnqueueAsync(
            DemoWorkContract.Definition.CreateRequest(
                new DurableScopeId(DemoWorkContract.Scope),
                DurableCommandId.New(),
                Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
                new DemoWork(args[2]))).ConfigureAwait(false);
        if (!acceptance.IsSuccess || acceptance.Value is null)
        {
            Console.Error.WriteLine($"[accept-demo-work] failed with {acceptance.Problem?.Code ?? "no-code"}");
            return 3;
        }

        var receipt = acceptance.Value;
        Console.WriteLine(JsonSerializer.Serialize(new DemoWorkReceiptResponse(
            DemoWorkContract.Scope,
            receipt.WorkId.Value,
            receipt.CommandId.Value,
            receipt.Kind.ToString(),
            receipt.Revision,
            receipt.AcceptedAtUtc.ToString("O", CultureInfo.InvariantCulture))));
        return 0;
    }

    /// <summary>Reads one persisted Work snapshot from its explicitly authorized scope.</summary>
    /// <param name="args">Command line containing the fixed verb and required scope/work identifiers.</param>
    private static async Task<int> InspectDemoWorkAsync(string[] args)
    {
        if (args.Length != 5 || args[1] != "--scope" || args[3] != "--work-id")
        {
            PrintUsage(Console.Error);
            return 2;
        }

        var scopeId = new DurableScopeId(args[2]);
        var workId = new DurableWorkId(args[4]);
        var runtimeConnection = RequireEnvironment(RuntimeConnectionKey);
        var dispatcherConnection = RequireEnvironment(DispatcherConnectionKey);
        var runtimeEpoch = RequireRuntimeEpoch();
        await using var dispatcherDataSource = NpgsqlDataSource.Create(dispatcherConnection);
        await using var runtimeDataSource = NpgsqlDataSource.Create(runtimeConnection);
        var status = await new PostgreSqlDurableRuntimeSchemaManager(runtimeDataSource).GetStatusAsync().ConfigureAwait(false);
        if (!status.IsCompatible || status.ActiveRuntimeEpoch != runtimeEpoch)
        {
            throw new InvalidOperationException("Runtime schema or active epoch does not match the configured process identity.");
        }

        var services = new ServiceCollection();
        ConfigureDemoWorkServices(services);
        _ = services.AddAppSurfaceDurablePostgreSql(
            dispatcherDataSource,
            runtimeDataSource,
            new PostgreSqlDurableWorkOptions(runtimeEpoch, status.StoreId),
            new PostgreSqlDurableScheduleOptions(RequireRuntimeRole(runtimeConnection)));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        var result = await provider.GetRequiredService<IDurableWorkControlClient>().GetAsync(
            new DurableWorkGetRequest(scopeId, workId)).ConfigureAwait(false);
        if (!result.IsSuccess || result.Value is null)
        {
            Console.WriteLine(JsonSerializer.Serialize(new InspectionFailureResponse(result.Problem?.Code)));
            return 3;
        }

        var snapshot = result.Value;
        Console.WriteLine(JsonSerializer.Serialize(new WorkInspectionResponse(
            snapshot.ScopeId.Value,
            snapshot.WorkId.Value,
            snapshot.State.ToString(),
            snapshot.AttemptNumber,
            snapshot.Revision,
            snapshot.AcceptedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            snapshot.UpdatedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            snapshot.TerminalAtUtc?.ToString("O", CultureInfo.InvariantCulture),
            snapshot.TerminalCode)));
        return 0;
    }

    /// <summary>Creates a standard web builder with graph validation and a loopback-only sample listener default.</summary>
    /// <param name="args">Application arguments.</param>
    /// <returns>Configured web application builder.</returns>
    internal static WebApplicationBuilder CreateWebApplicationBuilder(string[] args)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args });
        builder.WebHost.UseUrls(builder.Configuration["urls"] ?? "http://127.0.0.1:5080");
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateOnBuild = true;
            options.ValidateScopes = true;
        });
        return builder;
    }

    /// <summary>Registers the example's explicit typed Work, passive PostgreSQL runtime, and OTel integration.</summary>
    /// <param name="services">Host service collection.</param>
    /// <param name="configuration">Host configuration.</param>
    /// <param name="args">Startup arguments captured by the observability identity context.</param>
    /// <param name="dispatcherDataSource">Distinct dispatcher login data source.</param>
    /// <param name="runtimeDataSource">Distinct runtime login data source.</param>
    /// <param name="storeId">Verified schema store identity.</param>
    /// <param name="runtimeEpoch">Verified active runtime epoch.</param>
    /// <param name="runtimeRole">Exact runtime login role used by Schedule safety options.</param>
    internal static void ConfigurePostgreSqlServices(
        IServiceCollection services,
        IConfiguration configuration,
        string[] args,
        NpgsqlDataSource dispatcherDataSource,
        NpgsqlDataSource runtimeDataSource,
        Guid storeId,
        Guid runtimeEpoch,
        string runtimeRole)
    {
        ConfigureDemoWorkServices(services);
        services.AddAppSurfaceDurablePostgreSql(
            dispatcherDataSource,
            runtimeDataSource,
            new PostgreSqlDurableWorkOptions(runtimeEpoch, storeId),
            new PostgreSqlDurableScheduleOptions(runtimeRole));
        services.AddAppSurfaceObservability(
            new StartupContext(args, new NoHostModule(), "Durable External Activation Example"),
            configuration);
    }

    /// <summary>Registers only the fixed typed Work contract and its deterministic executor.</summary>
    /// <param name="services">Service collection receiving Work registrations.</param>
    internal static void ConfigureDemoWorkServices(IServiceCollection services)
    {
        services.AddDurableWork(DemoWorkContract.Definition.ExecutedBy<DemoWorkExecutor>());
    }

    /// <summary>Reads and validates the host-owned activation request limits.</summary>
    /// <param name="configuration">Configuration source.</param>
    private static ActivationHostSettings ReadActivationSettings(IConfiguration configuration)
    {
        var maximumItems = configuration.GetValue("DurableActivation:PumpMaximumItems", 32);
        var discoverySeconds = configuration.GetValue("DurableActivation:PumpDiscoveryBudgetSeconds", 2);
        var requestSeconds = configuration.GetValue("DurableActivation:RequestBudgetSeconds", 10);
        var discoveryBudget = TimeSpan.FromSeconds(discoverySeconds);
        var requestBudget = TimeSpan.FromSeconds(requestSeconds);
        _ = new DurableRuntimePumpRequest(maximumItems, discoveryBudget, DurableRuntimeSurface.Work);
        _ = new DurableExternalActivationRequest(new DurableRuntimePumpRequest(1, TimeSpan.FromSeconds(1), DurableRuntimeSurface.Work), requestBudget);
        return new ActivationHostSettings(maximumItems, discoveryBudget, requestBudget);
    }

    /// <summary>Registers the named local activation policy.</summary>
    /// <param name="options">Authorization options to configure.</param>
    internal static void AddActivationPolicy(AuthorizationOptions options) => options.AddPolicy(
        ActivationHttpEndpoints.AuthorizationPolicy,
        policy => policy.RequireAuthenticatedUser().RequireClaim("permission", "durable-activation"));

    /// <summary>Writes the stable sample command synopsis.</summary>
    /// <param name="writer">Output stream.</param>
    private static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("Durable external-activation example commands:");
        writer.WriteLine("  schema-apply-dev       Apply forward schema migrations with the migration-owner role.");
        writer.WriteLine("  epoch-bootstrap-dev    Initialize the active runtime epoch once.");
        writer.WriteLine("  accept-demo-work --value <value>   Persist one typed Work item and print its receipt.");
        writer.WriteLine("  serve                  Start the passive HTTP host on http://127.0.0.1:5080 by default.");
        writer.WriteLine("  inspect-demo-work --scope <scope> --work-id <id>   Read one persisted Work snapshot.");
        writer.WriteLine("Role provisioning remains an out-of-band psql invocation of Durable/configure-postgresql-roles.sql.");
    }

    /// <summary>Returns a safe command-line error without printing supplied credentials or exception text.</summary>
    /// <param name="command">Unknown verb.</param>
    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown example command '{command}'. Use --help.");
        return 2;
    }

    /// <summary>Requires Development for explicitly named mutation commands.</summary>
    private static void RequireDevelopmentEnvironment()
    {
        var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        if (!string.Equals(environment, Environments.Development, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The schema and epoch mutation commands are Development-only.");
        }
    }

    /// <summary>Parses the configured nonempty runtime epoch.</summary>
    private static Guid RequireRuntimeEpoch()
    {
        var value = RequireEnvironment(RuntimeEpochKey);
        if (!Guid.TryParse(value, out var epoch) || epoch == Guid.Empty)
        {
            throw new InvalidOperationException($"{RuntimeEpochKey} must be a non-empty GUID.");
        }

        return epoch;
    }

    /// <summary>Reads and validates the runtime login role required by PostgreSQL Schedule safety options.</summary>
    /// <param name="connectionString">Runtime-role connection string.</param>
    private static string RequireRuntimeRole(string connectionString)
    {
        var role = new NpgsqlConnectionStringBuilder(connectionString).Username;
        return !string.IsNullOrWhiteSpace(role)
            ? role
            : throw new InvalidOperationException("The runtime connection must identify its login role.");
    }

    /// <summary>Reads a required environment setting without including its value in errors.</summary>
    /// <param name="key">Environment variable name.</param>
    private static string RequireEnvironment(string key) =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key))
            ? Environment.GetEnvironmentVariable(key)!
            : throw new InvalidOperationException($"Required setting {key} is absent.");

    /// <summary>Classifies process-fatal exceptions that must not become sample CLI status codes.</summary>
    /// <param name="exception">Exception under consideration.</param>
    private static bool IsNonfatal(Exception exception) => exception is not
        StackOverflowException and not OutOfMemoryException and not AccessViolationException;
}

/// <summary>Contains the typed Work receipt written by the sample CLI.</summary>
/// <param name="Scope">Trusted fixed sample scope.</param>
/// <param name="WorkId">Persisted Work id required for later inspection.</param>
/// <param name="CommandId">Acceptance command id.</param>
/// <param name="Kind">Accepted or Duplicate.</param>
/// <param name="Revision">Authoritative acceptance revision.</param>
/// <param name="AcceptedAtUtc">Authoritative acceptance timestamp.</param>
internal sealed record DemoWorkReceiptResponse(
    string Scope,
    string WorkId,
    string CommandId,
    string Kind,
    long Revision,
    string AcceptedAtUtc);

/// <summary>Contains a safe persisted Work inspection response for stdout.</summary>
/// <param name="Scope">Authorized scope identifier.</param>
/// <param name="WorkId">Inspected durable Work identifier.</param>
/// <param name="State">Current provider state name.</param>
/// <param name="AttemptNumber">Provider attempt count.</param>
/// <param name="Revision">Current durable revision.</param>
/// <param name="AcceptedAtUtc">Acceptance timestamp.</param>
/// <param name="UpdatedAtUtc">Last update timestamp.</param>
/// <param name="TerminalAtUtc">Terminal timestamp, if completed.</param>
/// <param name="TerminalCode">Bounded terminal code, if present.</param>
internal sealed record WorkInspectionResponse(
    string Scope,
    string WorkId,
    string State,
    int AttemptNumber,
    long Revision,
    string AcceptedAtUtc,
    string UpdatedAtUtc,
    string? TerminalAtUtc,
    string? TerminalCode);

/// <summary>Contains only a bounded problem code when persisted inspection finds no snapshot.</summary>
/// <param name="ProblemCode">Provider problem code, if available.</param>
internal sealed record InspectionFailureResponse(string? ProblemCode);
