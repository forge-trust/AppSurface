using System.Text.Json;
using System.Text.Json.Serialization;
using ForgeTrust.AppSurface.Durable;

namespace ForgeTrust.AppSurface.Durable.Tests;

public sealed class DurableReplayDataContractCoverageTests
{
    private static readonly DurableEncodedPayload EncodedInput = new(
        "replay.payload",
        "v1",
        DurableDataClassification.Operational,
        "{\"value\":\"safe\"}"u8.ToArray());

    [Fact]
    public void JsonCodec_ValidatesConfigurationAndObjectBoundaries()
    {
        Assert.Throws<ArgumentNullException>(() => new SystemTextJsonDurablePayloadCodec<ReplayPayload>(
            "replay.payload",
            "v1",
            DurableDataClassification.Operational,
            null!,
            _ => true));
        Assert.Throws<ArgumentNullException>(() => new SystemTextJsonDurablePayloadCodec<ReplayPayload>(
            "replay.payload",
            "v1",
            DurableDataClassification.Operational,
            ReplayCoverageJsonContext.Default.ReplayPayload,
            null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SystemTextJsonDurablePayloadCodec<ReplayPayload>(
            "replay.payload",
            "v1",
            DurableDataClassification.Operational,
            ReplayCoverageJsonContext.Default.ReplayPayload,
            _ => true,
            DurableEncodedPayload.ProtocolMaximumBytes + 1));

        var codec = CreateCodec(_ => true);
        Assert.Equal(typeof(ReplayPayload), codec.PayloadType);
        Assert.Equal("replay.payload", codec.ContractName);
        Assert.Equal("v1", codec.ContractVersion);
        Assert.Equal(DurableDataClassification.Operational, codec.Classification);
        Assert.Equal(DurableEncodedPayload.DefaultRetentionPolicyId, codec.RetentionPolicyId);
        Assert.Throws<ArgumentNullException>(() => codec.Encode(null!));
        Assert.Throws<ArgumentException>(() => codec.EncodeObject(new object()));

        var payload = new ReplayPayload("safe");
        var encoded = codec.EncodeObject(payload);
        Assert.Equal(payload, codec.DecodeObject(encoded));
    }

    [Fact]
    public void JsonCodec_RejectsOversizedAndNoLongerApprovedPayloads()
    {
        var tinyCodec = new SystemTextJsonDurablePayloadCodec<ReplayPayload>(
            "replay.payload",
            "v1",
            DurableDataClassification.Operational,
            ReplayCoverageJsonContext.Default.ReplayPayload,
            _ => true,
            maximumBytes: 1);
        Assert.Throws<ArgumentException>(() => tinyCodec.Encode(new ReplayPayload("too-large")));

        var rejectingCodec = CreateCodec(_ => false);
        Assert.Throws<ArgumentException>(() => rejectingCodec.Encode(new ReplayPayload("rejected")));
        Assert.Throws<JsonException>(() => rejectingCodec.Decode(EncodedInput));
        Assert.Throws<JsonException>(() => rejectingCodec.Decode(new DurableEncodedPayload(
            "replay.payload",
            "v1",
            DurableDataClassification.Operational,
            "null"u8.ToArray())));
        Assert.Throws<ArgumentNullException>(() => rejectingCodec.Decode(null!));
    }

    [Fact]
    public void JsonCodec_WrapsPolicyFailuresAndRequiresEveryContractDimension()
    {
        var throwingCodec = CreateCodec(_ => throw new InvalidOperationException("policy failed"));
        var policyError = Assert.Throws<JsonException>(() => throwingCodec.Decode(EncodedInput));
        Assert.IsType<InvalidOperationException>(policyError.InnerException);

        var codec = CreateCodec(_ => true);
        Assert.Throws<JsonException>(() => codec.Decode(new DurableEncodedPayload(
            "other.payload",
            "v1",
            DurableDataClassification.Operational,
            EncodedInput.Content)));
        Assert.Throws<JsonException>(() => codec.Decode(new DurableEncodedPayload(
            "replay.payload",
            "v2",
            DurableDataClassification.Operational,
            EncodedInput.Content)));
        Assert.Throws<JsonException>(() => codec.Decode(new DurableEncodedPayload(
            "replay.payload",
            "v1",
            DurableDataClassification.ApprovedApplication,
            EncodedInput.Content)));
        Assert.Throws<JsonException>(() => codec.Decode(new DurableEncodedPayload(
            "replay.payload",
            "v1",
            DurableDataClassification.Operational,
            EncodedInput.Content,
            "other-retention")));
    }

    [Fact]
    public void ScheduleTargetSnapshot_ValidatesTargetShapeAndPreservesValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateTargetSnapshot((DurableScheduleTargetKind)(-1), null));
        Assert.Throws<ArgumentNullException>(() => new DurableScheduleTargetSnapshot(
            DurableScheduleTargetKind.Flow,
            "flow",
            "v1",
            null!));
        Assert.Throws<ArgumentException>(() => CreateTargetSnapshot(DurableScheduleTargetKind.Work, null));
        Assert.Throws<ArgumentException>(() => CreateTargetSnapshot(
            DurableScheduleTargetKind.Flow,
            DurableProviderSafety.Idempotent));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateTargetSnapshot(
            DurableScheduleTargetKind.Work,
            (DurableProviderSafety)(-1)));

        var work = CreateTargetSnapshot(DurableScheduleTargetKind.Work, DurableProviderSafety.ProviderKeyed);
        Assert.Equal(DurableScheduleTargetKind.Work, work.Kind);
        Assert.Equal("registered", work.RegisteredName);
        Assert.Equal("v1", work.RegisteredVersion);
        Assert.Same(EncodedInput, work.Input);
        Assert.Equal(DurableProviderSafety.ProviderKeyed, work.ProviderSafety);

        var flow = CreateTargetSnapshot(DurableScheduleTargetKind.Flow, null);
        Assert.Null(flow.ProviderSafety);
    }

    [Fact]
    public void ScheduleListRequest_ValidatesBoundsAndPreservesOptionalFilters()
    {
        Assert.Throws<ArgumentException>(() => new DurableScheduleListRequest(default));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableScheduleListRequest(new DurableScopeId("scope"), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableScheduleListRequest(new DurableScopeId("scope"), 1_001));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableScheduleListRequest(
            new DurableScopeId("scope"),
            state: (DurableScheduleState)(-1)));

        var firstPage = new DurableScheduleListRequest(new DurableScopeId("scope"));
        Assert.Null(firstPage.ContinuationToken);
        Assert.Null(firstPage.State);
        Assert.Null(firstPage.RequiresRecoveryRelease);

        var filtered = new DurableScheduleListRequest(
            new DurableScopeId("scope"),
            25,
            "next",
            DurableScheduleState.Suspended,
            true);
        Assert.Equal(new DurableScopeId("scope"), filtered.ScopeId);
        Assert.Equal(25, filtered.PageSize);
        Assert.Equal("next", filtered.ContinuationToken);
        Assert.Equal(DurableScheduleState.Suspended, filtered.State);
        Assert.True(filtered.RequiresRecoveryRelease);
    }

    [Fact]
    public void ScheduleListItem_ValidatesEveryAuthoritativeDimension()
    {
        Assert.Throws<ArgumentException>(() => CreateScheduleListItem(useDefaultScheduleId: true));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateScheduleListItem(state: (DurableScheduleState)(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateScheduleListItem(generation: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateScheduleListItem(revision: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateScheduleListItem(scheduleKind: (DurableScheduleKind)(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateScheduleListItem(targetKind: (DurableScheduleTargetKind)(-1)));
        Assert.Throws<ArgumentNullException>(() => CreateScheduleListItem(useNullOverlapPolicy: true));
        Assert.Throws<ArgumentNullException>(() => CreateScheduleListItem(useNullMisfirePolicy: true));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateScheduleListItem(
            targetProviderSafety: (DurableProviderSafety)(-1)));
        Assert.Throws<ArgumentException>(() => CreateScheduleListItem(targetProviderSafety: null));
        Assert.Throws<ArgumentException>(() => CreateScheduleListItem(
            targetKind: DurableScheduleTargetKind.Flow,
            targetProviderSafety: DurableProviderSafety.Idempotent));
    }

    [Fact]
    public void ScheduleListItem_PreservesPayloadFreeInventoryAndNormalizesTime()
    {
        var occurrence = new DateTimeOffset(2026, 7, 15, 8, 30, 0, TimeSpan.FromHours(-4));
        var item = CreateScheduleListItem(nextOccurrenceUtc: occurrence, requiresRecoveryRelease: true);

        Assert.Equal(new DurableScheduleId("schedule"), item.ScheduleId);
        Assert.Equal("Replay schedule", item.DisplayName);
        Assert.Equal(DurableScheduleState.Active, item.State);
        Assert.Equal(2, item.Generation);
        Assert.Equal(3, item.Revision);
        Assert.Equal(DurableScheduleKind.Every, item.ScheduleKind);
        Assert.Same(ScheduleOverlapPolicy.QueueOne, item.OverlapPolicy);
        Assert.Same(ScheduleMisfirePolicy.RunOnce, item.MisfirePolicy);
        Assert.Equal(DurableScheduleTargetKind.Work, item.TargetKind);
        Assert.Equal("registered", item.TargetName);
        Assert.Equal("v1", item.TargetVersion);
        Assert.Equal(DurableProviderSafety.Idempotent, item.TargetProviderSafety);
        Assert.Equal(TimeSpan.Zero, item.NextOccurrenceUtc!.Value.Offset);
        Assert.True(item.RequiresRecoveryRelease);

        var flow = CreateScheduleListItem(
            displayName: null,
            targetKind: DurableScheduleTargetKind.Flow,
            targetProviderSafety: null,
            nextOccurrenceUtc: null);
        Assert.Null(flow.DisplayName);
        Assert.Null(flow.TargetProviderSafety);
        Assert.Null(flow.NextOccurrenceUtc);
    }

    [Fact]
    public void WorkAcceptance_ValidatesIdentityAndPreservesAuthoritativeResult()
    {
        Assert.Throws<ArgumentException>(() => CreateAcceptance(useDefaultWorkId: true));
        Assert.Throws<ArgumentException>(() => CreateAcceptance(useDefaultCommandId: true));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateAcceptance(kind: (DurableWorkAcceptanceKind)(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateAcceptance(revision: 0));

        var acceptance = CreateAcceptance();
        Assert.Equal(new DurableWorkId("work"), acceptance.WorkId);
        Assert.Equal(new DurableCommandId("command"), acceptance.CommandId);
        Assert.Equal(DurableWorkAcceptanceKind.Duplicate, acceptance.Kind);
        Assert.Equal(4, acceptance.Revision);
        Assert.Equal(TimeSpan.Zero, acceptance.AcceptedAtUtc.Offset);
    }

    [Fact]
    public void ProblemAndResultContracts_ExposeSuccessAndFailureShapes()
    {
        Assert.Throws<ArgumentNullException>(() => CreateProblem(documentationUrl: null!));
        Assert.Throws<ArgumentException>(() => CreateProblem(new Uri("relative", UriKind.Relative)));
        Assert.Throws<ArgumentException>(() => CreateProblem(new Uri("file:///tmp/durable")));

        var problem = CreateProblem(new Uri("https://example.test/durable"));
        Assert.Equal("ASDUR999", problem.Code);
        Assert.Equal("Problem", problem.Problem);
        Assert.Equal("Cause", problem.Cause);
        Assert.Equal("Fix", problem.Fix);
        Assert.Equal(new Uri("https://example.test/durable"), problem.DocumentationUrl);
        Assert.Equal("correlation", problem.CorrelationId);

        Assert.Throws<ArgumentNullException>(() => DurableOperationResult<string>.Success(null!));
        Assert.Throws<ArgumentNullException>(() => DurableOperationResult<string>.Failure(null!));
        var success = DurableOperationResult<string>.Success("value");
        Assert.True(success.IsSuccess);
        Assert.Equal("value", success.Value);
        Assert.Null(success.Problem);
        var failure = DurableOperationResult<string>.Failure(problem);
        Assert.False(failure.IsSuccess);
        Assert.Null(failure.Value);
        Assert.Same(problem, failure.Problem);
    }

    [Fact]
    public void ReconciliationContracts_ExposeAllProviderTruthOutcomes()
    {
        Assert.Throws<ArgumentNullException>(() => DurableEffectReconciliation<string>.Applied(null!));
        var applied = DurableEffectReconciliation<string>.Applied("done");
        Assert.Equal(DurableEffectReconciliationKind.Applied, applied.Kind);
        Assert.Equal("done", applied.Result);
        Assert.Equal(DurableEffectReconciliationKind.NotApplied, DurableEffectReconciliation<string>.NotApplied().Kind);
        Assert.Equal(DurableEffectReconciliationKind.Unknown, DurableEffectReconciliation<string>.Unknown().Kind);

        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableEncodedEffectReconciliation(
            (DurableEffectReconciliationKind)(-1),
            null));
        Assert.Throws<ArgumentException>(() => new DurableEncodedEffectReconciliation(
            DurableEffectReconciliationKind.Applied,
            null));
        Assert.Throws<ArgumentException>(() => new DurableEncodedEffectReconciliation(
            DurableEffectReconciliationKind.Unknown,
            EncodedInput));
        var encoded = new DurableEncodedEffectReconciliation(DurableEffectReconciliationKind.Applied, EncodedInput);
        Assert.Equal(DurableEffectReconciliationKind.Applied, encoded.Kind);
        Assert.Same(EncodedInput, encoded.Result);
    }

    private static SystemTextJsonDurablePayloadCodec<ReplayPayload> CreateCodec(Func<ReplayPayload, bool> policy) =>
        new(
            "replay.payload",
            "v1",
            DurableDataClassification.Operational,
            ReplayCoverageJsonContext.Default.ReplayPayload,
            policy);

    private static DurableScheduleTargetSnapshot CreateTargetSnapshot(
        DurableScheduleTargetKind kind,
        DurableProviderSafety? providerSafety) =>
        new(kind, "registered", "v1", EncodedInput, providerSafety);

    private static DurableScheduleListItem CreateScheduleListItem(
        bool useDefaultScheduleId = false,
        string? displayName = "Replay schedule",
        DurableScheduleState state = DurableScheduleState.Active,
        long generation = 2,
        long revision = 3,
        DurableScheduleKind scheduleKind = DurableScheduleKind.Every,
        bool useNullOverlapPolicy = false,
        bool useNullMisfirePolicy = false,
        DurableScheduleTargetKind targetKind = DurableScheduleTargetKind.Work,
        DurableProviderSafety? targetProviderSafety = DurableProviderSafety.Idempotent,
        DateTimeOffset? nextOccurrenceUtc = null,
        bool requiresRecoveryRelease = false) =>
        new(
            useDefaultScheduleId ? default : new DurableScheduleId("schedule"),
            displayName,
            state,
            generation,
            revision,
            scheduleKind,
            useNullOverlapPolicy ? null! : ScheduleOverlapPolicy.QueueOne,
            useNullMisfirePolicy ? null! : ScheduleMisfirePolicy.RunOnce,
            targetKind,
            "registered",
            "v1",
            targetProviderSafety,
            nextOccurrenceUtc,
            requiresRecoveryRelease);

    private static DurableWorkAcceptance CreateAcceptance(
        bool useDefaultWorkId = false,
        bool useDefaultCommandId = false,
        DurableWorkAcceptanceKind kind = DurableWorkAcceptanceKind.Duplicate,
        long revision = 4) =>
        new(
            useDefaultWorkId ? default : new DurableWorkId("work"),
            useDefaultCommandId ? default : new DurableCommandId("command"),
            kind,
            revision,
            new DateTimeOffset(2026, 7, 15, 8, 30, 0, TimeSpan.FromHours(-4)));

    private static DurableProblem CreateProblem(Uri documentationUrl) =>
        new("ASDUR999", "Problem", "Cause", "Fix", documentationUrl, "correlation");
}

public sealed record ReplayPayload(string Value);

[JsonSerializable(typeof(ReplayPayload))]
internal sealed partial class ReplayCoverageJsonContext : JsonSerializerContext;
