using System.Text;
using ForgeTrust.AppSurface.Durable;

namespace ForgeTrust.AppSurface.Durable.Tests.Scheduling;

public sealed class DurableScheduleClientContractTests
{
    private static readonly DurableScopeId ScopeId = new("household-1");
    private static readonly DurableCommandId CommandId = new("command-1");
    private static readonly DurableScheduleId ScheduleId = new("schedule-1");
    private static readonly IDurablePayloadCodec<WorkInput> WorkCodec = new TestCodec<WorkInput>("work.input");
    private static readonly IDurablePayloadCodec<FlowInput> FlowCodec = new TestCodec<FlowInput>("flow.input");
    private static readonly DurableSchedule Schedule = DurableSchedule.Every(TimeSpan.FromHours(1));
    private static readonly DurableScheduleTarget Target = DurableScheduleTarget.Work("cleanup", "v1", new WorkInput("provider-1"), WorkCodec);
    private static readonly DurableScheduleTargetSnapshot TargetSnapshot = new(
        DurableScheduleTargetKind.Work,
        "cleanup",
        "v1",
        new DurableEncodedPayload(
            "cleanup",
            "v1",
            DurableDataClassification.ApprovedApplication,
            "{}"u8.ToArray()),
        DurableProviderSafety.Idempotent);

    [Fact]
    public void ScheduleId_ValidatesAndCreatesOpaqueValues()
    {
        Assert.Throws<ArgumentException>(() => new DurableScheduleId(" "));

        var generated = DurableScheduleId.New();

        Assert.False(string.IsNullOrWhiteSpace(generated.Value));
        Assert.Equal(generated.Value, generated.ToString());
    }

    [Fact]
    public void TargetFactories_PreserveRegisteredIdentityAndTypedInput()
    {
        var work = DurableScheduleTarget.Work("cleanup", "v1", new WorkInput("provider-1"), WorkCodec);
        var flow = DurableScheduleTarget.Flow("exit-household", "v2", new FlowInput(42), FlowCodec);

        Assert.Equal(DurableScheduleTargetKind.Work, work.Kind);
        Assert.Equal("cleanup", work.WorkName);
        Assert.Equal("v1", work.WorkVersion);
        Assert.Equal("cleanup", ((DurableScheduleTarget)work).RegisteredName);
        Assert.Equal("v1", ((DurableScheduleTarget)work).RegisteredVersion);
        Assert.Equal(WorkCodec.Encode(new WorkInput("provider-1")).Sha256, work.EncodedInputPayload.Sha256);
        Assert.Equal(work.EncodedInputPayload.Sha256, ((DurableScheduleTarget)work).EncodedInput.Sha256);
        Assert.Equal(DurableScheduleTargetKind.Flow, flow.Kind);
        Assert.Equal("exit-household", flow.FlowId);
        Assert.Equal("v2", flow.Version);
        Assert.Equal(FlowCodec.Encode(new FlowInput(42)).Sha256, flow.EncodedInitialContext.Sha256);
        Assert.Equal("exit-household", ((DurableScheduleTarget)flow).RegisteredName);
        Assert.Equal("v2", ((DurableScheduleTarget)flow).RegisteredVersion);
        Assert.Equal(flow.EncodedInitialContext.Sha256, ((DurableScheduleTarget)flow).EncodedInput.Sha256);
    }

    [Fact]
    public void TargetFactories_EncodeInputAtConstruction()
    {
        var input = new MutableInput { Value = "before" };
        var target = DurableScheduleTarget.Work(
            "cleanup",
            "v1",
            input,
            new TestCodec<MutableInput>("mutable.input"));
        var encodedBeforeMutation = target.EncodedInputPayload.Sha256;

        input.Value = "after";

        Assert.Equal(encodedBeforeMutation, target.EncodedInputPayload.Sha256);
    }

