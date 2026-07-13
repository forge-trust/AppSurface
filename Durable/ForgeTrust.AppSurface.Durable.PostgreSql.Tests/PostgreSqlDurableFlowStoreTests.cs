using System.Text.Json.Serialization;
using ForgeTrust.AppSurface.Flow;
using ForgeTrust.AppSurface.Workers;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

public sealed class PostgreSqlDurableFlowStoreTests
{
    [Fact]
    public async Task StartAndProcessing_AreIdempotentAndEvaluateExactlyOneNodePerClaim()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var tracker = new FlowNodeTracker();
        var definition = FlowGraphBuilder<PostgreSqlFlowContext>
            .Create("tests.next-flow", "v1")
            .AddNode("first", new CountingNextFlowNode(tracker), "done")
            .AddNode("done", new CountingCompleteFlowNode(tracker))
            .StartAt("first")
            .Build();
        var runtime = CreateRuntime(database.DataSource, definition);
        var request = CreateStart(runtime, "scope-next", "command-start", "instance-next");

        var accepted = await runtime.Client.StartAsync(request);
        var duplicate = await runtime.Client.StartAsync(request);
        var transportRetry = await runtime.Client.StartAsync(new DurableFlowStartRequest(
            request.ScopeId,
            new DurableCommandId("command-start-retry"),
            request.IdempotencyKey,
            request.InstanceId,
            request.FlowId,
            request.FlowVersion,
            request.Context));
        var conflict = await runtime.Client.StartAsync(
            CreateStart(runtime, "scope-next", "command-start", "instance-next", step: 9));

        Assert.True(accepted.IsSuccess);
        Assert.Equal(DurableFlowCommandOutcome.Accepted, accepted.Value!.Outcome);
        Assert.True(duplicate.IsSuccess);
        Assert.Equal(DurableFlowCommandOutcome.Duplicate, duplicate.Value!.Outcome);
        Assert.True(transportRetry.IsSuccess);
        Assert.Equal(DurableFlowCommandOutcome.Duplicate, transportRetry.Value!.Outcome);
        Assert.False(conflict.IsSuccess);
        Assert.Equal(DurableProblemCodes.FlowStartConflict, conflict.Problem!.Code);
        Assert.Equal(("ready", 1L), await ReadFlowAsync(database.DataSource, request.ScopeId, request.InstanceId));

        var first = await ProcessFlowAsync(runtime, request.ScopeId);

        Assert.Equal(PostgreSqlFlowProcessingOutcome.Applied, first.Outcome);
        Assert.Equal(DurableFlowState.Ready, first.State);
        Assert.Equal(1, tracker.FirstExecutions);
        Assert.Equal(0, tracker.CompleteExecutions);

        string? terminalCode = null;
        var terminalCallbackSawOpenTransaction = false;
        var secondCandidate = Assert.Single(
            await runtime.Store.DiscoverAsync(10),
            candidate => candidate.ScopeId == request.ScopeId && candidate.AggregateKind == "flow");
        var second = await runtime.Store.TryProcessAsync(
            secondCandidate,
            "worker-terminal",
            runtime.Registry,
            runtime.Codecs,
            (transaction, scopeId, instanceId, code, _) =>
            {
                terminalCallbackSawOpenTransaction = transaction.Connection?.State == System.Data.ConnectionState.Open;
                Assert.Equal(request.ScopeId, scopeId);
                Assert.Equal(request.InstanceId, instanceId);
                terminalCode = code;
                return ValueTask.CompletedTask;
            });

