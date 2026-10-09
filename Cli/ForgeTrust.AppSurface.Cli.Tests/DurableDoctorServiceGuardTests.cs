using System.Data;
using ForgeTrust.AppSurface.Cli;
using ForgeTrust.AppSurface.Durable.PostgreSql;
using Npgsql;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Verifies that corrupted or contradictory database facts cannot become a completed doctor observation.</summary>
public sealed class DurableDoctorServiceGuardTests
{
    // Value: protects=runtime facts must agree with the authoritative schema snapshot; fails_when=missing duplicate or invalid rows are accepted; why_new=normal fixture tests preserve database constraints; seam=ReadRuntimeAsync
    [Theory]
    [InlineData("missing-metadata")]
    [InlineData("duplicate-metadata")]
    [InlineData("empty-store")]
    [InlineData("store-mismatch")]
    [InlineData("empty-active-epoch")]
    [InlineData("epoch-mismatch")]
    [InlineData("duplicate-worker")]
    [InlineData("empty-worker-epoch")]
    [InlineData("minimum-heartbeat")]
    [InlineData("maximum-heartbeat")]
    public async Task Runtime_reader_rejects_corrupted_or_contradictory_snapshot_facts(string mutation)
    {
        await using var fixture = await DurableDoctorFixture.CreateAsync();
        var observation = await fixture.InspectAsync();
        var schema = observation.Schema!;
        await fixture.SeedHeartbeatAsync(age: TimeSpan.FromSeconds(2));
        await fixture.MutateAsync(mutation switch
        {
            "missing-metadata" => "DELETE FROM appsurface_durable.store_metadata",
            "duplicate-metadata" => "ALTER TABLE appsurface_durable.store_metadata DROP CONSTRAINT store_metadata_pkey; INSERT INTO appsurface_durable.store_metadata SELECT * FROM appsurface_durable.store_metadata",
            "empty-store" => "ALTER TABLE appsurface_durable.store_metadata DROP CONSTRAINT store_metadata_store_id_check; UPDATE appsurface_durable.store_metadata SET store_id='00000000-0000-0000-0000-000000000000'",
            "store-mismatch" => "UPDATE appsurface_durable.store_metadata SET store_id=gen_random_uuid()",
            "empty-active-epoch" => "ALTER TABLE appsurface_durable.store_metadata DROP CONSTRAINT store_metadata_active_runtime_epoch_check; UPDATE appsurface_durable.store_metadata SET active_runtime_epoch='00000000-0000-0000-0000-000000000000'",
            "epoch-mismatch" => "UPDATE appsurface_durable.store_metadata SET active_runtime_epoch=gen_random_uuid()",
            "duplicate-worker" => "ALTER TABLE appsurface_durable.runtime_heartbeat DROP CONSTRAINT runtime_heartbeat_pkey; INSERT INTO appsurface_durable.runtime_heartbeat SELECT * FROM appsurface_durable.runtime_heartbeat",
            "empty-worker-epoch" => "UPDATE appsurface_durable.runtime_heartbeat SET runtime_epoch='00000000-0000-0000-0000-000000000000'",
            "minimum-heartbeat" => "UPDATE appsurface_durable.runtime_heartbeat SET last_heartbeat_at='-infinity'",
            "maximum-heartbeat" => "UPDATE appsurface_durable.runtime_heartbeat SET last_heartbeat_at='infinity'",
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        });
        await using var connection = new NpgsqlConnection(fixture.RuntimeConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        var request = fixture.CreateRequest(DurableDoctorFixture.RuntimeWorkerId, DurableDoctorFixture.ReferenceStaleAfter);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await DurableDoctorService.ReadRuntimeAsync(connection, transaction, request, schema,
                [], [], TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Same(connection, transaction.Connection);
        await transaction.RollbackAsync();
    }

    // Value: protects=an expired observation performs no query; fails_when=the runtime reader ignores its remaining budget; why_new=stage deadlines cancel before this seam is entered; seam=ReadRuntimeAsync
    [Fact]
    public async Task Expired_runtime_budget_rejects_before_database_access()
    {
        await using var fixture = await DurableDoctorFixture.CreateAsync();
        var schema = (await fixture.InspectAsync()).Schema!;
        await using var connection = new NpgsqlConnection(fixture.RuntimeConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var failure = await Assert.ThrowsAsync<DurableDoctorFailureException>(async () =>
            await DurableDoctorService.ReadRuntimeAsync(connection, transaction, fixture.CreateRequest(), schema,
                [], [], TimeSpan.Zero, CancellationToken.None));
        Assert.Equal(DurableDoctorFailureKind.Unavailable, failure.Kind);
        Assert.Equal(["deadline"], failure.Categories);
        await transaction.RollbackAsync();
    }
}
