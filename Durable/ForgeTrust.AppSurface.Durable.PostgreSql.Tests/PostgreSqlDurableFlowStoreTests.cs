using System.Diagnostics;
using System.Text.Json;
using ForgeTrust.AppSurface.Core;
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

        await using (var evaluating = database.DataSource.CreateCommand(
            """
            UPDATE appsurface_durable.flow_instance
            SET state = 'evaluating', lease_owner = 'list-filter-test',
                lease_started_at = clock_timestamp(), lease_expires_at = clock_timestamp() + interval '1 minute'
            WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id;
            """))
        {
            evaluating.Parameters.AddWithValue("scope_id", scope.Value);
            evaluating.Parameters.AddWithValue("flow_instance_id", instance.Value);
            Assert.Equal(1, await evaluating.ExecuteNonQueryAsync());
        }
        var evaluatingList = await client.ListAsync(
            new DurableFlowListRequest(scope, state: DurableFlowState.Ready, pageSize: 10));
        Assert.Equal(DurableFlowState.Ready, Assert.Single(evaluatingList.Value!.Flows).State);
        await using (var ready = database.DataSource.CreateCommand(
            """
            UPDATE appsurface_durable.flow_instance
            SET state = 'ready', lease_owner = NULL, lease_started_at = NULL, lease_expires_at = NULL
            WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id;
            """))
        {
            ready.Parameters.AddWithValue("scope_id", scope.Value);
            ready.Parameters.AddWithValue("flow_instance_id", instance.Value);
            Assert.Equal(1, await ready.ExecuteNonQueryAsync());
        }

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
    public async Task EventDuplicate_ReturnsPersistedTruthAfterEventCodecRetires()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "tests", "slice4-event-codec-retirement");
        var status = await schema.GetStatusAsync();
        var contextCodec = new PostgreSqlOpaqueTestCodec("tests.flow.context", "v1");
        var eventCodec = new PostgreSqlOpaqueTestCodec("tests.flow.event", "v1");
        var payloads = new DurablePayloadCodecRegistry([contextCodec, eventCodec]);
        var work = new DurableWorkRegistry([]);
        var registration = new RequiredPayloadWaitingRegistration(contextCodec);
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
        var scope = new DurableScopeId("slice4-event-codec-retirement");
        var instance = new DurableFlowInstanceId("event-codec-retirement");
        Assert.True((await client.StartAsync(new DurableFlowStartRequest(
            scope,
            new DurableCommandId("event-codec-retirement-start"),
            "event-codec-retirement-key",
            instance,
            registration.FlowId,
            registration.FlowVersion,
            contextCodec.EncodeObject(new byte[] { 1 })))).IsSuccess);
        var waited = await processor.TryProcessAsync(
            Assert.Single(await processor.DiscoverAsync()),
            "event-codec-retirement-worker");
        var request = new DurableFlowEventRequest(
            scope,
            new DurableCommandId("event-codec-retirement-command"),
            new DurableFlowEventId("event-codec-retirement-event"),
            instance,
            "approved",
            eventCodec.EncodeObject(new byte[] { 2 }),
            waited.Revision);

        var accepted = await client.RaiseEventAsync(request);
        Assert.Equal(DurableFlowCommandOutcome.Accepted, accepted.Value!.Outcome);

        var retryClient = new PostgreSqlDurableFlowClient(
            database.DataSource,
            flows,
            new DurablePayloadCodecRegistry([contextCodec]),
            options);
        var duplicate = await retryClient.RaiseEventAsync(request);

        Assert.Equal(DurableFlowCommandOutcome.Duplicate, duplicate.Value!.Outcome);
        Assert.Equal(accepted.Value.Revision, duplicate.Value.Revision);
    }

    [Fact]
    public async Task FlowClaim_ConcurrentProcessorsApplyOneDecisionAndRejectStaleCandidates()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "tests", "slice4-flow-concurrent-claim");
        var status = await schema.GetStatusAsync();
        var codec = new PostgreSqlOpaqueTestCodec("tests.flow.context", "v1");
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
        var scope = new DurableScopeId("slice4-flow-concurrent-claim");
        var instance = new DurableFlowInstanceId("concurrent-flow");
        Assert.True((await client.StartAsync(new DurableFlowStartRequest(
            scope,
            new DurableCommandId("concurrent-flow-start"),
            "concurrent-flow-start-key",
            instance,
            registration.FlowId,
            registration.FlowVersion,
            codec.EncodeObject(new byte[] { 1 })))).IsSuccess);

        var candidate = Assert.Single(await processor.DiscoverAsync());
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(index =>
            processor.TryProcessAsync(candidate, $"concurrent-flow-worker-{index}").AsTask()));

        var applied = Assert.Single(results, result => result.Outcome == PostgreSqlFlowProcessingOutcome.Applied);
        Assert.Equal(DurableFlowState.WaitingForEvent, applied.State);
        Assert.All(results, result => Assert.True(
            result.Outcome is PostgreSqlFlowProcessingOutcome.Applied or PostgreSqlFlowProcessingOutcome.NotClaimed));

        var snapshot = await client.GetAsync(new DurableFlowGetRequest(scope, instance));
        Assert.Equal(DurableFlowState.WaitingForEvent, snapshot.Value!.State);
        Assert.Equal(applied.Revision, snapshot.Value.Revision);
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

        var competingStart = await client.StartAsync(new DurableFlowStartRequest(
            scope,
            new DurableCommandId("competing-start-identity"),
            "competing-start-key",
            instance,
            registration.FlowId,
            registration.FlowVersion,
            codec.EncodeObject(new byte[] { 1 })));
        Assert.False(competingStart.IsSuccess);
        Assert.Equal(DurableProblemCodes.FlowStartConflict, competingStart.Problem!.Code);

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

        await using (var injectUniqueViolation = database.DataSource.CreateCommand(
            """
            CREATE SEQUENCE appsurface_durable.flow_start_unique_violation_sequence;

            CREATE FUNCTION appsurface_durable.raise_flow_start_unique_violation()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                IF nextval('appsurface_durable.flow_start_unique_violation_sequence') <= 2 THEN
                    RAISE EXCEPTION 'Simulated Flow start unique violation' USING ERRCODE = '23505';
                END IF;

                RETURN NEW;
            END;
            $$;

            CREATE TRIGGER flow_start_unique_violation
            BEFORE INSERT ON appsurface_durable.flow_instance
            FOR EACH ROW
            EXECUTE FUNCTION appsurface_durable.raise_flow_start_unique_violation();
            """))
        {
            await injectUniqueViolation.ExecuteNonQueryAsync();
        }

        var boundedRetry = await client.StartAsync(new DurableFlowStartRequest(
            scope,
            new DurableCommandId("bounded-retry-start-identity"),
            "bounded-retry-start-key",
            new DurableFlowInstanceId("bounded-retry-flow"),
            registration.FlowId,
            registration.FlowVersion,
            codec.EncodeObject(new byte[] { 1 })));
        Assert.False(boundedRetry.IsSuccess);
        Assert.Equal(DurableProblemCodes.FlowStartConflict, boundedRetry.Problem!.Code);
        WriteEvidence("flow-identity-retry", 1);
    }

    [Fact]
    public async Task FlowStore_BoundsUniqueViolationRetriesForEventsAndLifecycleCommands()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "tests", "slice4-command-retry");
        var status = await schema.GetStatusAsync();
        var codec = new PostgreSqlOpaqueTestCodec("tests.flow.context", "v1");
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
        var scope = new DurableScopeId("slice4-command-retry");
        var instance = new DurableFlowInstanceId("command-retry-flow");

        Assert.True((await client.StartAsync(new DurableFlowStartRequest(
            scope,
            new DurableCommandId("command-retry-start"),
            "command-retry-key",
            instance,
            registration.FlowId,
            registration.FlowVersion,
            codec.EncodeObject(new byte[] { 1 })))).IsSuccess);
        var waiting = await processor.TryProcessAsync(
            Assert.Single(await processor.DiscoverAsync()),
            "command-retry-wait-worker");
        Assert.Equal(DurableFlowState.WaitingForEvent, waiting.State);

        await using (var injectUniqueViolation = database.DataSource.CreateCommand(
            """
            CREATE SEQUENCE appsurface_durable.flow_event_unique_violation_sequence;
            CREATE SEQUENCE appsurface_durable.flow_cancel_unique_violation_sequence;

            CREATE FUNCTION appsurface_durable.raise_flow_command_unique_violation()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                IF (NEW.command_type = 'event'
                    AND nextval('appsurface_durable.flow_event_unique_violation_sequence') <= 2)
                   OR (NEW.command_type = 'cancel'
                    AND nextval('appsurface_durable.flow_cancel_unique_violation_sequence') <= 2) THEN
                    RAISE EXCEPTION 'Simulated Flow command unique violation' USING ERRCODE = '23505';
                END IF;

                RETURN NEW;
            END;
            $$;

            CREATE TRIGGER flow_command_unique_violation
            BEFORE INSERT ON appsurface_durable.flow_command
            FOR EACH ROW
            EXECUTE FUNCTION appsurface_durable.raise_flow_command_unique_violation();
            """))
        {
            await injectUniqueViolation.ExecuteNonQueryAsync();
        }

        var boundedEvent = await client.RaiseEventAsync(new DurableFlowEventRequest(
            scope,
            new DurableCommandId("command-retry-event"),
            new DurableFlowEventId("command-retry-event-id"),
            instance,
            "approved",
            expectedRevision: waiting.Revision));
        Assert.False(boundedEvent.IsSuccess);
        Assert.Equal(DurableProblemCodes.FlowCommandConflict, boundedEvent.Problem!.Code);

        var afterEvent = await client.GetAsync(new DurableFlowGetRequest(scope, instance));
        Assert.Equal(DurableFlowState.WaitingForEvent, afterEvent.Value!.State);
        Assert.Equal(waiting.Revision, afterEvent.Value.Revision);

        var boundedCancel = await client.CancelAsync(new DurableFlowCancelRequest(
            scope,
            new DurableCommandId("command-retry-cancel"),
            instance,
            "tests",
            "bounded-retry",
            waiting.Revision));
        Assert.False(boundedCancel.IsSuccess);
        Assert.Equal(DurableProblemCodes.FlowCommandConflict, boundedCancel.Problem!.Code);

        var afterCancel = await client.GetAsync(new DurableFlowGetRequest(scope, instance));
        Assert.Equal(DurableFlowState.WaitingForEvent, afterCancel.Value!.State);
        Assert.Equal(waiting.Revision, afterCancel.Value.Revision);
    }

    [Fact]
    public async Task FlowStore_RejectsInvalidRuntimeMetadataAndEventsForMissingFlows()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "tests", "slice4-runtime-metadata");
        var status = await schema.GetStatusAsync();
        var codec = new PostgreSqlOpaqueTestCodec("tests.flow.context", "v1");
        var payloads = new DurablePayloadCodecRegistry([codec]);
        var work = new DurableWorkRegistry([]);
        var registration = new WaitingTestFlowRegistration(codec);
        var flows = new DurableFlowRegistry([registration], work, payloads);
        var options = new PostgreSqlDurableWorkOptions(
            epoch,
            status.StoreId,
            PostgreSqlDurableWakeNotificationMode.Enabled);
        var client = new PostgreSqlDurableFlowClient(database.DataSource, flows, payloads, options);
        var scope = new DurableScopeId("slice4-runtime-metadata");

        var started = await client.StartAsync(new DurableFlowStartRequest(
            scope,
            new DurableCommandId("metadata-start-command"),
            "metadata-start-key",
            new DurableFlowInstanceId("metadata-flow"),
            registration.FlowId,
            registration.FlowVersion,
            codec.EncodeObject(new byte[] { 1 })));
        Assert.Equal(DurableFlowCommandOutcome.Accepted, started.Value!.Outcome);

        var missing = await client.RaiseEventAsync(new DurableFlowEventRequest(
            scope,
            new DurableCommandId("missing-flow-event-command"),
            new DurableFlowEventId("missing-flow-event-id"),
            new DurableFlowInstanceId("missing-flow"),
            "approved"));
        Assert.Equal(DurableProblemCodes.FlowNotFound, missing.Problem!.Code);

        await using var metadata = database.DataSource.CreateCommand(
            """
            UPDATE appsurface_durable.store_metadata
            SET schema_version = @schema_version,
                store_id = @store_id,
                active_runtime_epoch = @runtime_epoch
            WHERE singleton;
            """);
        metadata.Parameters.Add(new NpgsqlParameter("schema_version", NpgsqlTypes.NpgsqlDbType.Integer));
        metadata.Parameters.Add(new NpgsqlParameter("store_id", NpgsqlTypes.NpgsqlDbType.Uuid));
        metadata.Parameters.Add(new NpgsqlParameter("runtime_epoch", NpgsqlTypes.NpgsqlDbType.Uuid));

        metadata.Parameters["schema_version"].Value = 2;
        metadata.Parameters["store_id"].Value = status.StoreId;
        metadata.Parameters["runtime_epoch"].Value = epoch;
        Assert.Equal(1, await metadata.ExecuteNonQueryAsync());
        var upgradeRequired = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await client.ListAsync(new DurableFlowListRequest(scope, pageSize: 10)));
        Assert.Contains(DurableProblemCodes.SchemaUpgradeRequired, upgradeRequired.Message, StringComparison.Ordinal);

        metadata.Parameters["schema_version"].Value = 3;
        metadata.Parameters["store_id"].Value = Guid.NewGuid();
        Assert.Equal(1, await metadata.ExecuteNonQueryAsync());
        var storeMismatch = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await client.GetAsync(new DurableFlowGetRequest(scope, new DurableFlowInstanceId("metadata-flow"))));
        Assert.Contains(DurableProblemCodes.StoreIdentityMismatch, storeMismatch.Message, StringComparison.Ordinal);

        metadata.Parameters["store_id"].Value = status.StoreId;
        metadata.Parameters["runtime_epoch"].Value = Guid.NewGuid();
        Assert.Equal(1, await metadata.ExecuteNonQueryAsync());
        var epochMismatch = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await client.ListAsync(new DurableFlowListRequest(scope, pageSize: 10)));
        Assert.Contains(DurableProblemCodes.RecoveryEpochRequired, epochMismatch.Message, StringComparison.Ordinal);

        metadata.Parameters["runtime_epoch"].Value = epoch;
        Assert.Equal(1, await metadata.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task FlowStore_FailsClosedWhenPersistedCommandIdentitiesCross()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "tests", "slice4-crossed-identities");
        var status = await schema.GetStatusAsync();
        var codec = new PostgreSqlOpaqueTestCodec("tests.flow.context", "v1");
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
        var scope = new DurableScopeId("slice4-crossed-identities");
        var first = new DurableFlowInstanceId("crossed-first");
        var second = new DurableFlowInstanceId("crossed-second");
        var third = new DurableFlowInstanceId("crossed-third");

        foreach (var (instance, commandId, key, marker) in new[]
                 {
                     (first, "crossed-start-first", "crossed-start-key-first", (byte)1),
                     (second, "crossed-start-second", "crossed-start-key-second", (byte)2),
                     (third, "crossed-start-third", "crossed-start-key-third", (byte)3),
                 })
        {
            Assert.True((await client.StartAsync(new DurableFlowStartRequest(
                scope,
                new DurableCommandId(commandId),
                key,
                instance,
                registration.FlowId,
                registration.FlowVersion,
                codec.EncodeObject(new byte[] { marker })))).IsSuccess);
        }

        var crossedStart = new DurableFlowStartRequest(
            scope,
            new DurableCommandId("crossed-start-first"),
            "crossed-start-key-second",
            third,
            registration.FlowId,
            registration.FlowVersion,
            codec.EncodeObject(new byte[] { 3 }));
        await using (var alignStartFingerprints = database.DataSource.CreateCommand(
            """
            UPDATE appsurface_durable.flow_command
            SET fingerprint_schema = @fingerprint_schema, fingerprint_sha256 = @fingerprint_sha256
            WHERE scope_id = @scope_id
              AND command_id IN ('crossed-start-first', 'crossed-start-second');
            """))
        {
            alignStartFingerprints.Parameters.AddWithValue("fingerprint_schema", crossedStart.Fingerprint.SchemaId);
            alignStartFingerprints.Parameters.AddWithValue("fingerprint_sha256", crossedStart.Fingerprint.Sha256);
            alignStartFingerprints.Parameters.AddWithValue("scope_id", scope.Value);
            Assert.Equal(2, await alignStartFingerprints.ExecuteNonQueryAsync());
        }
        var crossedStartResult = await client.StartAsync(crossedStart);
        Assert.Equal(DurableProblemCodes.FlowCommandConflict, crossedStartResult.Problem!.Code);

        foreach (var instance in new[] { first, second })
        {
            _ = await processor.TryProcessAsync(
                Assert.Single(await processor.DiscoverAsync(), candidate => candidate.InstanceId == instance),
                $"crossed-identities-wait-{instance.Value}");
        }

        Assert.Equal(DurableFlowCommandOutcome.Accepted, (await client.RaiseEventAsync(new DurableFlowEventRequest(
            scope,
            new DurableCommandId("crossed-event-command-first"),
            new DurableFlowEventId("crossed-event-id-first"),
            first,
            "approved"))).Value!.Outcome);
        Assert.Equal(DurableFlowCommandOutcome.Accepted, (await client.RaiseEventAsync(new DurableFlowEventRequest(
            scope,
            new DurableCommandId("crossed-event-command-second"),
            new DurableFlowEventId("crossed-event-id-second"),
            second,
            "approved"))).Value!.Outcome);

        var crossedEvent = new DurableFlowEventRequest(
            scope,
            new DurableCommandId("crossed-event-command-first"),
            new DurableFlowEventId("crossed-event-id-second"),
            third,
            "approved");
        await using (var alignEventFingerprints = database.DataSource.CreateCommand(
            """
            UPDATE appsurface_durable.flow_command
            SET fingerprint_schema = @fingerprint_schema, fingerprint_sha256 = @fingerprint_sha256
            WHERE scope_id = @scope_id
              AND (command_id = 'crossed-event-command-first' OR event_id = 'crossed-event-id-second');
            """))
        {
            alignEventFingerprints.Parameters.AddWithValue("fingerprint_schema", crossedEvent.Fingerprint.SchemaId);
            alignEventFingerprints.Parameters.AddWithValue("fingerprint_sha256", crossedEvent.Fingerprint.Sha256);
            alignEventFingerprints.Parameters.AddWithValue("scope_id", scope.Value);
            Assert.Equal(2, await alignEventFingerprints.ExecuteNonQueryAsync());
        }
        var crossedEventResult = await client.RaiseEventAsync(crossedEvent);
        Assert.Equal(DurableProblemCodes.FlowCommandConflict, crossedEventResult.Problem!.Code);

        var concurrentStart = new DurableFlowStartRequest(
            scope,
            new DurableCommandId("crossed-concurrent-start"),
            "crossed-concurrent-key",
            new DurableFlowInstanceId("crossed-concurrent"),
            registration.FlowId,
            registration.FlowVersion,
            codec.EncodeObject(new byte[] { 4 }));
        var concurrentResults = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            client.StartAsync(concurrentStart).AsTask()));
        Assert.Contains(concurrentResults, result => result.Value?.Outcome == DurableFlowCommandOutcome.Accepted);
        Assert.All(concurrentResults, result => Assert.True(
            result.IsSuccess && result.Value!.Outcome is DurableFlowCommandOutcome.Accepted or DurableFlowCommandOutcome.Duplicate));

        var cancellationInstance = new DurableFlowInstanceId("crossed-concurrent-cancel");
        Assert.True((await client.StartAsync(new DurableFlowStartRequest(
            scope,
            new DurableCommandId("crossed-concurrent-cancel-start"),
            "crossed-concurrent-cancel-key",
            cancellationInstance,
            registration.FlowId,
            registration.FlowVersion,
            codec.EncodeObject(new byte[] { 5 })))).IsSuccess);
        var waiting = await processor.TryProcessAsync(
            Assert.Single(await processor.DiscoverAsync(), candidate => candidate.InstanceId == cancellationInstance),
            "crossed-concurrent-cancel-wait-worker");
        var cancellation = new DurableFlowCancelRequest(
            scope,
            new DurableCommandId("crossed-concurrent-cancel-command"),
            cancellationInstance,
            "tests",
            "consumer-requested",
            waiting.Revision);
        var cancellationResults = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            client.CancelAsync(cancellation).AsTask()));
        Assert.Contains(cancellationResults, result => result.Value?.Outcome == DurableFlowCommandOutcome.Accepted);
        Assert.All(cancellationResults, result => Assert.True(
            result.IsSuccess && result.Value!.Outcome is DurableFlowCommandOutcome.Accepted or DurableFlowCommandOutcome.Duplicate));
    }

    [Fact]
    public async Task Flow_IdentityAndTimerGuards_RejectChangedRetriesAndStaleTimerRows()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "tests", "slice4-identity-coverage");
        var status = await schema.GetStatusAsync();
        var codec = new PostgreSqlOpaqueTestCodec("tests.flow.context", "v1");
        var payloads = new DurablePayloadCodecRegistry([codec]);
        var work = new DurableWorkRegistry([]);
        var waitingRegistration = new WaitingTestFlowRegistration(codec);
        var timerRegistration = new TimerTestFlowRegistration(codec);
        var flows = new DurableFlowRegistry([waitingRegistration, timerRegistration], work, payloads);
        var options = new PostgreSqlDurableWorkOptions(epoch, status.StoreId);
        var client = new PostgreSqlDurableFlowClient(database.DataSource, flows, payloads, options);
        var processor = new PostgreSqlDurableFlowProcessor(
            database.DataSource,
            database.DataSource,
            flows,
            work,
            payloads,
            options);
        var scope = new DurableScopeId("slice4-identity-coverage");
        var firstInstance = new DurableFlowInstanceId("identity-first");
        Assert.True((await client.StartAsync(new DurableFlowStartRequest(
            scope,
            new DurableCommandId("identity-first-command"),
            "identity-shared-key",
            firstInstance,
            waitingRegistration.FlowId,
            waitingRegistration.FlowVersion,
            codec.EncodeObject(new byte[] { 1 })))).IsSuccess);
        var idempotencyConflict = await client.StartAsync(new DurableFlowStartRequest(
            scope,
            new DurableCommandId("identity-second-command"),
            "identity-shared-key",
            new DurableFlowInstanceId("identity-second"),
            waitingRegistration.FlowId,
            waitingRegistration.FlowVersion,
            codec.EncodeObject(new byte[] { 2 })));
        Assert.Equal(DurableProblemCodes.FlowStartConflict, idempotencyConflict.Problem!.Code);

        var waited = await processor.TryProcessAsync(
            Assert.Single(
                await processor.DiscoverAsync(),
                candidate => candidate.InstanceId == firstInstance),
            "identity-wait-worker");
        var acceptedEvent = new DurableFlowEventRequest(
            scope,
            new DurableCommandId("identity-event-command"),
            new DurableFlowEventId("identity-event-id"),
            firstInstance,
            "approved",
            expectedRevision: waited.Revision);
        Assert.Equal(DurableFlowCommandOutcome.Accepted, (await client.RaiseEventAsync(acceptedEvent)).Value!.Outcome);
        var changedEvent = await client.RaiseEventAsync(new DurableFlowEventRequest(
            scope,
            acceptedEvent.CommandId,
            new DurableFlowEventId("identity-event-id-changed"),
            firstInstance,
            "approved",
            expectedRevision: waited.Revision));
        Assert.Equal(DurableProblemCodes.FlowCommandConflict, changedEvent.Problem!.Code);

        var timerCandidate = new PostgreSqlFlowDispatchCandidate(
            Guid.NewGuid(),
            scope,
            PostgreSqlFlowDispatchKind.Timer,
            firstInstance,
            null,
            DateTimeOffset.UtcNow,
            1,
            0);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await processor.TryProcessAsync(timerCandidate, "timer-without-id"));

        var timerInstance = new DurableFlowInstanceId("timer-row-missing");
        Assert.True((await client.StartAsync(new DurableFlowStartRequest(
            scope,
            new DurableCommandId("timer-row-missing-start"),
            "timer-row-missing-key",
            timerInstance,
            timerRegistration.FlowId,
            timerRegistration.FlowVersion,
            codec.EncodeObject(new byte[] { 3 })))).IsSuccess);
        var timerWait = await processor.TryProcessAsync(
            Assert.Single(
                await processor.DiscoverAsync(),
                candidate => candidate.InstanceId == timerInstance),
            "timer-row-missing-register");
        await ForceTimerDueAsync(database.DataSource, scope, timerInstance);
        var staleTimer = Assert.Single(
            await processor.DiscoverAsync(),
            candidate => candidate.InstanceId == timerInstance && candidate.Kind == PostgreSqlFlowDispatchKind.Timer);
        await using (var deleteTimer = database.DataSource.CreateCommand(
            """
            DELETE FROM appsurface_durable.flow_dispatch
            WHERE timer_id = @timer_id;

            DELETE FROM appsurface_durable.flow_timer
            WHERE timer_id = @timer_id;
            """))
        {
            deleteTimer.Parameters.AddWithValue("timer_id", staleTimer.TimerId!.Value);
            Assert.Equal(2, await deleteTimer.ExecuteNonQueryAsync());
        }
        Assert.Equal(
            PostgreSqlFlowProcessingOutcome.RaceLost,
            (await processor.TryProcessAsync(staleTimer, "timer-row-missing-resolve")).Outcome);
        Assert.Equal(DurableFlowState.WaitingForEvent, timerWait.State);

        var missingParentTimer = new PostgreSqlFlowDispatchCandidate(
            Guid.NewGuid(),
            scope,
            PostgreSqlFlowDispatchKind.Timer,
            new DurableFlowInstanceId("timer-parent-missing"),
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            ExpectedRevision: 1,
            Priority: 0);
        var missingParentResult = await processor.TryProcessAsync(
            missingParentTimer,
            "timer-parent-missing-resolve");
        Assert.Equal(PostgreSqlFlowProcessingOutcome.RaceLost, missingParentResult.Outcome);
        Assert.Null(missingParentResult.State);
    }

    [Fact]
    public async Task FlowClaim_RecoversExpiredLeaseAndReportsAConcurrentSuspensionAsStale()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "tests", "slice4-claim-recovery");
        var status = await schema.GetStatusAsync();
        var codec = new PostgreSqlOpaqueTestCodec("tests.flow.context", "v1");
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
        var store = new PostgreSqlDurableFlowStore(database.DataSource, options);
        var scope = new DurableScopeId("slice4-claim-recovery");
        var instance = new DurableFlowInstanceId("expired-evaluation-lease");
        Assert.True((await client.StartAsync(new DurableFlowStartRequest(
            scope,
            new DurableCommandId("expired-evaluation-lease-start"),
            "expired-evaluation-lease-key",
            instance,
            registration.FlowId,
            registration.FlowVersion,
            codec.EncodeObject(new byte[] { 1 })))).IsSuccess);
        var candidate = Assert.Single(await processor.DiscoverAsync());

        await using (var expireLease = database.DataSource.CreateCommand(
            """
            UPDATE appsurface_durable.flow_instance
            SET state = 'evaluating', lease_owner = 'crashed-worker',
                lease_started_at = clock_timestamp() - interval '5 minutes',
                lease_expires_at = clock_timestamp() - interval '1 minute'
            WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id;
            """))
        {
            expireLease.Parameters.AddWithValue("scope_id", scope.Value);
            expireLease.Parameters.AddWithValue("flow_instance_id", instance.Value);
            Assert.Equal(1, await expireLease.ExecuteNonQueryAsync());
        }

        var claim = await store.TryClaimFlowAsync(
            candidate,
            "recovery-worker",
            TimeSpan.FromMinutes(1),
            default);
        Assert.NotNull(claim);
        Assert.Equal("recovery-worker", claim!.LeaseOwner);
        Assert.Equal(candidate.ExpectedRevision + 1, claim.Revision);

        await using (var loseSuspensionRace = database.DataSource.CreateCommand(
            """
            UPDATE appsurface_durable.flow_instance
            SET state = 'ready', lease_owner = NULL, lease_started_at = NULL, lease_expires_at = NULL
            WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id;

            UPDATE appsurface_durable.flow_dispatch
            SET expected_revision = @stale_revision
            WHERE dispatch_id = @dispatch_id;
            """))
        {
            loseSuspensionRace.Parameters.AddWithValue("scope_id", scope.Value);
            loseSuspensionRace.Parameters.AddWithValue("flow_instance_id", instance.Value);
            loseSuspensionRace.Parameters.AddWithValue("dispatch_id", candidate.DispatchId);
            loseSuspensionRace.Parameters.AddWithValue("stale_revision", claim.Revision + 1);
            Assert.Equal(2, await loseSuspensionRace.ExecuteNonQueryAsync());
        }

        var stale = await store.SuspendClaimAsync(claim, "flow.evaluation_failed", default);
        Assert.Equal(PostgreSqlFlowProcessingOutcome.Stale, stale.Outcome);
        Assert.Equal(claim.Revision, stale.Revision);

        await using (var removeDispatch = database.DataSource.CreateCommand(
            "DELETE FROM appsurface_durable.flow_dispatch WHERE dispatch_id = @dispatch_id;"))
        {
            removeDispatch.Parameters.AddWithValue("dispatch_id", candidate.DispatchId);
            Assert.Equal(1, await removeDispatch.ExecuteNonQueryAsync());
        }
        Assert.Null(await store.TryClaimFlowAsync(
            candidate,
            "missing-dispatch-worker",
            TimeSpan.FromMinutes(1),
            default));
    }

    [Fact]
    public async Task FlowClaim_ReturnsNullWhenScopeIsInactiveOrRemoved()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "tests", "slice4-flow-claim-disabled-scope");
        var status = await schema.GetStatusAsync();
        var codec = new PostgreSqlOpaqueTestCodec("tests.flow.context", "v1");
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
        var store = new PostgreSqlDurableFlowStore(database.DataSource, options);
        var scope = new DurableScopeId("slice4-flow-claim-disabled-scope");
        var instance = new DurableFlowInstanceId("disabled-scope-flow");

        Assert.True((await client.StartAsync(new DurableFlowStartRequest(
            scope,
            new DurableCommandId("disabled-scope-start"),
            "disabled-scope-start-key",
            instance,
            registration.FlowId,
            registration.FlowVersion,
            codec.EncodeObject(new byte[] { 1 })))).IsSuccess);
        var candidate = Assert.Single(await processor.DiscoverAsync());

        var workStore = new PostgreSqlDurableWorkStore(database.DataSource, epoch);
        var disabled = await workStore.DisableScopeAsync(scope, "tests", "disable", expectedGeneration: 1);
        Assert.Equal(PostgreSqlScopeMutationOutcome.Applied, disabled.Outcome);

        Assert.Null(await store.TryClaimFlowAsync(
            candidate,
            "disabled-scope-claim-worker",
            TimeSpan.FromMinutes(1),
            default));

        var removedScope = new DurableScopeId("slice4-flow-claim-removed-scope");
        var removedInstance = new DurableFlowInstanceId("removed-scope-flow");
        Assert.True((await client.StartAsync(new DurableFlowStartRequest(
            removedScope,
            new DurableCommandId("removed-scope-start"),
            "removed-scope-start-key",
            removedInstance,
            registration.FlowId,
            registration.FlowVersion,
            codec.EncodeObject(new byte[] { 2 })))).IsSuccess);
        var removedCandidate = Assert.Single(
            await processor.DiscoverAsync(),
            item => item.InstanceId == removedInstance);

        await using (var removeScope = database.DataSource.CreateCommand(
            """
            DELETE FROM appsurface_durable.flow_dispatch WHERE scope_id = @scope_id;
            DELETE FROM appsurface_durable.flow_history WHERE scope_id = @scope_id;
            DELETE FROM appsurface_durable.flow_command WHERE scope_id = @scope_id;
            DELETE FROM appsurface_durable.flow_instance WHERE scope_id = @scope_id;
            DELETE FROM appsurface_durable.scope WHERE scope_id = @scope_id;
            """))
        {
            removeScope.Parameters.AddWithValue("scope_id", removedScope.Value);
            Assert.Equal(5, await removeScope.ExecuteNonQueryAsync());
        }

        Assert.Null(await store.TryClaimFlowAsync(
            removedCandidate,
            "removed-scope-claim-worker",
            TimeSpan.FromMinutes(1),
            default));
    }

    [Fact]
    public async Task FlowClaim_RejectsPersistedContextWithAnInvalidHash()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "tests", "slice4-context-hash");
        var status = await schema.GetStatusAsync();
        var codec = new PostgreSqlOpaqueTestCodec("tests.flow.context", "v1");
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
        var store = new PostgreSqlDurableFlowStore(database.DataSource, options);
        var scope = new DurableScopeId("slice4-context-hash");
        var instance = new DurableFlowInstanceId("invalid-context-hash");
        Assert.True((await client.StartAsync(new DurableFlowStartRequest(
            scope,
            new DurableCommandId("invalid-context-hash-start"),
            "invalid-context-hash-key",
            instance,
            registration.FlowId,
            registration.FlowVersion,
            codec.EncodeObject(new byte[] { 1, 2, 3 })))).IsSuccess);
        var candidate = Assert.Single(await processor.DiscoverAsync());

        await using (var tamperContext = database.DataSource.CreateCommand(
            """
            UPDATE appsurface_durable.flow_instance
            SET context_payload = '\\xDEADBEEF'::bytea
            WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id;
            """))
        {
            tamperContext.Parameters.AddWithValue("scope_id", scope.Value);
            tamperContext.Parameters.AddWithValue("flow_instance_id", instance.Value);
            Assert.Equal(1, await tamperContext.ExecuteNonQueryAsync());
        }

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.TryClaimFlowAsync(candidate, "context-hash-worker", TimeSpan.FromMinutes(1), default));
        Assert.Contains("Flow context failed persisted SHA-256 verification", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FlowClaim_RejectsPersistedEventPayloadWithAnInvalidHash()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "tests", "slice4-event-hash");
        var status = await schema.GetStatusAsync();
        var contextCodec = new PostgreSqlOpaqueTestCodec("tests.flow.context", "v1");
        var eventCodec = new PostgreSqlOpaqueTestCodec("tests.flow.event", "v1");
        var payloads = new DurablePayloadCodecRegistry([contextCodec, eventCodec]);
        var work = new DurableWorkRegistry([]);
        var registration = new RequiredPayloadWaitingRegistration(contextCodec);
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
        var store = new PostgreSqlDurableFlowStore(database.DataSource, options);
        var scope = new DurableScopeId("slice4-event-hash");

        await using (var findConstraint = database.DataSource.CreateCommand(
            """
            SELECT format('%I', conname)
            FROM pg_catalog.pg_constraint
            WHERE conrelid = 'appsurface_durable.flow_instance'::regclass
              AND contype = 'c'
              AND pg_get_constraintdef(oid) LIKE '%resume_event_payload%';
            """))
        {
            var resumeEventConstraint = (string)(await findConstraint.ExecuteScalarAsync())!;
            await using (var dropConstraint = database.DataSource.CreateCommand(
                $"ALTER TABLE appsurface_durable.flow_instance DROP CONSTRAINT {resumeEventConstraint};"))
            {
                await dropConstraint.ExecuteNonQueryAsync();
            }
        }

        async Task AssertInvalidEventHashAsync(
            string suffix,
            string updateSql,
            string expectedMessage,
            string workerId)
        {
            var instance = new DurableFlowInstanceId($"invalid-event-hash-{suffix}");
            Assert.True((await client.StartAsync(new DurableFlowStartRequest(
                scope,
                new DurableCommandId($"invalid-event-hash-{suffix}-start"),
                $"invalid-event-hash-{suffix}-key",
                instance,
                registration.FlowId,
                registration.FlowVersion,
                contextCodec.EncodeObject(new byte[] { 1 })))).IsSuccess);

            var waiting = await processor.TryProcessAsync(
                Assert.Single(
                    await processor.DiscoverAsync(),
                    item => item.InstanceId == instance),
                "event-hash-wait-worker");
            Assert.Equal(DurableFlowState.WaitingForEvent, waiting.State);

            Assert.Equal(DurableFlowCommandOutcome.Accepted, (await client.RaiseEventAsync(new DurableFlowEventRequest(
                scope,
                new DurableCommandId($"invalid-event-hash-{suffix}-event"),
                new DurableFlowEventId($"invalid-event-hash-{suffix}-id"),
                instance,
                "approved",
                eventCodec.EncodeObject(new byte[] { 9 }),
                waiting.Revision))).Value!.Outcome);

            var candidate = Assert.Single(
                await processor.DiscoverAsync(),
                item => item.InstanceId == instance);

            await using (var tamperEvent = database.DataSource.CreateCommand(updateSql))
            {
                tamperEvent.Parameters.AddWithValue("scope_id", scope.Value);
                tamperEvent.Parameters.AddWithValue("flow_instance_id", instance.Value);
                Assert.Equal(1, await tamperEvent.ExecuteNonQueryAsync());
            }

            var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await store.TryClaimFlowAsync(candidate, workerId, TimeSpan.FromMinutes(1), default));
            Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
        }

        await AssertInvalidEventHashAsync(
            "payload-null",
            """
            UPDATE appsurface_durable.flow_instance
            SET resume_event_payload = NULL
            WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id;
            """,
            "Flow event has a hash without persisted payload bytes.",
            "event-hash-null-payload-worker");

        await AssertInvalidEventHashAsync(
            "hash-null",
            """
            UPDATE appsurface_durable.flow_instance
            SET resume_event_sha256 = NULL
            WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id;
            """,
            "Flow event is missing its persisted SHA-256.",
            "event-hash-null-hash-worker");

        await AssertInvalidEventHashAsync(
            "mismatch",
            """
            UPDATE appsurface_durable.flow_instance
            SET resume_event_payload = '\\xDEADBEEF'::bytea
            WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id;
            """,
            "Flow event failed persisted SHA-256 verification.",
            "event-hash-mismatch-worker");
    }

    [Fact]
    public async Task FlowClaim_RejectsPersistedActivityResultWithAnInvalidHash()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "tests", "slice4-activity-result-hash");
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
        var processor = new PostgreSqlDurableFlowProcessor(
            database.DataSource,
            database.DataSource,
            flows,
            work,
            payloads,
            options);
        var workStore = new PostgreSqlDurableWorkStore(database.DataSource, epoch);
        var store = new PostgreSqlDurableFlowStore(database.DataSource, options);
        var scope = new DurableScopeId("slice4-activity-result-hash");
        var instance = new DurableFlowInstanceId("activity-result-hash-flow");

        Assert.True((await client.StartAsync(new DurableFlowStartRequest(
            scope,
            new DurableCommandId("activity-result-hash-start"),
            "activity-result-hash-key",
            instance,
            registration.FlowId,
            registration.FlowVersion,
            contextCodec.EncodeObject(new byte[] { 1 })))).IsSuccess);
        var activity = await processor.TryProcessAsync(
            Assert.Single(await processor.DiscoverAsync()),
            "activity-result-hash-flow-worker");
        var candidate = Assert.Single(
            await workStore.DiscoverAsync(10),
            item => item.WorkId == activity.ChildWorkId);
        var claim = await workStore.TryClaimAsync(candidate, "activity-result-hash-work-worker");
        var permit = await workStore.TryAcquireEffectPermitAsync(claim!);
        Assert.Equal(
            DurableWorkState.Succeeded,
            (await workStore.RecordCompletionAsync(
                permit!.Claim,
                new PostgreSqlWorkCompletion(
                    PostgreSqlWorkCompletionKind.Succeeded,
                    "activity.completed",
                    "{}",
                    resultCodec.EncodeObject(new byte[] { 2 })))).State);

        await using (var corrupt = database.DataSource.CreateCommand(
            """
            UPDATE appsurface_durable.flow_instance
            SET activity_result_payload = '\\xDEADBEEF'::bytea
            WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id;
            """))
        {
            corrupt.Parameters.AddWithValue("scope_id", scope.Value);
            corrupt.Parameters.AddWithValue("flow_instance_id", instance.Value);
            Assert.Equal(1, await corrupt.ExecuteNonQueryAsync());
        }

        var ready = Assert.Single(
            await processor.DiscoverAsync(),
            item => item.InstanceId == instance && item.Kind == PostgreSqlFlowDispatchKind.Flow);
        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.TryClaimFlowAsync(ready, "activity-result-hash-claim-worker", TimeSpan.FromMinutes(1), default));
        Assert.Contains("Flow activity result failed persisted SHA-256 verification", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FlowProcessor_SuspendsAnActivityDecisionWithAnInvalidWorkPayload()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "tests", "slice4-invalid-activity-payload");
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
        var registration = new InvalidActivityPayloadFlowRegistration(contextCodec, workRegistration);
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
        var scope = new DurableScopeId("slice4-invalid-activity-payload");
        var instance = new DurableFlowInstanceId("invalid-activity-payload");
        Assert.True((await client.StartAsync(new DurableFlowStartRequest(
            scope,
            new DurableCommandId("invalid-activity-payload-start"),
            "invalid-activity-payload-key",
            instance,
            registration.FlowId,
            registration.FlowVersion,
            contextCodec.EncodeObject(new byte[] { 1 })))).IsSuccess);

        var result = await processor.TryProcessAsync(
            Assert.Single(await processor.DiscoverAsync()),
            "invalid-activity-payload-worker");
        Assert.Equal(PostgreSqlFlowProcessingOutcome.Suspended, result.Outcome);
        Assert.Equal(DurableFlowState.Suspended, result.State);
        Assert.Equal("flow.evaluation_invalid", result.ProblemCode);
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

        var missingResultInstance = new DurableFlowInstanceId("activity-flow-missing-result");
        Assert.True((await client.StartAsync(new DurableFlowStartRequest(
            scope,
            new DurableCommandId("activity-missing-result-start"),
            "activity-missing-result-start-key",
            missingResultInstance,
            registration.FlowId,
            registration.FlowVersion,
            contextCodec.EncodeObject(new byte[] { 3 })))).IsSuccess);
        var missingResultActivity = await flowProcessor.TryProcessAsync(
            Assert.Single(await flowProcessor.DiscoverAsync()),
            "flow-activity-missing-result-worker");
        var missingResultCandidate = Assert.Single(
            await workStore.DiscoverAsync(10),
            candidate => candidate.WorkId == missingResultActivity.ChildWorkId);
        var missingResultClaim = await workStore.TryClaimAsync(
            missingResultCandidate,
            "work-activity-missing-result-worker");
        var missingResultPermit = await workStore.TryAcquireEffectPermitAsync(missingResultClaim!);
        var missingResultCompletion = await workStore.RecordCompletionAsync(
            missingResultPermit!.Claim,
            new PostgreSqlWorkCompletion(
                PostgreSqlWorkCompletionKind.Succeeded,
                "activity.completed-without-result",
                "{}"));
        Assert.Equal(DurableWorkState.Succeeded, missingResultCompletion.State);
        Assert.Equal(
            DurableFlowState.Suspended,
            (await client.GetAsync(new DurableFlowGetRequest(scope, missingResultInstance))).Value!.State);
        var missingResultSnapshot = (await client.GetAsync(
            new DurableFlowGetRequest(scope, missingResultInstance))).Value!;
        var invalidActivityRelease = await client.ReleaseSuspensionAsync(new DurableFlowReleaseRequest(
            scope,
            new DurableCommandId("activity-missing-result-release"),
            missingResultInstance,
            "operator",
            "child-terminal",
            missingResultSnapshot.Revision));
        Assert.Equal(DurableProblemCodes.FlowReleaseStateMismatch, invalidActivityRelease.Problem!.Code);

        var failedInstance = new DurableFlowInstanceId("activity-flow-failed");
        Assert.True((await client.StartAsync(new DurableFlowStartRequest(
            scope,
            new DurableCommandId("activity-failed-start"),
            "activity-failed-start-key",
            failedInstance,
            registration.FlowId,
            registration.FlowVersion,
            contextCodec.EncodeObject(new byte[] { 4 })))).IsSuccess);
        var failedActivity = await flowProcessor.TryProcessAsync(
            Assert.Single(await flowProcessor.DiscoverAsync()),
            "flow-activity-failed-worker");
        var failedCandidate = Assert.Single(
            await workStore.DiscoverAsync(10),
            candidate => candidate.WorkId == failedActivity.ChildWorkId);
        var failedClaim = await workStore.TryClaimAsync(failedCandidate, "work-activity-failed-worker");
        var failedPermit = await workStore.TryAcquireEffectPermitAsync(failedClaim!);
        var failedCompletion = await workStore.RecordCompletionAsync(
            failedPermit!.Claim,
            new PostgreSqlWorkCompletion(
                PostgreSqlWorkCompletionKind.FailedTerminal,
                "activity.failed",
                "{}"));
        Assert.Equal(DurableWorkState.Suspended, failedCompletion.State);
        Assert.Equal(
            DurableFlowState.Suspended,
            (await client.GetAsync(new DurableFlowGetRequest(scope, failedInstance))).Value!.State);

        var cancelPendingInstance = new DurableFlowInstanceId("activity-flow-cancel-pending");
        Assert.True((await client.StartAsync(new DurableFlowStartRequest(
            scope,
            new DurableCommandId("activity-cancel-pending-start"),
            "activity-cancel-pending-start-key",
            cancelPendingInstance,
            registration.FlowId,
            registration.FlowVersion,
            contextCodec.EncodeObject(new byte[] { 5 })))).IsSuccess);
        var cancelPendingActivity = await flowProcessor.TryProcessAsync(
            Assert.Single(await flowProcessor.DiscoverAsync()),
            "flow-activity-cancel-pending-worker");
        var cancelPendingCandidate = Assert.Single(
            await workStore.DiscoverAsync(10),
            candidate => candidate.WorkId == cancelPendingActivity.ChildWorkId);
        var cancelPendingClaim = await workStore.TryClaimAsync(
            cancelPendingCandidate,
            "work-activity-cancel-pending-worker");
        var cancelPendingPermit = await workStore.TryAcquireEffectPermitAsync(cancelPendingClaim!);
        await using (var markCancelPending = database.DataSource.CreateCommand(
            """
            UPDATE appsurface_durable.flow_instance
            SET state = 'cancel_pending', cancellation_requested_at = clock_timestamp()
            WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id;
            """))
        {
            markCancelPending.Parameters.AddWithValue("scope_id", scope.Value);
            markCancelPending.Parameters.AddWithValue("flow_instance_id", cancelPendingInstance.Value);
            Assert.Equal(1, await markCancelPending.ExecuteNonQueryAsync());
        }
        var cancelPendingCompletion = await workStore.RecordCompletionAsync(
            cancelPendingPermit!.Claim,
            new PostgreSqlWorkCompletion(
                PostgreSqlWorkCompletionKind.Succeeded,
                "activity.completed-after-cancel",
                "{}",
                resultCodec.EncodeObject(new byte[] { 8 })));
        Assert.Equal(DurableWorkState.Succeeded, cancelPendingCompletion.State);
        Assert.Equal(
            DurableFlowState.Canceled,
            (await client.GetAsync(new DurableFlowGetRequest(scope, cancelPendingInstance))).Value!.State);

        var (cancelPendingFailureInstance, cancelPendingFailurePermit) =
            await StartPermittedActivityAsync("cancel-pending-failure", 10);
        var cancelPendingFailureSnapshot = (await client.GetAsync(
            new DurableFlowGetRequest(scope, cancelPendingFailureInstance))).Value!;
        var cancelPendingFailure = await client.CancelAsync(new DurableFlowCancelRequest(
            scope,
            new DurableCommandId("activity-cancel-pending-failure"),
            cancelPendingFailureInstance,
            "tests",
            "consumer-requested",
            cancelPendingFailureSnapshot.Revision));
        Assert.Equal(DurableFlowState.CancelPending, cancelPendingFailure.Value!.State);
        await workStore.RecordCompletionAsync(
            cancelPendingFailurePermit.Claim,
            new PostgreSqlWorkCompletion(
                PostgreSqlWorkCompletionKind.FailedTerminal,
                "activity.failed-after-cancel",
                "{}"));
        Assert.Equal(
            DurableFlowState.Suspended,
            (await client.GetAsync(new DurableFlowGetRequest(scope, cancelPendingFailureInstance))).Value!.State);
        await using (var suspendedFrom = database.DataSource.CreateCommand(
            """
            SELECT suspended_from_state
            FROM appsurface_durable.flow_instance
            WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id;
            """))
        {
            suspendedFrom.Parameters.AddWithValue("scope_id", scope.Value);
            suspendedFrom.Parameters.AddWithValue("flow_instance_id", cancelPendingFailureInstance.Value);
            Assert.Equal("cancel_pending", (string?)await suspendedFrom.ExecuteScalarAsync());
        }

        var (suspendedParentInstance, suspendedParentPermit) =
            await StartPermittedActivityAsync("already-suspended-parent", 11);
        await using (var suspendParentAndWait = database.DataSource.CreateCommand(
            """
            UPDATE appsurface_durable.flow_instance
            SET state = 'suspended', suspended_from_state = 'waiting_activity',
                suspension_descriptor = '{"code":"operator-review"}'::jsonb
            WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id;

            UPDATE appsurface_durable.flow_wait
            SET state = 'suspended', suspension_descriptor = '{"code":"operator-review"}'::jsonb
            WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id AND kind = 'activity';

            UPDATE appsurface_durable.flow_dispatch
            SET state = 'suspended'
            WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id AND kind = 'flow';
            """))
        {
            suspendParentAndWait.Parameters.AddWithValue("scope_id", scope.Value);
            suspendParentAndWait.Parameters.AddWithValue("flow_instance_id", suspendedParentInstance.Value);
            Assert.Equal(3, await suspendParentAndWait.ExecuteNonQueryAsync());
        }
        var suspendedParentCompletion = await workStore.RecordCompletionAsync(
            suspendedParentPermit.Claim,
            new PostgreSqlWorkCompletion(
                PostgreSqlWorkCompletionKind.Succeeded,
                "activity.completed-after-parent-suspension",
                "{}",
                resultCodec.EncodeObject(new byte[] { 11 })));
        Assert.Equal(DurableWorkState.Succeeded, suspendedParentCompletion.State);
        Assert.Equal(
            DurableFlowState.Suspended,
            (await client.GetAsync(new DurableFlowGetRequest(scope, suspendedParentInstance))).Value!.State);

        var effectPermittedInstance = new DurableFlowInstanceId("activity-flow-effect-permitted-cancel");
        Assert.True((await client.StartAsync(new DurableFlowStartRequest(
            scope,
            new DurableCommandId("activity-effect-permitted-cancel-start"),
            "activity-effect-permitted-cancel-key",
            effectPermittedInstance,
            registration.FlowId,
            registration.FlowVersion,
            contextCodec.EncodeObject(new byte[] { 9 })))).IsSuccess);
        var effectPermittedActivity = await flowProcessor.TryProcessAsync(
            Assert.Single(await flowProcessor.DiscoverAsync()),
            "flow-activity-effect-permitted-cancel-worker");
        var effectPermittedCandidate = Assert.Single(
            await workStore.DiscoverAsync(10),
            candidate => candidate.WorkId == effectPermittedActivity.ChildWorkId);
        var effectPermittedClaim = await workStore.TryClaimAsync(
            effectPermittedCandidate,
            "work-activity-effect-permitted-cancel-worker");
        var effectPermitted = await workStore.TryAcquireEffectPermitAsync(effectPermittedClaim!);
        Assert.NotNull(effectPermitted);
        var pendingCancellation = await client.CancelAsync(new DurableFlowCancelRequest(
            scope,
            new DurableCommandId("activity-effect-permitted-cancel"),
            effectPermittedInstance,
            "tests",
            "consumer-requested",
            effectPermittedActivity.Revision));
        Assert.Equal(DurableFlowState.CancelPending, pendingCancellation.Value!.State);

        var releaseInstance = new DurableFlowInstanceId("activity-flow-release");
        Assert.True((await client.StartAsync(new DurableFlowStartRequest(
            scope,
            new DurableCommandId("activity-release-start"),
            "activity-release-start-key",
            releaseInstance,
            registration.FlowId,
            registration.FlowVersion,
            contextCodec.EncodeObject(new byte[] { 6 })))).IsSuccess);
        var releaseActivity = await flowProcessor.TryProcessAsync(
            Assert.Single(await flowProcessor.DiscoverAsync()),
            "flow-activity-release-worker");
        await using (var suspendParent = database.DataSource.CreateCommand(
            """
            UPDATE appsurface_durable.flow_instance
            SET state = 'suspended', suspended_from_state = 'waiting_activity',
                suspension_descriptor = '{"code":"operator-review"}'::jsonb
            WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id;
            """))
        {
            suspendParent.Parameters.AddWithValue("scope_id", scope.Value);
            suspendParent.Parameters.AddWithValue("flow_instance_id", releaseInstance.Value);
            Assert.Equal(1, await suspendParent.ExecuteNonQueryAsync());
        }
        var releasedActivity = await client.ReleaseSuspensionAsync(new DurableFlowReleaseRequest(
            scope,
            new DurableCommandId("activity-release-command"),
            releaseInstance,
            "operator",
            "child-reviewed",
            releaseActivity.Revision));
        Assert.Equal(DurableFlowCommandOutcome.Accepted, releasedActivity.Value!.Outcome);
        Assert.Equal(DurableFlowState.WaitingForActivity, releasedActivity.Value.State);

        async Task<(DurableFlowInstanceId Instance, PostgreSqlEffectPermit Permit)>
            StartPermittedActivityAsync(string scenario, byte context)
        {
            var scenarioInstance = new DurableFlowInstanceId($"activity-projection-{scenario}");
            Assert.True((await client.StartAsync(new DurableFlowStartRequest(
                scope,
                new DurableCommandId($"activity-projection-{scenario}-start"),
                $"activity-projection-{scenario}-key",
                scenarioInstance,
                registration.FlowId,
                registration.FlowVersion,
                contextCodec.EncodeObject(new[] { context })))).IsSuccess);
            var scenarioActivity = await flowProcessor.TryProcessAsync(
                Assert.Single(
                    await flowProcessor.DiscoverAsync(),
                    candidate => candidate.InstanceId == scenarioInstance),
                $"flow-activity-projection-{scenario}-worker");
            var scenarioCandidate = Assert.Single(
                await workStore.DiscoverAsync(10),
                candidate => candidate.WorkId == scenarioActivity.ChildWorkId);
            var scenarioClaim = await workStore.TryClaimAsync(
                scenarioCandidate,
                $"work-activity-projection-{scenario}-worker");
            var scenarioPermit = await workStore.TryAcquireEffectPermitAsync(scenarioClaim!);
            return (scenarioInstance, Assert.IsType<PostgreSqlEffectPermit>(scenarioPermit));
        }

        async Task DeleteParentDispatchAsync(DurableFlowInstanceId scenarioInstance)
        {
            await using var deleteDispatch = database.DataSource.CreateCommand(
                """
                DELETE FROM appsurface_durable.flow_dispatch
                WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id AND kind = 'flow';
                """);
            deleteDispatch.Parameters.AddWithValue("scope_id", scope.Value);
            deleteDispatch.Parameters.AddWithValue("flow_instance_id", scenarioInstance.Value);
            Assert.Equal(1, await deleteDispatch.ExecuteNonQueryAsync());
        }

        var (successProjectionInstance, successProjectionPermit) =
            await StartPermittedActivityAsync("success-dispatch-missing", 10);
        await DeleteParentDispatchAsync(successProjectionInstance);
        var successProjectionException = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await workStore.RecordCompletionAsync(
                successProjectionPermit.Claim,
                new PostgreSqlWorkCompletion(
                    PostgreSqlWorkCompletionKind.Succeeded,
                    "activity-projection-success",
                    "{}",
                    resultCodec.EncodeObject(new byte[] { 10 }))));
        Assert.Contains("Successful child Work did not project", successProjectionException.Message, StringComparison.Ordinal);
        Assert.Equal(
            DurableFlowState.WaitingForActivity,
            (await client.GetAsync(new DurableFlowGetRequest(scope, successProjectionInstance))).Value!.State);

        var (failureProjectionInstance, failureProjectionPermit) =
            await StartPermittedActivityAsync("failure-dispatch-missing", 11);
        await DeleteParentDispatchAsync(failureProjectionInstance);
        var failureProjectionException = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await workStore.RecordCompletionAsync(
                failureProjectionPermit.Claim,
                new PostgreSqlWorkCompletion(
                    PostgreSqlWorkCompletionKind.FailedTerminal,
                    "activity-projection-failure",
                    "{}")));
        Assert.Contains("Terminal child Work did not project", failureProjectionException.Message, StringComparison.Ordinal);
        Assert.Equal(
            DurableFlowState.WaitingForActivity,
            (await client.GetAsync(new DurableFlowGetRequest(scope, failureProjectionInstance))).Value!.State);
        WriteEvidence("flow-activity-resume", terminal.Revision);
    }

    [Fact]
    public async Task ClaimTimeRecovery_SuspendsTheParentActivityFlowAtomically()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var originalEpoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(originalEpoch, "tests", "slice4-claim-recovery");
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
        var originalOptions = new PostgreSqlDurableWorkOptions(originalEpoch, status.StoreId);
        var flowClient = new PostgreSqlDurableFlowClient(database.DataSource, flows, payloads, originalOptions);
        var flowProcessor = new PostgreSqlDurableFlowProcessor(
            database.DataSource,
            database.DataSource,
            flows,
            work,
            payloads,
            originalOptions);
        var scope = new DurableScopeId("slice4-claim-recovery");
        var instance = new DurableFlowInstanceId("claim-recovery-flow");
        Assert.True((await flowClient.StartAsync(new DurableFlowStartRequest(
            scope,
            new DurableCommandId("claim-recovery-start"),
            "claim-recovery-start-key",
            instance,
            registration.FlowId,
            registration.FlowVersion,
            contextCodec.EncodeObject(new byte[] { 1 })))).IsSuccess);

        var activity = await flowProcessor.TryProcessAsync(
            Assert.Single(await flowProcessor.DiscoverAsync()),
            "claim-recovery-flow-worker");
        Assert.Equal(DurableFlowState.WaitingForActivity, activity.State);
        Assert.NotNull(activity.ChildWorkId);

        var originalWorkStore = new PostgreSqlDurableWorkStore(database.DataSource, originalEpoch);
        var candidate = Assert.Single(
            await originalWorkStore.DiscoverAsync(10),
            item => item.WorkId == activity.ChildWorkId);
        var recoveryEpoch = Guid.NewGuid();
        await schema.RotateRuntimeEpochAsync(originalEpoch, recoveryEpoch, "tests", "claim-recovery");

        var recoveryStore = new PostgreSqlDurableWorkStore(database.DataSource, recoveryEpoch);
        Assert.Null(await recoveryStore.TryClaimAsync(candidate, "claim-recovery-work-worker"));

        var recoveryClient = new PostgreSqlDurableFlowClient(
            database.DataSource,
            flows,
            payloads,
            new PostgreSqlDurableWorkOptions(recoveryEpoch, status.StoreId));
        var snapshot = await recoveryClient.GetAsync(new DurableFlowGetRequest(scope, instance));
        Assert.Equal(DurableFlowState.Suspended, snapshot.Value!.State);

        await using var wait = database.DataSource.CreateCommand(
            """
            SELECT state, suspension_descriptor ->> 'work_state'
            FROM appsurface_durable.flow_wait
            WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id AND kind = 'activity';
            """);
        wait.Parameters.AddWithValue("scope_id", scope.Value);
        wait.Parameters.AddWithValue("flow_instance_id", instance.Value);
        await using var reader = await wait.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("suspended", reader.GetString(0));
        Assert.Equal(DurableWorkState.Suspended.ToString(), reader.GetString(1));
    }

    [Fact]
    public async Task FlowActivityCommit_GuardsProviderSafetyAndRollsBackStaleChildAcceptance()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "tests", "slice4-activity-commit-guards");
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
        var processor = new PostgreSqlDurableFlowProcessor(
            database.DataSource,
            database.DataSource,
            flows,
            work,
            payloads,
            options);
        var store = new PostgreSqlDurableFlowStore(database.DataSource, options);
        var scope = new DurableScopeId("slice4-activity-commit-guards");

        var safetyInstance = new DurableFlowInstanceId("activity-provider-safety");
        Assert.True((await client.StartAsync(new DurableFlowStartRequest(
            scope,
            new DurableCommandId("activity-provider-safety-start"),
            "activity-provider-safety-key",
            safetyInstance,
            registration.FlowId,
            registration.FlowVersion,
            contextCodec.EncodeObject(new byte[] { 1 })))).IsSuccess);
        var safetyClaim = await store.TryClaimFlowAsync(
            Assert.Single(await processor.DiscoverAsync(), candidate => candidate.InstanceId == safetyInstance),
            "activity-provider-safety-worker",
            TimeSpan.FromMinutes(1),
            default);
        var activity = new DurableFlowActivityCommand(
            "call-provider",
            1,
            workRegistration.WorkName,
            workRegistration.WorkVersion,
            DurableProviderSafety.Idempotent,
            workCodec.EncodeObject(new byte[] { 4, 5 }));
        var decision = new DurableFlowEvaluationResult(
            FlowTransitionKind.Activity,
            safetyClaim!.CurrentNodeId,
            safetyClaim.Context,
            null,
            null,
            null,
            null,
            activity);
        var mismatchedWork = new DurableWorkRegistry([
            new PostgreSqlOpaqueTestWorkRegistration(
                workRegistration.WorkName,
                workRegistration.WorkVersion,
                DurableProviderSafety.ProviderKeyed,
                workCodec,
                resultCodec),
        ]);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.CommitDecisionAsync(safetyClaim, decision, mismatchedWork, default));

        var staleInstance = new DurableFlowInstanceId("activity-stale-claim");
        Assert.True((await client.StartAsync(new DurableFlowStartRequest(
            scope,
            new DurableCommandId("activity-stale-claim-start"),
            "activity-stale-claim-key",
            staleInstance,
            registration.FlowId,
            registration.FlowVersion,
            contextCodec.EncodeObject(new byte[] { 2 })))).IsSuccess);
        var staleClaim = await store.TryClaimFlowAsync(
            Assert.Single(await processor.DiscoverAsync(), candidate => candidate.InstanceId == staleInstance),
            "activity-stale-claim-worker",
            TimeSpan.FromMinutes(1),
            default);
        var workCountBefore = await CountAsync(database.DataSource, scope, workRegistration.WorkName);
        await using (var makeClaimStale = database.DataSource.CreateCommand(
            """
            UPDATE appsurface_durable.flow_instance
            SET state = 'ready', revision = revision + 1,
                lease_owner = NULL, lease_started_at = NULL, lease_expires_at = NULL
            WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id;
            """))
        {
            makeClaimStale.Parameters.AddWithValue("scope_id", scope.Value);
            makeClaimStale.Parameters.AddWithValue("flow_instance_id", staleInstance.Value);
            Assert.Equal(1, await makeClaimStale.ExecuteNonQueryAsync());
        }
        var staleActivity = new DurableFlowActivityCommand(
            activity.CallsiteId,
            activity.ResultContractVersion,
            activity.WorkName,
            activity.WorkVersion,
            workRegistration.ProviderSafety,
            activity.Work);
        var staleDecision = new DurableFlowEvaluationResult(
            FlowTransitionKind.Activity,
            staleClaim!.CurrentNodeId,
            staleClaim.Context,
            null,
            null,
            null,
            null,
            staleActivity);
        var stale = await store.CommitDecisionAsync(staleClaim, staleDecision, work, default);
        Assert.Equal(PostgreSqlFlowProcessingOutcome.Stale, stale.Outcome);
        Assert.Equal(workCountBefore, await CountAsync(database.DataSource, scope, workRegistration.WorkName));

        var staleDispatchInstance = new DurableFlowInstanceId("activity-stale-dispatch");
        Assert.True((await client.StartAsync(new DurableFlowStartRequest(
            scope,
            new DurableCommandId("activity-stale-dispatch-start"),
            "activity-stale-dispatch-key",
            staleDispatchInstance,
            registration.FlowId,
            registration.FlowVersion,
            contextCodec.EncodeObject(new byte[] { 3 })))).IsSuccess);

        var staleDispatchCandidate = Assert.Single(
            await processor.DiscoverAsync(),
            candidate => candidate.InstanceId == staleDispatchInstance);
        var staleDispatchClaim = await store.TryClaimFlowAsync(
            staleDispatchCandidate,
            "activity-stale-dispatch-worker",
            TimeSpan.FromMinutes(1),
            default);
        Assert.NotNull(staleDispatchClaim);
        var staleDispatchWorkCountBefore = await CountAsync(database.DataSource, scope, workRegistration.WorkName);

        await using (var makeDispatchStale = database.DataSource.CreateCommand(
            """
            UPDATE appsurface_durable.flow_dispatch
            SET expected_revision = @stale_revision
            WHERE dispatch_id = @dispatch_id;
            """))
        {
            makeDispatchStale.Parameters.AddWithValue("dispatch_id", staleDispatchCandidate.DispatchId);
            makeDispatchStale.Parameters.AddWithValue("stale_revision", staleDispatchClaim.Revision + 1);
            Assert.Equal(1, await makeDispatchStale.ExecuteNonQueryAsync());
        }

        var staleDispatchDecision = new DurableFlowEvaluationResult(
            FlowTransitionKind.Activity,
            staleDispatchClaim.CurrentNodeId,
            staleDispatchClaim.Context,
            null,
            null,
            null,
            null,
            new DurableFlowActivityCommand(
                activity.CallsiteId,
                activity.ResultContractVersion,
                activity.WorkName,
                activity.WorkVersion,
                workRegistration.ProviderSafety,
                activity.Work));
        var staleDispatch = await store.CommitDecisionAsync(staleDispatchClaim, staleDispatchDecision, work, default);
        Assert.Equal(PostgreSqlFlowProcessingOutcome.Stale, staleDispatch.Outcome);
        Assert.Equal(staleDispatchWorkCountBefore, await CountAsync(database.DataSource, scope, workRegistration.WorkName));
    }

    [Fact]
    public async Task FlowClient_StartRejectsANonAuthoritativeContextCodec()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var contextCodec = new PostgreSqlOpaqueTestCodec("tests.flow.context", "v1");
        var alternateContextCodec = new PostgreSqlOpaqueTestCodec("tests.flow.context", "v1");
        var payloads = new DurablePayloadCodecRegistry([contextCodec]);
        var work = new DurableWorkRegistry([]);
        var registration = new WaitingTestFlowRegistration(contextCodec);
        var flows = new DurableFlowRegistry([registration], work, payloads);
        var client = new PostgreSqlDurableFlowClient(
            database.DataSource,
            flows,
            new MismatchedPayloadCodecRegistry(alternateContextCodec),
            new PostgreSqlDurableWorkOptions(Guid.NewGuid(), Guid.NewGuid()));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await client.StartAsync(new DurableFlowStartRequest(
                new DurableScopeId("slice4-non-authoritative-codec"),
                new DurableCommandId("non-authoritative-codec-start"),
                "non-authoritative-codec-key",
                new DurableFlowInstanceId("non-authoritative-codec-flow"),
                registration.FlowId,
                registration.FlowVersion,
                contextCodec.EncodeObject(new byte[] { 1 }))));
        Assert.Contains("exact allowlisted context codec", exception.Message, StringComparison.Ordinal);
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
        using var crashTraceListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AppSurfaceActivitySources.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(crashTraceListener);
        var processor = new PostgreSqlDurableFlowProcessor(
            database.DataSource,
            database.DataSource,
            flows,
            work,
            payloads,
            options);
        var scope = new DurableScopeId("slice4-timer-race");
        var instance = new DurableFlowInstanceId("timer-flow");
        using var incoming = new Activity("timer-race-incoming")
            .SetIdFormat(ActivityIdFormat.W3C)
            .Start();
        Assert.True((await client.StartAsync(new DurableFlowStartRequest(
            scope,
            new DurableCommandId("timer-start"),
            "timer-start-key",
            instance,
            registration.FlowId,
            registration.FlowVersion,
            codec.EncodeObject(new byte[] { 1 })))).IsSuccess);

        await using (var corruptTraceState = database.DataSource.CreateCommand(
            """
            UPDATE appsurface_durable.flow_trace_context
            SET tracestate = 'vendor=value=unsafe'
            WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id
              AND cause_kind = 'command_accepted';
            """))
        {
            corruptTraceState.Parameters.AddWithValue("scope_id", scope.Value);
            corruptTraceState.Parameters.AddWithValue("flow_instance_id", instance.Value);
            Assert.Equal(1, await corruptTraceState.ExecuteNonQueryAsync());
        }

        using var diagnosticOutput = new StringWriter();
        using var diagnosticListener = new TextWriterTraceListener(diagnosticOutput);
        Trace.Listeners.Add(diagnosticListener);
        PostgreSqlFlowProcessingResult waiting;
        try
        {
            waiting = await processor.TryProcessAsync(
                Assert.Single(await processor.DiscoverAsync()),
                "timer-register-worker");
            diagnosticListener.Flush();
        }
        finally
        {
            Trace.Listeners.Remove(diagnosticListener);
        }

        Assert.Equal(DurableFlowState.WaitingForEvent, waiting.State);
        Assert.Contains(DurableProblemCodes.TraceStateRejected, diagnosticOutput.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("vendor=value=unsafe", diagnosticOutput.ToString(), StringComparison.Ordinal);
        await ForceTimerDueAsync(database.DataSource, scope, instance);
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
        Assert.Equal("appsurface.durable.telemetry.evidence", crashCheckpoint.Event);
        Assert.NotNull(crashCheckpoint.TraceEvidence);
        Assert.Equal("appsurface.durable.flow.timer", crashCheckpoint.TraceEvidence.Operation);
        Assert.NotEmpty(crashCheckpoint.TraceEvidence.Links);

        await using (var trace = database.DataSource.CreateCommand(
            """
            SELECT
                count(*),
                count(*) FILTER (WHERE cause_kind = 'command_accepted'),
                count(*) FILTER (WHERE cause_kind = 'evaluation_committed'),
                count(*) FILTER (WHERE cause_kind = 'evaluation_committed' AND tracestate IS NULL),
                count(*) FILTER (WHERE cause_kind = 'timer_winner'),
                (SELECT trace_context_id IS NOT NULL
                 FROM appsurface_durable.flow_instance
                 WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id),
                (SELECT trace_context_id IS NOT NULL
                 FROM appsurface_durable.flow_wait
                 WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id),
                (SELECT trace_context_id IS NOT NULL
                 FROM appsurface_durable.flow_timer
                 WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id)
            FROM appsurface_durable.flow_trace_context
            WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id;
            """))
        {
            trace.Parameters.AddWithValue("scope_id", scope.Value);
            trace.Parameters.AddWithValue("flow_instance_id", instance.Value);
            await using var reader = await trace.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(3L, reader.GetInt64(0));
            Assert.Equal(1L, reader.GetInt64(1));
            Assert.Equal(1L, reader.GetInt64(2));
            Assert.Equal(1L, reader.GetInt64(3));
            Assert.Equal(1L, reader.GetInt64(4));
            Assert.True(reader.GetBoolean(5));
            Assert.True(reader.GetBoolean(6));
            Assert.True(reader.GetBoolean(7));
        }

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
    public async Task TimerResolution_MissingFlowDispatchRollsBackEveryWinnerMutation()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "tests", "slice4-timer-cardinality");
        var status = await schema.GetStatusAsync();
        var codec = new PostgreSqlOpaqueTestCodec("tests.flow.context", "v1");
        var payloads = new DurablePayloadCodecRegistry([codec]);
        var work = new DurableWorkRegistry([]);
        var registration = new TimerTestFlowRegistration(codec);
        var flows = new DurableFlowRegistry([registration], work, payloads);
        var options = new PostgreSqlDurableWorkOptions(epoch, status.StoreId);
        using var traceListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AppSurfaceActivitySources.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(traceListener);
        var client = new PostgreSqlDurableFlowClient(database.DataSource, flows, payloads, options);
        var processor = new PostgreSqlDurableFlowProcessor(
            database.DataSource,
            database.DataSource,
            flows,
            work,
            payloads,
            options);
        var scope = new DurableScopeId("slice4-timer-cardinality");
        var instance = new DurableFlowInstanceId("timer-cardinality-flow");
        Assert.True((await client.StartAsync(new DurableFlowStartRequest(
            scope,
            new DurableCommandId("timer-cardinality-start"),
            "timer-cardinality-start-key",
            instance,
            registration.FlowId,
            registration.FlowVersion,
            codec.EncodeObject(new byte[] { 1 })))).IsSuccess);
        Assert.Equal(
            DurableFlowState.WaitingForEvent,
            (await processor.TryProcessAsync(
                Assert.Single(await processor.DiscoverAsync()),
                "timer-cardinality-register-worker")).State);
        await ForceTimerDueAsync(database.DataSource, scope, instance);
        var timerCandidate = Assert.Single(await processor.DiscoverAsync());
        var traceCountBefore = await CountFlowTraceContextsAsync(database.DataSource, scope, instance);
        Assert.Equal(2, traceCountBefore);

        await using (var delete = database.DataSource.CreateCommand(
            """
            DELETE FROM appsurface_durable.flow_dispatch
            WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id AND kind = 'flow';
            """))
        {
            delete.Parameters.AddWithValue("scope_id", scope.Value);
            delete.Parameters.AddWithValue("flow_instance_id", instance.Value);
            Assert.Equal(1, await delete.ExecuteNonQueryAsync());
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await processor.TryProcessAsync(timerCandidate, "timer-cardinality-resolve-worker"));
        Assert.Contains("exactly once", exception.Message, StringComparison.Ordinal);
        Assert.Equal(traceCountBefore, await CountFlowTraceContextsAsync(database.DataSource, scope, instance));

        await using var verify = database.DataSource.CreateCommand(
            """
            SELECT flow.state, wait.state, timer.state, dispatch.state
            FROM appsurface_durable.flow_instance AS flow
            JOIN appsurface_durable.flow_wait AS wait
              ON wait.scope_id = flow.scope_id AND wait.flow_instance_id = flow.flow_instance_id
            JOIN appsurface_durable.flow_timer AS timer
              ON timer.scope_id = flow.scope_id AND timer.flow_instance_id = flow.flow_instance_id
            JOIN appsurface_durable.flow_dispatch AS dispatch
              ON dispatch.scope_id = flow.scope_id AND dispatch.flow_instance_id = flow.flow_instance_id
             AND dispatch.kind = 'timer'
            WHERE flow.scope_id = @scope_id AND flow.flow_instance_id = @flow_instance_id;
            """);
        verify.Parameters.AddWithValue("scope_id", scope.Value);
        verify.Parameters.AddWithValue("flow_instance_id", instance.Value);
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("waiting_event", reader.GetString(0));
        Assert.Equal("active", reader.GetString(1));
        Assert.Equal("scheduled", reader.GetString(2));
        Assert.Equal("available", reader.GetString(3));
    }

    [Fact]
    public async Task TimerResolution_SupersedesACandidateThatIsNoLongerDue()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "tests", "slice4-timer-not-due");
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
        var scope = new DurableScopeId("slice4-timer-not-due");
        var instance = new DurableFlowInstanceId("timer-not-due-flow");
        Assert.True((await client.StartAsync(new DurableFlowStartRequest(
            scope,
            new DurableCommandId("timer-not-due-start"),
            "timer-not-due-key",
            instance,
            registration.FlowId,
            registration.FlowVersion,
            codec.EncodeObject(new byte[] { 1 })))).IsSuccess);
        Assert.Equal(
            DurableFlowState.WaitingForEvent,
            (await processor.TryProcessAsync(
                Assert.Single(await processor.DiscoverAsync()),
                "timer-not-due-register-worker")).State);
        await ForceTimerDueAsync(database.DataSource, scope, instance);
        var candidate = Assert.Single(await processor.DiscoverAsync());

        await using (var deferTimer = database.DataSource.CreateCommand(
            """
            UPDATE appsurface_durable.flow_timer
            SET due_at = clock_timestamp() + interval '1 hour'
            WHERE timer_id = @timer_id;
            """))
        {
            deferTimer.Parameters.AddWithValue("timer_id", candidate.TimerId!.Value);
            Assert.Equal(1, await deferTimer.ExecuteNonQueryAsync());
        }

        var result = await processor.TryProcessAsync(candidate, "timer-not-due-resolve-worker");
        Assert.Equal(PostgreSqlFlowProcessingOutcome.RaceLost, result.Outcome);
        Assert.Equal(DurableFlowState.WaitingForEvent, result.State);
        Assert.Equal(
            DurableFlowState.WaitingForEvent,
            (await client.GetAsync(new DurableFlowGetRequest(scope, instance))).Value!.State);
    }

    [Fact]
    public async Task TimerResolution_ObservesItsCommittedBarrier()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "tests", "slice4-timer-barrier");
        var status = await schema.GetStatusAsync();
        var codec = new PostgreSqlOpaqueTestCodec("tests.flow.context", "v1");
        var payloads = new DurablePayloadCodecRegistry([codec]);
        var work = new DurableWorkRegistry([]);
        var registration = new TimerTestFlowRegistration(codec);
        var flows = new DurableFlowRegistry([registration], work, payloads);
        var barriers = new RecordingFlowBarrierObserver();
        var options = new PostgreSqlDurableWorkOptions(epoch, status.StoreId);
        var client = new PostgreSqlDurableFlowClient(database.DataSource, flows, payloads, options);
        var processor = new PostgreSqlDurableFlowProcessor(
            database.DataSource,
            database.DataSource,
            flows,
            work,
            payloads,
            options,
            barriers: barriers);
        var scope = new DurableScopeId("slice4-timer-barrier");
        var instance = new DurableFlowInstanceId("timer-barrier-flow");
        Assert.True((await client.StartAsync(new DurableFlowStartRequest(
            scope,
            new DurableCommandId("timer-barrier-start"),
            "timer-barrier-key",
            instance,
            registration.FlowId,
            registration.FlowVersion,
            codec.EncodeObject(new byte[] { 1 })))).IsSuccess);
        Assert.Equal(
            DurableFlowState.WaitingForEvent,
            (await processor.TryProcessAsync(
                Assert.Single(await processor.DiscoverAsync()),
                "timer-barrier-register-worker")).State);
        await ForceTimerDueAsync(database.DataSource, scope, instance);

        var result = await processor.TryProcessAsync(
            Assert.Single(await processor.DiscoverAsync()),
            "timer-barrier-resolve-worker");
        Assert.Equal(PostgreSqlFlowProcessingOutcome.Applied, result.Outcome);
        Assert.Contains("flow.timer-resolution.committed", barriers.Barriers);
    }

    [Fact]
    public async Task Flow_EventAndLifecycleCardinalityGuards_RollBackPartialProjections()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "tests", "slice4-cardinality-coverage");
        var status = await schema.GetStatusAsync();
        var codec = new PostgreSqlOpaqueTestCodec("tests.flow.context", "v1");
        var payloads = new DurablePayloadCodecRegistry([codec]);
        var work = new DurableWorkRegistry([]);
        var timerRegistration = new TimerTestFlowRegistration(codec);
        var waitingRegistration = new WaitingTestFlowRegistration(codec);
        var flows = new DurableFlowRegistry([timerRegistration, waitingRegistration], work, payloads);
        var options = new PostgreSqlDurableWorkOptions(epoch, status.StoreId);
        var client = new PostgreSqlDurableFlowClient(database.DataSource, flows, payloads, options);
        var processor = new PostgreSqlDurableFlowProcessor(
            database.DataSource,
            database.DataSource,
            flows,
            work,
            payloads,
            options);
        var scope = new DurableScopeId("slice4-cardinality-coverage");

        var timerInstance = new DurableFlowInstanceId("event-cardinality-timer");
        Assert.True((await client.StartAsync(new DurableFlowStartRequest(
            scope,
            new DurableCommandId("event-cardinality-timer-start"),
            "event-cardinality-timer-key",
            timerInstance,
            timerRegistration.FlowId,
            timerRegistration.FlowVersion,
            codec.EncodeObject(new byte[] { 1 })))).IsSuccess);
        var timerWait = await processor.TryProcessAsync(
            Assert.Single(await processor.DiscoverAsync()),
            "event-cardinality-timer-worker");
        Assert.Equal(DurableFlowState.WaitingForEvent, timerWait.State);
        await using (var deleteTimerDispatch = database.DataSource.CreateCommand(
            """
            DELETE FROM appsurface_durable.flow_dispatch
            WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id AND kind = 'timer';
            """))
        {
            deleteTimerDispatch.Parameters.AddWithValue("scope_id", scope.Value);
            deleteTimerDispatch.Parameters.AddWithValue("flow_instance_id", timerInstance.Value);
            Assert.Equal(1, await deleteTimerDispatch.ExecuteNonQueryAsync());
        }
        var eventCardinalityException = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await client.RaiseEventAsync(new DurableFlowEventRequest(
                scope,
                new DurableCommandId("event-cardinality-command"),
                new DurableFlowEventId("event-cardinality-event"),
                timerInstance,
                "approved",
                expectedRevision: timerWait.Revision)));
        Assert.Contains("timer lineage", eventCardinalityException.Message, StringComparison.Ordinal);
        Assert.Equal(
            DurableFlowState.WaitingForEvent,
            (await client.GetAsync(new DurableFlowGetRequest(scope, timerInstance))).Value!.State);

        var lifecycleInstance = new DurableFlowInstanceId("lifecycle-cardinality-wait");
        Assert.True((await client.StartAsync(new DurableFlowStartRequest(
            scope,
            new DurableCommandId("lifecycle-cardinality-wait-start"),
            "lifecycle-cardinality-wait-key",
            lifecycleInstance,
            waitingRegistration.FlowId,
            waitingRegistration.FlowVersion,
            codec.EncodeObject(new byte[] { 2 })))).IsSuccess);
        var waiting = await processor.TryProcessAsync(
            Assert.Single(
                await processor.DiscoverAsync(),
                candidate => candidate.InstanceId == lifecycleInstance),
            "lifecycle-cardinality-wait-worker");
        Assert.Equal(DurableFlowState.WaitingForEvent, waiting.State);
        await using (var deleteWait = database.DataSource.CreateCommand(
            """
            DELETE FROM appsurface_durable.flow_wait
            WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id;
            """))
        {
            deleteWait.Parameters.AddWithValue("scope_id", scope.Value);
            deleteWait.Parameters.AddWithValue("flow_instance_id", lifecycleInstance.Value);
            Assert.Equal(1, await deleteWait.ExecuteNonQueryAsync());
        }
        var lifecycleException = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await client.CancelAsync(new DurableFlowCancelRequest(
                scope,
                new DurableCommandId("lifecycle-cardinality-cancel"),
                lifecycleInstance,
                "operator",
                "invariant-test",
                waiting.Revision)));
        Assert.Contains("wait lineage exactly once", lifecycleException.Message, StringComparison.Ordinal);
        Assert.Equal(
            DurableFlowState.WaitingForEvent,
            (await client.GetAsync(new DurableFlowGetRequest(scope, lifecycleInstance))).Value!.State);
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

        var invalidScope = new DurableScopeId("slice4-scope-disable-missing-dispatch");
        var invalidInstance = new DurableFlowInstanceId("scope-flow-missing-dispatch");
        Assert.True((await client.StartAsync(new DurableFlowStartRequest(
            invalidScope,
            new DurableCommandId("scope-missing-dispatch-start"),
            "scope-missing-dispatch-start-key",
            invalidInstance,
            registration.FlowId,
            registration.FlowVersion,
            codec.EncodeObject(new byte[] { 2 })))).IsSuccess);
        await using (var delete = database.DataSource.CreateCommand(
            """
            DELETE FROM appsurface_durable.flow_dispatch
            WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id AND kind = 'flow';
            """))
        {
            delete.Parameters.AddWithValue("scope_id", invalidScope.Value);
            delete.Parameters.AddWithValue("flow_instance_id", invalidInstance.Value);
            Assert.Equal(1, await delete.ExecuteNonQueryAsync());
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.DisableScopeAsync(invalidScope, "tests", "disable", expectedGeneration: 1));
        Assert.Contains("dispatch and history exactly once", exception.Message, StringComparison.Ordinal);
        Assert.Equal(
            DurableFlowState.Ready,
            (await client.GetAsync(new DurableFlowGetRequest(invalidScope, invalidInstance))).Value!.State);

        var timerRegistration = new TimerTestFlowRegistration(codec);
        var timerFlows = new DurableFlowRegistry([timerRegistration], work, payloads);
        var timerClient = new PostgreSqlDurableFlowClient(database.DataSource, timerFlows, payloads, options);
        var timerProcessor = new PostgreSqlDurableFlowProcessor(
            database.DataSource,
            database.DataSource,
            timerFlows,
            work,
            payloads,
            options);
        var timerScope = new DurableScopeId("slice4-disabled-timer");
        var timerInstance = new DurableFlowInstanceId("disabled-timer-flow");
        Assert.True((await timerClient.StartAsync(new DurableFlowStartRequest(
            timerScope,
            new DurableCommandId("disabled-timer-start"),
            "disabled-timer-start-key",
            timerInstance,
            timerRegistration.FlowId,
            timerRegistration.FlowVersion,
            codec.EncodeObject(new byte[] { 3 })))).IsSuccess);
        Assert.Equal(
            DurableFlowState.WaitingForEvent,
            (await timerProcessor.TryProcessAsync(
                Assert.Single(await timerProcessor.DiscoverAsync()),
                "disabled-timer-register-worker")).State);
        await ForceTimerDueAsync(database.DataSource, timerScope, timerInstance);
        var disabledTimerCandidate = Assert.Single(await timerProcessor.DiscoverAsync());
        Assert.Equal(
            PostgreSqlScopeMutationOutcome.Applied,
            (await store.DisableScopeAsync(timerScope, "tests", "disable", expectedGeneration: 1)).Outcome);
        Assert.Equal(
            PostgreSqlFlowProcessingOutcome.RaceLost,
            (await timerProcessor.TryProcessAsync(
                disabledTimerCandidate,
                "disabled-timer-stale-worker")).Outcome);
        await using (var timerTruth = database.DataSource.CreateCommand(
            """
            SELECT flow.state, timer.state, dispatch.state
            FROM appsurface_durable.flow_instance AS flow
            JOIN appsurface_durable.flow_timer AS timer
              ON timer.scope_id = flow.scope_id AND timer.flow_instance_id = flow.flow_instance_id
            JOIN appsurface_durable.flow_dispatch AS dispatch
              ON dispatch.scope_id = flow.scope_id AND dispatch.flow_instance_id = flow.flow_instance_id
             AND dispatch.kind = 'timer'
            WHERE flow.scope_id = @scope_id AND flow.flow_instance_id = @flow_instance_id;
            """))
        {
            timerTruth.Parameters.AddWithValue("scope_id", timerScope.Value);
            timerTruth.Parameters.AddWithValue("flow_instance_id", timerInstance.Value);
            await using var timerReader = await timerTruth.ExecuteReaderAsync();
            Assert.True(await timerReader.ReadAsync());
            Assert.Equal("suspended", timerReader.GetString(0));
            Assert.Equal("scheduled", timerReader.GetString(1));
            Assert.Equal("suspended", timerReader.GetString(2));
        }
        WriteEvidence("flow-scope-disable", snapshot.Value.Revision);
    }

    [Fact]
    public async Task Flow_StateProjectionFiltersPaginationAndRecovery_CoverEveryPersistedShape()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "tests", "slice4-state-projection");
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
        var scope = new DurableScopeId("slice4-state-projection");
        var instance = new DurableFlowInstanceId("state-projection-flow");
        Assert.True((await client.StartAsync(new DurableFlowStartRequest(
            scope,
            new DurableCommandId("state-projection-start"),
            "state-projection-start-key",
            instance,
            registration.FlowId,
            registration.FlowVersion,
            codec.EncodeObject(new byte[] { 1 })))).IsSuccess);

        var persistedStates = new[]
        {
            ("ready", DurableFlowState.Ready),
            ("evaluating", DurableFlowState.Ready),
            ("waiting_event", DurableFlowState.WaitingForEvent),
            ("waiting_timer", DurableFlowState.WaitingForTimer),
            ("waiting_activity", DurableFlowState.WaitingForActivity),
            ("cancel_pending", DurableFlowState.CancelPending),
            ("completed", DurableFlowState.Completed),
            ("faulted", DurableFlowState.Faulted),
            ("canceled", DurableFlowState.Canceled),
            ("suspended", DurableFlowState.Suspended),
        };

        foreach (var (persistedState, publicState) in persistedStates)
        {
            await using (var update = database.DataSource.CreateCommand(
                """
                UPDATE appsurface_durable.flow_instance
                SET state = @state,
                    lease_owner = CASE WHEN @state = 'evaluating' THEN 'state-projection-test' ELSE NULL END,
                    lease_started_at = CASE WHEN @state = 'evaluating' THEN clock_timestamp() ELSE NULL END,
                    lease_expires_at = CASE WHEN @state = 'evaluating' THEN clock_timestamp() + interval '1 minute' ELSE NULL END,
                    terminal_at = CASE WHEN @state IN ('completed', 'faulted', 'canceled') THEN clock_timestamp() ELSE NULL END,
                    terminal_code = CASE WHEN @state IN ('completed', 'faulted', 'canceled') THEN @state ELSE NULL END,
                    cancellation_requested_at = CASE WHEN @state = 'cancel_pending' THEN clock_timestamp() ELSE NULL END,
                    suspension_descriptor = CASE WHEN @state = 'suspended' THEN '{}'::jsonb ELSE NULL END,
                    suspended_from_state = CASE WHEN @state = 'suspended' THEN 'ready' ELSE NULL END,
                    updated_at = clock_timestamp()
                WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id;
                """))
            {
                update.Parameters.AddWithValue("state", persistedState);
                update.Parameters.AddWithValue("scope_id", scope.Value);
                update.Parameters.AddWithValue("flow_instance_id", instance.Value);
                Assert.Equal(1, await update.ExecuteNonQueryAsync());
            }

            var snapshot = await client.GetAsync(new DurableFlowGetRequest(scope, instance));
            Assert.Equal(publicState, snapshot.Value!.State);
            var filtered = await client.ListAsync(
                new DurableFlowListRequest(scope, state: publicState, pageSize: 10));
            Assert.Contains(filtered.Value!.Flows, flow => flow.InstanceId == instance);
        }

        var oldEpoch = Guid.NewGuid();
        await using (var age = database.DataSource.CreateCommand(
            """
            UPDATE appsurface_durable.flow_instance
            SET state = 'suspended', runtime_epoch = @runtime_epoch,
                suspension_descriptor = '{}'::jsonb, suspended_from_state = 'ready',
                terminal_at = NULL, terminal_code = NULL, updated_at = clock_timestamp()
            WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id;
            """))
        {
            age.Parameters.AddWithValue("runtime_epoch", oldEpoch);
            age.Parameters.AddWithValue("scope_id", scope.Value);
            age.Parameters.AddWithValue("flow_instance_id", instance.Value);
            Assert.Equal(1, await age.ExecuteNonQueryAsync());
        }

        Assert.True((await client.GetAsync(new DurableFlowGetRequest(scope, instance))).Value!.RequiresRecoveryRelease);
        Assert.Contains(
            (await client.ListAsync(new DurableFlowListRequest(scope, requiresRecoveryRelease: true))).Value!.Flows,
            flow => flow.InstanceId == instance);
        Assert.DoesNotContain(
            (await client.ListAsync(new DurableFlowListRequest(scope, requiresRecoveryRelease: false))).Value!.Flows,
            flow => flow.InstanceId == instance);

        foreach (var suffix in new[] { "page-a", "page-b" })
        {
            Assert.True((await client.StartAsync(new DurableFlowStartRequest(
                scope,
                new DurableCommandId($"{suffix}-start"),
                $"{suffix}-key",
                new DurableFlowInstanceId(suffix),
                registration.FlowId,
                registration.FlowVersion,
                codec.EncodeObject(new byte[] { 2 })))).IsSuccess);
        }

        var firstPage = await client.ListAsync(new DurableFlowListRequest(scope, pageSize: 1));
        Assert.Single(firstPage.Value!.Flows);
        Assert.NotNull(firstPage.Value.ContinuationToken);
        var secondPage = await client.ListAsync(
            new DurableFlowListRequest(scope, pageSize: 1, continuationToken: firstPage.Value.ContinuationToken));
        Assert.Single(secondPage.Value!.Flows);

        var missing = await client.GetAsync(
            new DurableFlowGetRequest(scope, new DurableFlowInstanceId("state-projection-missing")));
        Assert.Equal(DurableProblemCodes.FlowNotFound, missing.Problem!.Code);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await client.ListAsync(new DurableFlowListRequest(scope, continuationToken: "e30")));
    }

    [Fact]
    public async Task Flow_Processor_CoversNextFaultAndTimedOutTransitions()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "tests", "slice4-transition-coverage");
        var status = await schema.GetStatusAsync();
        var codec = new PostgreSqlOpaqueTestCodec("tests.flow.context", "v1");
        var payloads = new DurablePayloadCodecRegistry([codec]);
        var work = new DurableWorkRegistry([]);
        var registrations = new[]
        {
            new TransitionTestFlowRegistration(codec, "next", FlowTransitionKind.Next),
            new TransitionTestFlowRegistration(codec, "fault", FlowTransitionKind.Fault),
            new TransitionTestFlowRegistration(codec, "timed-out", FlowTransitionKind.TimedOut),
        };
        var flows = new DurableFlowRegistry(registrations, work, payloads);
        var options = new PostgreSqlDurableWorkOptions(epoch, status.StoreId);
        var client = new PostgreSqlDurableFlowClient(database.DataSource, flows, payloads, options);
        var processor = new PostgreSqlDurableFlowProcessor(
            database.DataSource,
            database.DataSource,
            flows,
            work,
            payloads,
            options);
        var scope = new DurableScopeId("slice4-transition-coverage");

        foreach (var registration in registrations)
        {
            Assert.True((await client.StartAsync(new DurableFlowStartRequest(
                scope,
                new DurableCommandId($"{registration.Scenario}-start"),
                $"{registration.Scenario}-key",
                new DurableFlowInstanceId(registration.Scenario),
                registration.FlowId,
                registration.FlowVersion,
                codec.EncodeObject(new byte[] { 1 })))).IsSuccess);
        }

        var results = new Dictionary<string, PostgreSqlFlowProcessingResult>(StringComparer.Ordinal);
        foreach (var candidate in await processor.DiscoverAsync(maximumCandidates: 10))
        {
            results[candidate.InstanceId.Value] = await processor.TryProcessAsync(
                candidate,
                $"transition-{candidate.InstanceId.Value}");
        }

        Assert.Equal(PostgreSqlFlowProcessingOutcome.Applied, results["next"].Outcome);
        Assert.Equal(DurableFlowState.Ready, results["next"].State);
        Assert.Equal(PostgreSqlFlowProcessingOutcome.Terminal, results["fault"].Outcome);
        Assert.Equal(DurableFlowState.Faulted, results["fault"].State);
        Assert.Equal(PostgreSqlFlowProcessingOutcome.Terminal, results["timed-out"].Outcome);
        Assert.Equal(DurableFlowState.Completed, results["timed-out"].State);
    }

    [Fact]
    public async Task Flow_LifecycleCommands_CoverFailureDuplicateRaceAndReleaseRecoveryPaths()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "tests", "slice4-lifecycle-coverage");
        var status = await schema.GetStatusAsync();
        var codec = new PostgreSqlOpaqueTestCodec("tests.flow.context", "v1");
        var payloads = new DurablePayloadCodecRegistry([codec]);
        var work = new DurableWorkRegistry([]);
        var registration = new WaitingTestFlowRegistration(codec);
        var flows = new DurableFlowRegistry([registration], work, payloads);
        var options = new PostgreSqlDurableWorkOptions(epoch, status.StoreId);
        var client = new PostgreSqlDurableFlowClient(database.DataSource, flows, payloads, options);
        var scope = new DurableScopeId("slice4-lifecycle-coverage");

        async ValueTask StartAsync(string id)
        {
            var accepted = await client.StartAsync(new DurableFlowStartRequest(
                scope,
                new DurableCommandId($"{id}-start"),
                $"{id}-key",
                new DurableFlowInstanceId(id),
                registration.FlowId,
                registration.FlowVersion,
                codec.EncodeObject(new byte[] { 1 })));
            Assert.True(accepted.IsSuccess);
        }

        async ValueTask SetSuspendedAsync(string id, string suspendedFromState = "ready", Guid? runtimeEpoch = null)
        {
            await using var update = database.DataSource.CreateCommand(
                """
                UPDATE appsurface_durable.flow_instance
                SET state = 'suspended', runtime_epoch = @runtime_epoch,
                    suspension_descriptor = '{}'::jsonb, suspended_from_state = @suspended_from_state,
                    terminal_at = NULL, terminal_code = NULL,
                    lease_owner = NULL, lease_started_at = NULL, lease_expires_at = NULL,
                    cancellation_requested_at = NULL, updated_at = clock_timestamp()
                WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id;
                """);
            update.Parameters.AddWithValue("runtime_epoch", runtimeEpoch ?? epoch);
            update.Parameters.AddWithValue("suspended_from_state", suspendedFromState);
            update.Parameters.AddWithValue("scope_id", scope.Value);
            update.Parameters.AddWithValue("flow_instance_id", id);
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }

        await StartAsync("scope-seed");
        var missingCancel = await client.CancelAsync(new DurableFlowCancelRequest(
            scope,
            new DurableCommandId("missing-cancel"),
            new DurableFlowInstanceId("missing"),
            "operator",
            "missing",
            1));
        Assert.Equal(DurableProblemCodes.FlowNotFound, missingCancel.Problem!.Code);

        await StartAsync("cancel-accepted");
        var cancel = new DurableFlowCancelRequest(
            scope,
            new DurableCommandId("cancel-accepted-command"),
            new DurableFlowInstanceId("cancel-accepted"),
            "operator",
            "requested",
            1);
        var canceled = await client.CancelAsync(cancel);
        Assert.Equal(DurableFlowCommandOutcome.Accepted, canceled.Value!.Outcome);
        Assert.Equal(DurableFlowState.Canceled, canceled.Value.State);
        Assert.Equal(
            DurableFlowCommandOutcome.Duplicate,
            (await client.CancelAsync(cancel)).Value!.Outcome);
        var changedCancel = await client.CancelAsync(new DurableFlowCancelRequest(
            scope,
            cancel.CommandId,
            cancel.InstanceId,
            cancel.ActorId,
            "changed-reason",
            1));
        Assert.Equal(DurableProblemCodes.FlowCommandConflict, changedCancel.Problem!.Code);
        Assert.Equal(
            DurableFlowCommandOutcome.AlreadyTerminal,
            (await client.CancelAsync(new DurableFlowCancelRequest(
                scope,
                new DurableCommandId("cancel-terminal-command"),
                cancel.InstanceId,
                "operator",
                "again",
                canceled.Value.Revision))).Value!.Outcome);

        await StartAsync("cancel-race");
        var cancelRace = await client.CancelAsync(new DurableFlowCancelRequest(
            scope,
            new DurableCommandId("cancel-race-command"),
            new DurableFlowInstanceId("cancel-race"),
            "operator",
            "stale",
            2));
        Assert.Equal(DurableFlowCommandOutcome.RaceLost, cancelRace.Value!.Outcome);

        await StartAsync("release-state-mismatch");
        var stateMismatch = await client.ReleaseSuspensionAsync(new DurableFlowReleaseRequest(
            scope,
            new DurableCommandId("release-state-mismatch-command"),
            new DurableFlowInstanceId("release-state-mismatch"),
            "operator",
            "recover",
            1));
        Assert.Equal(DurableProblemCodes.FlowReleaseStateMismatch, stateMismatch.Problem!.Code);

        await StartAsync("release-suspended");
        await SetSuspendedAsync("release-suspended");
        var release = new DurableFlowReleaseRequest(
            scope,
            new DurableCommandId("release-suspended-command"),
            new DurableFlowInstanceId("release-suspended"),
            "operator",
            "recover",
            1);
        var released = await client.ReleaseSuspensionAsync(release);
        Assert.Equal(DurableFlowCommandOutcome.Accepted, released.Value!.Outcome);
        Assert.Equal(DurableFlowState.Ready, released.Value.State);
        Assert.Equal(
            DurableFlowCommandOutcome.Duplicate,
            (await client.ReleaseSuspensionAsync(release)).Value!.Outcome);

        await StartAsync("release-old-epoch");
        await using (var age = database.DataSource.CreateCommand(
            """
            UPDATE appsurface_durable.flow_instance
            SET runtime_epoch = @runtime_epoch
            WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id;
            """))
        {
            age.Parameters.AddWithValue("runtime_epoch", Guid.NewGuid());
            age.Parameters.AddWithValue("scope_id", scope.Value);
            age.Parameters.AddWithValue("flow_instance_id", "release-old-epoch");
            Assert.Equal(1, await age.ExecuteNonQueryAsync());
        }
        var oldEpochRelease = await client.ReleaseSuspensionAsync(new DurableFlowReleaseRequest(
            scope,
            new DurableCommandId("release-old-epoch-command"),
            new DurableFlowInstanceId("release-old-epoch"),
            "operator",
            "adopt",
            1));
        Assert.Equal(DurableFlowCommandOutcome.Accepted, oldEpochRelease.Value!.Outcome);
        Assert.Equal(DurableFlowState.Ready, oldEpochRelease.Value.State);

        await StartAsync("release-manifest-mismatch");
        await SetSuspendedAsync("release-manifest-mismatch");
        var mismatchedRegistration = new ManifestMismatchFlowRegistration(codec);
        var mismatchedFlows = new DurableFlowRegistry([mismatchedRegistration], work, payloads);
        var mismatchedClient = new PostgreSqlDurableFlowClient(
            database.DataSource,
            mismatchedFlows,
            payloads,
            options);
        var manifestMismatch = await mismatchedClient.ReleaseSuspensionAsync(new DurableFlowReleaseRequest(
            scope,
            new DurableCommandId("release-manifest-mismatch-command"),
            new DurableFlowInstanceId("release-manifest-mismatch"),
            "operator",
            "recover",
            1));
        Assert.Equal(DurableProblemCodes.FlowReleaseManifestMismatch, manifestMismatch.Problem!.Code);

        var disabledScope = new DurableScopeId("slice4-lifecycle-disabled");
        var disabledInstance = new DurableFlowInstanceId("disabled-existing");
        Assert.True((await client.StartAsync(new DurableFlowStartRequest(
            disabledScope,
            new DurableCommandId("disabled-existing-start"),
            "disabled-existing-key",
            disabledInstance,
            registration.FlowId,
            registration.FlowVersion,
            codec.EncodeObject(new byte[] { 1 })))).IsSuccess);
        var workStore = new PostgreSqlDurableWorkStore(database.DataSource, epoch);
        Assert.Equal(
            PostgreSqlScopeMutationOutcome.Applied,
            (await workStore.DisableScopeAsync(disabledScope, "operator", "disabled", 1)).Outcome);
        Assert.Equal(
            DurableProblemCodes.ScopeDisabled,
            (await client.StartAsync(new DurableFlowStartRequest(
                disabledScope,
                new DurableCommandId("disabled-new-start"),
                "disabled-new-key",
                new DurableFlowInstanceId("disabled-new"),
                registration.FlowId,
                registration.FlowVersion,
                codec.EncodeObject(new byte[] { 1 })))).Problem!.Code);
        Assert.Equal(
            DurableProblemCodes.ScopeDisabled,
            (await client.RaiseEventAsync(new DurableFlowEventRequest(
                disabledScope,
                new DurableCommandId("disabled-event"),
                new DurableFlowEventId("disabled-event-id"),
                disabledInstance,
                "approved"))).Problem!.Code);
        Assert.Equal(
            DurableProblemCodes.ScopeDisabled,
            (await client.CancelAsync(new DurableFlowCancelRequest(
                disabledScope,
                new DurableCommandId("disabled-cancel"),
                disabledInstance,
                "operator",
                "disabled",
                1))).Problem!.Code);
        Assert.Equal(
            DurableProblemCodes.ScopeDisabled,
            (await client.ReleaseSuspensionAsync(new DurableFlowReleaseRequest(
                disabledScope,
                new DurableCommandId("disabled-release"),
                disabledInstance,
                "operator",
                "disabled",
                1))).Problem!.Code);
    }

    [Fact]
    public async Task Flow_Processor_SuspendsEvaluationFailureManifestMismatchAndInvalidDecision()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "tests", "slice4-suspension-coverage");
        var status = await schema.GetStatusAsync();
        var codec = new PostgreSqlOpaqueTestCodec("tests.flow.context", "v1");
        var payloads = new DurablePayloadCodecRegistry([codec]);
        var work = new DurableWorkRegistry([]);
        var throwing = new SuspensionTestFlowRegistration(codec, "throwing", SuspensionScenario.Throw);
        var invalid = new SuspensionTestFlowRegistration(codec, "invalid", SuspensionScenario.InvalidDecision);
        var compatible = new SuspensionTestFlowRegistration(codec, "manifest", SuspensionScenario.Complete);
        var acceptedFlows = new DurableFlowRegistry([throwing, invalid, compatible], work, payloads);
        var options = new PostgreSqlDurableWorkOptions(epoch, status.StoreId);
        var client = new PostgreSqlDurableFlowClient(database.DataSource, acceptedFlows, payloads, options);
        var mismatched = new SuspensionTestFlowRegistration(
            codec,
            "manifest",
            SuspensionScenario.Complete,
            implementationVersion: "tests-manifest-v2");
        var processingFlows = new DurableFlowRegistry([throwing, invalid, mismatched], work, payloads);
        var processor = new PostgreSqlDurableFlowProcessor(
            database.DataSource,
            database.DataSource,
            processingFlows,
            work,
            payloads,
            options);
        var scope = new DurableScopeId("slice4-suspension-coverage");

        foreach (var registration in new[] { throwing, invalid, compatible })
        {
            Assert.True((await client.StartAsync(new DurableFlowStartRequest(
                scope,
                new DurableCommandId($"{registration.Scenario}-suspension-start"),
                $"{registration.Scenario}-suspension-key",
                new DurableFlowInstanceId(registration.Scenario),
                registration.FlowId,
                registration.FlowVersion,
                codec.EncodeObject(new byte[] { 1 })))).IsSuccess);
        }

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await processor.DiscoverAsync(maximumCandidates: 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await processor.DiscoverAsync(maximumCandidates: 1_001));

        var candidates = await processor.DiscoverAsync(maximumCandidates: 10);
        var results = new Dictionary<string, PostgreSqlFlowProcessingResult>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () =>
                await processor.TryProcessAsync(candidate, " "));
            await Assert.ThrowsAsync<ArgumentException>(async () =>
                await processor.TryProcessAsync(candidate, new string('x', 201)));
            results[candidate.InstanceId.Value] = await processor.TryProcessAsync(
                candidate,
                $"suspension-{candidate.InstanceId.Value}");
        }

        Assert.Equal("flow.evaluation_failed", results["throwing"].ProblemCode);
        Assert.Equal("flow.evaluation_invalid", results["invalid"].ProblemCode);
        Assert.Equal("flow.manifest_incompatible", results["manifest"].ProblemCode);
        Assert.All(results.Values, result =>
        {
            Assert.Equal(PostgreSqlFlowProcessingOutcome.Suspended, result.Outcome);
            Assert.Equal(DurableFlowState.Suspended, result.State);
        });
        await using (var suspendedFrom = database.DataSource.CreateCommand(
            """
            SELECT suspended_from_state
            FROM appsurface_durable.flow_instance
            WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id;
            """))
        {
            suspendedFrom.Parameters.AddWithValue("scope_id", scope.Value);
            suspendedFrom.Parameters.AddWithValue("flow_instance_id", "throwing");
            Assert.Equal("ready", (string?)await suspendedFrom.ExecuteScalarAsync());
        }
        Assert.Equal(
            PostgreSqlFlowProcessingOutcome.NotClaimed,
            (await processor.TryProcessAsync(candidates[0], "suspension-retry")).Outcome);
    }

    [Fact]
    public async Task Flow_EventContractAndEventWinner_CoverEveryPayloadMismatchAndTimerRaceLoss()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "tests", "slice4-event-contract-coverage");
        var status = await schema.GetStatusAsync();
        var codec = new PostgreSqlOpaqueTestCodec("tests.flow.context", "v1");
        var payloads = new DurablePayloadCodecRegistry([codec]);
        var work = new DurableWorkRegistry([]);
        var requiredRegistration = new RequiredPayloadWaitingRegistration(codec);
        var flows = new DurableFlowRegistry([requiredRegistration], work, payloads);
        var options = new PostgreSqlDurableWorkOptions(epoch, status.StoreId);
        var client = new PostgreSqlDurableFlowClient(database.DataSource, flows, payloads, options);
        var processor = new PostgreSqlDurableFlowProcessor(
            database.DataSource,
            database.DataSource,
            flows,
            work,
            payloads,
            options);
        var store = new PostgreSqlDurableFlowStore(database.DataSource, options);
        var scope = new DurableScopeId("slice4-event-contract-coverage");
        var instance = new DurableFlowInstanceId("required-payload");
        Assert.True((await client.StartAsync(new DurableFlowStartRequest(
            scope,
            new DurableCommandId("required-payload-start"),
            "required-payload-key",
            instance,
            requiredRegistration.FlowId,
            requiredRegistration.FlowVersion,
            codec.EncodeObject(new byte[] { 1 })))).IsSuccess);
        var requiredWait = await processor.TryProcessAsync(
            Assert.Single(await processor.DiscoverAsync()),
            "required-payload-worker");
        Assert.Equal(DurableFlowState.WaitingForEvent, requiredWait.State);

        DurableEncodedPayload Payload(
            string contractName = "tests.flow.event",
            string contractVersion = "v1",
            DurableDataClassification classification = DurableDataClassification.ApprovedApplication,
            string retention = DurableEncodedPayload.DefaultRetentionPolicyId) =>
            new(contractName, contractVersion, classification, new byte[] { 9 }, retention);

        var mismatches = new DurableEncodedPayload?[]
        {
            null,
            Payload(contractName: "tests.flow.other"),
            Payload(contractVersion: "v2"),
            Payload(classification: DurableDataClassification.Operational),
            Payload(retention: "tests-short"),
        };
        for (var index = 0; index < mismatches.Length; index++)
        {
            var mismatch = await store.RaiseEventAsync(
                new DurableFlowEventRequest(
                    scope,
                    new DurableCommandId($"required-payload-mismatch-{index}"),
                    new DurableFlowEventId($"required-payload-mismatch-event-{index}"),
                    instance,
                    "approved",
                    mismatches[index],
                    expectedRevision: requiredWait.Revision),
                CancellationToken.None);
            Assert.Equal(DurableProblemCodes.FlowEventContractMismatch, mismatch.Problem!.Code);
        }

        var accepted = await store.RaiseEventAsync(
            new DurableFlowEventRequest(
                scope,
                new DurableCommandId("required-payload-accepted"),
                new DurableFlowEventId("required-payload-accepted-event"),
                instance,
                "approved",
                Payload(),
                expectedRevision: requiredWait.Revision),
            CancellationToken.None);
        Assert.Equal(DurableFlowCommandOutcome.Accepted, accepted.Value!.Outcome);

        var timerRegistration = new TimerTestFlowRegistration(codec);
        var timerFlows = new DurableFlowRegistry([timerRegistration], work, payloads);
        var timerClient = new PostgreSqlDurableFlowClient(database.DataSource, timerFlows, payloads, options);
        var timerProcessor = new PostgreSqlDurableFlowProcessor(
            database.DataSource,
            database.DataSource,
            timerFlows,
            work,
            payloads,
            options);
        var timerInstance = new DurableFlowInstanceId("event-wins-timer");
        Assert.True((await timerClient.StartAsync(new DurableFlowStartRequest(
            scope,
            new DurableCommandId("event-wins-timer-start"),
            "event-wins-timer-key",
            timerInstance,
            timerRegistration.FlowId,
            timerRegistration.FlowVersion,
            codec.EncodeObject(new byte[] { 2 })))).IsSuccess);
        var waiting = await timerProcessor.TryProcessAsync(
            Assert.Single(
                await timerProcessor.DiscoverAsync(),
                candidate => candidate.InstanceId == timerInstance),
            "event-wins-timer-register");
        await ForceTimerDueAsync(database.DataSource, scope, timerInstance);
        var staleTimer = Assert.Single(
            await timerProcessor.DiscoverAsync(),
            candidate => candidate.Kind == PostgreSqlFlowDispatchKind.Timer);
        var eventWinner = await timerClient.RaiseEventAsync(new DurableFlowEventRequest(
            scope,
            new DurableCommandId("event-wins-timer-command"),
            new DurableFlowEventId("event-wins-timer-event"),
            timerInstance,
            "approved",
            expectedRevision: waiting.Revision));
        Assert.Equal(DurableFlowCommandOutcome.Accepted, eventWinner.Value!.Outcome);
        Assert.Equal(
            PostgreSqlFlowProcessingOutcome.RaceLost,
            (await timerProcessor.TryProcessAsync(staleTimer, "event-wins-stale-timer")).Outcome);
    }

    private static async ValueTask ForceTimerDueAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scope,
        DurableFlowInstanceId instance)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE appsurface_durable.flow_timer
            SET due_at = clock_timestamp() - interval '1 second', updated_at = clock_timestamp()
            WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id AND state = 'scheduled';

            UPDATE appsurface_durable.flow_dispatch
            SET due_at = clock_timestamp() - interval '1 second', updated_at = clock_timestamp()
            WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id
              AND kind = 'timer' AND state IN ('available', 'leased');
            """);
        command.Parameters.AddWithValue("scope_id", scope.Value);
        command.Parameters.AddWithValue("flow_instance_id", instance.Value);
        Assert.Equal(2, await command.ExecuteNonQueryAsync());
    }

    private static async ValueTask<long> CountAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scope,
        string workName)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT count(*)
            FROM appsurface_durable.work
            WHERE scope_id = @scope_id AND work_name = @work_name;
            """);
        command.Parameters.AddWithValue("scope_id", scope.Value);
        command.Parameters.AddWithValue("work_name", workName);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async ValueTask<long> CountFlowTraceContextsAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scope,
        DurableFlowInstanceId instance)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT count(*)
            FROM appsurface_durable.flow_trace_context
            WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id;
            """);
        command.Parameters.AddWithValue("scope_id", scope.Value);
        command.Parameters.AddWithValue("flow_instance_id", instance.Value);
        return (long)(await command.ExecuteScalarAsync())!;
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

    private class WaitingTestFlowRegistration(IDurablePayloadCodec contextCodec) : DurableFlowRegistration
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

    private sealed class InvalidActivityPayloadFlowRegistration : DurableFlowRegistration
    {
        private readonly IDurablePayloadCodec _contextCodec;
        private readonly DurableWorkRegistration _workRegistration;

        internal InvalidActivityPayloadFlowRegistration(
            IDurablePayloadCodec contextCodec,
            DurableWorkRegistration workRegistration)
        {
            _contextCodec = contextCodec;
            _workRegistration = workRegistration;
        }

        public override string FlowId => "tests.invalid-activity-payload-flow";

        public override string FlowVersion => "v1";

        public override string ImplementationVersion => "tests-invalid-activity-payload-v1";

        public override string StartNodeId => "activity";

        public override string DefinitionFingerprint => new('e', 64);

        public override IDurablePayloadCodec ContextCodec => _contextCodec;

        public override IReadOnlyList<DurableFlowEventBinding> EventBindings => [];

        public override IReadOnlyList<DurableWorkRegistration> ActivityWorkRegistrations => [_workRegistration];

        public override ValueTask<DurableFlowEvaluationResult> EvaluateAsync(
            DurableFlowEvaluationInput input,
            IDurablePayloadCodecRegistry payloadCodecs,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new DurableFlowEvaluationResult(
                FlowTransitionKind.Activity,
                input.NodeId,
                input.Context,
                null,
                null,
                null,
                null,
                new DurableFlowActivityCommand(
                    "invalid-payload",
                    1,
                    _workRegistration.WorkName,
                    _workRegistration.WorkVersion,
                    _workRegistration.ProviderSafety,
                    _contextCodec.EncodeObject(new byte[] { 2 }))));
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

    private sealed class TransitionTestFlowRegistration(
        IDurablePayloadCodec contextCodec,
        string scenario,
        FlowTransitionKind transitionKind) : DurableFlowRegistration
    {
        internal string Scenario { get; } = scenario;

        public override string FlowId => $"tests.{Scenario}-flow";

        public override string FlowVersion => "v1";

        public override string ImplementationVersion => $"tests-{Scenario}-v1";

        public override string StartNodeId => "start";

        public override string DefinitionFingerprint =>
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(Scenario)));

        public override IDurablePayloadCodec ContextCodec { get; } = contextCodec;

        public override IReadOnlyList<DurableFlowEventBinding> EventBindings => [];

        public override IReadOnlyList<DurableWorkRegistration> ActivityWorkRegistrations => [];

        public override ValueTask<DurableFlowEvaluationResult> EvaluateAsync(
            DurableFlowEvaluationInput input,
            IDurablePayloadCodecRegistry payloadCodecs,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new DurableFlowEvaluationResult(
                transitionKind,
                input.NodeId,
                context: null,
                transitionKind == FlowTransitionKind.Next ? "next" : null,
                eventName: null,
                timeout: null,
                transitionKind == FlowTransitionKind.Fault
                    ? new FlowFault("tests.transition-fault", "The coverage transition faulted.")
                    : null,
                activity: null));
    }

    private sealed class ManifestMismatchFlowRegistration(IDurablePayloadCodec contextCodec)
        : WaitingTestFlowRegistration(contextCodec)
    {
        public override string ImplementationVersion => "tests-manifest-mismatch-v2";
    }

    private enum SuspensionScenario
    {
        Complete,
        Throw,
        InvalidDecision,
    }

    private sealed class SuspensionTestFlowRegistration(
        IDurablePayloadCodec contextCodec,
        string scenario,
        SuspensionScenario behavior,
        string implementationVersion = "tests-suspension-v1") : DurableFlowRegistration
    {
        internal string Scenario { get; } = scenario;

        public override string FlowId => $"tests.suspension-{Scenario}";

        public override string FlowVersion => "v1";

        public override string ImplementationVersion { get; } = implementationVersion;

        public override string StartNodeId => "start";

        public override string DefinitionFingerprint =>
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(Scenario)));

        public override IDurablePayloadCodec ContextCodec { get; } = contextCodec;

        public override IReadOnlyList<DurableFlowEventBinding> EventBindings => [];

        public override IReadOnlyList<DurableWorkRegistration> ActivityWorkRegistrations => [];

        public override ValueTask<DurableFlowEvaluationResult> EvaluateAsync(
            DurableFlowEvaluationInput input,
            IDurablePayloadCodecRegistry payloadCodecs,
            CancellationToken cancellationToken = default) =>
            behavior switch
            {
                SuspensionScenario.Throw => throw new InvalidOperationException("The test evaluator failed."),
                SuspensionScenario.InvalidDecision => ValueTask.FromResult(new DurableFlowEvaluationResult(
                    FlowTransitionKind.Wait,
                    input.NodeId,
                    input.Context,
                    nextNodeId: null,
                    eventName: "invalid",
                    timeout: null,
                    fault: null,
                    activity: null,
                    eventContract: null)),
                _ => ValueTask.FromResult(new DurableFlowEvaluationResult(
                    FlowTransitionKind.Complete,
                    input.NodeId,
                    input.Context,
                    nextNodeId: null,
                    eventName: null,
                    timeout: null,
                    fault: null,
                    activity: null)),
            };
    }

    private sealed class RequiredPayloadWaitingRegistration(IDurablePayloadCodec contextCodec)
        : DurableFlowRegistration
    {
        public override string FlowId => "tests.required-payload-flow";

        public override string FlowVersion => "v1";

        public override string ImplementationVersion => "tests-required-payload-v1";

        public override string StartNodeId => "start";

        public override string DefinitionFingerprint => new('d', 64);

        public override IDurablePayloadCodec ContextCodec { get; } = contextCodec;

        public override IReadOnlyList<DurableFlowEventBinding> EventBindings => [];

        public override IReadOnlyList<DurableWorkRegistration> ActivityWorkRegistrations => [];

        public override ValueTask<DurableFlowEvaluationResult> EvaluateAsync(
            DurableFlowEvaluationInput input,
            IDurablePayloadCodecRegistry payloadCodecs,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new DurableFlowEvaluationResult(
                FlowTransitionKind.Wait,
                input.NodeId,
                input.Context,
                nextNodeId: null,
                eventName: "approved",
                timeout: null,
                fault: null,
                activity: null,
                new DurableFlowEventContract(
                    payloadRequired: true,
                    "tests.flow.event",
                    "v1",
                    DurableDataClassification.ApprovedApplication,
                    DurableEncodedPayload.DefaultRetentionPolicyId)));
    }

    private sealed class MismatchedPayloadCodecRegistry(IDurablePayloadCodec codec) : IDurablePayloadCodecRegistry
    {
        public void Register(IDurablePayloadCodec _) =>
            throw new NotSupportedException("The test registry resolves a fixed mismatched codec.");

        public IDurablePayloadCodec GetRequired(Type payloadType) => codec;

        public IDurablePayloadCodec GetRequired(Type payloadType, string contractName, string contractVersion) => codec;

        public IDurablePayloadCodec GetRequired(string contractName, string contractVersion) => codec;
    }

    private sealed class RecordingFlowBarrierObserver : IPostgreSqlDurableFlowBarrierObserver
    {
        internal List<string> Barriers { get; } = [];

        public ValueTask ObserveAsync(
            string barrier,
            DurableScopeId scopeId,
            DurableFlowInstanceId instanceId,
            long revision,
            PostgreSqlFlowTelemetryEvidence? traceEvidence,
            CancellationToken cancellationToken)
        {
            Barriers.Add(barrier);
            return ValueTask.CompletedTask;
        }
    }

}
