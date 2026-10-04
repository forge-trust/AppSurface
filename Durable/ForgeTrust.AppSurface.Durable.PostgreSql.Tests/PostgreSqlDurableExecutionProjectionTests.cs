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

        // Value: protects=one next-unconsumed-slot projection across executor and inspection boundaries;
        // fails_when=a consumed slot is exposed as the next retry; why_new=pending-only inspection missed claims; seam=none
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            await lab.Database.SetExecutionTimeAsync(Anchor.AddMinutes((attempt - 1) * 5));
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

            await lab.Store.RecordCompletionAsync(renewed, new(PostgreSqlWorkCompletionKind.Retry, "retry", "{}"));
        }
    }
}
