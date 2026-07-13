using ForgeTrust.AppSurface.Durable;
using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests.Scheduling;

public sealed class SchedulePersistenceModelTests
{
    private static readonly DurableScopeId ScopeId = new("scope-a");
    private static readonly DurableScheduleId ScheduleId = new("schedule-a");
    private static readonly DateTimeOffset AcceptedAt = DateTimeOffset.Parse("2026-07-12T12:00:00Z");

    [Fact]
    public void MigrationFour_DefinesRlsProtectedScheduleProtocolAndPayloadFreeDispatch()
    {
        var migration = Assert.Single(
            DurablePostgreSqlMigrationCatalog.Load(),
            item => item.Version == 4);

        Assert.Equal("schedule_protocol", migration.Name);
        Assert.Contains("CREATE TABLE appsurface_durable.schedule_current", migration.Sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE appsurface_durable.schedule_history", migration.Sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE appsurface_durable.schedule_command", migration.Sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE appsurface_durable.schedule_occurrence", migration.Sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE appsurface_durable.schedule_run_slot", migration.Sql, StringComparison.Ordinal);
        Assert.Contains("FORCE ROW LEVEL SECURITY", migration.Sql, StringComparison.Ordinal);
        Assert.Contains("actor_id", migration.Sql, StringComparison.Ordinal);
        Assert.Contains("reason_code", migration.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ALTER TABLE appsurface_durable.dispatch ADD", migration.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_PersistsAcceptanceAnchorsAndCronCompatibilityInputs()
    {
        var after = PostgreSqlScheduleStorage.Resolve(
            ScheduleId,
            DurableSchedule.After(TimeSpan.FromMinutes(5)),
            AcceptedAt);
        var every = PostgreSqlScheduleStorage.Resolve(
            ScheduleId,
            DurableSchedule.Every(TimeSpan.FromMinutes(10)),
            AcceptedAt);
        var cron = PostgreSqlScheduleStorage.Resolve(
            ScheduleId,
            DurableSchedule.Cron("H * * * *", "America/New_York"),
            AcceptedAt);

        Assert.Equal(AcceptedAt, after.AnchorUtc);
        Assert.Equal(AcceptedAt.AddMinutes(5), after.NextNominalDueUtc);
        Assert.Equal(AcceptedAt, every.AnchorUtc);
        Assert.Equal(AcceptedAt, every.NextNominalDueUtc);
        Assert.Equal("cronos_v1", cron.CronDialect);
        Assert.Equal("standard", cron.CronGrammar);
        Assert.Equal("0.13.0", cron.CronEvaluatorVersion);
        Assert.NotNull(cron.CronJitterSeed);
        Assert.Equal(64, cron.TimeZoneRulesFingerprint!.Length);
        Assert.True(cron.NextNominalDueUtc > AcceptedAt);
    }

    [Fact]
    public void RequestFingerprints_AreDeterministicAndBindAuditAndDefinitionInputs()
    {
        var payload = Payload("one");
        var target = DurableScheduleTarget.Work("work", "v1", new TestWork("one"));
        var create = new DurableScheduleCreateRequest(
            ScopeId,
            new DurableCommandId("command-a"),
            "retry-a",
            ScheduleId,
            DurableSchedule.Every(TimeSpan.FromMinutes(1)),
            target);
        var lifecycle = new DurableScheduleCommand(
            ScopeId,
            new DurableCommandId("command-b"),
            ScheduleId,
            "operator-a",
            "maintenance",
            1);

        var first = DurableScheduleRequestFingerprint.ComputeCreate(create, payload);
        var again = DurableScheduleRequestFingerprint.ComputeCreate(create, payload);
        var pause = DurableScheduleRequestFingerprint.ComputeCommand("pause", lifecycle);
        var resume = DurableScheduleRequestFingerprint.ComputeCommand("resume", lifecycle);
        var changedActor = DurableScheduleRequestFingerprint.ComputeCommand(
            "pause",
            new DurableScheduleCommand(
                ScopeId,
                lifecycle.CommandId,
                ScheduleId,
                "operator-b",
                "maintenance",
                1));

        Assert.Equal(first, again);
        Assert.NotEqual(first, pause);
        Assert.NotEqual(pause, resume);
        Assert.NotEqual(pause, changedActor);
    }

    [Fact]
    public void StorageEnumCodecs_RoundTripAndRejectUnknownValues()
    {
        Assert.Equal(DurableScheduleState.Active, PostgreSqlScheduleStorage.ParseState("active"));
        Assert.Equal("paused", PostgreSqlScheduleStorage.FormatState(DurableScheduleState.Paused));
        Assert.Equal(DurableScheduleTargetKind.Flow, PostgreSqlScheduleStorage.ParseTargetKind("flow"));
        Assert.Equal("work", PostgreSqlScheduleStorage.FormatTargetKind(DurableScheduleTargetKind.Work));
        Assert.Equal(
            DurableProviderSafety.ReconcileBeforeRetry,
            PostgreSqlScheduleStorage.ParseProviderSafety("reconcile_before_retry"));
        Assert.Equal(
            "manual_resolution",
            PostgreSqlScheduleStorage.FormatProviderSafety(DurableProviderSafety.ManualResolution));
        Assert.Equal(
            DurableScheduleMutationCode.Resumed,
            PostgreSqlScheduleStorage.ParseMutationCode("resumed"));
        Assert.Equal(
            "deleted",
            PostgreSqlScheduleStorage.FormatMutationCode(DurableScheduleMutationCode.Deleted));
        Assert.Equal(
            DurableScheduleMutationCode.RecoveryReleased,
            PostgreSqlScheduleStorage.ParseMutationCode("recovery_released"));
        Assert.Equal(
            "recovery_released",
            PostgreSqlScheduleStorage.FormatMutationCode(DurableScheduleMutationCode.RecoveryReleased));
        Assert.Throws<InvalidDataException>(() => PostgreSqlScheduleStorage.ParseState("unknown"));
        Assert.Throws<InvalidDataException>(() => PostgreSqlScheduleStorage.ParseTargetKind("unknown"));
        Assert.Throws<InvalidDataException>(() => PostgreSqlScheduleStorage.ParseProviderSafety("unknown"));
        Assert.Throws<InvalidDataException>(() => PostgreSqlScheduleStorage.ParseMutationCode("unknown"));
        Assert.Throws<ArgumentException>(() =>
            PostgreSqlScheduleStorage.FormatMutationCode(DurableScheduleMutationCode.Duplicate));
    }

    [Fact]
    public void ProcessingRequest_BoundsDatabaseAndOccurrenceWork()
    {
        var request = new PostgreSqlScheduleProcessingRequest(5, 7, TimeSpan.FromMilliseconds(10));

        Assert.Equal(5, request.MaximumSchedules);
        Assert.Equal(7, request.EvaluationBudget.MaximumOccurrences);
        Assert.Equal(TimeSpan.FromMilliseconds(10), request.EvaluationBudget.MaximumElapsedTime);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PostgreSqlScheduleProcessingRequest(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PostgreSqlScheduleProcessingRequest(1_001));
    }

    [Fact]
    public async Task ScheduleClient_ExplainsWithoutDatabaseMutationAndMapsInvalidDefinitions()
    {
        await using var dataSource = NpgsqlDataSource.Create(
            "Host=localhost;Database=not-opened;Username=not-opened;Password=not-opened");
        var client = new PostgreSqlDurableScheduleClient(
            dataSource,
            new DurablePayloadCodecRegistry(),
            new DurableWorkRegistry([]),
            new DurableFlowRegistry([]),
            Guid.NewGuid());
        var valid = await client.ExplainNextOccurrencesAsync(new DurableScheduleExplainRequest(
            ScopeId,
            ScheduleId,
            DurableSchedule.Cron("0 9 * * MON-FRI", "America/New_York"),
            AcceptedAt,
            2));
        var invalid = await client.ExplainNextOccurrencesAsync(new DurableScheduleExplainRequest(
            ScopeId,
            ScheduleId,
            DurableSchedule.Cron("not cron", "America/New_York"),
            AcceptedAt));

        Assert.True(valid.IsSuccess);
        Assert.Equal(2, valid.Value!.NextOccurrencesUtc.Count);
        Assert.False(invalid.IsSuccess);
        Assert.Equal(DurableScheduleProblemCodes.ScheduleInvalid, invalid.Problem!.Code);
    }

    private static DurableEncodedPayload Payload(string value) => new(
        "test-work",
        "v1",
        DurableDataClassification.ApprovedApplication,
        System.Text.Encoding.UTF8.GetBytes(value));

    private sealed record TestWork(string Value);
}
