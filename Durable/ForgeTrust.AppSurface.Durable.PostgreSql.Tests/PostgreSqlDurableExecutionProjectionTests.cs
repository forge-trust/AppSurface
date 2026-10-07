using System.Collections.Concurrent;
using ForgeTrust.AppSurface.Durable.Provider;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using static ForgeTrust.AppSurface.Durable.PostgreSql.Tests.PostgreSqlDurableWorkExecutionPolicyTests;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

public sealed class PostgreSqlDurableExecutionProjectionTests
{
    [Fact]
    public async Task Get_and_list_project_the_locked_execution_snapshot_and_keep_legacy_rows_null()
    {
        await using var lab = await Lab.CreateAsync();
        var workName = PostgreSqlTestWorkContracts.DeleteProviderAccessName(DurableProviderSafety.Idempotent);
        var registration = lab.Registry.GetRequired(workName, "v1");
        var schema = new PostgreSqlDurableRuntimeSchemaManager(lab.Database.DataSource);
        var status = await schema.GetStatusAsync();
        var services = new ServiceCollection();
        services.AddSingleton<DurableWorkRegistration>(registration);
        services.AddAppSurfaceDurablePostgreSql(
            lab.Database.DataSource,
            lab.Database.CreateDataSource(),
            new PostgreSqlDurableWorkOptions(lab.Epoch, status.StoreId),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options =>
            {
                options.WorkerId = "execution-projection-test-worker";
                options.SendWakeNotifications = false;
            });
        await using var provider = services.BuildServiceProvider();

        var optIn = await provider.GetRequiredService<IDurableWorkClient>().EnqueueAsync(
            Request("projection-opt-in", deadline: Anchor.AddMinutes(30), circuitMinutes: 240, offsets: [0, 5]));
        var legacy = await provider.GetRequiredService<IDurableWorkClient>().EnqueueAsync(new DurableWorkRequest(
            new DurableScopeId("execution-tests"),
            new DurableCommandId("projection-legacy"),
            "key-projection-legacy",
            workName,
            "v1",
            new PostgreSqlOpaqueTestCodec("tests.delete-provider-access", "v1").Encode("legacy"u8.ToArray()),
            DurableProviderSafety.Idempotent));

        Assert.True(optIn.IsSuccess, optIn.Problem?.Problem);
        Assert.True(legacy.IsSuccess, legacy.Problem?.Problem);
        var control = provider.GetRequiredService<IDurableWorkControlClient>();
        var listed = await control.ListAsync(new DurableWorkListRequest(new("execution-tests"), pageSize: 10));

        Assert.True(listed.IsSuccess, listed.Problem?.Problem);
        var optedItem = Assert.Single(listed.Value!.Items, item => item.WorkId == optIn.Value!.WorkId);
        var legacyItem = Assert.Single(listed.Value.Items, item => item.WorkId == legacy.Value!.WorkId);
        Assert.NotNull(optedItem.Execution);
        Assert.Equal(Anchor, optedItem.Execution!.AcceptedAtUtc);
        Assert.Equal(Anchor, optedItem.Execution.NextEligibilityAtUtc);
        Assert.Equal(Anchor.AddMinutes(30), optedItem.Execution.AdmissionCutoffUtc);
        Assert.Equal([TimeSpan.Zero, TimeSpan.FromMinutes(5)], optedItem.Execution.Policy.AttemptPlan!.ElapsedOffsets);
        Assert.Null(legacyItem.Execution);

        var inspected = await control.GetAsync(new DurableWorkGetRequest(new("execution-tests"), optIn.Value!.WorkId));
        var inspectedLegacy = await control.GetAsync(new DurableWorkGetRequest(new("execution-tests"), legacy.Value!.WorkId));
        Assert.True(inspected.IsSuccess, inspected.Problem?.Problem);
        Assert.True(inspectedLegacy.IsSuccess, inspectedLegacy.Problem?.Problem);
        Assert.NotNull(inspected.Value!.Execution);
        Assert.Equal(optedItem.Execution, inspected.Value.Execution);
        Assert.Null(inspectedLegacy.Value!.Execution);

        // Value: protects=one next-unconsumed-slot projection across executor and inspection boundaries;
        // fails_when=a consumed slot is exposed as the next retry; why_new=pending-only inspection missed claims; seam=none
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            await lab.Database.SetExecutionTimeAsync(Anchor.AddMinutes((attempt - 1) * 5d));
            var candidate = Assert.Single(await lab.Store.DiscoverAsync(10), item => item.WorkId == optIn.Value!.WorkId);
            var claim = Assert.IsType<PostgreSqlDurableWorkClaim>(await lab.Store.TryClaimAsync(candidate, "projection-worker"));
            DateTimeOffset? expectedNext = attempt == 1 ? Anchor.AddMinutes(5) : null;
            Assert.Equal(expectedNext, claim.Execution!.NextEligibilityAtUtc);
            Assert.Equal(claim.Execution, claim.ToProviderClaim().ToExecutionContext().Execution);

            var permit = Assert.IsType<PostgreSqlEffectPermit>(await lab.Store.TryAcquireEffectPermitAsync(claim));
            var replay = Assert.IsType<PostgreSqlEffectPermit>(await lab.Store.TryAcquireEffectPermitAsync(permit.Claim));
            var renewed = Assert.IsType<PostgreSqlDurableWorkClaim>(await lab.Store.RenewLeaseAsync(replay.Claim));
            Assert.Equal(claim.Execution, permit.Claim.Execution);
            Assert.Equal(claim.Execution, replay.Claim.Execution);
            Assert.Equal(claim.Execution, renewed.Execution);
            var currentInspection = await control.GetAsync(new DurableWorkGetRequest(new("execution-tests"), optIn.Value!.WorkId));
            Assert.True(currentInspection.IsSuccess, currentInspection.Problem?.Problem);
            Assert.Equal(claim.Execution, currentInspection.Value!.Execution);
            var currentList = await control.ListAsync(new DurableWorkListRequest(new("execution-tests"), pageSize: 10));
            Assert.True(currentList.IsSuccess, currentList.Problem?.Problem);
            Assert.Equal(claim.Execution, Assert.Single(currentList.Value!.Items,
                item => item.WorkId == optIn.Value.WorkId).Execution);

            await lab.Store.RecordCompletionAsync(renewed, new(PostgreSqlWorkCompletionKind.Retry, "retry", "{}"));
        }
    }
    // Value: protects=bounded authorized listing without per-item database round trips;
    // fails_when=policy projection adds queries for every Work; why_new=two-item correctness missed query amplification; seam=Npgsql logging
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task List_keeps_database_command_count_constant_for_full_pages(int policyKind)
    {
        await using var lab = await Lab.CreateAsync();
        var workName = PostgreSqlTestWorkContracts.DeleteProviderAccessName(DurableProviderSafety.Idempotent);
        var scope = new DurableScopeId("execution-tests");
        for (var index = 0; index < 501; index++)
        {
            var request = policyKind switch
            {
                0 => new DurableWorkRequest(scope, new DurableCommandId($"page-{index}"), $"page-key-{index}",
                    workName, "v1", new PostgreSqlOpaqueTestCodec("tests.delete-provider-access", "v1").Encode("legacy"u8.ToArray()),
                    DurableProviderSafety.Idempotent),
                1 => Request($"page-{index}", deadline: Anchor.AddMinutes(30), circuitMinutes: 240, offsets: [0, 5]),
                _ => DurableWorkRequest.CreateWithExecutionPolicy(scope, new DurableCommandId($"page-{index}"),
                    $"page-key-{index}", workName, "v1",
                    new PostgreSqlOpaqueTestCodec("tests.delete-provider-access", "v1").Encode("deadline-only"u8.ToArray()),
                    DurableProviderSafety.Idempotent, DurableWorkExecutionPolicy.FromRetryPolicy(DurableWorkRetryPolicy.Default),
                    new DurableExecutionDeadline(Anchor.AddMinutes(30))),
            };
            var accepted = await lab.Client.EnqueueAsync(request);
            Assert.True(accepted.IsSuccess, accepted.Problem?.Problem);
        }

        using var commands = new CommandCounter();
        using var logging = LoggerFactory.Create(builder => builder.AddProvider(commands).SetMinimumLevel(LogLevel.Information));
        var runtimeBuilder = new NpgsqlDataSourceBuilder(lab.Database.ConnectionString);
        runtimeBuilder.UseLoggerFactory(logging);
        await using var runtime = runtimeBuilder.Build();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(lab.Database.DataSource);
        var status = await schema.GetStatusAsync();
        var services = new ServiceCollection();
        services.AddSingleton<DurableWorkRegistration>(lab.Registry.GetRequired(workName, "v1"));
        services.AddAppSurfaceDurablePostgreSql(lab.Database.DataSource, runtime,
            new PostgreSqlDurableWorkOptions(lab.Epoch, status.StoreId), new PostgreSqlDurableScheduleOptions("appsurface"),
            options => { options.WorkerId = "list-command-count"; options.SendWakeNotifications = false; });
        await using var provider = services.BuildServiceProvider();
        var control = provider.GetRequiredService<IDurableWorkControlClient>();
        // Warm up schema validation and the connection, then compare the public requests on the same inputs.
        Assert.True((await control.ListAsync(new DurableWorkListRequest(scope, pageSize: 1))).IsSuccess);
        commands.Clear();
        var single = await control.ListAsync(new DurableWorkListRequest(scope, pageSize: 1));
        var singleCount = commands.Count;
        commands.Clear();
        var page = await control.ListAsync(new DurableWorkListRequest(scope, pageSize: 500));
        var fullCount = commands.Count;
        Assert.True(single.IsSuccess);
        Assert.True(page.IsSuccess);
        Assert.True(singleCount > 0, "The Npgsql command logger must observe actual database commands.");
        Assert.Equal(singleCount, fullCount);
        Assert.Single(single.Value!.Items);
        Assert.Equal(500, page.Value!.Items.Count);
        Assert.NotNull(page.Value.ContinuationToken);
        foreach (var item in page.Value.Items)
        {
            if (policyKind == 0) Assert.Null(item.Execution);
            else
            {
                Assert.NotNull(item.Execution);
                Assert.Equal(Anchor, item.Execution.AcceptedAtUtc);
                Assert.Equal(Anchor, item.Execution.NextEligibilityAtUtc);
                Assert.Equal(Anchor.AddMinutes(30), item.Execution.AdmissionCutoffUtc);
                Assert.Equal(policyKind == 1, item.Execution.Policy.AttemptPlan is not null);
            }
        }
        var last = await control.ListAsync(new DurableWorkListRequest(scope, pageSize: 500,
            continuationToken: page.Value.ContinuationToken));
        Assert.True(last.IsSuccess);
        Assert.Single(last.Value!.Items);
        Assert.Null(last.Value.ContinuationToken);
        Assert.DoesNotContain(last.Value.Items[0].WorkId, page.Value.Items.Select(item => item.WorkId));
    }

    private sealed class CommandCounter : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _commands = new();
        internal int Count => _commands.Count;
        internal void Clear() => _commands.Clear();
        public ILogger CreateLogger(string categoryName) => new CommandLogger(_commands, categoryName == "Npgsql.Command");
        public void Dispose() { }

        private sealed class CommandLogger(ConcurrentQueue<string> commands, bool enabled) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => enabled && logLevel == LogLevel.Information;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel) && state is IEnumerable<KeyValuePair<string, object?>> values)
                {
                    foreach (var value in values)
                    {
                        if (value.Key == "CommandText" && value.Value is string sql) commands.Enqueue(sql);
                    }
                }
            }
        }
    }

}
