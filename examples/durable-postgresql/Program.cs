using System.Globalization;
using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.PostgreSql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

internal static class Program
{
    private const string DefaultScope = "durable-example";

    public static async Task<int> Main(string[] args)
    {
        var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "help";
        if (command is "help" or "--help" or "-h")
        {
            PrintHelp();
            return 0;
        }

        if (command is "schema-apply-dev" or "schema-status")
        {
            return await RunSchemaCommandAsync(command).ConfigureAwait(false);
        }

        var connectionString = RequireEnvironmentVariable("APPSURFACE_DURABLE_CONNECTION");
        var runtimeEpoch = RequireRuntimeEpoch();
        var scopeId = new DurableScopeId(
            Environment.GetEnvironmentVariable("APPSURFACE_DURABLE_SCOPE") ?? DefaultScope);
        var workCodec = DurableExampleContracts.CreateWorkCodec();
        var resultCodec = DurableExampleContracts.CreateResultCodec();

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        if (command == "worker")
        {
            return await RunWorkerAsync(dataSource, runtimeEpoch, workCodec, resultCodec).ConfigureAwait(false);
        }

        var services = new ServiceCollection();
        RegisterDurableServices(services, dataSource, runtimeEpoch, workCodec, resultCodec);
        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IDurableRuntimeSchemaManager>()
            .ValidateAsync()
            .ConfigureAwait(false);

        return command switch
        {
            "enqueue" => await EnqueueAsync(provider, scopeId, workCodec, args, manualResolution: false)
                .ConfigureAwait(false),
            "enqueue-manual" => await EnqueueAsync(provider, scopeId, workCodec, args, manualResolution: true)
                .ConfigureAwait(false),
            "pump" => await PumpAsync(provider).ConfigureAwait(false),
            "status" => await GetStatusAsync(provider, scopeId, args).ConfigureAwait(false),
            "cancel" => await CancelAsync(provider, scopeId, args).ConfigureAwait(false),
            "resolve-not-applied" => await ResolveNotAppliedAsync(provider, scopeId, args).ConfigureAwait(false),
            "schedule" => await CreateScheduleAsync(provider, scopeId, args).ConfigureAwait(false),
            "schedule-status" => await GetScheduleStatusAsync(provider, scopeId, args).ConfigureAwait(false),
            _ => throw new ArgumentException($"Unknown command '{command}'. Run with 'help' to list commands."),
        };
    }

