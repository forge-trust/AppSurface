using System.Text.Json;
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
        await using (var assessmentBlocker = await database.DataSource.OpenConnectionAsync())
        await using (var assessmentTransaction = await assessmentBlocker.BeginTransactionAsync())
        {
            await using var lockFlowTable = new Npgsql.NpgsqlCommand(
                "LOCK TABLE appsurface_durable.flow_instance IN ACCESS EXCLUSIVE MODE;",
                assessmentBlocker,
                assessmentTransaction);
            await lockFlowTable.ExecuteNonQueryAsync();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await repairs.GetAssessmentAsync(new DurableFlowRepairAssessmentRequest(scope, instance), cancellation.Token));
            await assessmentTransaction.RollbackAsync();
        }

        var legacyRelease = await flows.ReleaseSuspensionAsync(new DurableFlowReleaseRequest(
            scope,
            new DurableCommandId($"flow-repair-legacy-release-{scenario}"),
            instance,
            "operator",
            "repair",
            suspended.Value.Revision));
        Assert.Equal(DurableProblemCodes.FlowReleaseStateMismatch, legacyRelease.Problem!.Code);

        var missingAssessment = await repairs.GetAssessmentAsync(new DurableFlowRepairAssessmentRequest(
            scope,
            new DurableFlowInstanceId($"flow-repair-missing-assessment-{scenario}")));
        Assert.False(missingAssessment.IsSuccess);
        Assert.Equal(DurableProblemCodes.FlowNotFound, missingAssessment.Problem!.Code);
        var oldShapeAssessment = await repairs.GetAssessmentAsync(new DurableFlowRepairAssessmentRequest(scope, instance));
        Assert.True(oldShapeAssessment.IsSuccess);
        Assert.NotNull(oldShapeAssessment.Value!.SuspensionDescriptorSha256);
        await SetFlowRepairDescriptorAsync(database.DataSource, scope, instance, null);
        var legacyAssessment = await repairs.GetAssessmentAsync(new DurableFlowRepairAssessmentRequest(scope, instance));
        Assert.True(legacyAssessment.IsSuccess);
        Assert.Null(legacyAssessment.Value!.SuspensionDescriptorSha256);
        Assert.Empty(legacyAssessment.Value.Candidates);
        var legacyRepair = await repairs.RepairAsync(DurableFlowRepairRequest.AssertChildEffectCompleted(
            scope,
            instance,
            new DurableCommandId($"flow-repair-legacy-descriptor-{scenario}"),
            oldShapeAssessment.Value.Revision,
            new string('a', 64),
            childWorkId,
            failed.Revision,
            1,
            new string('b', 64),
            "operator",
            "legacy-descriptor"));
        Assert.True(legacyRepair.IsSuccess);
        Assert.Equal(DurableFlowRepairOutcome.Refused, legacyRepair.Value!.Outcome);
        Assert.Equal(DurableProblemCodes.FlowRepairDescriptorUpgradeRequired, legacyRepair.Value.Problem!.Code);
        await SetFlowRepairDescriptorAsync(
            database.DataSource,
            scope,
            instance,
            oldShapeAssessment.Value.SuspensionDescriptorSha256!);

        var descriptorRequest = DurableFlowRepairRequest.AssertChildEffectCompleted(
            scope,
            instance,
            new DurableCommandId($"flow-repair-descriptor-{scenario}"),
            oldShapeAssessment.Value.Revision,
            oldShapeAssessment.Value.SuspensionDescriptorSha256!,
            childWorkId,
            failed.Revision,
            1,
            new string('b', 64),
            "operator",
            "descriptor-check");
        await SetScopeStateAsync(database.DataSource, scope, "disabled");
        var disabled = await repairs.RepairAsync(descriptorRequest);
        Assert.False(disabled.IsSuccess);
        Assert.Equal(DurableProblemCodes.ScopeDisabled, disabled.Problem!.Code);
        await SetScopeStateAsync(database.DataSource, scope, "active");

        var mismatchedChild = await repairs.RepairAsync(DurableFlowRepairRequest.AssertChildEffectCompleted(
            scope,
            instance,
            new DurableCommandId($"flow-repair-mismatched-child-{scenario}"),
            oldShapeAssessment.Value.Revision,
            oldShapeAssessment.Value.SuspensionDescriptorSha256!,
            new DurableWorkId($"flow-repair-other-child-{scenario}"),
            failed.Revision,
            1,
            new string('b', 64),
            "operator",
            "mismatched-child"));
        Assert.True(mismatchedChild.IsSuccess);
        Assert.Equal(DurableFlowRepairOutcome.Refused, mismatchedChild.Value!.Outcome);
        Assert.Equal(DurableProblemCodes.FlowRepairActionUnsupported, mismatchedChild.Value.Problem!.Code);

        await SetFlowRepairDescriptorCodeAsync(database.DataSource, scope, instance, "tampered");
        var descriptorMismatch = await repairs.RepairAsync(DurableFlowRepairRequest.AssertChildEffectCompleted(
            scope,
            instance,
            new DurableCommandId($"flow-repair-tampered-descriptor-{scenario}"),
            oldShapeAssessment.Value.Revision,
            oldShapeAssessment.Value.SuspensionDescriptorSha256!,
            childWorkId,
            failed.Revision,
            1,
            new string('b', 64),
            "operator",
            "tampered-descriptor"));
        Assert.True(descriptorMismatch.IsSuccess);
        Assert.Equal(DurableFlowRepairOutcome.Refused, descriptorMismatch.Value!.Outcome);
        Assert.Equal(DurableProblemCodes.FlowRepairEvidenceMismatch, descriptorMismatch.Value.Problem!.Code);
        await SetFlowRepairDescriptorCodeAsync(
            database.DataSource,
            scope,
            instance,
            "flow.child_work_requires_attention");

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

        if (effectApplied)
        {
            await UpdateWorkResultPayloadAsync(database.DataSource, scope, childWorkId, [9]);
            var tamperedAssessment = await repairs.GetAssessmentAsync(new DurableFlowRepairAssessmentRequest(scope, instance));
            Assert.True(tamperedAssessment.IsSuccess);
            var tamperedCandidate = Assert.Single(tamperedAssessment.Value!.Candidates);
            var tampered = await repairs.RepairAsync(CreateRepairRequest(
                effectApplied,
                scope,
                instance,
                new DurableCommandId("flow-repair-tampered-payload"),
                tamperedAssessment.Value,
                tamperedCandidate.Evidence,
                tamperedCandidate.Evidence.ChildWorkHistoryEventId,
                "tampered-persisted-payload"));
            Assert.True(tampered.IsSuccess);
            Assert.Equal(DurableFlowRepairOutcome.Refused, tampered.Value!.Outcome);
            Assert.Equal(DurableProblemCodes.FlowRepairEvidenceMismatch, tampered.Value.Problem!.Code);
            await UpdateWorkResultPayloadAsync(database.DataSource, scope, childWorkId, [3]);
        }

        var independentInstance = new DurableFlowInstanceId($"independent-{scenario}");
        Assert.True((await flows.StartAsync(new DurableFlowStartRequest(
            scope,
            new DurableCommandId($"flow-repair-independent-start-{scenario}"),
            $"flow-repair-independent-key-{scenario}",
            independentInstance,
            flowRegistration.FlowId,
            flowRegistration.FlowVersion,
            contextCodec.EncodeObject(new byte[] { 4 })))).IsSuccess);
        var independentBeforeRepair = await flows.GetAsync(new DurableFlowGetRequest(scope, independentInstance));
        Assert.Equal(DurableFlowState.Ready, independentBeforeRepair.Value!.State);
        var independentAssessment = await repairs.GetAssessmentAsync(new DurableFlowRepairAssessmentRequest(scope, independentInstance));
        Assert.True(independentAssessment.IsSuccess);
        Assert.Empty(independentAssessment.Value!.Candidates);

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
        if (!effectApplied)
        {
            await using var completionTransactionConnection = await database.DataSource.OpenConnectionAsync();
            await using var completionTransaction = await completionTransactionConnection.BeginTransactionAsync();
            await using (var workLock = new Npgsql.NpgsqlCommand(
                "SELECT 1 FROM appsurface_durable.work WHERE scope_id = @scope_id AND work_id = @work_id FOR UPDATE;",
                completionTransactionConnection,
                completionTransaction))
            {
                workLock.Parameters.AddWithValue("scope_id", scope.Value);
                workLock.Parameters.AddWithValue("work_id", childWorkId.Value);
                Assert.Equal(1, (int)(await workLock.ExecuteScalarAsync())!);
            }

            var blockedRepair = repairs.RepairAsync(refusedRequest).AsTask();
            using var waitForRepair = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await WaitForRepairToBlockOnWorkAsync(database.DataSource, waitForRepair.Token);
            await using (var flowLock = new Npgsql.NpgsqlCommand(
                "SELECT 1 FROM appsurface_durable.flow_instance WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id FOR UPDATE;",
                completionTransactionConnection,
                completionTransaction))
            {
                flowLock.Parameters.AddWithValue("scope_id", scope.Value);
                flowLock.Parameters.AddWithValue("flow_instance_id", instance.Value);
                flowLock.CommandTimeout = 5;
                Assert.Equal(1, (int)(await flowLock.ExecuteScalarAsync(waitForRepair.Token))!);
            }

            await completionTransaction.CommitAsync(waitForRepair.Token);
            var blockedRepairResult = await blockedRepair.WaitAsync(waitForRepair.Token);
            Assert.True(blockedRepairResult.IsSuccess);
            Assert.Equal(DurableFlowRepairOutcome.Refused, blockedRepairResult.Value!.Outcome);
        }

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
        if (effectApplied)
        {
            var incompatibleResultCodec = new PostgreSqlOpaqueTestCodec("tests.repair.incompatible-result", "v1");
            var incompatibleRegistration = new PostgreSqlOpaqueTestWorkRegistration(
                workRegistration.WorkName,
                workRegistration.WorkVersion,
                workRegistration.ProviderSafety,
                workCodec,
                incompatibleResultCodec);
            IFlowRepairOperatorClient incompatibleRepairs = new PostgreSqlDurableFlowRepairOperatorClient(
                database.DataSource,
                new DurableWorkRegistry([incompatibleRegistration]),
                options);
            var incompatibleResult = await incompatibleRepairs.RepairAsync(CreateRepairRequest(
                effectApplied,
                scope,
                instance,
                new DurableCommandId($"flow-repair-incompatible-result-{scenario}"),
                assessment.Value,
                evidence,
                evidence.ChildWorkHistoryEventId,
                "incompatible-result"));
            Assert.True(incompatibleResult.IsSuccess);
            Assert.Equal(DurableFlowRepairOutcome.Refused, incompatibleResult.Value!.Outcome);
            Assert.Equal(DurableProblemCodes.FlowRepairEvidenceMismatch, incompatibleResult.Value.Problem!.Code);

            var throwingRegistration = new PostgreSqlOpaqueTestWorkRegistration(
                workRegistration.WorkName,
                workRegistration.WorkVersion,
                workRegistration.ProviderSafety,
                workCodec,
                new ThrowingPayloadCodec(resultCodec));
            IFlowRepairOperatorClient throwingRepairs = new PostgreSqlDurableFlowRepairOperatorClient(
                database.DataSource,
                new DurableWorkRegistry([throwingRegistration]),
                options);
            var malformedResult = await throwingRepairs.RepairAsync(CreateRepairRequest(
                effectApplied,
                scope,
                instance,
                new DurableCommandId($"flow-repair-malformed-result-{scenario}"),
                assessment.Value,
                evidence,
                evidence.ChildWorkHistoryEventId,
                "malformed-result"));
            Assert.True(malformedResult.IsSuccess);
            Assert.Equal(DurableFlowRepairOutcome.Refused, malformedResult.Value!.Outcome);
            Assert.Equal(DurableProblemCodes.FlowRepairEvidenceMismatch, malformedResult.Value.Problem!.Code);

            foreach (var field in new[] { "contract", "version", "codec", "classification", "retention" })
            {
                var original = await ReadWorkResultMetadataAsync(database.DataSource, scope, childWorkId, field);
                await SetWorkResultMetadataAsync(database.DataSource, scope, childWorkId, field, $"tampered-{field}");
                var metadataMismatch = await repairs.RepairAsync(CreateRepairRequest(
                    effectApplied,
                    scope,
                    instance,
                    new DurableCommandId($"flow-repair-{field}-mismatch-{scenario}"),
                    assessment.Value,
                    evidence,
                    evidence.ChildWorkHistoryEventId,
                    $"{field}-mismatch"));
                Assert.True(metadataMismatch.IsSuccess);
                Assert.Equal(DurableFlowRepairOutcome.Refused, metadataMismatch.Value!.Outcome);
                Assert.Equal(DurableProblemCodes.FlowRepairEvidenceMismatch, metadataMismatch.Value.Problem!.Code);
                await SetWorkResultMetadataAsync(database.DataSource, scope, childWorkId, field, original);
            }
        }
        else
        {
            await SetWorkLeaseOwnerAsync(database.DataSource, scope, childWorkId, "unexpected-lease");
            var leased = await repairs.RepairAsync(CreateRepairRequest(
                effectApplied,
                scope,
                instance,
                new DurableCommandId($"flow-repair-leased-child-{scenario}"),
                assessment.Value,
                evidence,
                evidence.ChildWorkHistoryEventId,
                "leased-child"));
            Assert.True(leased.IsSuccess);
            Assert.Equal(DurableFlowRepairOutcome.Refused, leased.Value!.Outcome);
            Assert.Equal(DurableProblemCodes.FlowRepairEvidenceMismatch, leased.Value.Problem!.Code);
            await SetWorkLeaseOwnerAsync(database.DataSource, scope, childWorkId, null);
        }
        await CreateDiscardUpdateTriggerAsync(database.DataSource, "flow_instance");
        try
        {
            var flowFence = await Assert.ThrowsAsync<InvalidOperationException>(async () => await repairs.RepairAsync(request));
            Assert.Equal(
                effectApplied
                    ? "The Flow repair lost its locked completed-effect revision fence."
                    : "The Flow repair lost its locked no-effect revision fence.",
                flowFence.Message);
        }
        finally
        {
            await DropDiscardUpdateTriggerAsync(database.DataSource, "flow_instance");
        }

        await CreateDiscardUpdateTriggerAsync(database.DataSource, "flow_wait");
        try
        {
            var waitFence = await Assert.ThrowsAsync<InvalidOperationException>(async () => await repairs.RepairAsync(request));
            Assert.Equal(
                effectApplied
                    ? "The Flow repair could not resolve exactly one locked activity wait."
                    : "The Flow repair could not restore exactly one locked activity wait.",
                waitFence.Message);
        }
        finally
        {
            await DropDiscardUpdateTriggerAsync(database.DataSource, "flow_wait");
        }

        await DeleteFlowDispatchAsync(database.DataSource, scope, instance);
        try
        {
            var missingDispatch = await Assert.ThrowsAsync<InvalidOperationException>(async () => await repairs.RepairAsync(request));
            Assert.Equal("The Flow repair could not project its unique Flow dispatch row.", missingDispatch.Message);
        }
        finally
        {
            await InsertFlowDispatchAsync(database.DataSource, scope, instance, assessment.Value.Revision);
        }

        await CreateDiscardRepairCommandInsertTriggerAsync(database.DataSource);
        try
        {
            var missingCommand = await Assert.ThrowsAsync<InvalidOperationException>(async () => await repairs.RepairAsync(request));
            Assert.Equal("The Flow repair did not persist one terminal command record.", missingCommand.Message);
        }
        finally
        {
            await DropDiscardRepairCommandInsertTriggerAsync(database.DataSource);
        }

        await using (var blocker = await database.DataSource.OpenConnectionAsync())
        await using (var blockerTransaction = await blocker.BeginTransactionAsync())
        {
            await using var lockFlow = new Npgsql.NpgsqlCommand(
                "SELECT 1 FROM appsurface_durable.flow_instance WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id FOR UPDATE;",
                blocker,
                blockerTransaction);
            lockFlow.Parameters.AddWithValue("scope_id", scope.Value);
            lockFlow.Parameters.AddWithValue("flow_instance_id", instance.Value);
            Assert.Equal(1, (int)(await lockFlow.ExecuteScalarAsync())!);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await repairs.RepairAsync(request, cancellation.Token));
            await blockerTransaction.RollbackAsync();
        }
        var afterCancellation = await flows.GetAsync(new DurableFlowGetRequest(scope, instance));
        Assert.Equal(DurableFlowState.Suspended, afterCancellation.Value!.State);
        Assert.Equal(assessment.Value.Revision, afterCancellation.Value.Revision);
        Assert.Equal(0L, await CountRepairCommandsAsync(database.DataSource, scope, request.CommandId));
        var concurrentResults = await Task.WhenAll(
            repairs.RepairAsync(request).AsTask(),
            repairs.RepairAsync(request).AsTask());
        var applied = Assert.Single(concurrentResults, result => result.Value?.Outcome == DurableFlowRepairOutcome.Applied);
        var duplicate = Assert.Single(concurrentResults, result => result.Value?.Outcome == DurableFlowRepairOutcome.Duplicate);
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
        var independentAfterRepair = await flows.GetAsync(new DurableFlowGetRequest(scope, independentInstance));
        Assert.Equal(independentBeforeRepair.Value.State, independentAfterRepair.Value!.State);
        Assert.Equal(independentBeforeRepair.Value.Revision, independentAfterRepair.Value.Revision);
        Assert.Equal(
            effectApplied ? "succeeded" : "retry_wait",
            await ReadWorkStateAsync(database.DataSource, scope, childWorkId));
        Assert.Equal(
            effectApplied ? "activity_completed" : "active",
            await ReadFlowWaitStateAsync(database.DataSource, scope, instance));
        Assert.Equal(1L, await CountRepairCommandsAsync(database.DataSource, scope, request.CommandId));
        Assert.Equal(1L, await CountRepairCollisionsAsync(database.DataSource, scope, request.CommandId));
        await UpdateRepairReceiptSha256Async(database.DataSource, scope, request.CommandId, new string('f', 64));
        var receiptTamper = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await repairs.RepairAsync(request));
        Assert.Equal(
            "The persisted Flow repair receipt digest does not match its retained canonical evidence.",
            receiptTamper.Message);
        Assert.DoesNotContain("[3]", receiptTamper.Message, StringComparison.Ordinal);
    }

    private static async Task WaitForRepairToBlockOnWorkAsync(
        Npgsql.NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT EXISTS
            (
                SELECT 1
                FROM pg_stat_activity
                WHERE datname = current_database()
                  AND wait_event_type = 'Lock'
                  AND query LIKE '%FROM appsurface_durable.work%'
                  AND query LIKE '%FOR UPDATE%'
            );
            """;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var command = dataSource.CreateCommand(sql);
            if (await command.ExecuteScalarAsync(cancellationToken) is true)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }
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

    private static async ValueTask CreateDiscardUpdateTriggerAsync(
        Npgsql.NpgsqlDataSource dataSource,
        string tableName)
    {
        var triggerName = GetDiscardTriggerName(tableName);
        await using var command = dataSource.CreateCommand(
            $"""
            CREATE FUNCTION appsurface_durable.{triggerName}_function()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                RETURN NULL;
            END;
            $$;
            CREATE TRIGGER {triggerName}
            BEFORE UPDATE ON appsurface_durable.{tableName}
            FOR EACH ROW
            EXECUTE FUNCTION appsurface_durable.{triggerName}_function();
            """);
        await command.ExecuteNonQueryAsync();
    }

    private static async ValueTask DropDiscardUpdateTriggerAsync(
        Npgsql.NpgsqlDataSource dataSource,
        string tableName)
    {
        var triggerName = GetDiscardTriggerName(tableName);
        await using var command = dataSource.CreateCommand(
            $"DROP TRIGGER IF EXISTS {triggerName} ON appsurface_durable.{tableName}; DROP FUNCTION IF EXISTS appsurface_durable.{triggerName}_function();");
        await command.ExecuteNonQueryAsync();
    }

    private static async ValueTask DeleteFlowDispatchAsync(
        Npgsql.NpgsqlDataSource dataSource,
        DurableScopeId scope,
        DurableFlowInstanceId instance)
    {
        await using var command = dataSource.CreateCommand(
            "DELETE FROM appsurface_durable.flow_dispatch WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id AND kind = 'flow';");
        command.Parameters.AddWithValue("scope_id", scope.Value);
        command.Parameters.AddWithValue("flow_instance_id", instance.Value);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async ValueTask InsertFlowDispatchAsync(
        Npgsql.NpgsqlDataSource dataSource,
        DurableScopeId scope,
        DurableFlowInstanceId instance,
        long expectedRevision)
    {
        await using var command = dataSource.CreateCommand(
            "INSERT INTO appsurface_durable.flow_dispatch (dispatch_id, scope_id, kind, flow_instance_id, due_at, state, expected_revision) VALUES (@dispatch_id, @scope_id, 'flow', @flow_instance_id, clock_timestamp(), 'suspended', @expected_revision);");
        command.Parameters.AddWithValue("dispatch_id", Guid.NewGuid());
        command.Parameters.AddWithValue("scope_id", scope.Value);
        command.Parameters.AddWithValue("flow_instance_id", instance.Value);
        command.Parameters.AddWithValue("expected_revision", expectedRevision);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async ValueTask CreateDiscardRepairCommandInsertTriggerAsync(Npgsql.NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            """
            CREATE FUNCTION appsurface_durable.discard_flow_repair_command_insert_function()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                RETURN NULL;
            END;
            $$;
            CREATE TRIGGER discard_flow_repair_command_insert
            BEFORE INSERT ON appsurface_durable.flow_repair_command
            FOR EACH ROW
            EXECUTE FUNCTION appsurface_durable.discard_flow_repair_command_insert_function();
            """);
        await command.ExecuteNonQueryAsync();
    }

    private static async ValueTask DropDiscardRepairCommandInsertTriggerAsync(Npgsql.NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            "DROP TRIGGER IF EXISTS discard_flow_repair_command_insert ON appsurface_durable.flow_repair_command; DROP FUNCTION IF EXISTS appsurface_durable.discard_flow_repair_command_insert_function();");
        await command.ExecuteNonQueryAsync();
    }

    private static string GetDiscardTriggerName(string tableName) => tableName switch
    {
        "flow_instance" => "discard_flow_instance_update",
        "flow_wait" => "discard_flow_wait_update",
        _ => throw new ArgumentOutOfRangeException(nameof(tableName)),
    };

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

    private static async ValueTask UpdateWorkResultPayloadAsync(
        Npgsql.NpgsqlDataSource dataSource,
        DurableScopeId scope,
        DurableWorkId workId,
        byte[] payload)
    {
        await using var command = dataSource.CreateCommand(
            "UPDATE appsurface_durable.work SET result_payload = @result_payload WHERE scope_id = @scope_id AND work_id = @work_id;");
        command.Parameters.AddWithValue("result_payload", payload);
        command.Parameters.AddWithValue("scope_id", scope.Value);
        command.Parameters.AddWithValue("work_id", workId.Value);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async ValueTask SetFlowRepairDescriptorAsync(
        Npgsql.NpgsqlDataSource dataSource,
        DurableScopeId scope,
        DurableFlowInstanceId instance,
        string? descriptorSha256)
    {
        await using var command = dataSource.CreateCommand(
            "UPDATE appsurface_durable.flow_instance SET suspension_descriptor_schema = @schema, suspension_descriptor_sha256 = @sha256 WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id;");
        command.Parameters.AddWithValue(
            "schema",
            descriptorSha256 is null
                ? DBNull.Value
                : "appsurface.durable.flow.child-suspension.v1");
        command.Parameters.AddWithValue("sha256", descriptorSha256 ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("scope_id", scope.Value);
        command.Parameters.AddWithValue("flow_instance_id", instance.Value);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async ValueTask UpdateRepairReceiptSha256Async(
        Npgsql.NpgsqlDataSource dataSource,
        DurableScopeId scope,
        DurableCommandId commandId,
        string receiptSha256)
    {
        await using var command = dataSource.CreateCommand(
            "UPDATE appsurface_durable.flow_repair_command SET receipt_sha256 = @receipt_sha256 WHERE scope_id = @scope_id AND command_id = @command_id;");
        command.Parameters.AddWithValue("receipt_sha256", receiptSha256);
        command.Parameters.AddWithValue("scope_id", scope.Value);
        command.Parameters.AddWithValue("command_id", commandId.Value);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async ValueTask SetScopeStateAsync(
        Npgsql.NpgsqlDataSource dataSource,
        DurableScopeId scope,
        string state)
    {
        await using var command = dataSource.CreateCommand(
            "UPDATE appsurface_durable.scope SET state = @state WHERE scope_id = @scope_id;");
        command.Parameters.AddWithValue("state", state);
        command.Parameters.AddWithValue("scope_id", scope.Value);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async ValueTask SetFlowRepairDescriptorCodeAsync(
        Npgsql.NpgsqlDataSource dataSource,
        DurableScopeId scope,
        DurableFlowInstanceId instance,
        string code)
    {
        await using var command = dataSource.CreateCommand(
            "UPDATE appsurface_durable.flow_instance SET suspension_descriptor = jsonb_set(suspension_descriptor, '{code}', to_jsonb(@code::text)) WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id;");
        command.Parameters.AddWithValue("code", code);
        command.Parameters.AddWithValue("scope_id", scope.Value);
        command.Parameters.AddWithValue("flow_instance_id", instance.Value);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async ValueTask SetResultContractIdAsync(
        Npgsql.NpgsqlDataSource dataSource,
        DurableScopeId scope,
        DurableFlowInstanceId instance,
        DurableWorkId workId,
        string contractId)
    {
        await using var command = dataSource.CreateCommand(
            "UPDATE appsurface_durable.work SET result_contract_id = @contract_id WHERE scope_id = @scope_id AND work_id = @work_id; UPDATE appsurface_durable.flow_wait SET result_contract_id = @contract_id WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id AND child_work_id = @work_id;");
        command.Parameters.AddWithValue("contract_id", contractId);
        command.Parameters.AddWithValue("scope_id", scope.Value);
        command.Parameters.AddWithValue("flow_instance_id", instance.Value);
        command.Parameters.AddWithValue("work_id", workId.Value);
        Assert.Equal(2, await command.ExecuteNonQueryAsync());
    }

    private static async ValueTask SetWorkLeaseOwnerAsync(
        Npgsql.NpgsqlDataSource dataSource,
        DurableScopeId scope,
        DurableWorkId workId,
        string? leaseOwner)
    {
        await using var command = dataSource.CreateCommand(
            "UPDATE appsurface_durable.work SET lease_owner = @lease_owner WHERE scope_id = @scope_id AND work_id = @work_id;");
        command.Parameters.AddWithValue("lease_owner", leaseOwner ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("scope_id", scope.Value);
        command.Parameters.AddWithValue("work_id", workId.Value);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async ValueTask<string> ReadWorkResultMetadataAsync(
        Npgsql.NpgsqlDataSource dataSource,
        DurableScopeId scope,
        DurableWorkId workId,
        string field)
    {
        var column = GetResultMetadataColumn(field);
        await using var command = dataSource.CreateCommand(
            $"SELECT {column} FROM appsurface_durable.work WHERE scope_id = @scope_id AND work_id = @work_id;");
        command.Parameters.AddWithValue("scope_id", scope.Value);
        command.Parameters.AddWithValue("work_id", workId.Value);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async ValueTask SetWorkResultMetadataAsync(
        Npgsql.NpgsqlDataSource dataSource,
        DurableScopeId scope,
        DurableWorkId workId,
        string field,
        string value)
    {
        var column = GetResultMetadataColumn(field);
        await using var command = dataSource.CreateCommand(
            $"UPDATE appsurface_durable.work SET {column} = @value WHERE scope_id = @scope_id AND work_id = @work_id;");
        command.Parameters.AddWithValue("value", value);
        command.Parameters.AddWithValue("scope_id", scope.Value);
        command.Parameters.AddWithValue("work_id", workId.Value);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static string GetResultMetadataColumn(string field) => field switch
    {
        "contract" => "result_contract_id",
        "version" => "result_schema_version",
        "codec" => "result_codec_id",
        "classification" => "result_classification",
        "retention" => "result_retention_policy_id",
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

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

    private sealed class ThrowingPayloadCodec(IDurablePayloadCodec inner) : IDurablePayloadCodec
    {
        public Type PayloadType => inner.PayloadType;
        public string ContractName => inner.ContractName;
        public string ContractVersion => inner.ContractVersion;
        public DurableDataClassification Classification => inner.Classification;
        public string RetentionPolicyId => inner.RetentionPolicyId;
        public DurableEncodedPayload EncodeObject(object value) => inner.EncodeObject(value);
        public object DecodeObject(DurableEncodedPayload payload) => throw new JsonException("Malformed test payload.");
    }
}