    [Fact]
    public void Targets_RejectMissingIdentityOrInput()
    {
        Assert.Throws<ArgumentException>(() => new DurableWorkScheduleTarget<WorkInput>(" ", "v1", new WorkInput("p"), WorkCodec));
        Assert.Throws<ArgumentException>(() => new DurableWorkScheduleTarget<WorkInput>("work", " ", new WorkInput("p"), WorkCodec));
        Assert.Throws<ArgumentException>(() => new DurableWorkScheduleTarget<WorkInput>(new string('a', 201), "v1", new WorkInput("p"), WorkCodec));
        Assert.Throws<ArgumentException>(() => new DurableWorkScheduleTarget<WorkInput>("work", new string('a', 101), new WorkInput("p"), WorkCodec));
        Assert.Throws<ArgumentException>(() => new DurableWorkScheduleTarget<WorkInput>("work\nspoofed", "v1", new WorkInput("p"), WorkCodec));
        Assert.Throws<ArgumentNullException>(() => new DurableWorkScheduleTarget<WorkInput>("work", "v1", null!, WorkCodec));
        Assert.Throws<ArgumentNullException>(() => new DurableWorkScheduleTarget<WorkInput>("work", "v1", new WorkInput("p"), null!));
        Assert.Throws<ArgumentException>(() => new DurableFlowScheduleTarget<FlowInput>(" ", "v1", new FlowInput(1), FlowCodec));
        Assert.Throws<ArgumentException>(() => new DurableFlowScheduleTarget<FlowInput>("flow", " ", new FlowInput(1), FlowCodec));
        Assert.Throws<ArgumentException>(() => new DurableFlowScheduleTarget<FlowInput>(new string('a', 201), "v1", new FlowInput(1), FlowCodec));
        Assert.Throws<ArgumentException>(() => new DurableFlowScheduleTarget<FlowInput>("flow", new string('a', 101), new FlowInput(1), FlowCodec));
        Assert.Throws<ArgumentException>(() => new DurableFlowScheduleTarget<FlowInput>("flow", "v1\nspoofed", new FlowInput(1), FlowCodec));
        Assert.Throws<ArgumentNullException>(() => new DurableFlowScheduleTarget<FlowInput>("flow", "v1", null!, FlowCodec));
        Assert.Throws<ArgumentNullException>(() => new DurableFlowScheduleTarget<FlowInput>("flow", "v1", new FlowInput(1), null!));
    }

    [Fact]
    public void CreateRequest_PreservesTrustedInputs()
    {
        var request = new DurableScheduleCreateRequest(
            ScopeId,
            CommandId,
            "retry-key",
            ScheduleId,
            Schedule,
            Target,
            "Provider cleanup");

        Assert.Equal(ScopeId, request.ScopeId);
        Assert.Equal(CommandId, request.CommandId);
        Assert.Equal("retry-key", request.IdempotencyKey);
        Assert.Equal(ScheduleId, request.ScheduleId);
        Assert.Same(Schedule, request.Schedule);
        Assert.Same(Target, request.Target);
        Assert.Equal("Provider cleanup", request.DisplayName);
    }

    [Fact]
    public void CreateRequest_RejectsInvalidMembers()
    {
        Assert.Throws<ArgumentException>(() => new DurableScheduleCreateRequest(
            ScopeId, CommandId, " ", ScheduleId, Schedule, Target));
        Assert.Throws<ArgumentNullException>(() => new DurableScheduleCreateRequest(
            ScopeId, CommandId, "key", ScheduleId, null!, Target));
        Assert.Throws<ArgumentNullException>(() => new DurableScheduleCreateRequest(
            ScopeId, CommandId, "key", ScheduleId, Schedule, null!));
        Assert.Throws<ArgumentException>(() => new DurableScheduleCreateRequest(
            ScopeId, CommandId, "key", ScheduleId, Schedule, Target, " "));
    }

