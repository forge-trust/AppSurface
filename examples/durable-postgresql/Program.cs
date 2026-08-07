using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.PostgreSql;
using ForgeTrust.AppSurface.Durable.Provider;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

using var cancellationSource = new CancellationTokenSource();
ConsoleCancelEventHandler cancellationHandler = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellationSource.Cancel();
};
Console.CancelKeyPress += cancellationHandler;
try
{
    return await DurablePostgreSqlLocalExample.RunAsync(args, cancellationSource.Token);
}
finally
{
    Console.CancelKeyPress -= cancellationHandler;
}

/// <summary>Runs the disposable PostgreSQL adoption proof commands.</summary>
/// <remarks>
/// This internal entry point is intentionally exposed to the example's test project so command guards can be verified
/// without connecting to PostgreSQL. It is not a package API.
/// </remarks>
internal static class DurablePostgreSqlLocalExample
{
    private const string MigrationConnectionVariable = "APPSURFACE_DURABLE_MIGRATION_CONNECTION";
    private const string RuntimeConnectionVariable = "APPSURFACE_DURABLE_RUNTIME_CONNECTION";
    private const string DispatcherConnectionVariable = "APPSURFACE_DURABLE_DISPATCHER_CONNECTION";
    private const string RuntimeEpochVariable = "APPSURFACE_DURABLE_RUNTIME_EPOCH";
    private const string LocalProofConfirmationVariable = "APPSURFACE_DURABLE_LOCAL_PROOF";
    private const string MigrationOwnerRole = "appsurface_durable_owner";
    private const string DispatcherRole = "appsurface_durable_dispatcher";
    private const string RuntimeRole = "appsurface_durable_runtime";

    /// <summary>Executes the local-proof command line and returns its process-compatible exit code.</summary>
    internal static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        try
        {
            return await ExecuteAsync(args, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine("Local durable proof canceled. Connection strings and runtime epoch values are never printed.");
            return 130;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            Console.Error.WriteLine($"Command failed with {exception.GetType().Name}.");
            Console.Error.WriteLine("Connection strings and runtime epoch values are never printed.");
            return 1;
        }
    }

