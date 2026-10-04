namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

public sealed class PostgreSqlDurableWorkExecutionAdmissionCoverageTests
{
    [Fact]
    public async Task LegacyPermitInvocation_RequiresTheExactDatabasePermitAndKeepsPositiveCompatibility()
    {
        await using var lab = await PostgreSqlDurableWorkExecutionPolicyTests.Lab.CreateAsync();
        var accepted = await lab.Client.EnqueueAsync(LegacyRequest());
        Assert.True(accepted.IsSuccess, accepted.Problem?.Problem);

        var candidate = Assert.Single(await lab.Store.DiscoverAsync(10));
        var claim = await lab.Store.TryClaimAsync(candidate, "legacy-admission-worker");
        Assert.NotNull(claim);
        Assert.Null(claim!.Execution);

        var permit = await lab.Store.TryAcquireEffectPermitAsync(claim);
        Assert.NotNull(permit);
        Assert.Null(permit!.Claim.Execution);

        var revision = await lab.ScalarAsync<long>("SELECT revision FROM appsurface_durable.work;");
        var historyCount = await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work_history;");

        Assert.False(await lab.Store.TryAdmitInvocationAsync(permit with { PermitId = Guid.NewGuid() }));

        Assert.Equal(revision, await lab.ScalarAsync<long>("SELECT revision FROM appsurface_durable.work;"));
        Assert.Equal(historyCount, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work_history;"));
        Assert.Equal("granted", await lab.ScalarAsync<string>("SELECT status FROM appsurface_durable.effect_permit;"));
        Assert.Equal(0, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.effect_permit WHERE invocation_admitted_at IS NOT NULL;"));

        Assert.True(await lab.Store.TryAdmitInvocationAsync(permit));

        Assert.Equal(revision, await lab.ScalarAsync<long>("SELECT revision FROM appsurface_durable.work;"));
        Assert.Equal(historyCount, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work_history;"));
        Assert.Equal("granted", await lab.ScalarAsync<string>("SELECT status FROM appsurface_durable.effect_permit;"));
        Assert.Equal(0, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.effect_permit WHERE invocation_admitted_at IS NOT NULL;"));
    }

    [Fact]
    public async Task OptedInInvocation_WithWrongPermitIdDoesNotConsumeTheExactPermit()
    {
        await using var lab = await PostgreSqlDurableWorkExecutionPolicyTests.Lab.CreateAsync();
        var permit = await lab.AdmitReadyAsync(PostgreSqlDurableWorkExecutionPolicyTests.Request("wrong-permit-id"));
        var revision = await lab.ScalarAsync<long>("SELECT revision FROM appsurface_durable.work;");
        var historyCount = await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work_history;");

        Assert.False(await lab.Store.TryAdmitInvocationAsync(permit with { PermitId = Guid.NewGuid() }));

        Assert.Equal(revision, await lab.ScalarAsync<long>("SELECT revision FROM appsurface_durable.work;"));
        Assert.Equal(historyCount, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work_history;"));
        Assert.Equal(1, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.effect_permit WHERE status='granted' AND invocation_admitted_at IS NULL;"));

        Assert.True(await lab.Store.TryAdmitInvocationAsync(permit));

        Assert.Equal(revision, await lab.ScalarAsync<long>("SELECT revision FROM appsurface_durable.work;"));
        Assert.Equal(historyCount + 1, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work_history;"));
        Assert.Equal(1, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.effect_permit WHERE invocation_admitted_at IS NOT NULL;"));
    }

    private static DurableWorkRequest LegacyRequest() => new(
        new("execution-tests"),
        new("legacy-invocation"),
        "key-legacy-invocation",
        PostgreSqlTestWorkContracts.DeleteProviderAccessName(DurableProviderSafety.Idempotent),
        "v1",
        new DurableEncodedPayload("tests.delete-provider-access", "v1", DurableDataClassification.ApprovedApplication, new byte[] { 1, 2, 3 }),
        DurableProviderSafety.Idempotent);
}