    [Fact]
    public void UpdateAndLifecycleCommands_RequirePositiveExpectedRevision()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableScheduleUpdateRequest(
            ScopeId, CommandId, ScheduleId, 0, Schedule, Target));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableScheduleCommand(
            DurableScheduleCommandKind.Pause, ScopeId, CommandId, ScheduleId, "operator-1", "maintenance", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableScheduleCommand(
            (DurableScheduleCommandKind)99, ScopeId, CommandId, ScheduleId, "operator-1", "maintenance", 1));
    }

    [Fact]
    public void UpdateRequest_PreservesReplacement()
    {
        var replacement = DurableSchedule.Cron("0 4 * * *", "America/New_York");
        var request = new DurableScheduleUpdateRequest(
            ScopeId, CommandId, ScheduleId, 7, replacement, Target, "new label");

        Assert.Equal(7, request.ExpectedRevision);
        Assert.Equal(ScopeId, request.ScopeId);
        Assert.Equal(CommandId, request.CommandId);
        Assert.Equal(ScheduleId, request.ScheduleId);
        Assert.Same(replacement, request.Schedule);
        Assert.Same(Target, request.Target);
        Assert.Equal("new label", request.DisplayName);
    }

    [Fact]
    public void LifecycleCommand_PreservesOptimisticConcurrencyInputs()
    {
        var command = new DurableScheduleCommand(
            DurableScheduleCommandKind.Pause, ScopeId, CommandId, ScheduleId, "operator-1", "maintenance", 7);

        Assert.Equal(ScopeId, command.ScopeId);
        Assert.Equal(CommandId, command.CommandId);
        Assert.Equal(ScheduleId, command.ScheduleId);
        Assert.Equal("operator-1", command.ActorId);
        Assert.Equal("maintenance", command.ReasonCode);
        Assert.Equal(7, command.ExpectedRevision);
        Assert.Equal(DurableScheduleCommandKind.Pause, command.Kind);
        Assert.Throws<ArgumentException>(() => new DurableScheduleCommand(
            DurableScheduleCommandKind.Pause, ScopeId, CommandId, ScheduleId, " ", "maintenance", 7));
        Assert.Throws<ArgumentException>(() => new DurableScheduleCommand(
            DurableScheduleCommandKind.Pause, ScopeId, CommandId, ScheduleId, "operator-1", " ", 7));
    }

    [Fact]
    public void MutationResult_ValidatesAndNormalizesTimestamp()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableScheduleMutationResult(
            ScheduleId, CommandId, (DurableScheduleMutationCode)99, 1, 1, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableScheduleMutationResult(
            ScheduleId, CommandId, DurableScheduleMutationCode.Created, 0, 1, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableScheduleMutationResult(
            ScheduleId, CommandId, DurableScheduleMutationCode.Created, 1, 0, DateTimeOffset.UtcNow));

        var result = new DurableScheduleMutationResult(
            ScheduleId,
            CommandId,
            DurableScheduleMutationCode.Created,
            1,
            1,
            new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.FromHours(1)));
        Assert.Equal(TimeSpan.Zero, result.CommittedAtUtc.Offset);
        Assert.Equal(ScheduleId, result.ScheduleId);
        Assert.Equal(CommandId, result.CommandId);
        Assert.Equal(DurableScheduleMutationCode.Created, result.Code);
        Assert.Equal(1, result.Generation);
        Assert.Equal(1, result.Revision);
    }

    [Fact]
    public void Snapshot_ValidatesStateAndNormalizesNextOccurrence()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableScheduleSnapshot(
            ScheduleId, null, (DurableScheduleState)99, 1, 1, Schedule, TargetSnapshot, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableScheduleSnapshot(
            ScheduleId, null, DurableScheduleState.Active, 0, 1, Schedule, TargetSnapshot, null));

        var snapshot = new DurableScheduleSnapshot(
            ScheduleId,
            null,
            DurableScheduleState.Active,
            1,
            2,
            Schedule,
            TargetSnapshot,
            new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.FromHours(1)));
        Assert.Equal(TimeSpan.Zero, snapshot.NextOccurrenceUtc!.Value.Offset);
        Assert.Equal(ScheduleId, snapshot.ScheduleId);
        Assert.Null(snapshot.DisplayName);
        Assert.Equal(DurableScheduleState.Active, snapshot.State);
        Assert.Equal(1, snapshot.Generation);
        Assert.Equal(2, snapshot.Revision);
        Assert.Same(Schedule, snapshot.Schedule);
        Assert.Same(TargetSnapshot, snapshot.Target);

        Assert.Throws<ArgumentNullException>(() => new DurableScheduleSnapshot(
            ScheduleId, null, DurableScheduleState.Active, 1, 1, null!, TargetSnapshot, null));
        Assert.Throws<ArgumentNullException>(() => new DurableScheduleSnapshot(
            ScheduleId, null, DurableScheduleState.Active, 1, 1, Schedule, null!, null));
        Assert.Throws<ArgumentException>(() => new DurableScheduleSnapshot(
            ScheduleId, " ", DurableScheduleState.Active, 1, 1, Schedule, TargetSnapshot, null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public void ListRequest_BoundsPageSize(int pageSize)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableScheduleListRequest(ScopeId, pageSize));
    }

    [Fact]
    public void ListRequest_PreservesPagingInputs()
    {
        var request = new DurableScheduleListRequest(
            ScopeId,
            25,
            "next",
            DurableScheduleState.Suspended,
            requiresRecoveryRelease: true);

        Assert.Equal(ScopeId, request.ScopeId);
        Assert.Equal(25, request.PageSize);
        Assert.Equal("next", request.ContinuationToken);
        Assert.Equal(DurableScheduleState.Suspended, request.State);
        Assert.True(request.RequiresRecoveryRelease);
        Assert.Throws<ArgumentException>(() => new DurableScheduleListRequest(ScopeId, continuationToken: "bad token"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableScheduleListRequest(
            ScopeId,
            state: (DurableScheduleState)(-1)));
    }

    [Fact]
    public void ListResult_CopiesCallerCollection()
    {
        var items = new List<DurableScheduleListItem>();
        var result = new DurableScheduleListResult(items, "next");

        items.Add(CreateListItem());

        Assert.Empty(result.Schedules);
        Assert.Equal("next", result.ContinuationToken);
        Assert.Equal(
            "next",
            new DurableScheduleListRequest(ScopeId, continuationToken: result.ContinuationToken).ContinuationToken);
        Assert.Null(new DurableScheduleListResult([], null).ContinuationToken);
        Assert.Throws<ArgumentException>(() => new DurableScheduleListResult([], "bad token"));
        Assert.Throws<ArgumentException>(() => new DurableScheduleListResult([], new string('a', 201)));
        Assert.Throws<ArgumentNullException>(() => new DurableScheduleListResult(null!, null));
    }

    [Fact]
    public void ListItem_ValidatesShapeAndPreservesPayloadFreeInventory()
    {
        var item = CreateListItem();

        Assert.Equal(ScheduleId, item.ScheduleId);
        Assert.Equal(DurableScheduleKind.Every, item.ScheduleKind);
        Assert.Equal(DurableScheduleTargetKind.Work, item.TargetKind);
        Assert.Equal(DurableProviderSafety.Idempotent, item.TargetProviderSafety);
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateListItem(state: (DurableScheduleState)(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateListItem(generation: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateListItem(revision: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateListItem(scheduleKind: (DurableScheduleKind)(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateListItem(targetKind: (DurableScheduleTargetKind)(-1)));
        Assert.Throws<ArgumentException>(() => CreateListItem(targetProviderSafety: null));
        Assert.Throws<ArgumentException>(() => CreateListItem(
            targetKind: DurableScheduleTargetKind.Flow,
            targetProviderSafety: DurableProviderSafety.Idempotent));
    }

    private static DurableScheduleListItem CreateListItem(
        DurableScheduleState state = DurableScheduleState.Active,
        long generation = 1,
        long revision = 1,
        DurableScheduleKind scheduleKind = DurableScheduleKind.Every,
        DurableScheduleTargetKind targetKind = DurableScheduleTargetKind.Work,
        DurableProviderSafety? targetProviderSafety = DurableProviderSafety.Idempotent) =>
        new(
            ScheduleId,
            "Cleanup",
            state,
            generation,
            revision,
            scheduleKind,
            ScheduleOverlapPolicy.QueueOne,
            ScheduleMisfirePolicy.RunOnce,
            targetKind,
            "cleanup",
            "v1",
            targetProviderSafety,
            DateTimeOffset.UtcNow,
            requiresRecoveryRelease: false);

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void ExplainRequest_BoundsOccurrenceCount(int count)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableScheduleExplainRequest(
            ScopeId, ScheduleId, Schedule, DateTimeOffset.UtcNow, count));
    }

    [Fact]
    public void ExplainRequest_PreservesAndNormalizesInputs()
    {
        var request = new DurableScheduleExplainRequest(
            ScopeId,
            ScheduleId,
            Schedule,
            new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.FromHours(1)),
            3);

        Assert.Equal(ScopeId, request.ScopeId);
        Assert.Equal(ScheduleId, request.ScheduleId);
        Assert.Same(Schedule, request.Schedule);
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T00:00:00Z"), request.AnchorUtc);
        Assert.Equal(3, request.OccurrenceCount);
        Assert.Throws<ArgumentNullException>(() => new DurableScheduleExplainRequest(
            ScopeId, ScheduleId, null!, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Explanation_CopiesAndNormalizesCollections()
    {
        var values = new List<DateTimeOffset>
        {
            new(2026, 1, 1, 1, 0, 0, TimeSpan.FromHours(1)),
        };
        var notes = new List<string> { "note" };
        var explanation = new DurableScheduleExplanation(
            ScheduleId,
            DurableScheduleKind.Cron,
            ScheduleOverlapPolicy.QueueOne,
            ScheduleMisfirePolicy.RunOnce,
            values,
            CronDialect.CronosV1,
            CronGrammar.Standard,
            "UTC",
            "0.13.0",
            42,
            "fingerprint",
            notes);

        values.Clear();
        notes.Clear();

        Assert.Single(explanation.NextOccurrencesUtc);
        Assert.Equal(TimeSpan.Zero, explanation.NextOccurrencesUtc[0].Offset);
        Assert.Equal(["note"], explanation.Notes);
        Assert.Equal(ScheduleOverlapPolicy.QueueOne, explanation.OverlapPolicy);
        Assert.Equal(ScheduleMisfirePolicy.RunOnce, explanation.MisfirePolicy);
        Assert.Equal(ScheduleId, explanation.ScheduleId);
        Assert.Equal(DurableScheduleKind.Cron, explanation.Kind);
        Assert.Equal(CronDialect.CronosV1, explanation.CronDialect);
        Assert.Equal(CronGrammar.Standard, explanation.CronGrammar);
        Assert.Equal("UTC", explanation.IanaTimeZoneId);
        Assert.Equal("0.13.0", explanation.EvaluatorVersion);
        Assert.Equal(42, explanation.JitterSeed);
        Assert.Equal("fingerprint", explanation.TimeZoneRulesFingerprint);

        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableScheduleExplanation(
            ScheduleId,
            (DurableScheduleKind)99,
            ScheduleOverlapPolicy.QueueOne,
            ScheduleMisfirePolicy.RunOnce,
            []));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableScheduleExplanation(
            ScheduleId,
            DurableScheduleKind.Cron,
            ScheduleOverlapPolicy.QueueOne,
            ScheduleMisfirePolicy.RunOnce,
            [],
            (CronDialect)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableScheduleExplanation(
            ScheduleId,
            DurableScheduleKind.Cron,
            ScheduleOverlapPolicy.QueueOne,
            ScheduleMisfirePolicy.RunOnce,
            [],
            CronDialect.CronosV1,
            (CronGrammar)99));
        Assert.Throws<ArgumentException>(() => new DurableScheduleExplanation(
            ScheduleId,
            DurableScheduleKind.Cron,
            ScheduleOverlapPolicy.QueueOne,
            ScheduleMisfirePolicy.RunOnce,
            []));
        Assert.Throws<ArgumentException>(() => new DurableScheduleExplanation(
            ScheduleId,
            DurableScheduleKind.Cron,
            ScheduleOverlapPolicy.QueueOne,
            ScheduleMisfirePolicy.RunOnce,
            [],
            CronDialect.CronosV1,
            CronGrammar.Standard));
        Assert.Throws<ArgumentException>(() => new DurableScheduleExplanation(
            ScheduleId,
            DurableScheduleKind.Cron,
            ScheduleOverlapPolicy.QueueOne,
            ScheduleMisfirePolicy.RunOnce,
            [],
            CronDialect.CronosV1,
            CronGrammar.Standard,
            " "));
        Assert.Throws<ArgumentException>(() => new DurableScheduleExplanation(
            ScheduleId,
            DurableScheduleKind.Cron,
            ScheduleOverlapPolicy.QueueOne,
            ScheduleMisfirePolicy.RunOnce,
            [],
            CronDialect.CronosV1,
            CronGrammar.Standard,
            new string('A', 129)));
        Assert.Throws<ArgumentException>(() => new DurableScheduleExplanation(
            ScheduleId,
            DurableScheduleKind.At,
            ScheduleOverlapPolicy.QueueOne,
            ScheduleMisfirePolicy.RunOnce,
            [],
            CronDialect.CronosV1,
            CronGrammar.Standard,
            "UTC"));
        Assert.Throws<ArgumentException>(() => new DurableScheduleExplanation(
            ScheduleId,
            DurableScheduleKind.At,
            ScheduleOverlapPolicy.QueueOne,
            ScheduleMisfirePolicy.RunOnce,
            [],
            cronDialect: CronDialect.CronosV1));
        Assert.Throws<ArgumentException>(() => new DurableScheduleExplanation(
            ScheduleId,
            DurableScheduleKind.At,
            ScheduleOverlapPolicy.QueueOne,
            ScheduleMisfirePolicy.RunOnce,
            [],
            cronGrammar: CronGrammar.Standard));
        Assert.Throws<ArgumentException>(() => new DurableScheduleExplanation(
            ScheduleId,
            DurableScheduleKind.At,
            ScheduleOverlapPolicy.QueueOne,
            ScheduleMisfirePolicy.RunOnce,
            [],
            ianaTimeZoneId: "UTC"));
        Assert.Throws<ArgumentNullException>(() => new DurableScheduleExplanation(
            ScheduleId,
            DurableScheduleKind.At,
            null!,
            ScheduleMisfirePolicy.RunOnce,
            []));
        Assert.Throws<ArgumentNullException>(() => new DurableScheduleExplanation(
            ScheduleId,
            DurableScheduleKind.At,
            ScheduleOverlapPolicy.QueueOne,
            null!,
            []));
        Assert.Throws<ArgumentNullException>(() => new DurableScheduleExplanation(
            ScheduleId,
            DurableScheduleKind.At,
            ScheduleOverlapPolicy.QueueOne,
            ScheduleMisfirePolicy.RunOnce,
            null!));
    }

    private sealed record WorkInput(string ProviderId);

    private sealed record FlowInput(int Count);

    private sealed class MutableInput
    {
        public required string Value { get; set; }

        public override string ToString() => Value;
    }

    private sealed class TestCodec<T>(string contractName) : IDurablePayloadCodec<T>
        where T : notnull
    {
        public Type PayloadType => typeof(T);
        public string ContractName => contractName;
        public string ContractVersion => "v1";
        public DurableDataClassification Classification => DurableDataClassification.Operational;
        public string RetentionPolicyId => DurableEncodedPayload.DefaultRetentionPolicyId;
        public DurableEncodedPayload Encode(T value) => new(
            ContractName,
            ContractVersion,
            Classification,
            Encoding.UTF8.GetBytes(value.ToString()!),
            RetentionPolicyId);
        public T Decode(DurableEncodedPayload payload) => throw new NotSupportedException();
        public DurableEncodedPayload EncodeObject(object value) => Encode((T)value);
        public object DecodeObject(DurableEncodedPayload payload) => Decode(payload);
    }
}
