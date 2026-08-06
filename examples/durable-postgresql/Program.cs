using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.PostgreSql;
using ForgeTrust.AppSurface.Durable.Provider;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

const string MigrationConnectionVariable = "APPSURFACE_DURABLE_MIGRATION_CONNECTION";
const string RuntimeConnectionVariable = "APPSURFACE_DURABLE_RUNTIME_CONNECTION";
const string DispatcherConnectionVariable = "APPSURFACE_DURABLE_DISPATCHER_CONNECTION";
const string RuntimeEpochVariable = "APPSURFACE_DURABLE_RUNTIME_EPOCH";
const string LocalProofConfirmationVariable = "APPSURFACE_DURABLE_LOCAL_PROOF";
const string MigrationOwnerRole = "appsurface_durable_owner";
const string DispatcherRole = "appsurface_durable_dispatcher";
const string RuntimeRole = "appsurface_durable_runtime";

try
{
    return await RunAsync(args);
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Command failed with {exception.GetType().Name}.");
    Console.Error.WriteLine("Connection strings and runtime epoch values are never printed.");
    return 1;
}

static async Task<int> RunAsync(string[] args)
{
    if (args is ["help" or "--help" or "-h"])
    {
        PrintUsage(Console.Out);
        return 0;
    }

    if (args is not [var command])
    {
        PrintUsage(Console.Error);
        return 2;
    }

    return command switch
    {
        "schema-bootstrap-dev" => await BootstrapDevelopmentSchemaAsync(),
        "verify-local" => await VerifyLocalAsync(),
        _ => UnknownCommand(command),
    };
}

static async Task<int> BootstrapDevelopmentSchemaAsync()
{
    RequireLocalProof("schema-bootstrap-dev");
    var migrationConnection = RequireLocalConnectionString(MigrationConnectionVariable);
    var runtimeEpoch = RequireRuntimeEpoch();

    await using var migrationDataSource = NpgsqlDataSource.Create(migrationConnection);
    await VerifyConnectedRoleAsync(migrationDataSource, MigrationOwnerRole);
    var schemaManager = new PostgreSqlDurableRuntimeSchemaManager(migrationDataSource);
    var status = await schemaManager.GetStatusAsync();
    if (!status.IsCompatible)
    {
        throw new DurableRuntimeSchemaException(status);
    }

    if (status.ActiveRuntimeEpoch is not null)
    {
        throw new InvalidOperationException(
            "The durable store already has an active runtime epoch; schema-bootstrap-dev initializes an epoch only once.");
    }

    await schemaManager.InitializeRuntimeEpochAsync(
        runtimeEpoch,
        actorId: "durable-postgresql-local-example",
        reasonCode: "initial-development");

    Console.WriteLine("[schema-bootstrap-dev] active epoch initialized");
    return 0;
}

static async Task<int> VerifyLocalAsync()
{
    RequireLocalProof("verify-local");
    var runtimeConnection = RequireLocalConnectionString(RuntimeConnectionVariable);
    var dispatcherConnection = RequireLocalConnectionString(DispatcherConnectionVariable);
    var runtimeEpoch = RequireRuntimeEpoch();

    await using var runtimeDataSource = NpgsqlDataSource.Create(runtimeConnection);
    await using var dispatcherDataSource = NpgsqlDataSource.Create(dispatcherConnection);
    await VerifyConnectedRoleAsync(runtimeDataSource, RuntimeRole);
    await VerifyConnectedRoleAsync(dispatcherDataSource, DispatcherRole);
    Console.WriteLine("[verify-local] runtime data source configured");
    Console.WriteLine("[verify-local] dispatcher data source configured");

    var statusManager = new PostgreSqlDurableRuntimeSchemaManager(runtimeDataSource);
    var status = await statusManager.GetStatusAsync();
    if (!status.IsCompatible)
    {
        throw new DurableRuntimeSchemaException(status);
    }

    if (status.ActiveRuntimeEpoch != runtimeEpoch)
    {
        throw new InvalidOperationException(
            "The configured runtime epoch is not the active durable epoch; inspect deployment status before retrying.");
    }

    Console.WriteLine("[verify-local] schema compatible");
    Console.WriteLine("[verify-local] active epoch matches configured epoch");

    var workCodec = DurableExampleContracts.CreateWorkCodec();
    var resultCodec = DurableExampleContracts.CreateResultCodec();
    var flowCodec = DurableExampleContracts.CreateFlowCodec();
    var workOptions = new PostgreSqlDurableWorkOptions(runtimeEpoch, status.StoreId);
    var scheduleOptions = new PostgreSqlDurableScheduleOptions("appsurface_durable_runtime");
    var hostBuilder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
    hostBuilder.Services.AddDurableWork<LocalProofWork, LocalProofResult, LocalProofExecutor>(
        DurableExampleContracts.WorkName,
        DurableExampleContracts.WorkVersion,
        DurableProviderSafety.ProviderKeyed,
        workCodec,
        resultCodec);
    hostBuilder.Services.AddDurableFlow(
        DurableExampleContracts.CreateFlowDefinition(),
        flowCodec,
        implementationVersion: "local-proof-v1");
    hostBuilder.Services.AddAppSurfaceDurablePostgreSql(
        dispatcherDataSource,
        runtimeDataSource,
        workOptions,
        scheduleOptions)
        .AddWorkerHost();

    using var host = hostBuilder.Build();
    var services = host.Services;
    var scope = new DurableScopeId("durable-local-proof");
    var work = RequireSuccess(await services.GetRequiredService<IDurableWorkClient>().EnqueueAsync(
        new DurableWorkRequest(
            scope,
            new DurableCommandId("verify-local-work"),
            "verify-local-work",
            DurableExampleContracts.WorkName,
            DurableExampleContracts.WorkVersion,
            workCodec.Encode(new LocalProofWork("local-proof")),
            DurableProviderSafety.ProviderKeyed)));
    Console.WriteLine("[verify-local] Work accepted");

    using var flowTrace = new System.Diagnostics.Activity("durable-example.verify-local-flow").Start();
    _ = RequireSuccess(await services.GetRequiredService<IDurableFlowClient>().StartAsync(
        new DurableFlowStartRequest(
            scope,
            new DurableCommandId("verify-local-flow"),
            "verify-local-flow",
            new DurableFlowInstanceId("verify-local-flow"),
            DurableExampleContracts.FlowId,
            DurableExampleContracts.FlowVersion,
            flowCodec.Encode(new LocalProofFlowContext("local-proof")))));
    Console.WriteLine("[verify-local] Flow accepted with W3C trace context");

    _ = RequireSuccess(await services.GetRequiredService<IDurableScheduleClient>().CreateAsync(
        new DurableScheduleCreateRequest(
            scope,
            new DurableCommandId("verify-local-schedule"),
            "verify-local-schedule",
            new DurableScheduleId("verify-local-every-hour"),
            DurableSchedule.Every(TimeSpan.FromHours(1)),
            DurableScheduleTarget.Work(
                DurableExampleContracts.WorkName,
                DurableExampleContracts.WorkVersion,
                new LocalProofWork("local-proof"),
                workCodec),
            "Local proof schedule")));
    Console.WriteLine("[verify-local] Schedule accepted");

    var pump = await services.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
        new DurableRuntimePumpRequest(maximumItems: 32, timeBudget: TimeSpan.FromSeconds(10), surfaces: DurableRuntimeSurface.All));
    Console.WriteLine($"[verify-local] bounded host pass completed: processed={pump.Processed}");
    var health = await services.GetRequiredService<IDurableRuntimeHealth>().GetAsync();
    if (!health.SchemaCompatible || !health.EpochCompatible)
    {
        throw new InvalidOperationException("The durable runtime health checkpoint is incompatible.");
    }

    var drain = services.GetRequiredService<IDurableRuntimeDrainControl>();
    await drain.BeginDrainAsync();
    await drain.ResumeAsync();
    Console.WriteLine("[verify-local] health and drain checkpoints completed");
    Console.WriteLine("[verify-local] AddWorkerHost explicitly composed; no startup DDL was performed");
    return 0;
}

