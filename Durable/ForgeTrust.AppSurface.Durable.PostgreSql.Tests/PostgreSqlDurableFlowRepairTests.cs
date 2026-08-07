using ForgeTrust.AppSurface.Durable.Provider;
using ForgeTrust.AppSurface.Flow;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

public sealed class PostgreSqlDurableFlowRepairTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Repair_accepts_retained_manual_resolution_evidence_for_the_permitted_action(bool effectApplied)
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "tests", "flow-repair");
        var status = await schema.GetStatusAsync();
        var contextCodec = new PostgreSqlOpaqueTestCodec("tests.repair.context", "v1");
        var workCodec = new PostgreSqlOpaqueTestCodec("tests.repair.work", "v1");
        var resultCodec = new PostgreSqlOpaqueTestCodec("tests.repair.work.result", "v1");
        var workRegistration = new PostgreSqlOpaqueTestWorkRegistration(
            "tests.repair.work",
            "v1",
            DurableProviderSafety.ManualResolution,
            workCodec,
            resultCodec);
        var payloads = new DurablePayloadCodecRegistry([contextCodec, workCodec, resultCodec]);
        var workRegistry = new DurableWorkRegistry([workRegistration]);
        var flowRegistration = new RepairActivityFlowRegistration(contextCodec, workRegistration, workCodec);
        var flowRegistry = new DurableFlowRegistry([flowRegistration], workRegistry, payloads);
        var options = new PostgreSqlDurableWorkOptions(epoch, status.StoreId);
        var flows = new PostgreSqlDurableFlowClient(database.DataSource, flowRegistry, payloads, options);
        var processor = new PostgreSqlDurableFlowProcessor(
            database.DataSource,
            database.DataSource,
            flowRegistry,
            workRegistry,
            payloads,
            options);
        var workStore = new PostgreSqlDurableWorkStore(database.DataSource, epoch);
        using var services = new ServiceCollection().BuildServiceProvider();
        var operators = new PostgreSqlDurableWorkOperatorClient(
            database.DataSource,
            workRegistry,
            services.GetRequiredService<IServiceScopeFactory>(),
            epoch);
        IFlowRepairOperatorClient repairs = new PostgreSqlDurableFlowRepairOperatorClient(
            database.DataSource,
            workRegistry,
            options);
        var scenario = effectApplied ? "applied" : "no-effect";
        var scope = new DurableScopeId($"flow-repair-{scenario}");
        var instance = new DurableFlowInstanceId($"manual-{scenario}");
        Assert.True((await flows.StartAsync(new DurableFlowStartRequest(
            scope,
            new DurableCommandId($"flow-repair-start-{scenario}"),
            $"flow-repair-start-key-{scenario}",
            instance,
            flowRegistration.FlowId,
            flowRegistration.FlowVersion,
            contextCodec.EncodeObject(new byte[] { 1 })))).IsSuccess);

        var beforeActivity = await flows.GetAsync(new DurableFlowGetRequest(scope, instance));
        var unsupported = await repairs.RepairAsync(DurableFlowRepairRequest.AssertChildEffectCompleted(
            scope,
            instance,
            new DurableCommandId($"flow-repair-unsupported-{scenario}"),
            beforeActivity.Value!.Revision,
            new string('a', 64),
            new DurableWorkId($"flow-repair-missing-work-{scenario}"),
            1,
            1,
            new string('b', 64),
            "operator",
            "not-suspended"));
        var missing = await repairs.RepairAsync(DurableFlowRepairRequest.AssertChildEffectCompleted(
            scope,
            new DurableFlowInstanceId($"flow-repair-missing-flow-{scenario}"),
            new DurableCommandId($"flow-repair-missing-{scenario}"),
            1,
            new string('a', 64),
            new DurableWorkId($"flow-repair-missing-work-{scenario}"),
            1,
            1,
            new string('b', 64),
            "operator",
            "missing-flow"));
        Assert.True(unsupported.IsSuccess);
        Assert.Equal(DurableFlowRepairOutcome.Refused, unsupported.Value!.Outcome);
        Assert.Equal(DurableProblemCodes.FlowRepairActionUnsupported, unsupported.Value.Problem!.Code);
        Assert.True(missing.IsSuccess);
        Assert.Equal(DurableFlowRepairOutcome.Refused, missing.Value!.Outcome);
        Assert.Equal(DurableProblemCodes.FlowNotFound, missing.Value.Problem!.Code);

        var activity = await processor.TryProcessAsync(
            Assert.Single(await processor.DiscoverAsync()),
            "flow-repair-processor");
        Assert.Equal(DurableFlowState.WaitingForActivity, activity.State);
        var childWorkId = activity.ChildWorkId!.Value;
        var claimed = await workStore.TryClaimAsync(
            Assert.Single(await workStore.DiscoverAsync(10), candidate => candidate.WorkId == childWorkId),
            "flow-repair-worker");
        var permitted = await workStore.TryAcquireEffectPermitAsync(claimed!);
        var failed = await workStore.RecordCompletionAsync(
            permitted!.Claim,
            new PostgreSqlWorkCompletion(PostgreSqlWorkCompletionKind.FailedTerminal, "repair.failure", "{}"));
        Assert.Equal(DurableWorkState.Suspended, failed.State);

        var suspended = await flows.GetAsync(new DurableFlowGetRequest(scope, instance));
        Assert.Equal(DurableFlowState.Suspended, suspended.Value!.State);
        var legacyRelease = await flows.ReleaseSuspensionAsync(new DurableFlowReleaseRequest(
            scope,
            new DurableCommandId($"flow-repair-legacy-release-{scenario}"),
            instance,
            "operator",
            "repair",
            suspended.Value.Revision));
        Assert.Equal(DurableProblemCodes.FlowReleaseStateMismatch, legacyRelease.Problem!.Code);

        var proof = await operators.ResolveAsync(new DurableWorkManualResolutionRequest(
            scope,
            childWorkId,
            new DurableCommandId($"flow-repair-proof-{scenario}"),
            "operator",
            effectApplied ? "provider-confirmed-applied" : "provider-confirmed-no-effect",
            failed.Revision,
            effectApplied ? DurableManualResolutionKind.Applied : DurableManualResolutionKind.ProvenNotApplied,
            effectApplied ? resultCodec.EncodeObject(new byte[] { 3 }) : null));
        Assert.True(proof.IsSuccess);
        Assert.Equal(effectApplied ? DurableWorkState.Succeeded : DurableWorkState.Ready, proof.Value!.State);

        var assessment = await repairs.GetAssessmentAsync(new DurableFlowRepairAssessmentRequest(scope, instance));
        Assert.True(assessment.IsSuccess);
        var candidate = Assert.Single(assessment.Value!.Candidates);
        Assert.Equal(
            effectApplied
                ? DurableFlowRepairAction.AssertChildEffectCompleted
                : DurableFlowRepairAction.AssertChildEffectNotApplied,
            candidate.Action);
        var evidence = candidate.Evidence;
        var refusedRequest = CreateRepairRequest(
            effectApplied,
            scope,
            instance,
            new DurableCommandId($"flow-repair-invalid-evidence-{scenario}"),
            assessment.Value!,
            evidence,
            evidence.ChildWorkHistoryEventId + 1,
            "invalid-retained-history");
        var refused = await repairs.RepairAsync(refusedRequest);
        var refusedReplay = await repairs.RepairAsync(refusedRequest);
        Assert.True(refused.IsSuccess);
        Assert.Equal(DurableFlowRepairOutcome.Refused, refused.Value!.Outcome);
        Assert.Equal(DurableProblemCodes.FlowRepairEvidenceMismatch, refused.Value.Problem!.Code);
        Assert.True(refusedReplay.IsSuccess);
        Assert.Equal(DurableFlowRepairOutcome.Refused, refusedReplay.Value!.Outcome);
        Assert.Equal(DurableProblemCodes.FlowRepairEvidenceMismatch, refusedReplay.Value.Problem!.Code);

        var request = CreateRepairRequest(
            effectApplied,
            scope,
            instance,
            new DurableCommandId($"flow-repair-command-{scenario}"),
            assessment.Value,
            evidence,
            evidence.ChildWorkHistoryEventId,
            effectApplied ? "provider-confirmed-applied" : "provider-confirmed-no-effect");
        var applied = await repairs.RepairAsync(request);
        var duplicate = await repairs.RepairAsync(request);
        var collisionRequest = CreateRepairRequest(
            effectApplied,
            scope,
            instance,
            request.CommandId,
            assessment.Value,
            evidence,
            evidence.ChildWorkHistoryEventId,
            "different-repair-reason");
        var conflict = await repairs.RepairAsync(collisionRequest);
        var staleRequest = CreateRepairRequest(
            effectApplied,
            scope,
            instance,
            new DurableCommandId($"flow-repair-stale-{scenario}"),
            assessment.Value,
            evidence,
            evidence.ChildWorkHistoryEventId,
            "stale-assessment");
        var stale = await repairs.RepairAsync(staleRequest);
        var staleReplay = await repairs.RepairAsync(staleRequest);

        Assert.True(applied.IsSuccess);
        Assert.Equal(DurableFlowRepairOutcome.Applied, applied.Value!.Outcome);
        Assert.Equal(effectApplied ? DurableFlowState.Ready : DurableFlowState.WaitingForActivity, applied.Value.ObservedFlowState);
        Assert.True(duplicate.IsSuccess);
        Assert.Equal(DurableFlowRepairOutcome.Duplicate, duplicate.Value!.Outcome);
        Assert.Equal(applied.Value.Receipt!.ReceiptSha256, duplicate.Value.Receipt!.ReceiptSha256);
        Assert.True(conflict.IsSuccess);
        Assert.Equal(DurableFlowRepairOutcome.Conflict, conflict.Value!.Outcome);
        Assert.Equal(DurableProblemCodes.FlowCommandConflict, conflict.Value.Problem!.Code);
        Assert.True(stale.IsSuccess);
        Assert.Equal(DurableFlowRepairOutcome.RaceLost, stale.Value!.Outcome);
        Assert.Equal(DurableProblemCodes.FlowRaceLost, stale.Value.Problem!.Code);
        Assert.True(staleReplay.IsSuccess);
        Assert.Equal(DurableFlowRepairOutcome.RaceLost, staleReplay.Value!.Outcome);
        Assert.Equal(DurableProblemCodes.FlowRaceLost, staleReplay.Value.Problem!.Code);
        Assert.Equal(
            effectApplied ? DurableFlowState.Ready : DurableFlowState.WaitingForActivity,
            (await flows.GetAsync(new DurableFlowGetRequest(scope, instance))).Value!.State);
        Assert.Equal(
            effectApplied ? "succeeded" : "retry_wait",
            await ReadWorkStateAsync(database.DataSource, scope, childWorkId));
        Assert.Equal(
            effectApplied ? "activity_completed" : "active",
            await ReadFlowWaitStateAsync(database.DataSource, scope, instance));
        Assert.Equal(1L, await CountRepairCommandsAsync(database.DataSource, scope, request.CommandId));
        Assert.Equal(1L, await CountRepairCollisionsAsync(database.DataSource, scope, request.CommandId));
    }

    private static async ValueTask<string> ReadWorkStateAsync(
        Npgsql.NpgsqlDataSource dataSource,
        DurableScopeId scope,
        DurableWorkId workId)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT state FROM appsurface_durable.work WHERE scope_id = @scope_id AND work_id = @work_id;");
        command.Parameters.AddWithValue("scope_id", scope.Value);
        command.Parameters.AddWithValue("work_id", workId.Value);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async ValueTask<string> ReadFlowWaitStateAsync(
        Npgsql.NpgsqlDataSource dataSource,
        DurableScopeId scope,
        DurableFlowInstanceId instance)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT state FROM appsurface_durable.flow_wait WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id AND kind = 'activity';");
        command.Parameters.AddWithValue("scope_id", scope.Value);
        command.Parameters.AddWithValue("flow_instance_id", instance.Value);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async ValueTask<long> CountRepairCommandsAsync(
        Npgsql.NpgsqlDataSource dataSource,
        DurableScopeId scope,
        DurableCommandId commandId)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT count(*) FROM appsurface_durable.flow_repair_command WHERE scope_id = @scope_id AND command_id = @command_id;");
        command.Parameters.AddWithValue("scope_id", scope.Value);
        command.Parameters.AddWithValue("command_id", commandId.Value);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async ValueTask<long> CountRepairCollisionsAsync(
        Npgsql.NpgsqlDataSource dataSource,
        DurableScopeId scope,
        DurableCommandId commandId)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT count(*) FROM appsurface_durable.flow_repair_collision WHERE scope_id = @scope_id AND command_id = @command_id;");
        command.Parameters.AddWithValue("scope_id", scope.Value);
        command.Parameters.AddWithValue("command_id", commandId.Value);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static DurableFlowRepairRequest CreateRepairRequest(
        bool effectApplied,
        DurableScopeId scope,
        DurableFlowInstanceId instance,
        DurableCommandId commandId,
        DurableFlowRepairAssessment assessment,
        DurableFlowRepairEvidenceReference evidence,
        long historyEventId,
        string reasonCode) =>
        effectApplied
            ? DurableFlowRepairRequest.AssertChildEffectCompleted(
                scope,
                instance,
                commandId,
                assessment.Revision,
                assessment.SuspensionDescriptorSha256!,
                evidence.ChildWorkId,
                evidence.ExpectedChildWorkRevision,
                historyEventId,
                evidence.ExpectedChildResultSha256!,
                "operator",
                reasonCode)
            : DurableFlowRepairRequest.AssertChildEffectNotApplied(
                scope,
                instance,
                commandId,
                assessment.Revision,
                assessment.SuspensionDescriptorSha256!,
                evidence.ChildWorkId,
                evidence.ExpectedChildWorkRevision,
                historyEventId,
                evidence.RequiredWorkOperatorCommandId!.Value,
                "operator",
                reasonCode);

    private sealed class RepairActivityFlowRegistration(
        IDurablePayloadCodec contextCodec,
        DurableWorkRegistration workRegistration,
        IDurablePayloadCodec workCodec) : DurableFlowRegistration
    {
        public override string FlowId => "tests.flow-repair";
        public override string FlowVersion => "v1";
        public override string ImplementationVersion => "tests-flow-repair-v1";
        public override string StartNodeId => "activity";
        public override string DefinitionFingerprint => new('a', 64);
        public override IDurablePayloadCodec ContextCodec { get; } = contextCodec;
        public override IReadOnlyList<DurableFlowEventBinding> EventBindings => [];
        public override IReadOnlyList<DurableWorkRegistration> ActivityWorkRegistrations { get; } = [workRegistration];

        public override ValueTask<DurableFlowEvaluationResult> EvaluateAsync(
            DurableFlowEvaluationInput input,
            IDurablePayloadCodecRegistry payloadCodecs,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new DurableFlowEvaluationResult(
                FlowTransitionKind.Activity,
                input.NodeId,
                input.Context,
                nextNodeId: null,
                eventName: null,
                timeout: null,
                fault: null,
                activity: new DurableFlowActivityCommand(
                    "repair-callsite",
                    1,
                    workRegistration.WorkName,
                    workRegistration.WorkVersion,
                    workRegistration.ProviderSafety,
                    workCodec.EncodeObject(new byte[] { 2 }))));
    }
}