    private static async Task<int> ExecuteAsync(string[] args, CancellationToken cancellationToken)
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
            "schema-bootstrap-dev" => await BootstrapDevelopmentSchemaAsync(cancellationToken),
            "verify-local" => await VerifyLocalAsync(cancellationToken),
            _ => UnknownCommand(command),
        };
    }

    static async Task<int> BootstrapDevelopmentSchemaAsync(CancellationToken cancellationToken)
    {
        RequireLocalProof("schema-bootstrap-dev");
        var migrationConnection = RequireLocalConnectionString(MigrationConnectionVariable);
        var runtimeEpoch = RequireRuntimeEpoch();

        await using var migrationDataSource = NpgsqlDataSource.Create(migrationConnection);
        await VerifyConnectedRoleAsync(migrationDataSource, MigrationOwnerRole, cancellationToken);
        var schemaManager = new PostgreSqlDurableRuntimeSchemaManager(migrationDataSource);
        var status = await schemaManager.GetStatusAsync(cancellationToken);
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
            reasonCode: "initial-development",
            cancellationToken);

        Console.WriteLine("[schema-bootstrap-dev] active epoch initialized");
        return 0;
    }

    static async Task<int> VerifyLocalAsync(CancellationToken cancellationToken)
    {
        RequireLocalProof("verify-local");
        var runtimeConnection = RequireLocalConnectionString(RuntimeConnectionVariable);
        var dispatcherConnection = RequireLocalConnectionString(DispatcherConnectionVariable);
        var runtimeEpoch = RequireRuntimeEpoch();

        await using var runtimeDataSource = NpgsqlDataSource.Create(runtimeConnection);
        await using var dispatcherDataSource = NpgsqlDataSource.Create(dispatcherConnection);
        await VerifyConnectedRoleAsync(runtimeDataSource, RuntimeRole, cancellationToken);
        await VerifyConnectedRoleAsync(dispatcherDataSource, DispatcherRole, cancellationToken);
        Console.WriteLine("[verify-local] runtime data source configured");
        Console.WriteLine("[verify-local] dispatcher data source configured");

        var statusManager = new PostgreSqlDurableRuntimeSchemaManager(runtimeDataSource);
        var status = await statusManager.GetStatusAsync(cancellationToken);
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
        var scheduleOptions = new PostgreSqlDurableScheduleOptions(RuntimeRole);
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
        _ = RequireSuccess(await services.GetRequiredService<IDurableWorkClient>().EnqueueAsync(
            new DurableWorkRequest(
                scope,
                new DurableCommandId("verify-local-work"),
                "verify-local-work",
                DurableExampleContracts.WorkName,
                DurableExampleContracts.WorkVersion,
                workCodec.Encode(new LocalProofWork("local-proof")),
                DurableProviderSafety.ProviderKeyed), cancellationToken));
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
                flowCodec.Encode(new LocalProofFlowContext("local-proof"))), cancellationToken));
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
                "Local proof schedule"), cancellationToken));
        Console.WriteLine("[verify-local] Schedule accepted");

        var pump = await services.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(maximumItems: 32, timeBudget: TimeSpan.FromSeconds(10), surfaces: DurableRuntimeSurface.All),
            cancellationToken);
        Console.WriteLine($"[verify-local] bounded host pass completed: processed={pump.Processed}");
        var health = await services.GetRequiredService<IDurableRuntimeHealth>().GetAsync(cancellationToken);
        EnsureRuntimeHealthIsCompatible(health);

        var drain = services.GetRequiredService<IDurableRuntimeDrainControl>();
        await drain.BeginDrainAsync(cancellationToken);
        await drain.ResumeAsync(cancellationToken);
        Console.WriteLine("[verify-local] health and drain checkpoints completed");

        var schemaBeforeWorkerStart = await statusManager.GetStatusAsync(cancellationToken);
        var catalogBeforeWorkerStart = await ReadDurableCatalogFingerprintAsync(runtimeDataSource, cancellationToken);
        var healthBeforeWorkerStart = await services.GetRequiredService<IDurableRuntimeHealth>().GetAsync(cancellationToken);
        await host.StartAsync(cancellationToken);
        try
        {
            await WaitForHostedWorkerSweepAsync(
                services.GetRequiredService<IDurableRuntimeHealth>(),
                healthBeforeWorkerStart.LastSuccessfulSweepAtUtc,
                cancellationToken);
            var schemaAfterWorkerStart = await statusManager.GetStatusAsync(cancellationToken);
            var catalogAfterWorkerStart = await ReadDurableCatalogFingerprintAsync(runtimeDataSource, cancellationToken);
            EnsureWorkerHostDidNotChangeSchema(
                schemaBeforeWorkerStart,
                schemaAfterWorkerStart,
                catalogBeforeWorkerStart,
                catalogAfterWorkerStart);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }

        Console.WriteLine("[verify-local] AddWorkerHost started under the restricted roles; no startup DDL was performed");
        return 0;
    }

    /// <summary>Confirms that worker-host startup left the durable schema metadata and catalog unchanged.</summary>
    /// <exception cref="InvalidOperationException">Thrown when startup changes durable schema identity or catalog metadata.</exception>
    internal static void EnsureWorkerHostDidNotChangeSchema(
        DurableRuntimeSchemaStatus before,
        DurableRuntimeSchemaStatus after,
        string catalogBefore,
        string catalogAfter)
    {
        if (before.StoreId != after.StoreId
            || before.ActiveRuntimeEpoch != after.ActiveRuntimeEpoch
            || before.InstalledVersion != after.InstalledVersion
            || before.RequiredVersion != after.RequiredVersion
            || !before.AppliedVersions.SequenceEqual(after.AppliedVersions)
            || !string.Equals(catalogBefore, catalogAfter, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("AddWorkerHost changed durable schema catalog or migration metadata. Worker startup must not apply DDL.");
        }
    }

    /// <summary>Confirms that the runtime health checkpoint authorizes both the schema and configured recovery epoch.</summary>
    /// <exception cref="InvalidOperationException">Thrown when the durable schema or recovery epoch is incompatible.</exception>
    internal static void EnsureRuntimeHealthIsCompatible(DurableRuntimeHealthSnapshot health)
    {
        ArgumentNullException.ThrowIfNull(health);
        if (!health.SchemaCompatible || !health.EpochCompatible)
        {
            throw new InvalidOperationException("The durable runtime health checkpoint is incompatible.");
        }
    }

    /// <summary>Waits for a hosted worker pass that is newer than the supplied baseline.</summary>
    /// <exception cref="TimeoutException">Thrown when the hosted worker does not complete before its bounded deadline.</exception>
    internal static async Task WaitForHostedWorkerSweepAsync(
        IDurableRuntimeHealth health,
        DateTimeOffset? baselineSuccessfulSweep,
        CancellationToken cancellationToken,
        TimeSpan? waitTimeout = null)
    {
        const int defaultTimeoutSeconds = 10;
        const int pollMilliseconds = 250;
        var effectiveWaitTimeout = waitTimeout ?? TimeSpan.FromSeconds(defaultTimeoutSeconds);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(effectiveWaitTimeout);
        try
        {
            while (true)
            {
                var snapshot = await health.GetAsync(deadline.Token);
                if (snapshot.WorkerInstanceId is not null
                    && snapshot.LastSuccessfulSweepAtUtc is { } successfulSweep
                    && (baselineSuccessfulSweep is null || successfulSweep > baselineSuccessfulSweep))
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(pollMilliseconds), deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(FormattableString.Invariant(
                $"AddWorkerHost did not complete a bounded hosted worker pass within {effectiveWaitTimeout.TotalSeconds:0.###} seconds."));
        }
    }

    static async Task<string> ReadDurableCatalogFingerprintAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        const string sql = """
        WITH catalog_entry AS
        (
            SELECT format('relation|%s|%s|%s|%s|%s', relation.relkind, relation.relname, relation.relowner, relation.relrowsecurity, relation.relforcerowsecurity) AS value
            FROM pg_catalog.pg_class AS relation
            JOIN pg_catalog.pg_namespace AS namespace_value ON namespace_value.oid = relation.relnamespace
            WHERE namespace_value.nspname = 'appsurface_durable'

            UNION ALL

            SELECT format('attribute|%s|%s|%s|%s|%s', relation.relname, attribute.attname, attribute.atttypid::pg_catalog.regtype, attribute.attnotnull, attribute.atthasdef)
            FROM pg_catalog.pg_attribute AS attribute
            JOIN pg_catalog.pg_class AS relation ON relation.oid = attribute.attrelid
            JOIN pg_catalog.pg_namespace AS namespace_value ON namespace_value.oid = relation.relnamespace
            WHERE namespace_value.nspname = 'appsurface_durable'
              AND attribute.attnum > 0
              AND NOT attribute.attisdropped

            UNION ALL

            SELECT format('function|%s|%s|%s', routine.proname, pg_catalog.pg_get_function_identity_arguments(routine.oid), routine.proowner)
            FROM pg_catalog.pg_proc AS routine
            JOIN pg_catalog.pg_namespace AS namespace_value ON namespace_value.oid = routine.pronamespace
            WHERE namespace_value.nspname = 'appsurface_durable'
        )
        SELECT coalesce(string_agg(value, E'\n' ORDER BY value), '')
        FROM catalog_entry;
        """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return (string?)await command.ExecuteScalarAsync(cancellationToken) ?? string.Empty;
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

    static async Task VerifyConnectedRoleAsync(NpgsqlDataSource dataSource, string expectedRole, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT current_user", connection);
        var currentRole = (string?)await command.ExecuteScalarAsync(cancellationToken);
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
}