    private static async Task<int> RunWorkerAsync(
        NpgsqlDataSource dataSource,
        Guid runtimeEpoch,
        IDurablePayloadCodec<ResourceCleanupWork> workCodec,
        IDurablePayloadCodec<ResourceCleanupResult> resultCodec)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        RegisterDurableServices(builder.Services, dataSource, runtimeEpoch, workCodec, resultCodec)
            .AddWorkerHost();
        using var host = builder.Build();
        Console.WriteLine("Starting the continuous durable worker. Press Ctrl+C to stop.");
        await host.RunAsync().ConfigureAwait(false);
        return 0;
    }

    private static AppSurfaceDurablePostgreSqlBuilder RegisterDurableServices(
        IServiceCollection services,
        NpgsqlDataSource dataSource,
        Guid runtimeEpoch,
        IDurablePayloadCodec<ResourceCleanupWork> workCodec,
        IDurablePayloadCodec<ResourceCleanupResult> resultCodec)
    {
        services.AddDurableWork<ResourceCleanupWork, ResourceCleanupResult, ResourceCleanupExecutor>(
            DurableExampleContracts.WorkName,
            DurableExampleContracts.WorkVersion,
            DurableProviderSafety.ProviderKeyed,
            workCodec,
            resultCodec);
        services.AddDurableWork<ResourceCleanupWork, ResourceCleanupResult, ResourceCleanupExecutor>(
            DurableExampleContracts.ManualResolutionWorkName,
            DurableExampleContracts.WorkVersion,
            DurableProviderSafety.ManualResolution,
            workCodec,
            resultCodec);
        return services.AddAppSurfaceDurablePostgreSql(
            dataSource,
            runtimeEpoch,
            options => options.WorkerId = $"durable-example:{Environment.ProcessId}");
    }

    private static async Task<int> RunSchemaCommandAsync(string command)
    {
        var connectionString = RequireEnvironmentVariable("APPSURFACE_DURABLE_MIGRATION_CONNECTION");
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var manager = new PostgreSqlDurableRuntimeSchemaManager(dataSource);

        if (command == "schema-apply-dev")
        {
            var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
            if (!string.Equals(environment, Environments.Development, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "schema-apply-dev is intentionally limited to DOTNET_ENVIRONMENT=Development. "
                    + "Use the AppSurface CLI deployment workflow outside local development.");
            }

            var applied = await manager.ApplyAsync().ConfigureAwait(false);
            Console.WriteLine(
                $"Schema moved from v{applied.PreviousVersion} to v{applied.CurrentVersion}; "
                + $"applied: {FormatVersions(applied.AppliedVersions)}.");
            return 0;
        }

        var status = await manager.GetStatusAsync().ConfigureAwait(false);
        Console.WriteLine(
            $"Schema v{status.InstalledVersion}; required v{status.RequiredVersion}; "
            + $"compatibility: {status.Compatibility}; pending: {FormatVersions(status.PendingVersions)}.");
        return status.IsCompatible ? 0 : 2;
    }

    private static async Task<int> EnqueueAsync(
        IServiceProvider services,
        DurableScopeId scopeId,
        IDurablePayloadCodec<ResourceCleanupWork> workCodec,
        string[] args,
        bool manualResolution)
    {
        var resourceCode = RequireArgument(args, 1, "resource-code");
        var commandId = args.Length > 2
            ? new DurableCommandId(args[2])
            : DurableCommandId.New();
        var workName = manualResolution
            ? DurableExampleContracts.ManualResolutionWorkName
            : DurableExampleContracts.WorkName;
        var providerSafety = manualResolution
            ? DurableProviderSafety.ManualResolution
            : DurableProviderSafety.ProviderKeyed;
        var result = await services.GetRequiredService<IDurableWorkClient>()
            .EnqueueAsync(new DurableWorkRequest(
                scopeId,
                commandId,
                commandId.Value,
                workName,
                DurableExampleContracts.WorkVersion,
                workCodec.Encode(new ResourceCleanupWork(resourceCode)),
                providerSafety))
            .ConfigureAwait(false);
        var acceptance = RequireSuccess(result);
        Console.WriteLine(
            $"{acceptance.Kind}: work {acceptance.WorkId.Value}, revision {acceptance.Revision}, "
            + $"command {acceptance.CommandId.Value}.");
        return 0;
    }

    private static async Task<int> ResolveNotAppliedAsync(
        IServiceProvider services,
        DurableScopeId scopeId,
        string[] args)
    {
        var workId = new DurableWorkId(RequireArgument(args, 1, "work-id"));
        var expectedRevision = long.Parse(
            RequireArgument(args, 2, "expected-revision"),
            CultureInfo.InvariantCulture);
        var commandId = args.Length > 3
            ? new DurableCommandId(args[3])
            : DurableCommandId.New();
        var result = await services.GetRequiredService<IDurableWorkOperatorClient>()
            .ResolveAsync(new DurableWorkManualResolutionRequest(
                scopeId,
                workId,
                commandId,
                "durable-example-operator",
                "tutorial-provider-proof",
                expectedRevision,
                DurableManualResolutionKind.ProvenNotApplied))
            .ConfigureAwait(false);
        var resolution = RequireSuccess(result);
        Console.WriteLine(
            $"Manual resolution {resolution.Outcome}: work {resolution.WorkId.Value}, "
            + $"state={resolution.State}, revision={resolution.Revision}.");
        return 0;
    }

    private static async Task<int> PumpAsync(IServiceProvider services)
    {
        var result = await services.GetRequiredService<IDurableRuntimePump>()
            .RunOnceAsync(new DurableRuntimePumpRequest(
                maximumItems: 32,
                timeBudget: TimeSpan.FromSeconds(10),
                surfaces: DurableRuntimeSurface.All))
            .ConfigureAwait(false);
        Console.WriteLine(
            $"Pump discovered={result.Discovered}, claimed={result.Claimed}, processed={result.Processed}, "
            + $"deferred={result.Deferred}, failed={result.Failed}, hasMore={result.HasMore}.");
        return result.Failed == 0 ? 0 : 2;
    }

    private static async Task<int> GetStatusAsync(
        IServiceProvider services,
        DurableScopeId scopeId,
        string[] args)
    {
        var workId = new DurableWorkId(RequireArgument(args, 1, "work-id"));
        var result = await services.GetRequiredService<IDurableWorkControlClient>()
            .GetAsync(new DurableWorkGetRequest(scopeId, workId))
            .ConfigureAwait(false);
        var snapshot = RequireSuccess(result);
        Console.WriteLine(
            $"Work {snapshot.WorkId.Value}: state={snapshot.State}, revision={snapshot.Revision}, "
            + $"attempt={snapshot.AttemptNumber}, terminalCode={snapshot.TerminalCode ?? "none"}.");
        return 0;
    }

    private static async Task<int> CancelAsync(
        IServiceProvider services,
        DurableScopeId scopeId,
        string[] args)
    {
        var workId = new DurableWorkId(RequireArgument(args, 1, "work-id"));
        var expectedRevision = long.Parse(
            RequireArgument(args, 2, "expected-revision"),
            CultureInfo.InvariantCulture);
        var result = await services.GetRequiredService<IDurableWorkControlClient>()
            .CancelAsync(new DurableWorkCancelRequest(
                scopeId,
                workId,
                "durable-example-operator",
                "tutorial-cancel",
                expectedRevision))
            .ConfigureAwait(false);
        var canceled = RequireSuccess(result);
        Console.WriteLine(
            $"Cancel {canceled.Outcome}: work {canceled.WorkId.Value}, state={canceled.State}, "
            + $"revision={canceled.Revision}.");
        return 0;
    }

    private static async Task<int> CreateScheduleAsync(
        IServiceProvider services,
        DurableScopeId scopeId,
        string[] args)
    {
        var scheduleId = new DurableScheduleId(RequireArgument(args, 1, "schedule-id"));
        var resourceCode = RequireArgument(args, 2, "resource-code");
        var expression = args.Length > 3 ? args[3] : "0 9 * * MON-FRI";
        var timeZone = args.Length > 4 ? args[4] : "Etc/UTC";
        var grammar = args.Length > 5 && string.Equals(args[5], "seconds", StringComparison.OrdinalIgnoreCase)
            ? CronGrammar.IncludeSeconds
            : CronGrammar.Standard;
        var schedule = DurableSchedule.Cron(expression, timeZone, grammar);
        var client = services.GetRequiredService<IDurableScheduleClient>();
        var explanation = RequireSuccess(await client.ExplainNextOccurrencesAsync(
            new DurableScheduleExplainRequest(scopeId, scheduleId, schedule, DateTimeOffset.UtcNow))
            .ConfigureAwait(false));
        var commandId = DurableCommandId.New();
        var created = RequireSuccess(await client.CreateAsync(new DurableScheduleCreateRequest(
            scopeId,
            commandId,
            commandId.Value,
            scheduleId,
            schedule,
            DurableScheduleTarget.Work(
                DurableExampleContracts.WorkName,
                DurableExampleContracts.WorkVersion,
                new ResourceCleanupWork(resourceCode)),
            "Example resource cleanup"))
            .ConfigureAwait(false));

        Console.WriteLine(
            $"{created.Code}: schedule {created.ScheduleId.Value}, generation={created.Generation}, "
            + $"revision={created.Revision}.");
        Console.WriteLine(
            $"Cron dialect={explanation.CronDialect}, overlap={explanation.OverlapPolicy.Kind}, "
            + $"misfire={explanation.MisfirePolicy.Kind}.");
        var occurrences = explanation.NextOccurrencesUtc
            .Select(static value => value.ToString("O", CultureInfo.InvariantCulture));
        Console.WriteLine($"Next UTC occurrences: {string.Join(", ", occurrences)}");
        return 0;
    }

    private static async Task<int> GetScheduleStatusAsync(
        IServiceProvider services,
        DurableScopeId scopeId,
        string[] args)
    {
        var scheduleId = new DurableScheduleId(RequireArgument(args, 1, "schedule-id"));
        var snapshot = RequireSuccess(await services.GetRequiredService<IDurableScheduleClient>()
            .GetAsync(scopeId, scheduleId)
            .ConfigureAwait(false));
        Console.WriteLine(
            $"Schedule {snapshot.ScheduleId.Value}: state={snapshot.State}, generation={snapshot.Generation}, "
            + $"revision={snapshot.Revision}, "
            + $"next={snapshot.NextOccurrenceUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "none"}, "
            + $"overlap={snapshot.Schedule.OverlapPolicy.Kind}, misfire={snapshot.Schedule.MisfirePolicy.Kind}.");
        return 0;
    }

    private static T RequireSuccess<T>(DurableOperationResult<T> result)
        where T : class
    {
        if (result.IsSuccess && result.Value is { } value)
        {
            return value;
        }

        var problem = result.Problem
            ?? throw new InvalidOperationException("A durable operation failed without an actionable problem.");
        throw new InvalidOperationException(
            $"{problem.Code}: {problem.Problem} Cause: {problem.Cause} Fix: {problem.Fix}");
    }

    private static Guid RequireRuntimeEpoch()
    {
        var value = RequireEnvironmentVariable("APPSURFACE_DURABLE_RUNTIME_EPOCH");
        return Guid.TryParse(value, out var epoch) && epoch != Guid.Empty
            ? epoch
            : throw new InvalidOperationException(
                "APPSURFACE_DURABLE_RUNTIME_EPOCH must be a stable, non-empty GUID stored outside the durable database.");
    }

    private static string RequireEnvironmentVariable(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Set the required {name} environment variable.");

    private static string RequireArgument(string[] args, int index, string name) =>
        args.Length > index && !string.IsNullOrWhiteSpace(args[index])
            ? args[index]
            : throw new ArgumentException($"Missing required {name} argument.");

    private static string FormatVersions(IReadOnlyList<int> versions) =>
        versions.Count == 0 ? "none" : string.Join(",", versions);

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            Durable PostgreSQL example

              schema-status
              schema-apply-dev
              enqueue <resource-code> [command-id]
              enqueue-manual <resource-code> [command-id]
              pump
              status <work-id>
              cancel <work-id> <expected-revision>
              resolve-not-applied <work-id> <expected-revision> [command-id]
              schedule <schedule-id> <resource-code> [cron] [iana-zone] [seconds]
              schedule-status <schedule-id>
              worker

            See README.md for environment variables, role grants, and safe production deployment.
            """);
    }
}
