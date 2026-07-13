using System.Text;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

public sealed class PostgreSqlDurableControlClientTests
{
    [Fact]
    public async Task WorkQueryAndCancellation_AreScopedAuditedAndRevisionChecked()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await new PostgreSqlDurableRuntimeSchemaManager(database.DataSource).ApplyAsync();
        var services = new ServiceCollection();
        AddControlWorkRegistration(services);
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            Guid.NewGuid(),
            options => options.SendWakeNotifications = false);
        await using var provider = services.BuildServiceProvider();
        var workClient = provider.GetRequiredService<IDurableWorkClient>();
        var control = provider.GetRequiredService<IDurableWorkControlClient>();
        var scopeId = new DurableScopeId("scope-control");
        var acceptance = await workClient.EnqueueAsync(CreateRequest(scopeId, "command-control"));
        var workId = acceptance.Value!.WorkId;

        var before = await control.GetAsync(new DurableWorkGetRequest(scopeId, workId));
        var wrongScope = await control.GetAsync(new DurableWorkGetRequest(new DurableScopeId("other-scope"), workId));
        var canceled = await control.CancelAsync(new DurableWorkCancelRequest(
            scopeId,
            workId,
            "operator-test",
            "consumer_requested",
            before.Value!.Revision));
        var stale = await control.CancelAsync(new DurableWorkCancelRequest(
            scopeId,
            workId,
            "operator-test",
            "stale_decision",
            before.Value.Revision));
        var terminal = await control.CancelAsync(new DurableWorkCancelRequest(
            scopeId,
            workId,
            "operator-test",
            "duplicate_decision",
            canceled.Value!.Revision));
        var after = await control.GetAsync(new DurableWorkGetRequest(scopeId, workId));

        Assert.True(before.IsSuccess);
        Assert.Equal(DurableWorkState.Ready, before.Value.State);
        Assert.Equal("tests.control", before.Value.WorkName);
        Assert.Null(before.Value.Result);
        Assert.False(wrongScope.IsSuccess);
        Assert.Equal(DurableProblemCodes.WorkNotFound, wrongScope.Problem!.Code);
        Assert.True(canceled.IsSuccess);
        Assert.Equal(DurableWorkCancelOutcome.Applied, canceled.Value.Outcome);
        Assert.Equal(DurableWorkState.CanceledBeforeEffect, canceled.Value.State);
        Assert.False(stale.IsSuccess);
        Assert.Equal(DurableProblemCodes.WorkRevisionConflict, stale.Problem!.Code);
        Assert.True(terminal.IsSuccess);
        Assert.Equal(DurableWorkCancelOutcome.AlreadyTerminal, terminal.Value!.Outcome);
        Assert.Equal(DurableWorkState.CanceledBeforeEffect, after.Value!.State);
        Assert.Null(after.Value.TerminalCode);
    }

    [Fact]
    public async Task ScopeDisable_FencesGenerationAndReturnsTypedProblems()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await new PostgreSqlDurableRuntimeSchemaManager(database.DataSource).ApplyAsync();
        var services = new ServiceCollection();
        AddControlWorkRegistration(services);
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            Guid.NewGuid(),
            options => options.SendWakeNotifications = false);
        await using var provider = services.BuildServiceProvider();
        var workClient = provider.GetRequiredService<IDurableWorkClient>();
        var control = provider.GetRequiredService<IDurableScopeControlClient>();
        var scopeId = new DurableScopeId("scope-disable-control");
        await workClient.EnqueueAsync(CreateRequest(scopeId, "command-disable"));

        var disabled = await control.DisableAsync(new DurableScopeDisableRequest(
            scopeId,
            "operator-test",
            "account_closed",
            expectedGeneration: 1));
        var stale = await control.DisableAsync(new DurableScopeDisableRequest(
            scopeId,
            "operator-test",
            "stale_decision",
            expectedGeneration: 1));
        var missing = await control.DisableAsync(new DurableScopeDisableRequest(
            new DurableScopeId("scope-never-created"),
            "operator-test",
            "account_closed",
            expectedGeneration: 1));

        Assert.True(disabled.IsSuccess);
        Assert.Equal(DurableScopeDisableOutcome.Applied, disabled.Value!.Outcome);
        Assert.Equal(2, disabled.Value.Generation);
        Assert.False(stale.IsSuccess);
        Assert.Equal(DurableProblemCodes.ScopeGenerationConflict, stale.Problem!.Code);
        Assert.False(missing.IsSuccess);
        Assert.Equal(DurableProblemCodes.ScopeNotFound, missing.Problem!.Code);
    }

    [Fact]
    public async Task WorkList_IsScopedPagedPayloadFreeAndFiltersRecoveryEpoch()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await new PostgreSqlDurableRuntimeSchemaManager(database.DataSource).ApplyAsync();
        var initialEpoch = Guid.NewGuid();
        var replacementEpoch = Guid.NewGuid();
        var initialServices = new ServiceCollection();
        AddControlWorkRegistration(initialServices);
        initialServices.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            initialEpoch,
            options => options.SendWakeNotifications = false);
        await using var initialProvider = initialServices.BuildServiceProvider();
        var workClient = initialProvider.GetRequiredService<IDurableWorkClient>();
        var scopeId = new DurableScopeId("scope-control-list");
        for (var index = 0; index < 3; index++)
        {
            Assert.True((await workClient.EnqueueAsync(CreateRequest(scopeId, $"command-control-list-{index}"))).IsSuccess);
        }

        var beforeRotation = await initialProvider.GetRequiredService<IDurableWorkControlClient>().ListAsync(
            new DurableWorkListRequest(scopeId, requiresRecoveryReleaseOnly: true));
        Assert.Empty(beforeRotation.Value!.Items);
        await new PostgreSqlDurableRuntimeSchemaManager(database.DataSource).RotateRuntimeEpochAsync(
            initialEpoch,
            replacementEpoch,
            "operator-test",
            "restore-complete");

        var replacementServices = new ServiceCollection();
        AddControlWorkRegistration(replacementServices);
        replacementServices.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            replacementEpoch,
            options => options.SendWakeNotifications = false);
        await using var replacementProvider = replacementServices.BuildServiceProvider();
        var control = replacementProvider.GetRequiredService<IDurableWorkControlClient>();
        var first = await control.ListAsync(new DurableWorkListRequest(
            scopeId,
            DurableWorkState.Ready,
            requiresRecoveryReleaseOnly: true,
            pageSize: 2));
        var second = await control.ListAsync(new DurableWorkListRequest(
            scopeId,
            DurableWorkState.Ready,
            requiresRecoveryReleaseOnly: true,
            pageSize: 2,
            continuationToken: first.Value!.ContinuationToken));
        var wrongScope = await control.ListAsync(new DurableWorkListRequest(
            new DurableScopeId("scope-control-list-other"),
            requiresRecoveryReleaseOnly: true));

        Assert.Equal(2, first.Value.Items.Count);
        Assert.NotNull(first.Value.ContinuationToken);
        Assert.Single(second.Value!.Items);
        Assert.Null(second.Value.ContinuationToken);
        Assert.All(first.Value.Items.Concat(second.Value.Items), item =>
        {
            Assert.Equal(DurableWorkState.Ready, item.State);
            Assert.True(item.RequiresRecoveryRelease);
            Assert.False(item.CancellationRequested);
            Assert.Equal("tests.control", item.WorkName);
            Assert.True(item.Revision > 0);
        });
        Assert.Empty(wrongScope.Value!.Items);
    }

    private static DurableWorkRequest CreateRequest(DurableScopeId scopeId, string commandId) =>
        new(
            scopeId,
            new DurableCommandId(commandId),
            $"idempotency-{commandId}",
            "tests.control",
            "v1",
            new DurableEncodedPayload(
                "tests.control-work",
                "v1",
                DurableDataClassification.Operational,
                Encoding.UTF8.GetBytes("safe")),
            DurableProviderSafety.ProviderKeyed);

    private static void AddControlWorkRegistration(IServiceCollection services)
    {
        var workCodec = new PostgreSqlOpaqueTestCodec(
            "tests.control-work",
            "v1",
            DurableDataClassification.Operational);
        var resultCodec = new PostgreSqlOpaqueTestCodec(
            "tests.control-result",
            "v1",
            DurableDataClassification.Operational);
        services.AddSingleton<DurableWorkRegistration>(new PostgreSqlOpaqueTestWorkRegistration(
            "tests.control",
            "v1",
            DurableProviderSafety.ProviderKeyed,
            workCodec,
            resultCodec));
    }
}
