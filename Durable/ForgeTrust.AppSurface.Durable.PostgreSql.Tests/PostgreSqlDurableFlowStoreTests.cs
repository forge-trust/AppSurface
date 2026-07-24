using System.Diagnostics;
using System.Text.Json;
using ForgeTrust.AppSurface.Flow;
using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

public sealed class DurableSlice4ReferenceWorkloadTests
{
    [Fact]
    public async Task Flow_StartWaitEventResumeComplete_IsIdempotentAndAuthoritative()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "tests", "slice4-flow");
        var status = await schema.GetStatusAsync();
        var codec = new PostgreSqlOpaqueTestCodec(
            "tests.flow.context",
            "v1",
            DurableDataClassification.ApprovedApplication);
        var payloads = new DurablePayloadCodecRegistry([codec]);
        var work = new DurableWorkRegistry([]);
        var registration = new WaitingTestFlowRegistration(codec);
        var flows = new DurableFlowRegistry([registration], work, payloads);
        var options = new PostgreSqlDurableWorkOptions(epoch, status.StoreId);
        var client = new PostgreSqlDurableFlowClient(database.DataSource, flows, payloads, options);
        var processor = new PostgreSqlDurableFlowProcessor(
            database.DataSource,
            database.DataSource,
            flows,
            work,
            payloads,
            options);
        var scope = new DurableScopeId("slice4-flow");
        var instance = new DurableFlowInstanceId("flow-1");
        var context = codec.EncodeObject(new byte[] { 1, 2, 3 });
        var start = new DurableFlowStartRequest(
            scope,
            new DurableCommandId("start-1"),
            "start-idempotency-1",
            instance,
            registration.FlowId,
            registration.FlowVersion,
            context);

        var accepted = await client.StartAsync(start);
        var duplicate = await client.StartAsync(start);
        var snapshot = await client.GetAsync(new DurableFlowGetRequest(scope, instance));
        var listed = await client.ListAsync(new DurableFlowListRequest(scope, pageSize: 10));

        Assert.True(accepted.IsSuccess);
        Assert.Equal(DurableFlowCommandOutcome.Accepted, accepted.Value!.Outcome);
        Assert.True(duplicate.IsSuccess);
        Assert.Equal(DurableFlowCommandOutcome.Duplicate, duplicate.Value!.Outcome);
        Assert.Equal(DurableFlowState.Ready, snapshot.Value!.State);
        Assert.Equal(instance, Assert.Single(listed.Value!.Flows).InstanceId);

        var firstCandidate = Assert.Single(await processor.DiscoverAsync());
        var waited = await processor.TryProcessAsync(firstCandidate, "processor-a");
        Assert.Equal(PostgreSqlFlowProcessingOutcome.Applied, waited.Outcome);
        Assert.Equal(DurableFlowState.WaitingForEvent, waited.State);

        var eventRequest = new DurableFlowEventRequest(
            scope,
            new DurableCommandId("event-1"),
            new DurableFlowEventId("event-id-1"),
            instance,
            "approved",
            expectedRevision: waited.Revision);
        var eventAccepted = await client.RaiseEventAsync(eventRequest);
        Assert.True(eventAccepted.IsSuccess);
        Assert.Equal(DurableFlowCommandOutcome.Accepted, eventAccepted.Value!.Outcome);
        Assert.Equal(DurableFlowState.Ready, eventAccepted.Value.State);

        var resumeCandidate = Assert.Single(await processor.DiscoverAsync());
        var completed = await processor.TryProcessAsync(resumeCandidate, "processor-b");
        Assert.Equal(PostgreSqlFlowProcessingOutcome.Terminal, completed.Outcome);
        Assert.Equal(DurableFlowState.Completed, completed.State);

        var eventDuplicate = await client.RaiseEventAsync(eventRequest);
        Assert.True(eventDuplicate.IsSuccess);
        Assert.Equal(DurableFlowCommandOutcome.Duplicate, eventDuplicate.Value!.Outcome);
        Assert.Equal(eventAccepted.Value.Revision, eventDuplicate.Value.Revision);
        var terminal = await client.GetAsync(new DurableFlowGetRequest(scope, instance));
        Assert.Equal(DurableFlowState.Completed, terminal.Value!.State);
        Assert.Equal(completed.Revision, terminal.Value.Revision);
        WriteEvidence("flow-event-resume", completed.Revision);
    }

    [Fact]
    public async Task EventBeforeWait_DoesNotConsumeIdentity_AndChangedStartConflicts()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "tests", "slice4-identities");
        var status = await schema.GetStatusAsync();
        var codec = new PostgreSqlOpaqueTestCodec("tests.flow.context", "v1");
        var payloads = new DurablePayloadCodecRegistry([codec]);
        var work = new DurableWorkRegistry([]);
        var registration = new WaitingTestFlowRegistration(codec);
        var flows = new DurableFlowRegistry([registration], work, payloads);
        var client = new PostgreSqlDurableFlowClient(
            database.DataSource,
            flows,
            payloads,
            new PostgreSqlDurableWorkOptions(epoch, status.StoreId));
        var scope = new DurableScopeId("slice4-identities");
        var instance = new DurableFlowInstanceId("flow-identity");
        var start = new DurableFlowStartRequest(
            scope,
            new DurableCommandId("start-identity"),
            "start-key",
            instance,
            registration.FlowId,
            registration.FlowVersion,
            codec.EncodeObject(new byte[] { 1 }));
        Assert.True((await client.StartAsync(start)).IsSuccess);

        var early = new DurableFlowEventRequest(
            scope,
            new DurableCommandId("event-early"),
            new DurableFlowEventId("event-early-id"),
            instance,
            "approved");
        var first = await client.RaiseEventAsync(early);
        var second = await client.RaiseEventAsync(early);
        Assert.Equal(DurableFlowCommandOutcome.NotWaitingYet, first.Value!.Outcome);
        Assert.Equal(DurableFlowCommandOutcome.NotWaitingYet, second.Value!.Outcome);

        var changed = new DurableFlowStartRequest(
            scope,
            start.CommandId,
            start.IdempotencyKey,
            instance,
            registration.FlowId,
            registration.FlowVersion,
            codec.EncodeObject(new byte[] { 9 }));
        var conflict = await client.StartAsync(changed);
        Assert.False(conflict.IsSuccess);
        Assert.Equal(DurableProblemCodes.FlowCommandConflict, conflict.Problem!.Code);
        WriteEvidence("flow-identity-retry", 1);
    }

    [Fact]
    public async Task ActivityCompletion_ProjectsWorkResultAndResumesParentAtomically()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "tests", "slice4-activity");
        var status = await schema.GetStatusAsync();
        var contextCodec = new PostgreSqlOpaqueTestCodec("tests.flow.context", "v1");
        var workCodec = new PostgreSqlOpaqueTestCodec("tests.flow.activity", "v1");
        var resultCodec = new PostgreSqlOpaqueTestCodec("tests.flow.activity.result", "v1");
        var workRegistration = new PostgreSqlOpaqueTestWorkRegistration(
            "tests.flow.activity",
            "v1",
            DurableProviderSafety.ProviderKeyed,
            workCodec,
            resultCodec);
        var payloads = new DurablePayloadCodecRegistry([contextCodec, workCodec, resultCodec]);
        var work = new DurableWorkRegistry([workRegistration]);
        var registration = new ActivityTestFlowRegistration(contextCodec, workRegistration, workCodec);
        var flows = new DurableFlowRegistry([registration], work, payloads);
        var options = new PostgreSqlDurableWorkOptions(epoch, status.StoreId);
        var client = new PostgreSqlDurableFlowClient(database.DataSource, flows, payloads, options);
        var flowProcessor = new PostgreSqlDurableFlowProcessor(
            database.DataSource,
            database.DataSource,
            flows,
            work,
            payloads,
            options);
        var scope = new DurableScopeId("slice4-activity");
        var instance = new DurableFlowInstanceId("activity-flow");
        var start = new DurableFlowStartRequest(
            scope,
            new DurableCommandId("activity-start"),
            "activity-start-key",
            instance,
            registration.FlowId,
            registration.FlowVersion,
            contextCodec.EncodeObject(new byte[] { 1 }));
        Assert.True((await client.StartAsync(start)).IsSuccess);

        var activityDecision = await flowProcessor.TryProcessAsync(
            Assert.Single(await flowProcessor.DiscoverAsync()),
            "flow-activity-worker");
        Assert.Equal(DurableFlowState.WaitingForActivity, activityDecision.State);
        Assert.NotNull(activityDecision.ChildWorkId);

        var workStore = new PostgreSqlDurableWorkStore(database.DataSource, epoch);
        var childCandidate = Assert.Single(await workStore.DiscoverAsync(10));
        var claim = await workStore.TryClaimAsync(childCandidate, "work-activity-worker");
        Assert.NotNull(claim);
        var permit = await workStore.TryAcquireEffectPermitAsync(claim!);
        Assert.NotNull(permit);
        var completion = await workStore.RecordCompletionAsync(
            permit!.Claim,
            new PostgreSqlWorkCompletion(
                PostgreSqlWorkCompletionKind.Succeeded,
                "activity.completed",
                "{}",
                resultCodec.EncodeObject(new byte[] { 7, 8 })));
        Assert.Equal(DurableWorkState.Succeeded, completion.State);

        var resumed = await client.GetAsync(new DurableFlowGetRequest(scope, instance));
        Assert.Equal(DurableFlowState.Ready, resumed.Value!.State);
        var terminal = await flowProcessor.TryProcessAsync(
            Assert.Single(await flowProcessor.DiscoverAsync()),
            "flow-resume-worker");
        Assert.Equal(PostgreSqlFlowProcessingOutcome.Terminal, terminal.Outcome);
        Assert.Equal(DurableFlowState.Completed, terminal.State);

        var canceledInstance = new DurableFlowInstanceId("activity-flow-canceled");
        Assert.True((await client.StartAsync(new DurableFlowStartRequest(
            scope,
            new DurableCommandId("activity-cancel-start"),
            "activity-cancel-start-key",
            canceledInstance,
            registration.FlowId,
            registration.FlowVersion,
            contextCodec.EncodeObject(new byte[] { 2 })))).IsSuccess);
        var pendingActivity = await flowProcessor.TryProcessAsync(
            Assert.Single(await flowProcessor.DiscoverAsync()),
            "flow-activity-cancel-worker");
        var canceled = await client.CancelAsync(new DurableFlowCancelRequest(
            scope,
            new DurableCommandId("activity-cancel"),
            canceledInstance,
            "tests",
            "consumer-requested",
            pendingActivity.Revision));
        Assert.Equal(DurableFlowState.Canceled, canceled.Value!.State);
        Assert.DoesNotContain(
            await workStore.DiscoverAsync(10),
            candidate => candidate.WorkId == pendingActivity.ChildWorkId);
        Assert.Equal(
            DurableFlowState.Canceled,
            (await client.GetAsync(new DurableFlowGetRequest(scope, canceledInstance))).Value!.State);
        WriteEvidence("flow-activity-resume", terminal.Revision);
    }

    [Fact]
    public async Task TimerAndEventRace_HasOneRevisionWinnerAndDuplicateStableLoser()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "tests", "slice4-timer-race");
        var status = await schema.GetStatusAsync();
        var codec = new PostgreSqlOpaqueTestCodec("tests.flow.context", "v1");
        var payloads = new DurablePayloadCodecRegistry([codec]);
        var work = new DurableWorkRegistry([]);
        var registration = new TimerTestFlowRegistration(codec);
        var flows = new DurableFlowRegistry([registration], work, payloads);
        var options = new PostgreSqlDurableWorkOptions(epoch, status.StoreId);
        var client = new PostgreSqlDurableFlowClient(database.DataSource, flows, payloads, options);
        var processor = new PostgreSqlDurableFlowProcessor(
            database.DataSource,
            database.DataSource,
            flows,
            work,
            payloads,
            options);
        var scope = new DurableScopeId("slice4-timer-race");
        var instance = new DurableFlowInstanceId("timer-flow");
        Assert.True((await client.StartAsync(new DurableFlowStartRequest(
            scope,
            new DurableCommandId("timer-start"),
            "timer-start-key",
            instance,
            registration.FlowId,
            registration.FlowVersion,
            codec.EncodeObject(new byte[] { 1 })))).IsSuccess);

        var waiting = await processor.TryProcessAsync(
            Assert.Single(await processor.DiscoverAsync()),
            "timer-register-worker");
        Assert.Equal(DurableFlowState.WaitingForEvent, waiting.State);
        await Task.Delay(25);
        var timerCandidate = Assert.Single(await processor.DiscoverAsync());
        Assert.Equal(PostgreSqlFlowDispatchKind.Timer, timerCandidate.Kind);
        var crashCheckpoint = await RunTimerUntilCommitAndTerminateAsync(
            database.ConnectionString,
            epoch,
            status.StoreId,
            scope,
            instance);
        Assert.Equal("flow.timer-resolution.committed", crashCheckpoint.Phase);
        Assert.Equal(waiting.Revision + 1, crashCheckpoint.Revision);

        var restartedProcessor = new PostgreSqlDurableFlowProcessor(
            database.DataSource,
            database.DataSource,
            flows,
            work,
            payloads,
            options);

        var losingEvent = new DurableFlowEventRequest(
            scope,
            new DurableCommandId("timer-loser-command"),
            new DurableFlowEventId("timer-loser-event"),
            instance,
            "approved",
            expectedRevision: waiting.Revision);
        var firstLoser = await client.RaiseEventAsync(losingEvent);
        var duplicateLoser = await client.RaiseEventAsync(losingEvent);
        Assert.Equal(DurableFlowCommandOutcome.RaceLost, firstLoser.Value!.Outcome);
        Assert.Equal(DurableFlowCommandOutcome.Duplicate, duplicateLoser.Value!.Outcome);
        Assert.Equal(firstLoser.Value.Revision, duplicateLoser.Value.Revision);

        var terminal = await restartedProcessor.TryProcessAsync(
            Assert.Single(await restartedProcessor.DiscoverAsync()),
            "timer-resume-worker");
        Assert.Equal(PostgreSqlFlowProcessingOutcome.Terminal, terminal.Outcome);
        WriteEvidence("flow-timer-race", terminal.Revision);
    }

    [Fact]
    public async Task ScopeDisable_SuspendsFlowDispatchWaitAndHistoryTogether()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "tests", "slice4-scope-disable");
        var status = await schema.GetStatusAsync();
        var codec = new PostgreSqlOpaqueTestCodec("tests.flow.context", "v1");
        var payloads = new DurablePayloadCodecRegistry([codec]);
        var work = new DurableWorkRegistry([]);
        var registration = new WaitingTestFlowRegistration(codec);
        var flows = new DurableFlowRegistry([registration], work, payloads);
        var options = new PostgreSqlDurableWorkOptions(epoch, status.StoreId);
        var client = new PostgreSqlDurableFlowClient(database.DataSource, flows, payloads, options);
        var scope = new DurableScopeId("slice4-scope-disable");
        var instance = new DurableFlowInstanceId("scope-flow");
        Assert.True((await client.StartAsync(new DurableFlowStartRequest(
            scope,
            new DurableCommandId("scope-start"),
            "scope-start-key",
            instance,
            registration.FlowId,
            registration.FlowVersion,
            codec.EncodeObject(new byte[] { 1 })))).IsSuccess);

        var store = new PostgreSqlDurableWorkStore(database.DataSource, epoch);
        var disabled = await store.DisableScopeAsync(scope, "tests", "disable", expectedGeneration: 1);
        Assert.Equal(PostgreSqlScopeMutationOutcome.Applied, disabled.Outcome);
        var snapshot = await client.GetAsync(new DurableFlowGetRequest(scope, instance));
        Assert.Equal(DurableFlowState.Suspended, snapshot.Value!.State);
        await using var command = database.DataSource.CreateCommand(
            """
            SELECT
                (SELECT count(*) FROM appsurface_durable.flow_dispatch WHERE state = 'suspended'),
                (SELECT count(*) FROM appsurface_durable.flow_history WHERE transition_kind = 'scope_disabled');
            """);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt64(0));
        Assert.Equal(1, reader.GetInt64(1));
        WriteEvidence("flow-scope-disable", snapshot.Value.Revision);
    }

    private static void WriteEvidence(string scenario, long terminalRevision)
    {
        var directory = Environment.GetEnvironmentVariable("APPSURFACE_POSTGRES_REFERENCE_EVIDENCE_DIRECTORY");
        var mode = Environment.GetEnvironmentVariable("APPSURFACE_POSTGRES_REFERENCE_EVIDENCE_MODE");
        var runId = Environment.GetEnvironmentVariable("APPSURFACE_POSTGRES_REFERENCE_EVIDENCE_RUN_ID");
        if (string.IsNullOrWhiteSpace(directory)
            || string.IsNullOrWhiteSpace(mode)
            || string.IsNullOrWhiteSpace(runId))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        var evidence = new
        {
            SchemaVersion = 1,
            RunId = runId,
            Mode = mode,
            DatabaseSource = PostgreSqlTestContainerImage.Reference,
            Scenario = scenario,
            TerminalRevision = terminalRevision,
            Result = "passed",
            RerunCommand = "./Durable/verify-postgresql.sh --quick --flow",
            RecordedAtUtc = DateTimeOffset.UtcNow,
        };
        File.WriteAllText(
            Path.Combine(directory, $"{scenario}.json"),
            JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async ValueTask<FlowCrashCheckpoint> RunTimerUntilCommitAndTerminateAsync(
        string connectionString,
        Guid runtimeEpoch,
        Guid storeId,
        DurableScopeId scopeId,
        DurableFlowInstanceId instanceId)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(typeof(ReferenceWorkloadHostMarker).Assembly.Location);
        startInfo.ArgumentList.Add("flow-timer");
        startInfo.ArgumentList.Add(runtimeEpoch.ToString("D"));
        startInfo.ArgumentList.Add(storeId.ToString("D"));
        startInfo.ArgumentList.Add(scopeId.Value);
        startInfo.ArgumentList.Add(instanceId.Value);
        startInfo.Environment["APPSURFACE_POSTGRES_REFERENCE_CONNECTION"] = connectionString;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The Flow timer crash child process could not start.");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
            if (string.IsNullOrWhiteSpace(line))
            {
                var error = await process.StandardError.ReadToEndAsync(timeout.Token);
                throw new InvalidOperationException($"Flow timer child exited before its commit checkpoint: {error}");
            }

            var checkpoint = JsonSerializer.Deserialize<FlowCrashCheckpoint>(line)
                ?? throw new InvalidOperationException("Flow timer child returned an invalid checkpoint.");
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(timeout.Token);
            return checkpoint;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    private sealed class WaitingTestFlowRegistration(IDurablePayloadCodec contextCodec) : DurableFlowRegistration
    {
        public override string FlowId => "tests.wait-flow";

        public override string FlowVersion => "v1";

        public override string ImplementationVersion => "tests-v1";

        public override string StartNodeId => "start";

        public override string DefinitionFingerprint => new('a', 64);

        public override IDurablePayloadCodec ContextCodec { get; } = contextCodec;

        public override IReadOnlyList<DurableFlowEventBinding> EventBindings => [];

        public override IReadOnlyList<DurableWorkRegistration> ActivityWorkRegistrations => [];

        public override ValueTask<DurableFlowEvaluationResult> EvaluateAsync(
            DurableFlowEvaluationInput input,
            IDurablePayloadCodecRegistry payloadCodecs,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(input.ResumeEventName is null
                ? new DurableFlowEvaluationResult(
                    FlowTransitionKind.Wait,
                    input.NodeId,
                    input.Context,
                    null,
                    "approved",
                    null,
                    null,
                    null,
                    new DurableFlowEventContract(payloadRequired: false))
                : new DurableFlowEvaluationResult(
                    FlowTransitionKind.Complete,
                    input.NodeId,
                    input.Context,
                    null,
                    null,
                    null,
                    null,
                    null));
    }

    private sealed class ActivityTestFlowRegistration(
        IDurablePayloadCodec contextCodec,
        DurableWorkRegistration workRegistration,
        IDurablePayloadCodec workCodec) : DurableFlowRegistration
    {
        public override string FlowId => "tests.activity-flow";

        public override string FlowVersion => "v1";

        public override string ImplementationVersion => "tests-activity-v1";

        public override string StartNodeId => "activity";

        public override string DefinitionFingerprint => new('b', 64);

        public override IDurablePayloadCodec ContextCodec { get; } = contextCodec;

        public override IReadOnlyList<DurableFlowEventBinding> EventBindings => [];

        public override IReadOnlyList<DurableWorkRegistration> ActivityWorkRegistrations { get; } = [workRegistration];

        public override ValueTask<DurableFlowEvaluationResult> EvaluateAsync(
            DurableFlowEvaluationInput input,
            IDurablePayloadCodecRegistry payloadCodecs,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(input.ActivityResult is null
                ? new DurableFlowEvaluationResult(
                    FlowTransitionKind.Activity,
                    input.NodeId,
                    input.Context,
                    null,
                    null,
                    null,
                    null,
                    new DurableFlowActivityCommand(
                        "call-provider",
                        1,
                        workRegistration.WorkName,
                        workRegistration.WorkVersion,
                        workRegistration.ProviderSafety,
                        workCodec.EncodeObject(new byte[] { 4, 5 })))
                : new DurableFlowEvaluationResult(
                    FlowTransitionKind.Complete,
                    input.NodeId,
                    input.Context,
                    null,
                    null,
                    null,
                    null,
                    null));
    }

    private sealed class TimerTestFlowRegistration(IDurablePayloadCodec contextCodec) : DurableFlowRegistration
    {
        public override string FlowId => "tests.timer-flow";

        public override string FlowVersion => "v1";

        public override string ImplementationVersion => "tests-timer-v1";

        public override string StartNodeId => "timer";

        public override string DefinitionFingerprint => new('c', 64);

        public override IDurablePayloadCodec ContextCodec { get; } = contextCodec;

        public override IReadOnlyList<DurableFlowEventBinding> EventBindings => [];

        public override IReadOnlyList<DurableWorkRegistration> ActivityWorkRegistrations => [];

        public override ValueTask<DurableFlowEvaluationResult> EvaluateAsync(
            DurableFlowEvaluationInput input,
            IDurablePayloadCodecRegistry payloadCodecs,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(input.IsTimeout
                ? new DurableFlowEvaluationResult(
                    FlowTransitionKind.Complete,
                    input.NodeId,
                    input.Context,
                    null,
                    null,
                    null,
                    null,
                    null)
                : new DurableFlowEvaluationResult(
                    FlowTransitionKind.Wait,
                    input.NodeId,
                    input.Context,
                    null,
                    "approved",
                    new FlowTimeout(TimeSpan.FromMilliseconds(1)),
                    null,
                    null,
                    new DurableFlowEventContract(payloadRequired: false)));
    }

}
