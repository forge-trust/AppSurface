using ForgeTrust.AppSurface.Durable.Provider;
using Microsoft.Extensions.DependencyInjection;
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
    }
}