static T RequireSuccess<T>(DurableOperationResult<T> result)
    where T : class => result.Value ?? throw new InvalidOperationException("The local durable proof did not return an accepted result.");

static Guid RequireRuntimeEpoch()
{
    var rawEpoch = RequireEnvironmentVariable(RuntimeEpochVariable);
    if (!Guid.TryParse(rawEpoch, out var runtimeEpoch) || runtimeEpoch == Guid.Empty)
    {
        throw new InvalidOperationException($"{RuntimeEpochVariable} must contain a non-empty UUID.");
    }

    return runtimeEpoch;
}

static void RequireLocalProof(string command)
{
    var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
    if (!string.Equals(environment, "Development", StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"{command} requires DOTNET_ENVIRONMENT exactly Development.");
    }

    if (!string.Equals(Environment.GetEnvironmentVariable(LocalProofConfirmationVariable), "1", StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"{command} requires {LocalProofConfirmationVariable}=1 to confirm the disposable local proof.");
    }
}

static string RequireLocalConnectionString(string name)
{
    var value = RequireEnvironmentVariable(name);
    NpgsqlConnectionStringBuilder builder;
    try
    {
        builder = new NpgsqlConnectionStringBuilder(value);
    }
    catch (ArgumentException exception)
    {
        throw new InvalidOperationException($"{name} must contain a valid PostgreSQL connection string for this local proof.", exception);
    }

    if (!IsLoopbackHost(builder.Host))
    {
        throw new InvalidOperationException($"{name} must target localhost, 127.0.0.1, or ::1 for this local proof.");
    }

    return value;
}

static bool IsLoopbackHost(string? host) =>
    string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
    string.Equals(host, "127.0.0.1", StringComparison.Ordinal) ||
    string.Equals(host, "::1", StringComparison.Ordinal);

static async Task VerifyConnectedRoleAsync(NpgsqlDataSource dataSource, string expectedRole)
{
    await using var connection = await dataSource.OpenConnectionAsync();
    await using var command = new NpgsqlCommand("SELECT current_user", connection);
    var currentRole = (string?)await command.ExecuteScalarAsync();
    if (!string.Equals(currentRole, expectedRole, StringComparison.Ordinal))
    {
        throw new InvalidOperationException("The local proof connection does not use the tutorial's expected restricted role.");
    }
}

static string RequireEnvironmentVariable(string name)
{
    var value = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrWhiteSpace(value))
    {
        throw new InvalidOperationException($"Required environment variable {name} is not set.");
    }

    return value;
}

static int UnknownCommand(string command)
{
    Console.Error.WriteLine($"Unknown command '{command}'.");
    PrintUsage(Console.Error);
    return 2;
}

static void PrintUsage(TextWriter output) =>
    output.WriteLine(
        """
        Usage:
          dotnet run -- schema-bootstrap-dev
          dotnet run -- verify-local
        """);