        Assert.Equal(PostgreSqlFlowProcessingOutcome.Applied, second.Outcome);
        Assert.Equal(DurableFlowState.Completed, second.State);
        Assert.Equal("completed", terminalCode);
        Assert.True(terminalCallbackSawOpenTransaction);
        Assert.Equal(1, tracker.FirstExecutions);
        Assert.Equal(1, tracker.CompleteExecutions);
        Assert.DoesNotContain(
            await runtime.Store.DiscoverAsync(10),
            candidate => candidate.ScopeId == request.ScopeId);
        Assert.Equal(("completed", 5L), await ReadFlowAsync(database.DataSource, request.ScopeId, request.InstanceId));
        Assert.Equal(
            1,
            await CountScopedAsync(
                database.DataSource,
                request.ScopeId,
                "SELECT count(*) FROM appsurface_durable.flow_instance WHERE scope_id = @scope_id;"));
        var history = await ReadTransitionHistoryAsync(database.DataSource, request.ScopeId, "transition_next");
        Assert.Equal(0, runtime.ContextCodec.Decode(history.InputContext).Step);
        Assert.Equal(1, runtime.ContextCodec.Decode(history.OutputContext).Step);
        Assert.Equal(DurableFlowRegistration.CurrentAuthoringModel, history.AuthoringModel);
        Assert.Equal("flow-command-v1", history.CommandSchemaVersion);
        Assert.Equal(32, history.DefinitionFingerprintLength);
        Assert.Equal("appsurface.flow-transition-output", history.TransitionOutputContract);
    }

    [Fact]
    public async Task RaiseEvent_ConsumesOnlyAnActiveMatchingWaitAndPreservesEarlyIds()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var definition = CreateWaitDefinition("tests.event-flow", timeout: null);
        var runtime = CreateRuntime(database.DataSource, definition);
        var start = CreateStart(runtime, "scope-event", "command-start", "instance-event");
        await runtime.Client.StartAsync(start);
        var eventRequest = new DurableFlowEventRequest(
            start.ScopeId,
            new DurableCommandId("command-event"),
            new DurableFlowEventId("event-once"),
            start.InstanceId,
            "approved");

        var early = await runtime.Client.RaiseEventAsync(eventRequest);

        Assert.True(early.IsSuccess);
        Assert.Equal(DurableFlowCommandOutcome.NotWaitingYet, early.Value!.Outcome);
        Assert.Equal(
            0,
            await CountScopedAsync(
                database.DataSource,
                start.ScopeId,
                """
                SELECT count(*) FROM appsurface_durable.flow_command
                WHERE scope_id = @scope_id AND command_type = 'external_event';
                """));

        var waiting = await ProcessFlowAsync(runtime, start.ScopeId);
        var accepted = await runtime.Client.RaiseEventAsync(eventRequest);
        var duplicate = await runtime.Client.RaiseEventAsync(eventRequest);

        Assert.Equal(DurableFlowState.WaitingForEvent, waiting.State);
        Assert.Equal(DurableFlowCommandOutcome.Accepted, accepted.Value!.Outcome);
        Assert.Equal(DurableFlowCommandOutcome.Duplicate, duplicate.Value!.Outcome);
        Assert.Equal(4, accepted.Value.Revision);
        Assert.Equal(
            1,
            await CountScopedAsync(
                database.DataSource,
                start.ScopeId,
                """
                SELECT count(*) FROM appsurface_durable.flow_command
                WHERE scope_id = @scope_id AND command_type = 'external_event';
                """));

        var completed = await ProcessFlowAsync(runtime, start.ScopeId);

        Assert.Equal(DurableFlowState.Completed, completed.State);
        Assert.Equal(("completed", 6L), await ReadFlowAsync(database.DataSource, start.ScopeId, start.InstanceId));
    }

    [Fact]
    public async Task TypedEventWait_RequiresExactContractAndRejectsTypeSmugglingBeforeConsumption()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var (runtime, eventCodec, otherCodec) = CreateTypedEventRuntime(database.DataSource);
        var start = CreateStart(runtime, "scope-typed-event", "command-start", "instance-typed-event");
        await runtime.Client.StartAsync(start);
        await ProcessFlowAsync(runtime, start.ScopeId);
        var eventId = new DurableFlowEventId("typed-event-once");

        var missingPayload = await runtime.Client.RaiseEventAsync(
            new DurableFlowEventRequest(
                start.ScopeId,
                new DurableCommandId("command-missing-payload"),
                eventId,
                start.InstanceId,
                "typed-approved"));
        var wrongContract = await runtime.Client.RaiseEventAsync(
            new DurableFlowEventRequest(
                start.ScopeId,
                new DurableCommandId("command-wrong-contract"),
                eventId,
                start.InstanceId,
                "typed-approved",
                otherCodec.Encode(new PostgreSqlFlowEvent("safe-other"))));
        var smuggled = new DurableEncodedPayload(
            eventCodec.ContractName,
            eventCodec.ContractVersion,
            DurableDataClassification.ApprovedApplication,
            "{\"Unexpected\":\"safe\"}"u8.ToArray());
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(
            async () => await runtime.Client.RaiseEventAsync(
                new DurableFlowEventRequest(
                    start.ScopeId,
                    new DurableCommandId("command-smuggled"),
                    eventId,
                    start.InstanceId,
                    "typed-approved",
                    smuggled)));

        Assert.False(missingPayload.IsSuccess);
        Assert.Equal(DurableProblemCodes.FlowEventContractMismatch, missingPayload.Problem!.Code);
        Assert.False(wrongContract.IsSuccess);
        Assert.Equal(DurableProblemCodes.FlowEventContractMismatch, wrongContract.Problem!.Code);
        Assert.Equal(
            0,
            await CountScopedAsync(
                database.DataSource,
                start.ScopeId,
                """
                SELECT count(*) FROM appsurface_durable.flow_command
                WHERE scope_id = @scope_id AND command_type = 'external_event';
                """));

        var acceptedRequest = new DurableFlowEventRequest(
            start.ScopeId,
            new DurableCommandId("command-typed-event"),
            eventId,
            start.InstanceId,
            "typed-approved",
            eventCodec.Encode(new PostgreSqlFlowEvent("safe-approved")));
        var accepted = await runtime.Client.RaiseEventAsync(acceptedRequest);
        var transportRetry = await runtime.Client.RaiseEventAsync(
            new DurableFlowEventRequest(
                start.ScopeId,
                new DurableCommandId("command-typed-event-retry"),
                eventId,
                start.InstanceId,
                "typed-approved",
                eventCodec.Encode(new PostgreSqlFlowEvent("safe-approved"))));

        Assert.Equal(DurableFlowCommandOutcome.Accepted, accepted.Value!.Outcome);
        Assert.Equal(DurableFlowCommandOutcome.Duplicate, transportRetry.Value!.Outcome);
        Assert.Equal(DurableFlowState.Completed, (await ProcessFlowAsync(runtime, start.ScopeId)).State);
    }

    [Fact]
    public async Task TimerAndEventRace_ProducesOneWinnerAndOneContinuation()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var definition = CreateWaitDefinition("tests.timer-flow", TimeSpan.FromHours(1));
        var runtime = CreateRuntime(database.DataSource, definition);
        var start = CreateStart(runtime, "scope-timer", "command-start", "instance-timer");
        await runtime.Client.StartAsync(start);
        var waiting = await ProcessFlowAsync(runtime, start.ScopeId);
        Assert.Equal(DurableFlowState.WaitingForTimer, waiting.State);
        await ForceTimerDueAsync(database.DataSource, start.ScopeId);
        var timer = Assert.Single(
            await runtime.Store.DiscoverAsync(10),
            candidate => candidate.ScopeId == start.ScopeId && candidate.AggregateKind == "timer");
        var eventRequest = new DurableFlowEventRequest(
            start.ScopeId,
            new DurableCommandId("command-race-event"),
            new DurableFlowEventId("event-race"),
            start.InstanceId,
            "approved");

        var timerTask = runtime.Store.TryProcessAsync(
            timer,
            "timer-worker",
            runtime.Registry,
            runtime.Codecs).AsTask();
        var eventTask = runtime.Client.RaiseEventAsync(eventRequest).AsTask();
        await Task.WhenAll(timerTask, eventTask);
        var timerResult = await timerTask;
        var eventResult = (await eventTask).Value!;

        Assert.Equal(
            1,
            (timerResult.Outcome == PostgreSqlFlowProcessingOutcome.Applied ? 1 : 0)
            + (eventResult.Outcome == DurableFlowCommandOutcome.Accepted ? 1 : 0));
        Assert.Contains(
            eventResult.Outcome,
            new[] { DurableFlowCommandOutcome.Accepted, DurableFlowCommandOutcome.NotWaitingYet });
        Assert.Contains(
            timerResult.Outcome,
            new[]
            {
                PostgreSqlFlowProcessingOutcome.Applied,
                PostgreSqlFlowProcessingOutcome.NotClaimed,
                PostgreSqlFlowProcessingOutcome.RaceLost,
            });
        var waitState = await ReadScopedScalarAsync<string>(
            database.DataSource,
            start.ScopeId,
            "SELECT state FROM appsurface_durable.flow_wait WHERE scope_id = @scope_id;");
        Assert.Contains(waitState, new[] { "event_won", "timer_won" });

        var completed = await ProcessFlowAsync(runtime, start.ScopeId);

        Assert.Equal(DurableFlowState.Completed, completed.State);
        Assert.DoesNotContain(
            await runtime.Store.DiscoverAsync(10),
            candidate => candidate.ScopeId == start.ScopeId);
    }

    [Fact]
    public async Task ActivityCompletionFailureAndCancellation_ResolveFlowAndWorkAtomically()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var runtime = CreateActivityRuntime(database.DataSource);
        var workStore = new PostgreSqlDurableWorkStore(database.DataSource, runtime.Epoch);

        var successStart = CreateStart(runtime, "scope-activity-success", "command-start", "instance-success");
        await runtime.Client.StartAsync(successStart);
        var waiting = await ProcessFlowAsync(runtime, successStart.ScopeId);
        var workId = Assert.IsType<DurableWorkId>(waiting.ActivityWorkId);
        var workCandidate = Assert.Single(
            await workStore.DiscoverAsync(10),
            candidate => candidate.ScopeId == successStart.ScopeId);
        var claim = await workStore.TryClaimAsync(workCandidate, "activity-worker");
        Assert.NotNull(claim);
        PostgreSqlFlowProcessingResult? resumed = null;
        var result = runtime.ResultCodec!.Encode(new PostgreSqlFlowResult("done"));

        var workCompletion = await workStore.RecordCompletionAsync(
            claim!,
            new PostgreSqlWorkCompletion(
                PostgreSqlWorkCompletionKind.Succeeded,
                "provider_succeeded",
                "{}",
                result),
            async (transaction, _, cancellationToken) =>
            {
                resumed = await runtime.Store.ResumeActivityAsync(
                    transaction,
                    successStart.ScopeId,
                    workId,
                    result,
                    cancellationToken);
            });

        Assert.Equal(DurableWorkState.Succeeded, workCompletion.State);
        Assert.Equal(PostgreSqlFlowProcessingOutcome.Applied, resumed!.Outcome);
        Assert.Equal(
            "succeeded|ready",
            await ReadScopedScalarAsync<string>(
                database.DataSource,
                successStart.ScopeId,
                """
                SELECT work.state || '|' || flow.state
                FROM appsurface_durable.work AS work
                JOIN appsurface_durable.flow_wait AS wait
                  ON wait.scope_id = work.scope_id AND wait.activity_work_id = work.work_id
                JOIN appsurface_durable.flow_instance AS flow
                  ON flow.scope_id = wait.scope_id AND flow.flow_instance_id = wait.flow_instance_id
                WHERE work.scope_id = @scope_id;
                """));
        Assert.Equal(DurableFlowState.Completed, (await ProcessFlowAsync(runtime, successStart.ScopeId)).State);

        var failureStart = CreateStart(runtime, "scope-activity-failure", "command-start", "instance-failure");
        await runtime.Client.StartAsync(failureStart);
        var failureWaiting = await ProcessFlowAsync(runtime, failureStart.ScopeId);
        var failureWorkId = Assert.IsType<DurableWorkId>(failureWaiting.ActivityWorkId);
        var failureCandidate = Assert.Single(
            await workStore.DiscoverAsync(10),
            candidate => candidate.ScopeId == failureStart.ScopeId);
        var failureClaim = await workStore.TryClaimAsync(failureCandidate, "activity-worker");
        Assert.NotNull(failureClaim);
        PostgreSqlFlowProcessingResult? failed = null;

        await workStore.RecordCompletionAsync(
            failureClaim!,
            new PostgreSqlWorkCompletion(PostgreSqlWorkCompletionKind.FailedTerminal, "ASDUR_ACTIVITY_FAILED", "{}"),
            async (transaction, _, cancellationToken) =>
            {
                failed = await runtime.Store.FailActivityAsync(
                    transaction,
                    failureStart.ScopeId,
                    failureWorkId,
                    PostgreSqlFlowActivityFailureKind.FailedTerminal,
                    "ASDUR_ACTIVITY_FAILED",
                    cancellationToken);
            });

        Assert.Equal(DurableFlowState.Suspended, failed!.State);
        Assert.Equal(
            ("suspended", 4L),
            await ReadFlowAsync(database.DataSource, failureStart.ScopeId, failureStart.InstanceId));

        var cancelStart = CreateStart(runtime, "scope-activity-cancel", "command-start", "instance-cancel");
        await runtime.Client.StartAsync(cancelStart);
        var cancelWaiting = await ProcessFlowAsync(runtime, cancelStart.ScopeId);
        var canceled = await runtime.Client.CancelAsync(
            new DurableFlowCancelRequest(
                cancelStart.ScopeId,
                new DurableCommandId("command-cancel"),
                cancelStart.InstanceId,
                "operator-7",
                "consumer_requested",
                cancelWaiting.Revision));

        Assert.Equal(DurableFlowCommandOutcome.Accepted, canceled.Value!.Outcome);
        Assert.Equal(DurableFlowState.Canceled, canceled.Value.State);
        Assert.Equal(
            "operator-7|consumer_requested|accepted",
            await ReadScopedScalarAsync<string>(
                database.DataSource,
                cancelStart.ScopeId,
                """
                SELECT actor_id || '|' || reason_code || '|' || outcome
                FROM appsurface_durable.flow_command
                WHERE scope_id = @scope_id AND command_type = 'cancel';
                """));
        Assert.Equal(
            "canceled_before_effect|canceled",
            await ReadScopedScalarAsync<string>(
                database.DataSource,
                cancelStart.ScopeId,
                """
                SELECT work.state || '|' || flow.state
                FROM appsurface_durable.work AS work
                JOIN appsurface_durable.flow_wait AS wait
                  ON wait.scope_id = work.scope_id AND wait.activity_work_id = work.work_id
                JOIN appsurface_durable.flow_instance AS flow
                  ON flow.scope_id = wait.scope_id AND flow.flow_instance_id = wait.flow_instance_id
                WHERE work.scope_id = @scope_id;
                """));
    }

    [Fact]
    public async Task CancelPendingActivitySuccess_TerminalizesCanceledAndInvokesEveryTerminalHook()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var terminalCodes = new List<string>();
        var runtime = CreateActivityRuntime(
            database.DataSource,
            (transaction, _, _, terminalCode, _) =>
            {
                Assert.Equal(System.Data.ConnectionState.Open, transaction.Connection?.State);
                terminalCodes.Add(terminalCode);
                return ValueTask.CompletedTask;
            });
        var workStore = new PostgreSqlDurableWorkStore(database.DataSource, runtime.Epoch);
        var start = CreateStart(runtime, "scope-cancel-pending", "command-start", "instance-cancel-pending");
        await runtime.Client.StartAsync(start);
        var waiting = await ProcessFlowAsync(runtime, start.ScopeId);
        var workId = Assert.IsType<DurableWorkId>(waiting.ActivityWorkId);
        var candidate = Assert.Single(
            await workStore.DiscoverAsync(10),
            item => item.ScopeId == start.ScopeId);
        var claim = await workStore.TryClaimAsync(candidate, "effect-worker");
        Assert.NotNull(claim);
        var permit = await workStore.TryAcquireEffectPermitAsync(claim!);
        Assert.NotNull(permit);

        var cancellation = await runtime.Client.CancelAsync(
            new DurableFlowCancelRequest(
                start.ScopeId,
                new DurableCommandId("command-cancel"),
                start.InstanceId,
                "operator-8",
                "consumer_requested",
                waiting.Revision));

        Assert.Equal(DurableFlowState.CancelPending, cancellation.Value!.State);
        Assert.Empty(terminalCodes);
        var encodedResult = runtime.ResultCodec!.Encode(new PostgreSqlFlowResult("effect-applied"));
        PostgreSqlFlowProcessingResult? resumed = null;
        var completion = await workStore.RecordCompletionAsync(
            permit!.Claim,
            new PostgreSqlWorkCompletion(
                PostgreSqlWorkCompletionKind.Succeeded,
                "provider_succeeded",
                "{}",
                encodedResult),
            async (transaction, terminalState, cancellationToken) =>
            {
                Assert.Equal(DurableWorkState.SucceededAfterCancelRequested, terminalState);
                resumed = await runtime.Store.ResumeActivityAsync(
                    transaction,
                    start.ScopeId,
                    workId,
                    encodedResult,
                    cancellationToken);
            });

        Assert.Equal(DurableWorkState.SucceededAfterCancelRequested, completion.State);
        Assert.Equal(DurableFlowState.Canceled, resumed!.State);
        Assert.Equal(new[] { "canceled_after_activity_success" }, terminalCodes);
        Assert.Equal(
            "succeeded_after_cancel_requested|canceled|activity_completed|terminal",
            await ReadScopedScalarAsync<string>(
                database.DataSource,
                start.ScopeId,
                """
                SELECT work.state || '|' || flow.state || '|' || wait.state || '|' || dispatch.state
                FROM appsurface_durable.work AS work
                JOIN appsurface_durable.flow_wait AS wait
                  ON wait.scope_id = work.scope_id AND wait.activity_work_id = work.work_id
                JOIN appsurface_durable.flow_instance AS flow
                  ON flow.scope_id = wait.scope_id AND flow.flow_instance_id = wait.flow_instance_id
                JOIN appsurface_durable.dispatch AS dispatch
                  ON dispatch.scope_id = flow.scope_id
                 AND dispatch.aggregate_kind = 'flow'
                 AND dispatch.aggregate_id = flow.flow_instance_id
                WHERE work.scope_id = @scope_id;
                """));

        var directStart = CreateStart(runtime, "scope-direct-cancel", "command-start", "instance-direct-cancel");
        await runtime.Client.StartAsync(directStart);
        var directWaiting = await ProcessFlowAsync(runtime, directStart.ScopeId);
        var directCancel = await runtime.Client.CancelAsync(
            new DurableFlowCancelRequest(
                directStart.ScopeId,
                new DurableCommandId("command-cancel"),
                directStart.InstanceId,
                "operator-8",
                "direct_cancel",
                directWaiting.Revision));
        Assert.Equal(DurableFlowState.Canceled, directCancel.Value!.State);
        Assert.Contains("direct_cancel", terminalCodes);

        var workCancelStart = CreateStart(runtime, "scope-work-cancel", "command-start", "instance-work-cancel");
        await runtime.Client.StartAsync(workCancelStart);
        var workCancelWaiting = await ProcessFlowAsync(runtime, workCancelStart.ScopeId);
        var workCancelId = Assert.IsType<DurableWorkId>(workCancelWaiting.ActivityWorkId);
        var workCanceled = await workStore.RequestCancellationAsync(
            workCancelStart.ScopeId,
            workCancelId,
            "operator-8",
            "work_canceled",
            expectedRevision: 1,
            async (transaction, terminalState, cancellationToken) =>
            {
                Assert.Equal(DurableWorkState.CanceledBeforeEffect, terminalState);
                _ = await runtime.Store.FailActivityAsync(
                    transaction,
                    workCancelStart.ScopeId,
                    workCancelId,
                    PostgreSqlFlowActivityFailureKind.CanceledBeforeEffect,
                    "work_canceled",
                    cancellationToken);
            });
        Assert.Equal(DurableWorkState.CanceledBeforeEffect, workCanceled.State);
        Assert.Equal(
            ("canceled", 4L),
            await ReadFlowAsync(database.DataSource, workCancelStart.ScopeId, workCancelStart.InstanceId));
        Assert.Contains("work_canceled", terminalCodes);
    }

    [Theory]
    [InlineData(false, "suspended_reconciliation_required")]
    [InlineData(true, "reconciling")]
    public async Task ParentCancellation_DuringChildSuspensionOrReconciliation_PreservesIntentAndPreventsRetry(
        bool cancelWhileReconciling,
        string expectedChildStateDuringCancellation)
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var runtimeEpoch = Guid.NewGuid();
        var capture = new FlowCancellationReconciliationCapture(cancelWhileReconciling);
        var callsite = new FlowActivityCallsite<PostgreSqlFlowWork, PostgreSqlFlowResult>("reconcile-work", 1, 1);
        var definition = FlowGraphBuilder<PostgreSqlFlowContext>
            .Create("tests.cancel-reconciling-flow", "v1")
            .AddNode("activity", new PostgreSqlActivityFlowNode(callsite))
            .StartAt("activity")
            .Build();
        var contextCodec = CreateContextCodec();
        var workCodec = new SystemTextJsonDurablePayloadCodec<PostgreSqlFlowWork>(
            "tests.cancel-reconciling-work",
            "v1",
            DurableDataClassification.Operational,
            PostgreSqlFlowJsonContext.Default.PostgreSqlFlowWork,
            _ => true);
        var resultCodec = new SystemTextJsonDurablePayloadCodec<PostgreSqlFlowResult>(
            "tests.cancel-reconciling-result",
            "v1",
            DurableDataClassification.Operational,
            PostgreSqlFlowJsonContext.Default.PostgreSqlFlowResult,
            _ => true);
        var workRegistration = new DurableWorkRegistration<
            PostgreSqlFlowWork,
            PostgreSqlFlowResult,
            PostgreSqlCancelRaceFlowExecutor>(
                "tests.cancel-reconciling-work",
                "v1",
                DurableProviderSafety.ReconcileBeforeRetry,
                workCodec,
                resultCodec,
                provider => provider.GetRequiredService<PostgreSqlCancelRaceFlowReconciler>());
        var binding = new DurableFlowActivityBinding<
            PostgreSqlFlowContext,
            PostgreSqlFlowWork,
            PostgreSqlFlowResult>(callsite, workRegistration, workCodec, resultCodec);
        var services = new ServiceCollection();
        services.AddSingleton(capture);
        services.AddSingleton<IDurablePayloadCodec>(workCodec);
        services.AddSingleton<IDurablePayloadCodec>(resultCodec);
        services.AddSingleton<DurableWorkRegistration>(workRegistration);
        services.AddTransient<PostgreSqlCancelRaceFlowExecutor>();
        services.AddTransient<PostgreSqlCancelRaceFlowReconciler>();
        services.AddDurableFlow(definition, contextCodec, "implementation-v1", [binding]);
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            runtimeEpoch,
            options => options.SendWakeNotifications = false);
        await using var provider = services.BuildServiceProvider();
        var flowClient = provider.GetRequiredService<IDurableFlowClient>();
        var pump = provider.GetRequiredService<IDurableRuntimePump>();
        var scopeId = new DurableScopeId($"scope-parent-cancel-{cancelWhileReconciling}");
        var instanceId = new DurableFlowInstanceId($"instance-parent-cancel-{cancelWhileReconciling}");
        var start = new DurableFlowStartRequest(
            scopeId,
            new DurableCommandId("command-start"),
            "parent-cancel-start",
            instanceId,
            definition.FlowId,
            definition.Version,
            contextCodec.Encode(new PostgreSqlFlowContext(0)));
        Assert.True((await flowClient.StartAsync(start)).IsSuccess);
        await pump.RunOnceAsync(
            new DurableRuntimePumpRequest(10, TimeSpan.FromSeconds(10), DurableRuntimeSurface.Flow));
        var workId = new DurableWorkId(await ReadScopedScalarAsync<string>(
            database.DataSource,
            scopeId,
            """
            SELECT activity_work_id FROM appsurface_durable.flow_wait
            WHERE scope_id = @scope_id AND state = 'active';
            """));
        await pump.RunOnceAsync(
            new DurableRuntimePumpRequest(10, TimeSpan.FromSeconds(10), DurableRuntimeSurface.Work));
        var control = provider.GetRequiredService<IDurableWorkControlClient>();
        var childSuspended = await control.GetAsync(new DurableWorkGetRequest(scopeId, workId));
        var parentSuspended = await flowClient.GetAsync(new DurableFlowGetRequest(scopeId, instanceId));

        Assert.Equal(1, capture.ExecutorCalls);
        Assert.Equal(DurableWorkState.Suspended, childSuspended.Value!.State);
        Assert.Equal(DurableFlowState.Suspended, parentSuspended.Value!.State);

        var operatorClient = provider.GetRequiredService<IDurableWorkOperatorClient>();
        Task<DurableOperationResult<DurableWorkOperatorResult>>? reconciliationTask = null;
        if (cancelWhileReconciling)
        {
            reconciliationTask = operatorClient.ReconcileAsync(new DurableWorkReconcileRequest(
                scopeId,
                workId,
                new DurableCommandId("reconcile-child"),
                "operator-reconcile",
                "provider-check",
                childSuspended.Value.Revision)).AsTask();
            await capture.ReconciliationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        DurableOperationResult<DurableFlowCommandResult> parentCancellation;
        string childStateDuringCancellation;
        try
        {
            parentCancellation = await flowClient.CancelAsync(new DurableFlowCancelRequest(
                scopeId,
                new DurableCommandId("cancel-parent"),
                instanceId,
                "operator-parent",
                "consumer_requested",
                parentSuspended.Value.Revision));
            childStateDuringCancellation = await ReadScopedScalarAsync<string>(
                database.DataSource,
                scopeId,
                """
                SELECT work.state || '|' || (work.cancellation_requested_at IS NOT NULL)::text || '|' || dispatch.state
                FROM appsurface_durable.work AS work
                JOIN appsurface_durable.dispatch AS dispatch
                  ON dispatch.scope_id = work.scope_id
                 AND dispatch.aggregate_kind = 'work'
                 AND dispatch.aggregate_id = work.work_id
                WHERE work.scope_id = @scope_id;
                """);
        }
        finally
        {
            capture.AllowReconciliationCompletion.TrySetResult(true);
        }

        Assert.Equal(DurableFlowState.CancelPending, parentCancellation.Value!.State);
        Assert.Equal($"{expectedChildStateDuringCancellation}|true|suspended", childStateDuringCancellation);
        var childAfterCancellation = await control.GetAsync(new DurableWorkGetRequest(scopeId, workId));
        reconciliationTask ??= operatorClient.ReconcileAsync(new DurableWorkReconcileRequest(
            scopeId,
            workId,
            new DurableCommandId("reconcile-child"),
            "operator-reconcile",
            "provider-check",
            childAfterCancellation.Value!.Revision)).AsTask();
        var reconciled = await reconciliationTask;

        Assert.True(reconciled.IsSuccess);
        Assert.Equal(DurableWorkState.CanceledBeforeEffect, reconciled.Value!.State);
        Assert.Equal(1, capture.ReconciliationCalls);
        Assert.Equal(
            "canceled_before_effect|canceled|canceled|terminal|true",
            await ReadScopedScalarAsync<string>(
                database.DataSource,
                scopeId,
                """
                SELECT work.state || '|' || flow.state || '|' || wait.state || '|' || dispatch.state || '|' ||
                       (work.cancellation_requested_at IS NOT NULL)::text
                FROM appsurface_durable.work AS work
                JOIN appsurface_durable.flow_wait AS wait
                  ON wait.scope_id = work.scope_id AND wait.activity_work_id = work.work_id
                JOIN appsurface_durable.flow_instance AS flow
                  ON flow.scope_id = wait.scope_id AND flow.flow_instance_id = wait.flow_instance_id
                JOIN appsurface_durable.dispatch AS dispatch
                  ON dispatch.scope_id = work.scope_id
                 AND dispatch.aggregate_kind = 'work'
                 AND dispatch.aggregate_id = work.work_id
                WHERE work.scope_id = @scope_id;
                """));
        var after = await pump.RunOnceAsync(
            new DurableRuntimePumpRequest(10, TimeSpan.FromSeconds(10), DurableRuntimeSurface.Work));
        Assert.Equal(0, after.Claimed);
        Assert.Equal(1, capture.ExecutorCalls);
    }

    [Fact]
    public async Task ParentCancellation_AfterEarlierPermitAndBeforeRetryPermit_PreservesAmbiguousChild()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var runtime = CreateActivityRuntime(database.DataSource);
        var workStore = new PostgreSqlDurableWorkStore(database.DataSource, runtime.Epoch);
        var start = CreateStart(
            runtime,
            "scope-parent-cancel-historical-permit",
            "command-start",
            "instance-parent-cancel-historical-permit");
        await runtime.Client.StartAsync(start);
        var waiting = await ProcessFlowAsync(runtime, start.ScopeId);
        var workId = Assert.IsType<DurableWorkId>(waiting.ActivityWorkId);
        var firstCandidate = Assert.Single(
            await workStore.DiscoverAsync(10),
            candidate => candidate.ScopeId == start.ScopeId);
        var firstClaim = Assert.IsType<PostgreSqlDurableWorkClaim>(
            await workStore.TryClaimAsync(firstCandidate, "first-attempt-worker"));
        Assert.Equal(1, firstClaim.AttemptNumber);
        var firstPermit = Assert.IsType<PostgreSqlEffectPermit>(
            await workStore.TryAcquireEffectPermitAsync(firstClaim));
        var retry = await workStore.RecordCompletionAsync(
            firstPermit.Claim,
            new PostgreSqlWorkCompletion(
                PostgreSqlWorkCompletionKind.Retry,
                "provider_retry",
                "{}",
                RetryDelay: TimeSpan.FromMinutes(1)));
        Assert.Equal(DurableWorkState.Ready, retry.State);

        await ForceWorkDueAsync(database.DataSource, start.ScopeId);
        var secondCandidate = Assert.Single(
            await workStore.DiscoverAsync(10),
            candidate => candidate.ScopeId == start.ScopeId);
        var secondClaim = Assert.IsType<PostgreSqlDurableWorkClaim>(
            await workStore.TryClaimAsync(secondCandidate, "second-attempt-worker"));
        Assert.Equal(2, secondClaim.AttemptNumber);

        var canceled = await runtime.Client.CancelAsync(new DurableFlowCancelRequest(
            start.ScopeId,
            new DurableCommandId("cancel-parent"),
            start.InstanceId,
            "operator-parent",
            "consumer_requested",
            waiting.Revision));

        Assert.True(canceled.IsSuccess);
        Assert.Equal(DurableFlowState.CancelPending, canceled.Value!.State);
        Assert.Equal(
            "suspended_ambiguous_external_outcome|cancel_pending|active|suspended|true",
            await ReadScopedScalarAsync<string>(
                database.DataSource,
                start.ScopeId,
                """
                SELECT work.state || '|' || flow.state || '|' || wait.state || '|' || dispatch.state || '|' ||
                       (work.cancellation_requested_at IS NOT NULL)::text
                FROM appsurface_durable.work AS work
                JOIN appsurface_durable.flow_wait AS wait
                  ON wait.scope_id = work.scope_id AND wait.activity_work_id = work.work_id
                JOIN appsurface_durable.flow_instance AS flow
                  ON flow.scope_id = wait.scope_id AND flow.flow_instance_id = wait.flow_instance_id
                JOIN appsurface_durable.dispatch AS dispatch
                  ON dispatch.scope_id = work.scope_id
                 AND dispatch.aggregate_kind = 'work'
                 AND dispatch.aggregate_id = work.work_id
                WHERE work.scope_id = @scope_id;
                """));
        Assert.Equal(
            "granted|1",
            await ReadScopedScalarAsync<string>(
                database.DataSource,
                start.ScopeId,
                """
                SELECT min(status) || '|' || count(*)::text
                FROM appsurface_durable.effect_permit
                WHERE scope_id = @scope_id;
                """));
        Assert.Null(await workStore.TryAcquireEffectPermitAsync(secondClaim));
        Assert.DoesNotContain(
            await workStore.DiscoverAsync(10),
            candidate => candidate.ScopeId == start.ScopeId);
    }

    [Fact]
    public async Task WorkCancellation_AfterHistoricalPermit_ProjectsAppliedProofIntoParentExactlyOnce()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var epoch = Guid.NewGuid();
        var capture = new FlowAppliedReconciliationCapture();
        var runtime = CreateOperatorProjectionRuntime(
            database.DataSource,
            epoch,
            DurableProviderSafety.ProviderKeyed,
            capture);
        await using var provider = runtime.Provider;
        var scope = new DurableScopeId("scope-work-cancel-historical-permit");
        var instance = new DurableFlowInstanceId("instance-work-cancel-historical-permit");
        var start = new DurableFlowStartRequest(
            scope,
            new DurableCommandId("command-start"),
            "start-work-cancel-historical-permit",
            instance,
            runtime.FlowId,
            runtime.FlowVersion,
            runtime.ContextCodec.Encode(new PostgreSqlFlowContext(0)));
        var flowClient = provider.GetRequiredService<IDurableFlowClient>();
        Assert.True((await flowClient.StartAsync(start)).IsSuccess);
        var pump = provider.GetRequiredService<IDurableRuntimePump>();
        await pump.RunOnceAsync(
            new DurableRuntimePumpRequest(10, TimeSpan.FromSeconds(10), DurableRuntimeSurface.Flow));
        var workId = new DurableWorkId(await ReadScopedScalarAsync<string>(
            database.DataSource,
            scope,
            """
            SELECT activity_work_id
            FROM appsurface_durable.flow_wait
            WHERE scope_id = @scope_id AND state = 'active';
            """));
        var workStore = new PostgreSqlDurableWorkStore(database.DataSource, epoch);
        var firstClaim = Assert.IsType<PostgreSqlDurableWorkClaim>(
            await workStore.TryClaimAsync(
                Assert.Single(await workStore.DiscoverAsync(10), item => item.ScopeId == scope),
                "first-attempt-worker"));
        var firstPermit = Assert.IsType<PostgreSqlEffectPermit>(
            await workStore.TryAcquireEffectPermitAsync(firstClaim));
        var retry = await workStore.RecordCompletionAsync(
            firstPermit.Claim,
            new PostgreSqlWorkCompletion(
                PostgreSqlWorkCompletionKind.Retry,
                "provider_retry",
                "{}",
                RetryDelay: TimeSpan.FromMinutes(1)));
        Assert.Equal(DurableWorkState.Ready, retry.State);
        await ForceWorkDueAsync(database.DataSource, scope);
        var secondClaim = Assert.IsType<PostgreSqlDurableWorkClaim>(
            await workStore.TryClaimAsync(
                Assert.Single(await workStore.DiscoverAsync(10), item => item.ScopeId == scope),
                "second-attempt-worker"));
        Assert.Equal(2, secondClaim.AttemptNumber);

        var canceled = await provider.GetRequiredService<IDurableWorkControlClient>().CancelAsync(
            new DurableWorkCancelRequest(
                scope,
                workId,
                "operator-cancel",
                "consumer_requested",
                secondClaim.Revision));
        Assert.True(canceled.IsSuccess);
        Assert.Equal(DurableWorkState.Suspended, canceled.Value!.State);
        var suspendedParent = await flowClient.GetAsync(new DurableFlowGetRequest(scope, instance));
        Assert.Equal(DurableFlowState.Suspended, suspendedParent.Value!.State);

        var applied = await provider.GetRequiredService<IDurableWorkOperatorClient>().ResolveAsync(
            new DurableWorkManualResolutionRequest(
                scope,
                workId,
                new DurableCommandId("operator-applied-proof"),
                "operator-proof",
                "provider-applied",
                canceled.Value.Revision,
                DurableManualResolutionKind.Applied,
                runtime.ResultCodec.Encode(new PostgreSqlFlowResult("operator-confirmed-applied"))));
        Assert.True(applied.IsSuccess);
        Assert.Equal(DurableWorkState.SucceededAfterCancelRequested, applied.Value!.State);
        var projected = await flowClient.GetAsync(new DurableFlowGetRequest(scope, instance));
        Assert.Equal(DurableFlowState.Suspended, projected.Value!.State);

        var released = await flowClient.ReleaseSuspensionAsync(
            CreateRelease(start, "release-applied-proof", projected.Value.Revision));
        Assert.Equal(DurableFlowState.Ready, released.Value!.State);
        await pump.RunOnceAsync(
            new DurableRuntimePumpRequest(10, TimeSpan.FromSeconds(10), DurableRuntimeSurface.Flow));
        Assert.Equal(
            DurableFlowState.Completed,
            (await flowClient.GetAsync(new DurableFlowGetRequest(scope, instance))).Value!.State);
        Assert.Equal(
            1,
            await CountScopedAsync(
                database.DataSource,
                scope,
                """
                SELECT count(*)
                FROM appsurface_durable.flow_history
                WHERE scope_id = @scope_id
                  AND event_type = 'activity_result_projected_while_suspended';
                """));
        Assert.Equal(
            1,
            await CountScopedAsync(
                database.DataSource,
                scope,
                """
                SELECT count(*)
                FROM appsurface_durable.flow_history
                WHERE scope_id = @scope_id AND event_type = 'completed';
                """));
    }

    [Fact]
    public async Task CancelPendingEpochSuspension_ReleasesBackToCancelPendingBeforeChildTruthApplies()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var runtime = CreateActivityRuntime(database.DataSource);
        var workStore = new PostgreSqlDurableWorkStore(database.DataSource, runtime.Epoch);
        var start = CreateStart(
            runtime,
            "scope-cancel-pending-epoch",
            "command-start",
            "instance-cancel-pending-epoch");
        await runtime.Client.StartAsync(start);
        var waiting = await ProcessFlowAsync(runtime, start.ScopeId);
        var workId = Assert.IsType<DurableWorkId>(waiting.ActivityWorkId);
        var workCandidate = Assert.Single(
            await workStore.DiscoverAsync(10),
            item => item.ScopeId == start.ScopeId);
        var claim = Assert.IsType<PostgreSqlDurableWorkClaim>(
            await workStore.TryClaimAsync(workCandidate, "effect-worker"));
        var permit = Assert.IsType<PostgreSqlEffectPermit>(await workStore.TryAcquireEffectPermitAsync(claim));
        var cancelPending = await runtime.Client.CancelAsync(
            new DurableFlowCancelRequest(
                start.ScopeId,
                new DurableCommandId("command-cancel"),
                start.InstanceId,
                "operator-epoch",
                "consumer_requested",
                waiting.Revision));
        Assert.Equal(DurableFlowState.CancelPending, cancelPending.Value!.State);

        var newEpoch = Guid.NewGuid();
        await RotateStoreEpochAsync(database.DataSource, runtime.Epoch, newEpoch);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await runtime.Client.GetAsync(
            new DurableFlowGetRequest(start.ScopeId, start.InstanceId)));
        var rotated = RotateRuntime(database.DataSource, runtime, newEpoch);
        var rotatedWorkStore = new PostgreSqlDurableWorkStore(database.DataSource, newEpoch);
        var fenced = await rotated.Client.CancelAsync(
            new DurableFlowCancelRequest(
                start.ScopeId,
                new DurableCommandId("command-fence"),
                start.InstanceId,
                "operator-epoch",
                "runtime_rotated",
                cancelPending.Value.Revision));
        Assert.Equal(DurableFlowState.Suspended, fenced.Value!.State);
        var suspended = await rotated.Client.GetAsync(new DurableFlowGetRequest(start.ScopeId, start.InstanceId));

        await ForceWorkDueAsync(database.DataSource, start.ScopeId);
        var recoveryProbe = Assert.Single(
            await rotatedWorkStore.DiscoverAsync(10),
            item => item.ScopeId == start.ScopeId);
        Assert.Null(await rotatedWorkStore.TryClaimAsync(recoveryProbe, "recovery-probe"));

        var services = new ServiceCollection();
        services.AddSingleton<IDurablePayloadCodec>(runtime.ContextCodec);
        services.AddSingleton<IDurablePayloadCodec>(runtime.WorkCodec!);
        services.AddSingleton<IDurablePayloadCodec>(runtime.ResultCodec!);
        services.AddSingleton(runtime.WorkRegistration!);
        services.AddSingleton(runtime.Registration);
        services.AddTransient<PostgreSqlFlowExecutor>();
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            newEpoch,
            options => options.SendWakeNotifications = false);
        await using var provider = services.BuildServiceProvider();
        var workControl = provider.GetRequiredService<IDurableWorkControlClient>();
        var recoverySuspended = await workControl.GetAsync(new DurableWorkGetRequest(start.ScopeId, workId));
        Assert.Equal(DurableWorkState.Suspended, recoverySuspended.Value!.State);
        var workOperator = provider.GetRequiredService<IDurableWorkOperatorClient>();
        var workRelease = await workOperator.ReleaseAfterRecoveryAsync(
            new DurableWorkRecoveryReleaseRequest(
                start.ScopeId,
                workId,
                new DurableCommandId("release-work-after-recovery"),
                "operator-epoch",
                "provider-key-replay-safe",
                recoverySuspended.Value.Revision));
        Assert.True(workRelease.IsSuccess);
        Assert.Equal(DurableWorkState.Suspended, workRelease.Value!.State);
        var retryRelease = await workOperator.RetrySafeAsync(new DurableWorkRetrySafeRequest(
            start.ScopeId,
            workId,
            new DurableCommandId("retry-work-after-recovery"),
            "operator-epoch",
            "provider-key-replay-safe",
            workRelease.Value.Revision));
        Assert.True(retryRelease.IsSuccess);
        Assert.Equal(DurableWorkState.Ready, retryRelease.Value!.State);

        var recoveryCandidate = Assert.Single(
            await rotatedWorkStore.DiscoverAsync(10),
            item => item.ScopeId == start.ScopeId);
        var recoveryClaim = Assert.IsType<PostgreSqlDurableWorkClaim>(
            await rotatedWorkStore.TryClaimAsync(recoveryCandidate, "recovery-worker"));
        var recoveryPermit = Assert.IsType<PostgreSqlEffectPermit>(
            await rotatedWorkStore.TryAcquireEffectPermitAsync(recoveryClaim));

        var released = await rotated.Client.ReleaseSuspensionAsync(
            CreateRelease(start, "release-cancel-pending", suspended.Value!.Revision));

        Assert.Equal(DurableFlowState.CancelPending, released.Value!.State);
        var result = rotated.ResultCodec!.Encode(new PostgreSqlFlowResult("effect-applied"));
        PostgreSqlFlowProcessingResult? parent = null;
        await rotatedWorkStore.RecordCompletionAsync(
            recoveryPermit.Claim,
            new PostgreSqlWorkCompletion(
                PostgreSqlWorkCompletionKind.Succeeded,
                "provider_succeeded",
                "{}",
                result),
            async (transaction, _, cancellationToken) =>
            {
                parent = await rotated.Store.ResumeActivityAsync(
                    transaction,
                    start.ScopeId,
                    workId,
                    result,
                    cancellationToken);
            });

        Assert.Equal(DurableFlowState.Canceled, parent!.State);
        Assert.Equal(DurableFlowState.Canceled, (await rotated.Client.GetAsync(
            new DurableFlowGetRequest(start.ScopeId, start.InstanceId))).Value!.State);
    }

    [Fact]
    public async Task CancelPendingAmbiguity_AppliedTruthProjectsCanceledBeforeRelease()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var runtime = CreateActivityRuntime(database.DataSource);
        var workStore = new PostgreSqlDurableWorkStore(database.DataSource, runtime.Epoch);
        var start = CreateStart(
            runtime,
            "scope-cancel-pending-ambiguous",
            "command-start",
            "instance-cancel-pending-ambiguous");
        await runtime.Client.StartAsync(start);
        var waiting = await ProcessFlowAsync(runtime, start.ScopeId);
        var workId = Assert.IsType<DurableWorkId>(waiting.ActivityWorkId);
        var workCandidate = Assert.Single(
            await workStore.DiscoverAsync(10),
            item => item.ScopeId == start.ScopeId);
        var claim = Assert.IsType<PostgreSqlDurableWorkClaim>(
            await workStore.TryClaimAsync(workCandidate, "effect-worker"));
        _ = Assert.IsType<PostgreSqlEffectPermit>(await workStore.TryAcquireEffectPermitAsync(claim));
        var cancelPending = await runtime.Client.CancelAsync(
            new DurableFlowCancelRequest(
                start.ScopeId,
                new DurableCommandId("command-cancel"),
                start.InstanceId,
                "operator-ambiguity",
                "consumer_requested",
                waiting.Revision));
        Assert.Equal(DurableFlowState.CancelPending, cancelPending.Value!.State);

        await using (var connection = await database.DataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var suspended = await runtime.Store.FailActivityAsync(
                transaction,
                start.ScopeId,
                workId,
                PostgreSqlFlowActivityFailureKind.Suspended,
                "provider_truth_ambiguous");
            Assert.Equal(DurableFlowState.Suspended, suspended!.State);
            await transaction.CommitAsync();
        }

        PostgreSqlFlowProcessingResult? projected;
        await using (var connection = await database.DataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            projected = await runtime.Store.ResumeActivityAsync(
                transaction,
                start.ScopeId,
                workId,
                runtime.ResultCodec!.Encode(new PostgreSqlFlowResult("operator-confirmed-applied")));
            Assert.Equal(DurableFlowState.Suspended, projected!.State);
            Assert.Null(await runtime.Store.ResumeActivityAsync(
                transaction,
                start.ScopeId,
                workId,
                runtime.ResultCodec.Encode(new PostgreSqlFlowResult("duplicate"))));
            await transaction.CommitAsync();
        }

        var released = await runtime.Client.ReleaseSuspensionAsync(
            CreateRelease(start, "release-canceled", projected!.Revision));

        Assert.Equal(DurableFlowState.Canceled, released.Value!.State);
        Assert.Equal(
            1,
            await CountScopedAsync(
                database.DataSource,
                start.ScopeId,
                """
                SELECT count(*) FROM appsurface_durable.flow_history
                WHERE scope_id = @scope_id
                  AND event_type = 'activity_succeeded_after_cancel_requested_while_suspended';
                """));
    }

    [Fact]
    public async Task ListAsync_PagesPayloadFreeRecoveryInventoryAndFiltersByStateAndOldEpoch()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var initialEpoch = Guid.NewGuid();
        var definition = CreateWaitDefinition("tests.flow-inventory", timeout: null);
        var initial = CreateRuntime(database.DataSource, definition, initialEpoch);
        const string scope = "scope-flow-inventory";
        var eventStart = CreateStart(initial, scope, "command-start-event", "inventory-a-event");
        await initial.Client.StartAsync(eventStart);
        var eventWaiting = await ProcessFlowAsync(initial, eventStart.ScopeId);

        var completedStart = CreateStart(initial, scope, "command-start-completed", "inventory-b-completed");
        await initial.Client.StartAsync(completedStart);
        await ProcessFlowAsync(initial, completedStart.ScopeId);
        var completedEvent = await initial.Client.RaiseEventAsync(new DurableFlowEventRequest(
            completedStart.ScopeId,
            new DurableCommandId("command-complete-event"),
            new DurableFlowEventId("event-complete"),
            completedStart.InstanceId,
            "approved"));
        Assert.Equal(DurableFlowCommandOutcome.Accepted, completedEvent.Value!.Outcome);
        Assert.Equal(DurableFlowState.Completed, (await ProcessFlowAsync(initial, completedStart.ScopeId)).State);

        var oldReadyStart = CreateStart(initial, scope, "command-start-old-ready", "inventory-c-ready-old");
        await initial.Client.StartAsync(oldReadyStart);
        var activeEpoch = Guid.NewGuid();
        await RotateStoreEpochAsync(database.DataSource, initialEpoch, activeEpoch);
        var active = RotateRuntime(database.DataSource, initial, activeEpoch);
        var currentReadyStart = CreateStart(
            active,
            scope,
            "command-start-current-ready",
            "inventory-d-ready-current");
        await active.Client.StartAsync(currentReadyStart);

        var firstPage = await active.Client.ListAsync(new DurableFlowListRequest(
            eventStart.ScopeId,
            requiresRecoveryRelease: true,
            pageSize: 1));
        var first = Assert.Single(firstPage.Value!.Flows);
        Assert.Equal(eventStart.InstanceId, first.InstanceId);
        Assert.Equal(DurableFlowState.WaitingForEvent, first.State);
        Assert.Equal(eventWaiting.Revision, first.Revision);
        Assert.True(first.RequiresRecoveryRelease);
        Assert.Equal(eventStart.InstanceId.Value, firstPage.Value.ContinuationToken);

        var secondPage = await active.Client.ListAsync(new DurableFlowListRequest(
            eventStart.ScopeId,
            requiresRecoveryRelease: true,
            pageSize: 1,
            continuationToken: firstPage.Value.ContinuationToken));
        var second = Assert.Single(secondPage.Value!.Flows);
        Assert.Equal(oldReadyStart.InstanceId, second.InstanceId);
        Assert.Equal(DurableFlowState.Ready, second.State);
        Assert.True(second.RequiresRecoveryRelease);
        Assert.Null(secondPage.Value.ContinuationToken);

        var eventInventory = await active.Client.ListAsync(new DurableFlowListRequest(
            eventStart.ScopeId,
            DurableFlowState.WaitingForEvent,
            requiresRecoveryRelease: true));
        Assert.Equal(eventStart.InstanceId, Assert.Single(eventInventory.Value!.Flows).InstanceId);
        var readyInventory = await active.Client.ListAsync(new DurableFlowListRequest(
            eventStart.ScopeId,
            DurableFlowState.Ready,
            requiresRecoveryRelease: true));
        Assert.Equal(oldReadyStart.InstanceId, Assert.Single(readyInventory.Value!.Flows).InstanceId);

        var currentInventory = await active.Client.ListAsync(new DurableFlowListRequest(
            eventStart.ScopeId,
            requiresRecoveryRelease: false));
        Assert.Equal(
            [completedStart.InstanceId, currentReadyStart.InstanceId],
            currentInventory.Value!.Flows.Select(static flow => flow.InstanceId).ToArray());
        Assert.All(currentInventory.Value.Flows, static flow => Assert.False(flow.RequiresRecoveryRelease));
        var crossScope = await active.Client.ListAsync(new DurableFlowListRequest(
            new DurableScopeId("scope-flow-inventory-other"),
            requiresRecoveryRelease: true));
        Assert.Empty(crossScope.Value!.Flows);

        var byId = await active.Client.GetAsync(new DurableFlowGetRequest(eventStart.ScopeId, eventStart.InstanceId));
        Assert.True(byId.Value!.RequiresRecoveryRelease);
        var release = await active.Client.ReleaseSuspensionAsync(
            CreateRelease(eventStart, "release-inventory-event", first.Revision));
        Assert.Equal(DurableFlowState.WaitingForEvent, release.Value!.State);
        Assert.False((await active.Client.GetAsync(
            new DurableFlowGetRequest(eventStart.ScopeId, eventStart.InstanceId))).Value!.RequiresRecoveryRelease);
        var afterRelease = await active.Client.ListAsync(new DurableFlowListRequest(
            eventStart.ScopeId,
            requiresRecoveryRelease: true));
        Assert.Equal(oldReadyStart.InstanceId, Assert.Single(afterRelease.Value!.Flows).InstanceId);

        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableFlowListRequest(
            eventStart.ScopeId,
            pageSize: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableFlowListRequest(
            eventStart.ScopeId,
            pageSize: 1_001));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableFlowListRequest(
            eventStart.ScopeId,
            (DurableFlowState)int.MaxValue));
        Assert.Throws<ArgumentException>(() => new DurableFlowListRequest(
            eventStart.ScopeId,
            continuationToken: string.Empty));
        Assert.Throws<ArgumentNullException>(() => new DurableFlowListResult(null!, continuationToken: null));
    }

    [Fact]
    public async Task ReleaseSuspension_DirectlyReFencesOldEpochFutureTimerWithoutChangingDueTime()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var initialEpoch = Guid.NewGuid();
        var definition = CreateWaitDefinition("tests.direct-release-timer", TimeSpan.FromHours(1));
        var runtime = CreateRuntime(database.DataSource, definition, initialEpoch);
        var start = CreateStart(runtime, "scope-direct-timer", "command-start", "instance-direct-timer");
        await runtime.Client.StartAsync(start);
        var waiting = await ProcessFlowAsync(runtime, start.ScopeId);
        var before = await ReadTimerRecoverySnapshotAsync(database.DataSource, start.ScopeId, start.InstanceId);

        Assert.Equal(DurableFlowState.WaitingForTimer, waiting.State);
        Assert.True(before.TimerDueAtUtc > DateTimeOffset.UtcNow);
        Assert.Equal(initialEpoch, before.RuntimeEpoch);

        var activeEpoch = Guid.NewGuid();
        await RotateStoreEpochAsync(database.DataSource, initialEpoch, activeEpoch);
        var rotated = RotateRuntime(database.DataSource, runtime, activeEpoch);
        var stale = await rotated.Client.ReleaseSuspensionAsync(
            CreateRelease(start, "release-stale-revision", waiting.Revision - 1));

        Assert.Equal(DurableFlowCommandOutcome.RaceLost, stale.Value!.Outcome);
        Assert.Equal(waiting.Revision, stale.Value.Revision);
        Assert.Equal(
            initialEpoch,
            await ReadScopedScalarAsync<Guid>(
                database.DataSource,
                start.ScopeId,
                "SELECT runtime_epoch FROM appsurface_durable.flow_instance WHERE scope_id = @scope_id;"));

        var changedRegistration = new DurableFlowRegistration<PostgreSqlFlowContext>(
            definition,
            runtime.ContextCodec,
            "implementation-v2",
            new FlowTransitionEvaluator<PostgreSqlFlowContext>());
        var changed = CreateRuntime(
            database.DataSource,
            changedRegistration,
            runtime.ContextCodec,
            [runtime.ContextCodec],
            runtimeEpoch: activeEpoch);
        var release = CreateRelease(start, "release-direct-timer", waiting.Revision);
        var incompatible = await changed.Client.ReleaseSuspensionAsync(release);

        Assert.False(incompatible.IsSuccess);
        Assert.Equal(DurableProblemCodes.FlowReleaseManifestMismatch, incompatible.Problem!.Code);

        var released = await rotated.Client.ReleaseSuspensionAsync(release);
        var duplicate = await rotated.Client.ReleaseSuspensionAsync(release);
        var after = await ReadTimerRecoverySnapshotAsync(database.DataSource, start.ScopeId, start.InstanceId);

        Assert.Equal(DurableFlowCommandOutcome.Accepted, released.Value!.Outcome);
        Assert.Equal(DurableFlowCommandOutcome.Duplicate, duplicate.Value!.Outcome);
        Assert.Equal(DurableFlowState.WaitingForTimer, released.Value.State);
        Assert.Equal(waiting.Revision + 1, released.Value.Revision);
        Assert.Equal(before.TimerId, after.TimerId);
        Assert.Equal(before.WaitId, after.WaitId);
        Assert.Equal(before.TimerDueAtUtc, after.TimerDueAtUtc);
        Assert.Equal(before.DispatchDueAtUtc, after.DispatchDueAtUtc);
        Assert.Equal(before.WaitRegisteredRevision, after.WaitRegisteredRevision);
        Assert.Equal("active", after.WaitState);
        Assert.Equal("scheduled", after.TimerState);
        Assert.Equal("available", after.DispatchState);
        Assert.Equal(released.Value.Revision, after.TimerExpectedRevision);
        Assert.Equal(released.Value.Revision, after.DispatchExpectedRevision);
        Assert.Equal(activeEpoch, after.RuntimeEpoch);
        Assert.Equal("waiting_timer", after.FlowState);
        Assert.Equal(released.Value.Revision, after.FlowRevision);
        Assert.DoesNotContain(
            await rotated.Store.DiscoverAsync(20),
            item => item.ScopeId == start.ScopeId && item.AggregateKind == "timer");
        Assert.Equal(
            1,
            await CountScopedAsync(
                database.DataSource,
                start.ScopeId,
                """
                SELECT count(*) FROM appsurface_durable.flow_history
                WHERE scope_id = @scope_id AND event_type = 'runtime_epoch_released';
                """));
        Assert.Equal(
            0,
            await CountScopedAsync(
                database.DataSource,
                start.ScopeId,
                """
                SELECT count(*) FROM appsurface_durable.flow_history
                WHERE scope_id = @scope_id AND event_type = 'runtime_epoch_mismatch_suspended';
                """));
    }

    [Fact]
    public async Task ReleaseSuspension_DirectlyReFencesOldEpochIndefiniteEventWaitAndPreservesDelivery()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var initialEpoch = Guid.NewGuid();
        var definition = CreateWaitDefinition("tests.direct-release-event", timeout: null);
        var runtime = CreateRuntime(database.DataSource, definition, initialEpoch);
        var start = CreateStart(runtime, "scope-direct-event", "command-start", "instance-direct-event");
        await runtime.Client.StartAsync(start);
        var waiting = await ProcessFlowAsync(runtime, start.ScopeId);
        var waitId = await ReadScopedScalarAsync<Guid>(
            database.DataSource,
            start.ScopeId,
            "SELECT wait_id FROM appsurface_durable.flow_wait WHERE scope_id = @scope_id AND state = 'active';");
        var waitRegisteredRevision = await ReadScopedScalarAsync<long>(
            database.DataSource,
            start.ScopeId,
            "SELECT registered_revision FROM appsurface_durable.flow_wait WHERE scope_id = @scope_id AND state = 'active';");

        Assert.Equal(DurableFlowState.WaitingForEvent, waiting.State);
        var currentEpochRelease = await runtime.Client.ReleaseSuspensionAsync(
            CreateRelease(start, "release-current-epoch", waiting.Revision));
        Assert.False(currentEpochRelease.IsSuccess);
        Assert.Equal(DurableProblemCodes.FlowReleaseStateMismatch, currentEpochRelease.Problem!.Code);

        var activeEpoch = Guid.NewGuid();
        await RotateStoreEpochAsync(database.DataSource, initialEpoch, activeEpoch);
        var rotated = RotateRuntime(database.DataSource, runtime, activeEpoch);
        var release = CreateRelease(start, "release-direct-event", waiting.Revision);
        await SetOnlyFlowWaitStateAsync(database.DataSource, start.ScopeId, "canceled");
        var invalidShape = await rotated.Client.ReleaseSuspensionAsync(release);

        Assert.False(invalidShape.IsSuccess);
        Assert.Equal(DurableProblemCodes.FlowReleaseStateMismatch, invalidShape.Problem!.Code);
        Assert.Equal(
            0,
            await CountScopedAsync(
                database.DataSource,
                start.ScopeId,
                """
                SELECT count(*) FROM appsurface_durable.flow_command
                WHERE scope_id = @scope_id AND command_type = 'release';
                """));

        await SetOnlyFlowWaitStateAsync(database.DataSource, start.ScopeId, "active");
        var released = await rotated.Client.ReleaseSuspensionAsync(release);
        var duplicate = await rotated.Client.ReleaseSuspensionAsync(release);

        Assert.Equal(DurableFlowCommandOutcome.Accepted, released.Value!.Outcome);
        Assert.Equal(DurableFlowCommandOutcome.Duplicate, duplicate.Value!.Outcome);
        Assert.Equal(DurableFlowState.WaitingForEvent, released.Value.State);
        Assert.Equal(waiting.Revision + 1, released.Value.Revision);
        Assert.Equal(
            $"{waitId}|{waitRegisteredRevision}|active|0|{activeEpoch}",
            await ReadScopedScalarAsync<string>(
                database.DataSource,
                start.ScopeId,
                """
                SELECT wait.wait_id::text || '|' || wait.registered_revision || '|' || wait.state || '|' ||
                       count(timer.timer_id) || '|' || flow.runtime_epoch::text
                FROM appsurface_durable.flow_instance AS flow
                JOIN appsurface_durable.flow_wait AS wait
                  ON wait.scope_id = flow.scope_id AND wait.flow_instance_id = flow.flow_instance_id
                LEFT JOIN appsurface_durable.flow_timer AS timer
                  ON timer.scope_id = wait.scope_id AND timer.wait_id = wait.wait_id
                WHERE flow.scope_id = @scope_id
                GROUP BY wait.wait_id, wait.registered_revision, wait.state, flow.runtime_epoch;
                """));
        Assert.Equal(
            "operator-recovery|runtime_compatibility_verified|accepted",
            await ReadScopedScalarAsync<string>(
                database.DataSource,
                start.ScopeId,
                """
                SELECT actor_id || '|' || reason_code || '|' || outcome
                FROM appsurface_durable.flow_command
                WHERE scope_id = @scope_id AND command_id = 'release-direct-event';
                """));

        var delivered = await rotated.Client.RaiseEventAsync(
            new DurableFlowEventRequest(
                start.ScopeId,
                new DurableCommandId("command-event-after-direct-release"),
                new DurableFlowEventId("event-after-direct-release"),
                start.InstanceId,
                "approved"));
        Assert.Equal(DurableFlowCommandOutcome.Accepted, delivered.Value!.Outcome);
        var completed = await ProcessFlowAsync(rotated, start.ScopeId);
        Assert.Equal(DurableFlowState.Completed, completed.State);

        var terminalRelease = await rotated.Client.ReleaseSuspensionAsync(
            CreateRelease(start, "release-terminal", completed.Revision));
        Assert.Equal(DurableFlowCommandOutcome.AlreadyTerminal, terminalRelease.Value!.Outcome);
        Assert.Equal(DurableFlowState.Completed, terminalRelease.Value.State);
        Assert.Equal(completed.Revision, terminalRelease.Value.Revision);
    }

    [Fact]
    public async Task RotatedRuntimeEpoch_SuspendsEventTimerCancelAndActivityMutations()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);

        var initialEpoch = Guid.NewGuid();
        var eventRuntime = CreateRuntime(
            database.DataSource,
            CreateWaitDefinition("tests.epoch-event", timeout: null),
            initialEpoch);
        var eventStart = CreateStart(eventRuntime, "scope-epoch-event", "command-start", "instance-epoch-event");
        await eventRuntime.Client.StartAsync(eventStart);
        await ProcessFlowAsync(eventRuntime, eventStart.ScopeId);
        var eventRequest = new DurableFlowEventRequest(
            eventStart.ScopeId,
            new DurableCommandId("command-event"),
            new DurableFlowEventId("event-epoch"),
            eventStart.InstanceId,
            "approved");

        var timerRuntime = CreateRuntime(
            database.DataSource,
            CreateWaitDefinition("tests.epoch-timer", TimeSpan.FromHours(1)),
            initialEpoch);
        var timerStart = CreateStart(timerRuntime, "scope-epoch-timer", "command-start", "instance-epoch-timer");
        await timerRuntime.Client.StartAsync(timerStart);
        await ProcessFlowAsync(timerRuntime, timerStart.ScopeId);
        await ForceTimerDueAsync(database.DataSource, timerStart.ScopeId);

        var activityRuntime = CreateActivityRuntime(database.DataSource, runtimeEpoch: initialEpoch);
        var cancelStart = CreateStart(
            activityRuntime,
            "scope-epoch-cancel",
            "command-start",
            "instance-epoch-cancel");
        var resumeStart = CreateStart(
            activityRuntime,
            "scope-epoch-resume",
            "command-start",
            "instance-epoch-resume");
        await activityRuntime.Client.StartAsync(cancelStart);
        await activityRuntime.Client.StartAsync(resumeStart);
        var cancelWaiting = await ProcessFlowAsync(activityRuntime, cancelStart.ScopeId);
        var resumeWaiting = await ProcessFlowAsync(activityRuntime, resumeStart.ScopeId);

        var activeEpoch = Guid.NewGuid();
        await RotateStoreEpochAsync(database.DataSource, initialEpoch, activeEpoch);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await eventRuntime.Client.RaiseEventAsync(eventRequest));

        var rotatedEvent = RotateRuntime(database.DataSource, eventRuntime, activeEpoch);
        var rotatedTimer = RotateRuntime(database.DataSource, timerRuntime, activeEpoch);
        var rotatedActivity = RotateRuntime(database.DataSource, activityRuntime, activeEpoch);
        var eventResult = await rotatedEvent.Client.RaiseEventAsync(eventRequest);
        Assert.Equal(DurableFlowCommandOutcome.RaceLost, eventResult.Value!.Outcome);
        Assert.Equal(DurableFlowState.Suspended, eventResult.Value.State);

        var timerCandidate = Assert.Single(
            await rotatedTimer.Store.DiscoverAsync(20),
            item => item.ScopeId == timerStart.ScopeId && item.AggregateKind == "timer");
        var timerResult = await rotatedTimer.Store.TryProcessAsync(
            timerCandidate,
            "rotated-timer-worker",
            rotatedTimer.Registry,
            rotatedTimer.Codecs);
        Assert.Equal(PostgreSqlFlowProcessingOutcome.Stale, timerResult.Outcome);
        Assert.Equal(DurableFlowState.Suspended, timerResult.State);

        var cancelResult = await rotatedActivity.Client.CancelAsync(
            new DurableFlowCancelRequest(
                cancelStart.ScopeId,
                new DurableCommandId("command-cancel"),
                cancelStart.InstanceId,
                "operator-epoch",
                "consumer_requested",
                cancelWaiting.Revision));
        Assert.Equal(DurableFlowCommandOutcome.RaceLost, cancelResult.Value!.Outcome);
        Assert.Equal(DurableFlowState.Suspended, cancelResult.Value.State);

        var resumeWorkId = Assert.IsType<DurableWorkId>(resumeWaiting.ActivityWorkId);
        PostgreSqlFlowProcessingResult? resumeFenceResult;
        await using (var connection = await database.DataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            resumeFenceResult = await rotatedActivity.Store.ResumeActivityAsync(
                transaction,
                resumeStart.ScopeId,
                resumeWorkId,
                rotatedActivity.ResultCodec!.Encode(new PostgreSqlFlowResult("stale-result")));
            Assert.Equal(PostgreSqlFlowProcessingOutcome.Applied, resumeFenceResult!.Outcome);
            Assert.Equal(DurableFlowState.Suspended, resumeFenceResult.State);
            await transaction.CommitAsync();
        }

        foreach (var start in new[] { eventStart, timerStart, cancelStart, resumeStart })
        {
            Assert.Equal(
                "suspended",
                (await ReadFlowAsync(database.DataSource, start.ScopeId, start.InstanceId)).State);
            Assert.Equal(
                1,
                await CountScopedAsync(
                    database.DataSource,
                    start.ScopeId,
                    """
                    SELECT count(*) FROM appsurface_durable.flow_history
                    WHERE scope_id = @scope_id AND event_type = 'runtime_epoch_mismatch_suspended';
                    """));
            Assert.Equal(
                0,
                await CountScopedAsync(
                    database.DataSource,
                    start.ScopeId,
                    """
                    SELECT count(*) FROM appsurface_durable.dispatch
                    WHERE scope_id = @scope_id
                      AND aggregate_kind IN ('flow', 'timer')
                      AND state <> 'suspended';
                    """));
        }

        var eventRelease = await rotatedEvent.Client.ReleaseSuspensionAsync(
            CreateRelease(eventStart, "release-event", eventResult.Value.Revision));
        Assert.Equal(DurableFlowState.WaitingForEvent, eventRelease.Value!.State);
        Assert.Equal(DurableFlowCommandOutcome.Accepted, (await rotatedEvent.Client.RaiseEventAsync(eventRequest)).Value!.Outcome);
        Assert.Equal(DurableFlowState.Completed, (await ProcessFlowAsync(rotatedEvent, eventStart.ScopeId)).State);

        var timerRelease = await rotatedTimer.Client.ReleaseSuspensionAsync(
            CreateRelease(timerStart, "release-timer", timerResult.Revision));
        Assert.Equal(DurableFlowState.WaitingForTimer, timerRelease.Value!.State);
        var restoredTimerCandidate = Assert.Single(
            await rotatedTimer.Store.DiscoverAsync(20),
            item => item.ScopeId == timerStart.ScopeId && item.AggregateKind == "timer");
        Assert.Equal(
            PostgreSqlFlowProcessingOutcome.Applied,
            (await rotatedTimer.Store.TryProcessAsync(
                restoredTimerCandidate,
                "restored-timer-worker",
                rotatedTimer.Registry,
                rotatedTimer.Codecs)).Outcome);
        Assert.Equal(DurableFlowState.Completed, (await ProcessFlowAsync(rotatedTimer, timerStart.ScopeId)).State);

        var cancelRelease = await rotatedActivity.Client.ReleaseSuspensionAsync(
            CreateRelease(cancelStart, "release-cancel", cancelResult.Value.Revision));
        Assert.Equal(DurableFlowState.WaitingForActivity, cancelRelease.Value!.State);
        var acceptedCancel = await rotatedActivity.Client.CancelAsync(
            new DurableFlowCancelRequest(
                cancelStart.ScopeId,
                new DurableCommandId("command-cancel-after-release"),
                cancelStart.InstanceId,
                "operator-epoch",
                "consumer_requested",
                cancelRelease.Value.Revision));
        Assert.Equal(DurableFlowState.Canceled, acceptedCancel.Value!.State);

        var resumeRelease = await rotatedActivity.Client.ReleaseSuspensionAsync(
            CreateRelease(resumeStart, "release-resume", resumeFenceResult!.Revision));
        Assert.Equal(DurableFlowState.Ready, resumeRelease.Value!.State);

        Assert.Equal(DurableFlowState.Completed, (await ProcessFlowAsync(rotatedActivity, resumeStart.ScopeId)).State);
    }

    [Fact]
    public async Task AmbiguousActivityResolution_ProjectsResultWhileSuspendedThenReleasesExactlyOnce()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var runtime = CreateActivityRuntime(database.DataSource);
        var start = CreateStart(runtime, "scope-activity-recovery", "command-start", "instance-activity-recovery");
        await runtime.Client.StartAsync(start);
        var waiting = await ProcessFlowAsync(runtime, start.ScopeId);
        var workId = Assert.IsType<DurableWorkId>(waiting.ActivityWorkId);

        await using (var connection = await database.DataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var suspended = await runtime.Store.FailActivityAsync(
                transaction,
                start.ScopeId,
                workId,
                PostgreSqlFlowActivityFailureKind.Suspended,
                "provider_truth_ambiguous");
            Assert.Equal(DurableFlowState.Suspended, suspended!.State);
            await transaction.CommitAsync();
        }

        var snapshot = await runtime.Client.GetAsync(new DurableFlowGetRequest(start.ScopeId, start.InstanceId));
        Assert.Equal(DurableFlowState.Suspended, snapshot.Value!.State);
        Assert.Equal(runtime.FlowId, snapshot.Value.FlowId);
        Assert.Equal(runtime.FlowVersion, snapshot.Value.FlowVersion);
        Assert.Equal("activity", snapshot.Value.CurrentNodeId);
        Assert.Equal("provider_truth_ambiguous", snapshot.Value.TerminalCode);
        Assert.True(snapshot.Value.UpdatedAtUtc >= snapshot.Value.CreatedAtUtc);
        Assert.False((await runtime.Client.GetAsync(
            new DurableFlowGetRequest(new DurableScopeId("scope-other"), start.InstanceId))).IsSuccess);

        PostgreSqlFlowProcessingResult? projected;
        await using (var connection = await database.DataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            projected = await runtime.Store.ResumeActivityAsync(
                transaction,
                start.ScopeId,
                workId,
                runtime.ResultCodec!.Encode(new PostgreSqlFlowResult("operator-confirmed-applied")));
            Assert.Equal(PostgreSqlFlowProcessingOutcome.Applied, projected!.Outcome);
            Assert.Equal(DurableFlowState.Suspended, projected.State);
            Assert.Null(await runtime.Store.ResumeActivityAsync(
                transaction,
                start.ScopeId,
                workId,
                runtime.ResultCodec.Encode(new PostgreSqlFlowResult("duplicate-applied"))));
            await transaction.CommitAsync();
        }

        var releaseRequest = CreateRelease(start, "release-activity-recovery", projected!.Revision);
        var released = await runtime.Client.ReleaseSuspensionAsync(releaseRequest);
        var duplicateRelease = await runtime.Client.ReleaseSuspensionAsync(releaseRequest);

        Assert.Equal(DurableFlowState.Ready, released.Value!.State);
        Assert.Equal(DurableFlowCommandOutcome.Duplicate, duplicateRelease.Value!.Outcome);
        Assert.Equal(DurableFlowState.Completed, (await ProcessFlowAsync(runtime, start.ScopeId)).State);
        Assert.Equal(
            1,
            await CountScopedAsync(
                database.DataSource,
                start.ScopeId,
                """
                SELECT count(*) FROM appsurface_durable.flow_history
                WHERE scope_id = @scope_id
                  AND event_type = 'activity_result_projected_while_suspended';
                """));
        Assert.Equal(
            1,
            await CountScopedAsync(
                database.DataSource,
                start.ScopeId,
                """
                SELECT count(*) FROM appsurface_durable.flow_history
                WHERE scope_id = @scope_id AND event_type = 'completed';
                """));
    }

    [Theory]
    [InlineData(DurableProviderSafety.ReconcileBeforeRetry)]
    [InlineData(DurableProviderSafety.ManualResolution)]
    public async Task OperatorAppliedTruth_AfterEpochRotationProjectsIntoFlowBeforeRelease(
        DurableProviderSafety providerSafety)
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var oldEpoch = Guid.NewGuid();
        var newEpoch = Guid.NewGuid();
        var capture = new FlowAppliedReconciliationCapture();
        var oldRuntime = CreateOperatorProjectionRuntime(
            database.DataSource,
            oldEpoch,
            providerSafety,
            capture);
        await using var oldProvider = oldRuntime.Provider;
        var scope = new DurableScopeId($"scope-operator-epoch-{providerSafety}");
        var instance = new DurableFlowInstanceId($"instance-operator-epoch-{providerSafety}");
        var start = new DurableFlowStartRequest(
            scope,
            new DurableCommandId("command-start"),
            $"start-operator-epoch-{providerSafety}",
            instance,
            oldRuntime.FlowId,
            oldRuntime.FlowVersion,
            oldRuntime.ContextCodec.Encode(new PostgreSqlFlowContext(0)));
        var oldFlowClient = oldProvider.GetRequiredService<IDurableFlowClient>();
        Assert.True((await oldFlowClient.StartAsync(start)).IsSuccess);
        await oldProvider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(10, TimeSpan.FromSeconds(10), DurableRuntimeSurface.Flow));
        var workId = new DurableWorkId(await ReadScopedScalarAsync<string>(
            database.DataSource,
            scope,
            """
            SELECT activity_work_id
            FROM appsurface_durable.flow_wait
            WHERE scope_id = @scope_id AND state = 'active';
            """));
        var oldWorkStore = new PostgreSqlDurableWorkStore(database.DataSource, oldEpoch);
        var candidate = Assert.Single(
            await oldWorkStore.DiscoverAsync(10),
            item => item.ScopeId == scope);
        var claim = Assert.IsType<PostgreSqlDurableWorkClaim>(
            await oldWorkStore.TryClaimAsync(candidate, "old-effect-worker"));
        Assert.NotNull(await oldWorkStore.TryAcquireEffectPermitAsync(claim));
        var ambiguous = await oldWorkStore.RecordCompletionAsync(
            claim,
            new PostgreSqlWorkCompletion(
                PostgreSqlWorkCompletionKind.AmbiguousExternalOutcome,
                "provider_truth_ambiguous",
                "{}"));
        Assert.Equal(DurableWorkState.Suspended, ambiguous.State);
        Assert.Equal(
            "waiting_activity",
            (await ReadFlowAsync(database.DataSource, scope, instance)).State);

        await RotateStoreEpochAsync(database.DataSource, oldEpoch, newEpoch);
        var newRuntime = CreateOperatorProjectionRuntime(
            database.DataSource,
            newEpoch,
            providerSafety,
            capture);
        await using var newProvider = newRuntime.Provider;
        var operatorClient = newProvider.GetRequiredService<IDurableWorkOperatorClient>();
        DurableOperationResult<DurableWorkOperatorResult> operated;
        if (providerSafety == DurableProviderSafety.ReconcileBeforeRetry)
        {
            operated = await operatorClient.ReconcileAsync(new DurableWorkReconcileRequest(
                scope,
                workId,
                new DurableCommandId("operator-reconcile"),
                "operator-recovery",
                "provider-read",
                ambiguous.Revision));
        }
        else
        {
            operated = await operatorClient.ResolveAsync(new DurableWorkManualResolutionRequest(
                scope,
                workId,
                new DurableCommandId("operator-resolve"),
                "operator-recovery",
                "provider-proof",
                ambiguous.Revision,
                DurableManualResolutionKind.Applied,
                newRuntime.ResultCodec.Encode(new PostgreSqlFlowResult("operator-confirmed-applied"))));
        }

        Assert.True(operated.IsSuccess);
        Assert.Equal(DurableWorkState.Succeeded, operated.Value!.State);
        Assert.Equal(
            providerSafety == DurableProviderSafety.ReconcileBeforeRetry ? 1 : 0,
            capture.Calls);
        var newFlowClient = newProvider.GetRequiredService<IDurableFlowClient>();
        var projected = await newFlowClient.GetAsync(new DurableFlowGetRequest(scope, instance));
        Assert.Equal(DurableFlowState.Suspended, projected.Value!.State);
        Assert.True(projected.Value.RequiresRecoveryRelease);
        Assert.Equal(
            1,
            await CountScopedAsync(
                database.DataSource,
                scope,
                """
                SELECT count(*)
                FROM appsurface_durable.flow_history
                WHERE scope_id = @scope_id
                  AND event_type = 'activity_result_projected_while_suspended';
                """));

        var released = await newFlowClient.ReleaseSuspensionAsync(
            CreateRelease(start, "release-projected-result", projected.Value.Revision));
        Assert.Equal(DurableFlowState.Ready, released.Value!.State);
        await newProvider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(10, TimeSpan.FromSeconds(10), DurableRuntimeSurface.Flow));
        Assert.Equal(
            DurableFlowState.Completed,
            (await newFlowClient.GetAsync(new DurableFlowGetRequest(scope, instance))).Value!.State);
    }

    [Fact]
    public async Task CancelBeforeEffect_AfterEpochRotationTerminalizesFlowBeforeRelease()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var oldEpoch = Guid.NewGuid();
        var newEpoch = Guid.NewGuid();
        var capture = new FlowAppliedReconciliationCapture();
        var oldRuntime = CreateOperatorProjectionRuntime(
            database.DataSource,
            oldEpoch,
            DurableProviderSafety.ProviderKeyed,
            capture);
        await using var oldProvider = oldRuntime.Provider;
        var scope = new DurableScopeId("scope-cancel-before-effect-epoch");
        var instance = new DurableFlowInstanceId("instance-cancel-before-effect-epoch");
        var start = new DurableFlowStartRequest(
            scope,
            new DurableCommandId("command-start"),
            "start-cancel-before-effect-epoch",
            instance,
            oldRuntime.FlowId,
            oldRuntime.FlowVersion,
            oldRuntime.ContextCodec.Encode(new PostgreSqlFlowContext(0)));
        Assert.True((await oldProvider.GetRequiredService<IDurableFlowClient>().StartAsync(start)).IsSuccess);
        await oldProvider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(10, TimeSpan.FromSeconds(10), DurableRuntimeSurface.Flow));
        var workId = new DurableWorkId(await ReadScopedScalarAsync<string>(
            database.DataSource,
            scope,
            """
            SELECT activity_work_id
            FROM appsurface_durable.flow_wait
            WHERE scope_id = @scope_id AND state = 'active';
            """));
        var workBefore = await oldProvider.GetRequiredService<IDurableWorkControlClient>().GetAsync(
            new DurableWorkGetRequest(scope, workId));

        await RotateStoreEpochAsync(database.DataSource, oldEpoch, newEpoch);
        var newRuntime = CreateOperatorProjectionRuntime(
            database.DataSource,
            newEpoch,
            DurableProviderSafety.ProviderKeyed,
            capture);
        await using var newProvider = newRuntime.Provider;
        var canceled = await newProvider.GetRequiredService<IDurableWorkControlClient>().CancelAsync(
            new DurableWorkCancelRequest(
                scope,
                workId,
                "operator-recovery",
                "consumer_requested",
                workBefore.Value!.Revision));
        Assert.True(canceled.IsSuccess);
        Assert.Equal(DurableWorkState.CanceledBeforeEffect, canceled.Value!.State);
        var flowClient = newProvider.GetRequiredService<IDurableFlowClient>();
        var flow = await flowClient.GetAsync(new DurableFlowGetRequest(scope, instance));
        Assert.Equal(DurableFlowState.Canceled, flow.Value!.State);
        Assert.False(flow.Value.RequiresRecoveryRelease);

        var release = await flowClient.ReleaseSuspensionAsync(
            CreateRelease(start, "release-terminal-flow", flow.Value.Revision));
        Assert.True(release.IsSuccess);
        Assert.Equal(DurableFlowCommandOutcome.AlreadyTerminal, release.Value!.Outcome);
        Assert.Equal(DurableFlowState.Canceled, release.Value.State);
        Assert.Equal(
            1,
            await CountScopedAsync(
                database.DataSource,
                scope,
                """
                SELECT count(*)
                FROM appsurface_durable.flow_history
                WHERE scope_id = @scope_id AND event_type = 'activity_canceled_before_effect';
                """));
    }

    [Fact]
    public async Task InvalidDurableFaultCode_SuspendsBeforePersistenceAndRemainsQueryable()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var definition = FlowGraphBuilder<PostgreSqlFlowContext>
            .Create("tests.invalid-fault", "v1")
            .AddNode("fault", new PostgreSqlInvalidFaultCodeNode())
            .StartAt("fault")
            .Build();
        var runtime = CreateRuntime(database.DataSource, definition);
        var start = CreateStart(runtime, "scope-invalid-fault", "command-start", "instance-invalid-fault");
        await runtime.Client.StartAsync(start);

        var processed = await ProcessFlowAsync(runtime, start.ScopeId);
        var snapshot = await runtime.Client.GetAsync(new DurableFlowGetRequest(start.ScopeId, start.InstanceId));

        Assert.Equal(PostgreSqlFlowProcessingOutcome.Failed, processed.Outcome);
        Assert.Equal(DurableFlowState.Suspended, snapshot.Value!.State);
        Assert.Equal("flow.evaluation_failed", snapshot.Value.TerminalCode);
        Assert.Equal("fault", snapshot.Value.CurrentNodeId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FatalEvaluatorFailure_PropagatesWithoutConvertingToRecoverableSuspension(bool outOfMemory)
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var definition = CreateWaitDefinition("tests.fatal-evaluator", timeout: null);
        var contextCodec = CreateContextCodec();
        var registration = new DurableFlowRegistration<PostgreSqlFlowContext>(
            definition,
            contextCodec,
            "implementation-v1",
            new FatalFlowEvaluator(outOfMemory));
        var runtime = CreateRuntime(
            database.DataSource,
            registration,
            contextCodec,
            [contextCodec]);
        var start = CreateStart(
            runtime,
            $"scope-fatal-evaluator-{outOfMemory}",
            "command-start",
            $"instance-fatal-evaluator-{outOfMemory}");
        await runtime.Client.StartAsync(start);

        if (outOfMemory)
        {
            await Assert.ThrowsAsync<OutOfMemoryException>(async () =>
                await ProcessFlowAsync(runtime, start.ScopeId));
        }
        else
        {
            await Assert.ThrowsAsync<StackOverflowException>(async () =>
                await ProcessFlowAsync(runtime, start.ScopeId));
        }

        Assert.Equal(
            0,
            await CountScopedAsync(
                database.DataSource,
                start.ScopeId,
                """
                SELECT count(*)
                FROM appsurface_durable.flow_history
                WHERE scope_id = @scope_id AND event_type = 'evaluation_suspended';
                """));
    }

    [Fact]
    public async Task ChangedImplementationManifest_SuspendsAndOnlyCompatibleRegistrationCanRelease()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var epoch = Guid.NewGuid();
        var definition = FlowGraphBuilder<PostgreSqlFlowContext>
            .Create("tests.manifest-fence", "v1")
            .AddNode("done", new CountingCompleteFlowNode(new FlowNodeTracker()))
            .StartAt("done")
            .Build();
        var codec = CreateContextCodec();
        var compatibleRegistration = new DurableFlowRegistration<PostgreSqlFlowContext>(
            definition,
            codec,
            "implementation-v1",
            new FlowTransitionEvaluator<PostgreSqlFlowContext>());
        var changedRegistration = new DurableFlowRegistration<PostgreSqlFlowContext>(
            definition,
            codec,
            "implementation-v2",
            new FlowTransitionEvaluator<PostgreSqlFlowContext>());
        var compatible = CreateRuntime(
            database.DataSource,
            compatibleRegistration,
            codec,
            [codec],
            runtimeEpoch: epoch);
        var changed = CreateRuntime(
            database.DataSource,
            changedRegistration,
            codec,
            [codec],
            runtimeEpoch: epoch);
        var start = CreateStart(compatible, "scope-manifest-fence", "command-start", "instance-manifest-fence");
        await compatible.Client.StartAsync(start);

        var candidate = Assert.Single(
            await changed.Store.DiscoverAsync(20),
            item => item.ScopeId == start.ScopeId && item.AggregateKind == "flow");
        var processed = await changed.Store.TryProcessAsync(
            candidate,
            "changed-manifest-worker",
            changed.Registry,
            changed.Codecs);
        var snapshot = await compatible.Client.GetAsync(new DurableFlowGetRequest(start.ScopeId, start.InstanceId));

        Assert.Equal(PostgreSqlFlowProcessingOutcome.Failed, processed.Outcome);
        Assert.Equal(DurableFlowState.Suspended, snapshot.Value!.State);
        var incompatibleRelease = await changed.Client.ReleaseSuspensionAsync(
            CreateRelease(start, "release-incompatible", snapshot.Value.Revision));
        Assert.False(incompatibleRelease.IsSuccess);
        Assert.Equal(DurableProblemCodes.FlowReleaseManifestMismatch, incompatibleRelease.Problem!.Code);

        var compatibleRelease = await compatible.Client.ReleaseSuspensionAsync(
            CreateRelease(start, "release-compatible", snapshot.Value.Revision));
        Assert.Equal(DurableFlowState.Ready, compatibleRelease.Value!.State);
        Assert.Equal(DurableFlowState.Completed, (await ProcessFlowAsync(compatible, start.ScopeId)).State);
    }

    [Fact]
    public async Task RuntimeRoleRls_HidesEveryFlowPayloadTableWithoutScopeContext()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var role = $"durable_flow_runtime_{Guid.NewGuid():N}";
        const string password = "appsurface-flow-role-test-password";
        await CreateFlowRuntimeRoleAsync(database.DataSource, role, password);
        var builder = new NpgsqlConnectionStringBuilder(database.DataSource.ConnectionString)
        {
            Username = role,
            Password = password,
        };
        await using var runtimeDataSource = NpgsqlDataSource.Create(builder.ConnectionString);
        var definition = CreateWaitDefinition("tests.rls-flow", timeout: null);
        var runtime = CreateRuntime(runtimeDataSource, definition);
        var scopeA = new DurableScopeId("scope-flow-rls-a");
        var scopeB = new DurableScopeId("scope-flow-rls-b");
        await runtime.Client.StartAsync(CreateStart(runtime, scopeA.Value, "command-a", "instance-a"));
        await runtime.Client.StartAsync(CreateStart(runtime, scopeB.Value, "command-b", "instance-b"));

        foreach (var table in new[] { "flow_instance", "flow_command", "flow_history", "flow_wait", "flow_timer" })
        {
            await using var command = runtimeDataSource.CreateCommand(
                $"SELECT count(*) FROM appsurface_durable.{table};");
            Assert.Equal(0, (long)(await command.ExecuteScalarAsync())!);
        }

        Assert.Equal(
            1,
            await CountScopedAsync(
                runtimeDataSource,
                scopeA,
                "SELECT count(*) FROM appsurface_durable.flow_instance WHERE scope_id = @scope_id;"));
        var crossScope = await runtime.Client.RaiseEventAsync(
            new DurableFlowEventRequest(
                scopeA,
                new DurableCommandId("cross-scope-command"),
                new DurableFlowEventId("cross-scope-event"),
                new DurableFlowInstanceId("instance-b"),
                "approved"));
        Assert.False(crossScope.IsSuccess);
        Assert.Equal(DurableProblemCodes.FlowNotFound, crossScope.Problem!.Code);
    }

    private static DurableFlowRuntime CreateRuntime(
        NpgsqlDataSource dataSource,
        FlowDefinition<PostgreSqlFlowContext> definition,
        Guid? runtimeEpoch = null)
    {
        var contextCodec = CreateContextCodec();
        var registration = new DurableFlowRegistration<PostgreSqlFlowContext>(
            definition,
            contextCodec,
            "implementation-v1",
            new FlowTransitionEvaluator<PostgreSqlFlowContext>());
        return CreateRuntime(dataSource, registration, contextCodec, [contextCodec], runtimeEpoch: runtimeEpoch);
    }

    private static OperatorProjectionRuntime CreateOperatorProjectionRuntime(
        NpgsqlDataSource dataSource,
        Guid runtimeEpoch,
        DurableProviderSafety providerSafety,
        FlowAppliedReconciliationCapture capture)
    {
        var callsite = new FlowActivityCallsite<PostgreSqlFlowWork, PostgreSqlFlowResult>("operator-work", 1, 1);
        var definition = FlowGraphBuilder<PostgreSqlFlowContext>
            .Create("tests.operator-projection-flow", "v1")
            .AddNode("activity", new PostgreSqlActivityFlowNode(callsite))
            .StartAt("activity")
            .Build();
        var contextCodec = CreateContextCodec();
        var workCodec = new SystemTextJsonDurablePayloadCodec<PostgreSqlFlowWork>(
            "tests.operator-projection-work",
            "v1",
            DurableDataClassification.Operational,
            PostgreSqlFlowJsonContext.Default.PostgreSqlFlowWork,
            _ => true);
        var resultCodec = new SystemTextJsonDurablePayloadCodec<PostgreSqlFlowResult>(
            "tests.operator-projection-result",
            "v1",
            DurableDataClassification.Operational,
            PostgreSqlFlowJsonContext.Default.PostgreSqlFlowResult,
            _ => true);
        var workRegistration = new DurableWorkRegistration<
            PostgreSqlFlowWork,
            PostgreSqlFlowResult,
            PostgreSqlFlowExecutor>(
                "tests.operator-projection-work",
                "v1",
                providerSafety,
                workCodec,
                resultCodec,
                providerSafety == DurableProviderSafety.ReconcileBeforeRetry
                    ? provider => provider.GetRequiredService<FlowAppliedReconciler>()
                    : null);
        var binding = new DurableFlowActivityBinding<
            PostgreSqlFlowContext,
            PostgreSqlFlowWork,
            PostgreSqlFlowResult>(callsite, workRegistration, workCodec, resultCodec);
        var services = new ServiceCollection();
        services.AddSingleton(capture);
        services.AddSingleton<IDurablePayloadCodec>(workCodec);
        services.AddSingleton<IDurablePayloadCodec>(resultCodec);
        services.AddSingleton<DurableWorkRegistration>(workRegistration);
        services.AddTransient<PostgreSqlFlowExecutor>();
        services.AddTransient<FlowAppliedReconciler>();
        services.AddDurableFlow(definition, contextCodec, "implementation-v1", [binding]);
        services.AddAppSurfaceDurablePostgreSql(
            dataSource,
            runtimeEpoch,
            options => options.SendWakeNotifications = false);
        return new OperatorProjectionRuntime(
            services.BuildServiceProvider(),
            contextCodec,
            resultCodec,
            definition.FlowId,
            definition.Version);
    }

    private static DurableFlowRuntime CreateActivityRuntime(
        NpgsqlDataSource dataSource,
        Func<NpgsqlTransaction, DurableScopeId, DurableFlowInstanceId, string, CancellationToken, ValueTask>?
            onTerminalApplied = null,
        Guid? runtimeEpoch = null)
    {
        var callsite = new FlowActivityCallsite<PostgreSqlFlowWork, PostgreSqlFlowResult>("run-work", 1, 1);
        var definition = FlowGraphBuilder<PostgreSqlFlowContext>
            .Create("tests.activity-flow", "v1")
            .AddNode("activity", new PostgreSqlActivityFlowNode(callsite))
            .StartAt("activity")
            .Build();
        var contextCodec = CreateContextCodec();
        var workCodec = new SystemTextJsonDurablePayloadCodec<PostgreSqlFlowWork>(
            "tests.flow-work",
            "v1",
            DurableDataClassification.Operational,
            PostgreSqlFlowJsonContext.Default.PostgreSqlFlowWork,
            _ => true);
        var resultCodec = new SystemTextJsonDurablePayloadCodec<PostgreSqlFlowResult>(
            "tests.flow-result",
            "v1",
            DurableDataClassification.Operational,
            PostgreSqlFlowJsonContext.Default.PostgreSqlFlowResult,
            _ => true);
        var workRegistration = new DurableWorkRegistration<
            PostgreSqlFlowWork,
            PostgreSqlFlowResult,
            PostgreSqlFlowExecutor>(
                "tests.flow-work",
                "v1",
                DurableProviderSafety.ProviderKeyed,
                workCodec,
                resultCodec);
        var binding = new DurableFlowActivityBinding<
            PostgreSqlFlowContext,
            PostgreSqlFlowWork,
            PostgreSqlFlowResult>(callsite, workRegistration, workCodec, resultCodec);
        var registration = new DurableFlowRegistration<PostgreSqlFlowContext>(
            definition,
            contextCodec,
            "implementation-v1",
            new FlowTransitionEvaluator<PostgreSqlFlowContext>(),
            [binding]);
        return CreateRuntime(
            dataSource,
            registration,
            contextCodec,
            [contextCodec, workCodec, resultCodec],
            resultCodec,
            runtimeEpoch,
            onTerminalApplied,
            workRegistration,
            workCodec);
    }

    private static (
        DurableFlowRuntime Runtime,
        IDurablePayloadCodec<PostgreSqlFlowEvent> EventCodec,
        IDurablePayloadCodec<PostgreSqlFlowEvent> OtherCodec) CreateTypedEventRuntime(NpgsqlDataSource dataSource)
    {
        var callsite = new FlowEventCallsite<PostgreSqlFlowEvent>(
            "typed-approved",
            "tests.flow-event",
            "v1");
        var definition = FlowGraphBuilder<PostgreSqlFlowContext>
            .Create("tests.typed-event-flow", "v1")
            .AddNode("wait", new PostgreSqlTypedWaitFlowNode(callsite))
            .StartAt("wait")
            .Build();
        var contextCodec = CreateContextCodec();
        var eventCodec = new SystemTextJsonDurablePayloadCodec<PostgreSqlFlowEvent>(
            "tests.flow-event",
            "v1",
            DurableDataClassification.ApprovedApplication,
            PostgreSqlFlowJsonContext.Default.PostgreSqlFlowEvent,
            value => value.Code.StartsWith("safe-", StringComparison.Ordinal));
        var otherCodec = new SystemTextJsonDurablePayloadCodec<PostgreSqlFlowEvent>(
            "tests.other-flow-event",
            "v1",
            DurableDataClassification.ApprovedApplication,
            PostgreSqlFlowJsonContext.Default.PostgreSqlFlowEvent,
            value => value.Code.StartsWith("safe-", StringComparison.Ordinal));
        var registration = new DurableFlowRegistration<PostgreSqlFlowContext>(
            definition,
            contextCodec,
            "implementation-v1",
            new FlowTransitionEvaluator<PostgreSqlFlowContext>(),
            eventBindings: [new DurableFlowEventBinding<PostgreSqlFlowEvent>(callsite, eventCodec)]);
        var runtime = CreateRuntime(
            dataSource,
            registration,
            contextCodec,
            [contextCodec, eventCodec, otherCodec]);
        return (runtime, eventCodec, otherCodec);
    }

    private static DurableFlowRuntime CreateRuntime(
        NpgsqlDataSource dataSource,
        DurableFlowRegistration<PostgreSqlFlowContext> registration,
        IDurablePayloadCodec<PostgreSqlFlowContext> contextCodec,
        IEnumerable<IDurablePayloadCodec> codecs,
        IDurablePayloadCodec<PostgreSqlFlowResult>? resultCodec = null,
        Guid? runtimeEpoch = null,
        Func<NpgsqlTransaction, DurableScopeId, DurableFlowInstanceId, string, CancellationToken, ValueTask>?
            onTerminalApplied = null,
        DurableWorkRegistration? workRegistration = null,
        IDurablePayloadCodec<PostgreSqlFlowWork>? workCodec = null)
    {
        var registry = new DurableFlowRegistry([registration]);
        var codecRegistry = new DurablePayloadCodecRegistry(codecs);
        var epoch = runtimeEpoch ?? Guid.NewGuid();
        return new DurableFlowRuntime(
            new PostgreSqlDurableFlowClient(
                dataSource,
                registry,
                codecRegistry,
                epoch,
                sendWakeNotification: false,
                onTerminalApplied),
            new PostgreSqlDurableFlowStore(
                dataSource,
                epoch,
                sendWakeNotification: false,
                onTerminalApplied),
            registry,
            codecRegistry,
            registration,
            contextCodec,
            resultCodec,
            workRegistration,
            workCodec,
            epoch,
            registration.FlowId,
            registration.FlowVersion);
    }

    private static DurableFlowRuntime RotateRuntime(
        NpgsqlDataSource dataSource,
        DurableFlowRuntime runtime,
        Guid runtimeEpoch,
        Func<NpgsqlTransaction, DurableScopeId, DurableFlowInstanceId, string, CancellationToken, ValueTask>?
            onTerminalApplied = null) =>
        runtime with
        {
            Client = new PostgreSqlDurableFlowClient(
                dataSource,
                runtime.Registry,
                runtime.Codecs,
                runtimeEpoch,
                sendWakeNotification: false,
                onTerminalApplied),
            Store = new PostgreSqlDurableFlowStore(
                dataSource,
                runtimeEpoch,
                sendWakeNotification: false,
                onTerminalApplied),
            Epoch = runtimeEpoch,
        };

    private static async ValueTask RotateStoreEpochAsync(
        NpgsqlDataSource dataSource,
        Guid expectedEpoch,
        Guid activeEpoch)
    {
        var manager = new PostgreSqlDurableRuntimeSchemaManager(dataSource);
        var rotated = await manager.RotateRuntimeEpochAsync(
            expectedEpoch,
            activeEpoch,
            "operator-recovery",
            "test-runtime-rotation");
        Assert.Equal(activeEpoch, rotated.ActiveEpoch);
    }

    private static FlowDefinition<PostgreSqlFlowContext> CreateWaitDefinition(
        string flowId,
        TimeSpan? timeout) =>
        FlowGraphBuilder<PostgreSqlFlowContext>
            .Create(flowId, "v1")
            .AddNode("wait", new PostgreSqlWaitFlowNode(timeout))
            .StartAt("wait")
            .Build();

    private static DurableFlowStartRequest CreateStart(
        DurableFlowRuntime runtime,
        string scopeId,
        string commandId,
        string instanceId,
        int step = 0) =>
        new(
            new DurableScopeId(scopeId),
            new DurableCommandId(commandId),
            $"start-{instanceId}",
            new DurableFlowInstanceId(instanceId),
            runtime.FlowId,
            runtime.FlowVersion,
            runtime.ContextCodec.Encode(new PostgreSqlFlowContext(step)));

    private static DurableFlowReleaseRequest CreateRelease(
        DurableFlowStartRequest start,
        string commandId,
        long expectedRevision) =>
        new(
            start.ScopeId,
            new DurableCommandId(commandId),
            start.InstanceId,
            "operator-recovery",
            "runtime_compatibility_verified",
            expectedRevision);

    private static async ValueTask<PostgreSqlFlowProcessingResult> ProcessFlowAsync(
        DurableFlowRuntime runtime,
        DurableScopeId scopeId)
    {
        var candidate = Assert.Single(
            await runtime.Store.DiscoverAsync(20),
            item => item.ScopeId == scopeId && item.AggregateKind == "flow");
        return await runtime.Store.TryProcessAsync(
            candidate,
            "flow-worker",
            runtime.Registry,
            runtime.Codecs);
    }

    private static SystemTextJsonDurablePayloadCodec<PostgreSqlFlowContext> CreateContextCodec() =>
        new(
            "tests.flow-context",
            "v1",
            DurableDataClassification.ApprovedApplication,
            PostgreSqlFlowJsonContext.Default.PostgreSqlFlowContext,
            context => context.Step is >= 0 and < 100);

    private static async ValueTask ApplySchemaAsync(PostgreSqlIntegrationTestDatabase database)
    {
        var manager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await manager.ApplyAsync();
    }

    private static async ValueTask<(string State, long Revision)> ReadFlowAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scopeId,
        DurableFlowInstanceId instanceId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetScopeAsync(connection, transaction, scopeId);
        await using var command = new NpgsqlCommand(
            """
            SELECT state, revision
            FROM appsurface_durable.flow_instance
            WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("flow_instance_id", instanceId.Value);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.GetInt64(1));
    }

    private static async ValueTask<TransitionHistorySnapshot> ReadTransitionHistoryAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scopeId,
        string eventType)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetScopeAsync(connection, transaction, scopeId);
        await using var command = new NpgsqlCommand(
            """
            SELECT input_context_contract_id, input_context_schema_version, input_context_payload,
                   output_context_contract_id, output_context_schema_version, output_context_payload,
                   authoring_model, command_schema_version, octet_length(definition_fingerprint),
                   transition_output_contract_id
            FROM appsurface_durable.flow_history
            WHERE scope_id = @scope_id AND event_type = @event_type
            ORDER BY event_id
            LIMIT 1;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("event_type", eventType);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var result = new TransitionHistorySnapshot(
            new DurableEncodedPayload(
                reader.GetString(0),
                reader.GetString(1),
                DurableDataClassification.ApprovedApplication,
                reader.GetFieldValue<byte[]>(2)),
            new DurableEncodedPayload(
                reader.GetString(3),
                reader.GetString(4),
                DurableDataClassification.ApprovedApplication,
                reader.GetFieldValue<byte[]>(5)),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetInt32(8),
            reader.GetString(9));
        await reader.CloseAsync();
        await transaction.CommitAsync();
        return result;
    }

    private static async ValueTask<long> CountScopedAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scopeId,
        string sql) =>
        await ReadScopedScalarAsync<long>(dataSource, scopeId, sql);

    private static async ValueTask<T> ReadScopedScalarAsync<T>(
        NpgsqlDataSource dataSource,
        DurableScopeId scopeId,
        string sql)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetScopeAsync(connection, transaction, scopeId);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        var value = (T)(await command.ExecuteScalarAsync())!;
        await transaction.CommitAsync();
        return value;
    }

    private static async ValueTask SetScopeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId)
    {
        await using var command = new NpgsqlCommand(
            "SELECT set_config('appsurface_durable.scope_id', @scope_id, true);",
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static async ValueTask<TimerRecoverySnapshot> ReadTimerRecoverySnapshotAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scopeId,
        DurableFlowInstanceId instanceId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetScopeAsync(connection, transaction, scopeId);
        await using var command = new NpgsqlCommand(
            """
            SELECT timer.timer_id, wait.wait_id, timer.due_at, timer.state, timer.expected_flow_revision,
                   dispatch.due_at, dispatch.state, dispatch.expected_revision,
                   wait.state, wait.registered_revision, flow.runtime_epoch, flow.state, flow.revision
            FROM appsurface_durable.flow_instance AS flow
            JOIN appsurface_durable.flow_wait AS wait
              ON wait.scope_id = flow.scope_id AND wait.flow_instance_id = flow.flow_instance_id
            JOIN appsurface_durable.flow_timer AS timer
              ON timer.scope_id = wait.scope_id AND timer.wait_id = wait.wait_id
            JOIN appsurface_durable.dispatch AS dispatch
              ON dispatch.scope_id = timer.scope_id
             AND dispatch.aggregate_kind = 'timer'
             AND dispatch.aggregate_id = timer.timer_id::text
            WHERE flow.scope_id = @scope_id AND flow.flow_instance_id = @flow_instance_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("flow_instance_id", instanceId.Value);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var snapshot = new TimerRecoverySnapshot(
            reader.GetGuid(0),
            reader.GetGuid(1),
            new DateTimeOffset(reader.GetFieldValue<DateTime>(2), TimeSpan.Zero),
            reader.GetString(3),
            reader.GetInt64(4),
            new DateTimeOffset(reader.GetFieldValue<DateTime>(5), TimeSpan.Zero),
            reader.GetString(6),
            reader.GetInt64(7),
            reader.GetString(8),
            reader.GetInt64(9),
            reader.GetGuid(10),
            reader.GetString(11),
            reader.GetInt64(12));
        await reader.CloseAsync();
        await transaction.CommitAsync();
        return snapshot;
    }

    private static async ValueTask SetOnlyFlowWaitStateAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scopeId,
        string state)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetScopeAsync(connection, transaction, scopeId);
        await using var command = new NpgsqlCommand(
            """
            UPDATE appsurface_durable.flow_wait
            SET state = @state
            WHERE scope_id = @scope_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("state", state);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        await transaction.CommitAsync();
    }

    private static async ValueTask ForceTimerDueAsync(NpgsqlDataSource dataSource, DurableScopeId scopeId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetScopeAsync(connection, transaction, scopeId);
        await using var command = new NpgsqlCommand(
            """
            UPDATE appsurface_durable.flow_timer
            SET due_at = clock_timestamp() - interval '1 second'
            WHERE scope_id = @scope_id AND state = 'scheduled';

            UPDATE appsurface_durable.dispatch
            SET due_at = clock_timestamp() - interval '1 second'
            WHERE scope_id = @scope_id AND aggregate_kind = 'timer';
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private static async ValueTask ForceWorkDueAsync(NpgsqlDataSource dataSource, DurableScopeId scopeId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetScopeAsync(connection, transaction, scopeId);
        await using var command = new NpgsqlCommand(
            """
            UPDATE appsurface_durable.work
            SET due_at = clock_timestamp() - interval '1 second'
            WHERE scope_id = @scope_id;

            UPDATE appsurface_durable.dispatch
            SET due_at = clock_timestamp() - interval '1 second'
            WHERE scope_id = @scope_id AND aggregate_kind = 'work';
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private static async ValueTask CreateFlowRuntimeRoleAsync(
        NpgsqlDataSource ownerDataSource,
        string runtimeRole,
        string password)
    {
        var sql = $"""
            CREATE ROLE "{runtimeRole}" LOGIN PASSWORD '{password}' NOSUPERUSER NOBYPASSRLS;
            GRANT USAGE ON SCHEMA appsurface_durable TO "{runtimeRole}";
            GRANT EXECUTE ON FUNCTION appsurface_durable.initialize_runtime_epoch(uuid) TO "{runtimeRole}";
            GRANT SELECT ON appsurface_durable.store_metadata TO "{runtimeRole}";
            GRANT SELECT ON appsurface_durable.schema_migration TO "{runtimeRole}";
            GRANT SELECT, INSERT, UPDATE ON appsurface_durable.scope TO "{runtimeRole}";
            GRANT SELECT, INSERT, UPDATE ON appsurface_durable.flow_instance TO "{runtimeRole}";
            GRANT SELECT, INSERT, UPDATE ON appsurface_durable.flow_command TO "{runtimeRole}";
            GRANT SELECT, INSERT, UPDATE ON appsurface_durable.flow_history TO "{runtimeRole}";
            GRANT SELECT, INSERT, UPDATE ON appsurface_durable.flow_wait TO "{runtimeRole}";
            GRANT SELECT, INSERT, UPDATE ON appsurface_durable.flow_timer TO "{runtimeRole}";
            GRANT SELECT, INSERT, UPDATE ON appsurface_durable.dispatch TO "{runtimeRole}";
            GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA appsurface_durable TO "{runtimeRole}";
            """;
        await using var command = ownerDataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private sealed record DurableFlowRuntime(
        PostgreSqlDurableFlowClient Client,
        PostgreSqlDurableFlowStore Store,
        DurableFlowRegistry Registry,
        DurablePayloadCodecRegistry Codecs,
        DurableFlowRegistration Registration,
        IDurablePayloadCodec<PostgreSqlFlowContext> ContextCodec,
        IDurablePayloadCodec<PostgreSqlFlowResult>? ResultCodec,
        DurableWorkRegistration? WorkRegistration,
        IDurablePayloadCodec<PostgreSqlFlowWork>? WorkCodec,
        Guid Epoch,
        string FlowId,
        string FlowVersion);

    private sealed record TransitionHistorySnapshot(
        DurableEncodedPayload InputContext,
        DurableEncodedPayload OutputContext,
        string AuthoringModel,
        string CommandSchemaVersion,
        int DefinitionFingerprintLength,
        string TransitionOutputContract);

    private sealed record TimerRecoverySnapshot(
        Guid TimerId,
        Guid WaitId,
        DateTimeOffset TimerDueAtUtc,
        string TimerState,
        long TimerExpectedRevision,
        DateTimeOffset DispatchDueAtUtc,
        string DispatchState,
        long DispatchExpectedRevision,
        string WaitState,
        long WaitRegisteredRevision,
        Guid RuntimeEpoch,
        string FlowState,
        long FlowRevision);
}

internal sealed record PostgreSqlFlowContext(int Step);

internal sealed record PostgreSqlFlowWork(string Code);

internal sealed record PostgreSqlFlowResult(string Code);

internal sealed record PostgreSqlFlowEvent(string Code);

internal sealed class FlowNodeTracker
{
    internal int FirstExecutions;

    internal int CompleteExecutions;
}

internal sealed class CountingNextFlowNode(FlowNodeTracker tracker) : IFlowNode<PostgreSqlFlowContext>
{
    public ValueTask<FlowNodeOutcome<PostgreSqlFlowContext>> ExecuteAsync(
        FlowExecutionContext<PostgreSqlFlowContext> context,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref tracker.FirstExecutions);
        return ValueTask.FromResult<FlowNodeOutcome<PostgreSqlFlowContext>>(
            FlowNodeOutcome<PostgreSqlFlowContext>.Next("done", context.State with { Step = context.State.Step + 1 }));
    }
}

internal sealed class CountingCompleteFlowNode(FlowNodeTracker tracker) : IFlowNode<PostgreSqlFlowContext>
{
    public ValueTask<FlowNodeOutcome<PostgreSqlFlowContext>> ExecuteAsync(
        FlowExecutionContext<PostgreSqlFlowContext> context,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref tracker.CompleteExecutions);
        return ValueTask.FromResult<FlowNodeOutcome<PostgreSqlFlowContext>>(
            FlowNodeOutcome<PostgreSqlFlowContext>.Complete(context.State with { Step = context.State.Step + 1 }));
    }
}

internal sealed class PostgreSqlWaitFlowNode(TimeSpan? timeout) : IFlowNode<PostgreSqlFlowContext>
{
    public ValueTask<FlowNodeOutcome<PostgreSqlFlowContext>> ExecuteAsync(
        FlowExecutionContext<PostgreSqlFlowContext> context,
        CancellationToken cancellationToken = default)
    {
        if (context.ResumeEvent is null)
        {
            return ValueTask.FromResult<FlowNodeOutcome<PostgreSqlFlowContext>>(
                FlowNodeOutcome<PostgreSqlFlowContext>.Wait(
                    "approved",
                    context.State,
                    timeout is { } duration ? new FlowTimeout(duration) : null));
        }

        return ValueTask.FromResult<FlowNodeOutcome<PostgreSqlFlowContext>>(
            context.ResumeEvent.IsTimeout
                ? FlowNodeOutcome<PostgreSqlFlowContext>.TimedOut(
                    context.ResumeEvent.EventName,
                    context.State with { Step = context.State.Step + 1 })
                : FlowNodeOutcome<PostgreSqlFlowContext>.Complete(
                    context.State with { Step = context.State.Step + 1 }));
    }
}

internal sealed class PostgreSqlActivityFlowNode(
    FlowActivityCallsite<PostgreSqlFlowWork, PostgreSqlFlowResult> callsite) :
    IFlowNode<PostgreSqlFlowContext>
{
    public ValueTask<FlowNodeOutcome<PostgreSqlFlowContext>> ExecuteAsync(
        FlowExecutionContext<PostgreSqlFlowContext> context,
        CancellationToken cancellationToken = default)
    {
        if (callsite.TryGetResult(context.ActivityResult, out _))
        {
            return ValueTask.FromResult<FlowNodeOutcome<PostgreSqlFlowContext>>(
                FlowNodeOutcome<PostgreSqlFlowContext>.Complete(
                    context.State with { Step = context.State.Step + 1 }));
        }

        return ValueTask.FromResult<FlowNodeOutcome<PostgreSqlFlowContext>>(
            FlowNodeOutcome<PostgreSqlFlowContext>.Activity(
                callsite,
                new PostgreSqlFlowWork($"work-{context.State.Step}"),
                context.State));
    }
}

internal sealed class PostgreSqlTypedWaitFlowNode(FlowEventCallsite<PostgreSqlFlowEvent> callsite) :
    IFlowNode<PostgreSqlFlowContext>
{
    public ValueTask<FlowNodeOutcome<PostgreSqlFlowContext>> ExecuteAsync(
        FlowExecutionContext<PostgreSqlFlowContext> context,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<FlowNodeOutcome<PostgreSqlFlowContext>>(
            context.ResumeEvent is null
                ? FlowNodeOutcome<PostgreSqlFlowContext>.Wait(callsite, context.State)
                : FlowNodeOutcome<PostgreSqlFlowContext>.Complete(
                    context.State with { Step = context.State.Step + 1 }));
}

internal sealed class PostgreSqlInvalidFaultCodeNode : IFlowNode<PostgreSqlFlowContext>
{
    public ValueTask<FlowNodeOutcome<PostgreSqlFlowContext>> ExecuteAsync(
        FlowExecutionContext<PostgreSqlFlowContext> context,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<FlowNodeOutcome<PostgreSqlFlowContext>>(
            FlowNodeOutcome<PostgreSqlFlowContext>.Fault(
                "approval failed",
                "This invalid machine code must never reach durable terminal storage."));
}

internal sealed class FatalFlowEvaluator(bool outOfMemory) : IFlowTransitionEvaluator<PostgreSqlFlowContext>
{
    public string EvaluatorId => "tests.fatal-flow-evaluator";

    public string EvaluatorVersion => "v1";

    public ValueTask<FlowTransition<PostgreSqlFlowContext>> EvaluateAsync(
        FlowDefinition<PostgreSqlFlowContext> definition,
        FlowTransitionInput<PostgreSqlFlowContext> input,
        CancellationToken cancellationToken = default) =>
        outOfMemory
            ? throw new OutOfMemoryException("Synthetic fatal evaluator failure.")
            : throw new StackOverflowException("Synthetic fatal evaluator failure.");
}

internal sealed class FlowCancellationReconciliationCapture(bool pauseReconciliation)
{
    internal readonly TaskCompletionSource<bool> ReconciliationStarted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal readonly TaskCompletionSource<bool> AllowReconciliationCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal int ExecutorCalls;

    internal int ReconciliationCalls;

    internal bool PauseReconciliation { get; } = pauseReconciliation;
}

internal sealed class PostgreSqlCancelRaceFlowExecutor(FlowCancellationReconciliationCapture capture) :
    IDurableWorkerExecutor<PostgreSqlFlowWork, PostgreSqlFlowResult>
{
    public ValueTask<PostgreSqlFlowResult> ExecuteAsync(
        DurableWorkerEnvelope<PostgreSqlFlowWork> work,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref capture.ExecutorCalls);
        return ValueTask.FromException<PostgreSqlFlowResult>(
            new TimeoutException("The test provider outcome is intentionally ambiguous."));
    }
}

internal sealed class PostgreSqlCancelRaceFlowReconciler(FlowCancellationReconciliationCapture capture) :
    IDurableEffectReconciler<PostgreSqlFlowWork, PostgreSqlFlowResult>
{
    public async ValueTask<DurableEffectReconciliation<PostgreSqlFlowResult>> ReconcileAsync(
        DurableWorkerEnvelope<PostgreSqlFlowWork> work,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref capture.ReconciliationCalls);
        capture.ReconciliationStarted.TrySetResult(true);
        if (capture.PauseReconciliation)
        {
            await capture.AllowReconciliationCompletion.Task
                .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken)
                .ConfigureAwait(false);
        }

        return DurableEffectReconciliation<PostgreSqlFlowResult>.NotApplied();
    }
}

internal sealed class FlowAppliedReconciliationCapture
{
    internal int Calls;
}

internal sealed class FlowAppliedReconciler(FlowAppliedReconciliationCapture capture) :
    IDurableEffectReconciler<PostgreSqlFlowWork, PostgreSqlFlowResult>
{
    public ValueTask<DurableEffectReconciliation<PostgreSqlFlowResult>> ReconcileAsync(
        DurableWorkerEnvelope<PostgreSqlFlowWork> work,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref capture.Calls);
        return ValueTask.FromResult(
            DurableEffectReconciliation<PostgreSqlFlowResult>.Applied(
                new PostgreSqlFlowResult("operator-confirmed-applied")));
    }
}

internal sealed class PostgreSqlFlowExecutor : IDurableWorkerExecutor<PostgreSqlFlowWork, PostgreSqlFlowResult>
{
    public ValueTask<PostgreSqlFlowResult> ExecuteAsync(
        DurableWorkerEnvelope<PostgreSqlFlowWork> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        var payload = work.Payload ?? throw new InvalidOperationException("The durable Flow work payload is required.");
        return ValueTask.FromResult(new PostgreSqlFlowResult(payload.Code));
    }
}

[JsonSerializable(typeof(PostgreSqlFlowContext))]
[JsonSerializable(typeof(PostgreSqlFlowWork))]
[JsonSerializable(typeof(PostgreSqlFlowResult))]
[JsonSerializable(typeof(PostgreSqlFlowEvent))]
internal sealed partial class PostgreSqlFlowJsonContext : JsonSerializerContext;

internal sealed record OperatorProjectionRuntime(
    ServiceProvider Provider,
    IDurablePayloadCodec<PostgreSqlFlowContext> ContextCodec,
    IDurablePayloadCodec<PostgreSqlFlowResult> ResultCodec,
    string FlowId,
    string FlowVersion);
