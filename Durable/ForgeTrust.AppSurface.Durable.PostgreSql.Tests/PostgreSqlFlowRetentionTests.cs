using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.PostgreSql;
using ForgeTrust.AppSurface.Durable.Provider;
using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

public sealed class PostgreSqlFlowRetentionTests
{
    [Fact]
    public async Task Public_reads_reject_empty_scope_and_manifest_identifiers_before_opening_a_connection()
    {
        await using var dataSource = NpgsqlDataSource.Create(
            "Host=localhost;Database=not-opened;Username=not-opened;Password=not-opened");
        var schema = new PostgreSqlDurableRuntimeSchemaManager(dataSource);
        var client = new PostgreSqlDurableFlowRetentionClient(dataSource, schema);
        var scope = new DurableScopeId("retention-read-validation");
        var manifest = new DurableRetentionManifestId("retention-read-validation-manifest");

        await Assert.ThrowsAsync<ArgumentException>(async () => await client.GetManifestAsync(default, manifest));
        await Assert.ThrowsAsync<ArgumentException>(async () => await client.GetManifestAsync(scope, default));
        await Assert.ThrowsAsync<ArgumentException>(async () => await client.BuildArchivePackageAsync(default, manifest));
        await Assert.ThrowsAsync<ArgumentException>(async () => await client.BuildArchivePackageAsync(scope, default));
    }

    [Fact]
    public void Procedure_result_mappers_preserve_all_documented_outcomes()
    {
        var scope = new DurableScopeId("retention-procedure-mappers");
        var flow = new DurableFlowInstanceId("retention-procedure-mappers-flow");
        var assessment = new DurableRetentionAssessment(
            scope,
            flow,
            DurableRetentionAssessmentStatus.Safe,
            DurableRetentionAssessmentReason.Safe,
            new DurableRetentionDigest("durable-flow-closure-v1", new string('1', 64)),
            new DurableRetentionDigest("durable-flow-source-watermark-v1", new string('2', 64)),
            1,
            1);
        var create = new DurableRetentionManifestCreateRequest(new DurableCommandId("retention-procedure-create"), assessment);
        var manifest = new DurableRetentionManifestId("retention-procedure-manifest");
        var mutation = new DurableRetentionPurgeRequest(
            scope,
            manifest,
            new DurableCommandId("retention-procedure-purge"),
            "operator",
            "purge",
            1);

        Assert.Equal(
            DurableProblemCodes.CommandConflict,
            PostgreSqlDurableFlowRetentionClient.MapManifestCreateProcedureOutcome(create, "command_conflict")!.Problem!.Code);
        Assert.Equal(
            DurableProblemCodes.RetentionSourceChanged,
            PostgreSqlDurableFlowRetentionClient.MapManifestCreateProcedureOutcome(create, "source_rejected")!.Problem!.Code);
        Assert.Equal(
            DurableProblemCodes.RetentionSourceChanged,
            PostgreSqlDurableFlowRetentionClient.MapManifestCreateProcedureOutcome(create, "scope_rejected")!.Problem!.Code);
        Assert.Equal(
            DurableProblemCodes.RetentionLifecycleRejected,
            PostgreSqlDurableFlowRetentionClient.MapManifestCreateProcedureOutcome(create, "lifecycle_rejected")!.Problem!.Code);
        Assert.Null(PostgreSqlDurableFlowRetentionClient.MapManifestCreateProcedureOutcome(create, "created"));

        Assert.Equal(
            DurableRetentionMutationOutcome.Applied,
            PostgreSqlDurableFlowRetentionClient.MapLifecycleProcedureOutcome(mutation, "applied", "verified", 1).Value!.Outcome);
        Assert.Equal(
            DurableRetentionMutationOutcome.Duplicate,
            PostgreSqlDurableFlowRetentionClient.MapLifecycleProcedureOutcome(mutation, "duplicate", "held", 2).Value!.Outcome);
        Assert.Equal(
            DurableRetentionMutationOutcome.AlreadyPurged,
            PostgreSqlDurableFlowRetentionClient.MapLifecycleProcedureOutcome(mutation, "already_purged", null, 3).Value!.Outcome);
        Assert.Equal(
            DurableProblemCodes.CommandConflict,
            PostgreSqlDurableFlowRetentionClient.MapLifecycleProcedureOutcome(mutation, "command_conflict", null, null).Problem!.Code);
        Assert.Equal(
            DurableProblemCodes.RetentionManifestNotFound,
            PostgreSqlDurableFlowRetentionClient.MapLifecycleProcedureOutcome(mutation, "manifest_not_found", null, null).Problem!.Code);
        Assert.Equal(
            DurableProblemCodes.RetentionLifecycleConflict,
            PostgreSqlDurableFlowRetentionClient.MapLifecycleProcedureOutcome(mutation, "lifecycle_conflict", null, null).Problem!.Code);
        Assert.Equal(
            DurableProblemCodes.RetentionLifecycleRejected,
            PostgreSqlDurableFlowRetentionClient.MapLifecycleProcedureOutcome(mutation, "lifecycle_rejected", null, null).Problem!.Code);
    }

    [Fact]
    public async Task Public_reads_and_lifecycle_report_a_missing_manifest()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var scope = new DurableScopeId("retention-missing-manifest");
        var manifest = new DurableRetentionManifestId("retention-missing-manifest-id");
        var client = new PostgreSqlDurableFlowRetentionClient(database.DataSource, schema);

        var read = await client.GetManifestAsync(scope, manifest);
        var package = await client.BuildArchivePackageAsync(scope, manifest);
        var purge = await client.PurgeAsync(new DurableRetentionPurgeRequest(
            scope,
            manifest,
            new DurableCommandId("retention-missing-manifest-purge"),
            "operator",
            "purge",
            1));

        Assert.Equal(DurableProblemCodes.RetentionManifestNotFound, read.Problem!.Code);
        Assert.Equal(DurableProblemCodes.RetentionManifestNotFound, package.Problem!.Code);
        Assert.Equal(DurableProblemCodes.RetentionManifestNotFound, purge.Problem!.Code);
    }

    [Fact]
    public async Task Concurrent_identical_manifest_commands_return_created_and_duplicate()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var scope = new DurableScopeId("retention-concurrent");
        var flow = new DurableFlowInstanceId("retention-concurrent-flow");
        await SeedTerminalFlowAsync(database.DataSource, scope, flow);
        var client = new PostgreSqlDurableFlowRetentionClient(database.DataSource, schema);
        var assessment = (await client.AssessAsync(new DurableRetentionAssessmentRequest(scope, flow))).Value!;
        var request = new DurableRetentionManifestCreateRequest(new DurableCommandId("retention-concurrent-create"), assessment);

        var results = await Task.WhenAll(
            client.CreateManifestAsync(request).AsTask(),
            client.CreateManifestAsync(request).AsTask());

        Assert.All(results, result => Assert.True(result.IsSuccess));
        Assert.Single(results, result => result.Value!.Outcome == DurableRetentionManifestCreateOutcome.Created);
        Assert.Single(results, result => result.Value!.Outcome == DurableRetentionManifestCreateOutcome.Duplicate);
        Assert.Single(results.Select(result => result.Value!.Manifest.ManifestId).Distinct());
        var changedAssessment = new DurableRetentionAssessment(
            scope,
            flow,
            DurableRetentionAssessmentStatus.Safe,
            DurableRetentionAssessmentReason.Safe,
            assessment.ClosureDigest,
            assessment.SourceWatermark,
            assessment.ClosureItemCount,
            assessment.ArchiveByteCount + 1);
        var conflict = await client.CreateManifestAsync(new DurableRetentionManifestCreateRequest(
            new DurableCommandId("retention-concurrent-create"), changedAssessment));
        Assert.False(conflict.IsSuccess);
        Assert.Equal(DurableProblemCodes.CommandConflict, conflict.Problem!.Code);
    }

    [Fact]
    public async Task Source_change_after_manifest_blocks_archive_package()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var scope = new DurableScopeId("retention-source-change");
        var flow = new DurableFlowInstanceId("retention-source-change-flow");
        await SeedTerminalFlowAsync(database.DataSource, scope, flow);
        var client = new PostgreSqlDurableFlowRetentionClient(database.DataSource, schema);
        var assessment = (await client.AssessAsync(new DurableRetentionAssessmentRequest(scope, flow))).Value!;
        var manifest = (await client.CreateManifestAsync(new DurableRetentionManifestCreateRequest(
            new DurableCommandId("retention-source-create"), assessment))).Value!.Manifest;
        await using (var mutate = database.DataSource.CreateCommand(
            "UPDATE appsurface_durable.flow_instance SET current_node_id = 'changed', updated_at = clock_timestamp() WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id;"))
        {
            mutate.Parameters.AddWithValue("scope_id", scope.Value);
            mutate.Parameters.AddWithValue("flow_instance_id", flow.Value);
            Assert.Equal(1, await mutate.ExecuteNonQueryAsync());
        }

        var package = await client.BuildArchivePackageAsync(scope, manifest.ManifestId);

        Assert.False(package.IsSuccess);
        Assert.Equal(DurableProblemCodes.RetentionSourceChanged, package.Problem!.Code);
    }

    [Fact]
    public async Task Public_lifecycle_rejections_preserve_a_frozen_manifest()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var scope = new DurableScopeId("retention-rejections");
        var flow = new DurableFlowInstanceId("retention-rejections-flow");
        var missingManifest = new DurableRetentionManifestId("retention-missing-manifest");
        await SeedTerminalFlowAsync(database.DataSource, scope, flow);
        var client = new PostgreSqlDurableFlowRetentionClient(database.DataSource, schema);

        var missing = await client.GetManifestAsync(scope, missingManifest);
        Assert.False(missing.IsSuccess);
        Assert.Equal(DurableProblemCodes.RetentionManifestNotFound, missing.Problem!.Code);
        var missingPackage = await client.BuildArchivePackageAsync(scope, missingManifest);
        Assert.False(missingPackage.IsSuccess);
        Assert.Equal(DurableProblemCodes.RetentionManifestNotFound, missingPackage.Problem!.Code);
        var missingVerification = await client.VerifyArchiveAsync(new DurableRetentionVerifyArchiveRequest(
            scope, missingManifest, new DurableCommandId("retention-missing-verify"), "operator", "verify", 1));
        Assert.False(missingVerification.IsSuccess);
        Assert.Equal(DurableProblemCodes.RetentionManifestNotFound, missingVerification.Problem!.Code);

        var assessment = (await client.AssessAsync(new DurableRetentionAssessmentRequest(scope, flow))).Value!;
        var manifest = (await client.CreateManifestAsync(new DurableRetentionManifestCreateRequest(
            new DurableCommandId("retention-rejections-create"), assessment))).Value!.Manifest;
        var earlyVerification = await client.VerifyArchiveAsync(new DurableRetentionVerifyArchiveRequest(
            scope, manifest.ManifestId, new DurableCommandId("retention-early-verify"), "operator", "verify", 1));
        Assert.False(earlyVerification.IsSuccess);
        Assert.Equal(DurableProblemCodes.RetentionLifecycleRejected, earlyVerification.Problem!.Code);
        var mismatchedReceipt = new DurableArchiveReceiptV1(
            "retention-mismatched-receipt",
            new DurableRetentionDigest("durable-flow-archive-v1", new string('3', 64)),
            new DurableRetentionDigest("durable-flow-closure-v1", new string('4', 64)),
            manifest.ClosureItemCount);
        var rejectedReceipt = await client.RecordArchiveReceiptAsync(new DurableRetentionRecordArchiveReceiptRequest(
            scope, manifest.ManifestId, new DurableCommandId("retention-mismatched-receipt"), "operator", "archive", 1, mismatchedReceipt));
        Assert.False(rejectedReceipt.IsSuccess);
        Assert.Equal(DurableProblemCodes.RetentionLifecycleRejected, rejectedReceipt.Problem!.Code);
        var rejectedRelease = await client.SetHoldAsync(new DurableRetentionHoldRequest(
            scope, manifest.ManifestId, new DurableCommandId("retention-early-release"), "operator", "release", 1, placeHold: false));
        Assert.False(rejectedRelease.IsSuccess);
        Assert.Equal(DurableProblemCodes.RetentionLifecycleRejected, rejectedRelease.Problem!.Code);
        var rejectedPurge = await client.PurgeAsync(new DurableRetentionPurgeRequest(
            scope, manifest.ManifestId, new DurableCommandId("retention-early-purge"), "operator", "purge", 1));
        Assert.False(rejectedPurge.IsSuccess);
        Assert.Equal(DurableProblemCodes.RetentionLifecycleRejected, rejectedPurge.Problem!.Code);
        var stored = await client.GetManifestAsync(scope, manifest.ManifestId);
        Assert.Equal(DurableRetentionManifestState.Frozen, stored.Value!.State);
    }

    [Fact]
    public async Task Assessment_and_receipt_verification_reject_changed_or_mismatched_source_facts()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var scope = new DurableScopeId("retention-stale-facts");
        var changedFlow = new DurableFlowInstanceId("retention-changed-before-freeze");
        var receiptFlow = new DurableFlowInstanceId("retention-mismatched-package");
        var client = new PostgreSqlDurableFlowRetentionClient(database.DataSource, schema);

        var missing = await client.AssessAsync(new DurableRetentionAssessmentRequest(scope, new DurableFlowInstanceId("retention-not-found")));
        Assert.Equal(DurableRetentionAssessmentStatus.Blocked, missing.Value!.Status);
        Assert.Equal(DurableRetentionAssessmentReason.FlowNotFound, missing.Value.Reason);

        await SeedTerminalFlowAsync(database.DataSource, scope, changedFlow);
        var staleAssessment = (await client.AssessAsync(new DurableRetentionAssessmentRequest(scope, changedFlow))).Value!;
        await UpdateCurrentNodeAsync(database.DataSource, scope, changedFlow, "changed-before-freeze");
        var staleCreate = await client.CreateManifestAsync(new DurableRetentionManifestCreateRequest(
            new DurableCommandId("retention-stale-create"), staleAssessment));
        Assert.False(staleCreate.IsSuccess);
        Assert.Equal(DurableProblemCodes.RetentionSourceChanged, staleCreate.Problem!.Code);

        await SeedTerminalFlowAsync(database.DataSource, scope, receiptFlow);
        var assessment = (await client.AssessAsync(new DurableRetentionAssessmentRequest(scope, receiptFlow))).Value!;
        var manifest = (await client.CreateManifestAsync(new DurableRetentionManifestCreateRequest(
            new DurableCommandId("retention-mismatched-package-create"), assessment))).Value!.Manifest;
        var package = (await client.BuildArchivePackageAsync(scope, manifest.ManifestId)).Value!;
        var receipt = new DurableArchiveReceiptV1(
            "retention-mismatched-package-receipt",
            new DurableRetentionDigest("durable-flow-archive-v1", new string('5', 64)),
            manifest.ClosureDigest,
            manifest.ClosureItemCount);
        var recorded = await client.RecordArchiveReceiptAsync(new DurableRetentionRecordArchiveReceiptRequest(
            scope, manifest.ManifestId, new DurableCommandId("retention-mismatched-package-receipt"), "operator", "archive", 1, receipt));
        Assert.Equal(DurableRetentionManifestState.ArchiveReceiptRecorded, recorded.Value!.State);
        Assert.NotEqual(package.PackageDigest, receipt.PackageDigest);
        var verification = await client.VerifyArchiveAsync(new DurableRetentionVerifyArchiveRequest(
            scope, manifest.ManifestId, new DurableCommandId("retention-mismatched-package-verify"), "operator", "verify", 2));
        Assert.False(verification.IsSuccess);
        Assert.Equal(DurableProblemCodes.RetentionLifecycleRejected, verification.Problem!.Code);
    }

    [Fact]
    public async Task Source_change_after_receipt_or_verification_blocks_the_next_lifecycle_operation()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var scope = new DurableScopeId("retention-source-change-lifecycle");
        var beforeVerify = new DurableFlowInstanceId("retention-source-change-before-verify");
        var beforePurge = new DurableFlowInstanceId("retention-source-change-before-purge");
        await SeedTerminalFlowAsync(database.DataSource, scope, beforeVerify);
        await SeedTerminalFlowAsync(database.DataSource, scope, beforePurge);
        var client = new PostgreSqlDurableFlowRetentionClient(database.DataSource, schema);

        var verifyAssessment = (await client.AssessAsync(new DurableRetentionAssessmentRequest(scope, beforeVerify))).Value!;
        var verifyManifest = (await client.CreateManifestAsync(new DurableRetentionManifestCreateRequest(
            new DurableCommandId("retention-source-change-verify-create"), verifyAssessment))).Value!.Manifest;
        var verifyPackage = (await client.BuildArchivePackageAsync(scope, verifyManifest.ManifestId)).Value!;
        var verifyReceipt = new DurableArchiveReceiptV1(
            "retention-source-change-verify-receipt",
            verifyPackage.PackageDigest,
            verifyManifest.ClosureDigest,
            verifyManifest.ClosureItemCount);
        await client.RecordArchiveReceiptAsync(new DurableRetentionRecordArchiveReceiptRequest(
            scope, verifyManifest.ManifestId, new DurableCommandId("retention-source-change-verify-receipt"), "operator", "archive", 1, verifyReceipt));
        await UpdateCurrentNodeAsync(database.DataSource, scope, beforeVerify, "changed-before-verify");
        var rejectedVerify = await client.VerifyArchiveAsync(new DurableRetentionVerifyArchiveRequest(
            scope, verifyManifest.ManifestId, new DurableCommandId("retention-source-change-verify"), "operator", "verify", 2));
        Assert.False(rejectedVerify.IsSuccess);
        Assert.Equal(DurableProblemCodes.RetentionLifecycleRejected, rejectedVerify.Problem!.Code);

        var purgeAssessment = (await client.AssessAsync(new DurableRetentionAssessmentRequest(scope, beforePurge))).Value!;
        var purgeManifest = (await client.CreateManifestAsync(new DurableRetentionManifestCreateRequest(
            new DurableCommandId("retention-source-change-purge-create"), purgeAssessment))).Value!.Manifest;
        var purgePackage = (await client.BuildArchivePackageAsync(scope, purgeManifest.ManifestId)).Value!;
        var purgeReceipt = new DurableArchiveReceiptV1(
            "retention-source-change-purge-receipt",
            purgePackage.PackageDigest,
            purgeManifest.ClosureDigest,
            purgeManifest.ClosureItemCount);
        await client.RecordArchiveReceiptAsync(new DurableRetentionRecordArchiveReceiptRequest(
            scope, purgeManifest.ManifestId, new DurableCommandId("retention-source-change-purge-receipt"), "operator", "archive", 1, purgeReceipt));
        var verified = await client.VerifyArchiveAsync(new DurableRetentionVerifyArchiveRequest(
            scope, purgeManifest.ManifestId, new DurableCommandId("retention-source-change-purge-verify"), "operator", "verify", 2));
        Assert.Equal(DurableRetentionManifestState.Verified, verified.Value!.State);
        await UpdateCurrentNodeAsync(database.DataSource, scope, beforePurge, "changed-before-purge");
        var rejectedPurge = await client.PurgeAsync(new DurableRetentionPurgeRequest(
            scope, purgeManifest.ManifestId, new DurableCommandId("retention-source-change-purge"), "operator", "purge", 3));
        Assert.False(rejectedPurge.IsSuccess);
        Assert.Equal(DurableProblemCodes.RetentionLifecycleRejected, rejectedPurge.Problem!.Code);
    }

    [Fact]
    public async Task Assessment_stops_when_the_closure_exceeds_the_caller_bound()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var scope = new DurableScopeId("retention-bound");
        var flow = new DurableFlowInstanceId("retention-bound-flow");
        await SeedTerminalFlowAsync(database.DataSource, scope, flow);
        await using (var history = database.DataSource.CreateCommand(
            "INSERT INTO appsurface_durable.flow_history (scope_id, flow_instance_id, aggregate_revision, transition_kind) VALUES (@scope_id, @flow_instance_id, 1, 'terminal');"))
        {
            history.Parameters.AddWithValue("scope_id", scope.Value);
            history.Parameters.AddWithValue("flow_instance_id", flow.Value);
            Assert.Equal(1, await history.ExecuteNonQueryAsync());
        }

        var client = new PostgreSqlDurableFlowRetentionClient(database.DataSource, schema);
        var assessment = await client.AssessAsync(new DurableRetentionAssessmentRequest(scope, flow, maximumClosureItems: 1));

        Assert.True(assessment.IsSuccess);
        Assert.Equal(DurableRetentionAssessmentStatus.Blocked, assessment.Value!.Status);
        Assert.Equal(DurableRetentionAssessmentReason.ClosureLimitExceeded, assessment.Value.Reason);
    }

    [Fact]
    public async Task Assessment_stops_when_a_flow_command_exceeds_the_caller_bound()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var scope = new DurableScopeId("retention-command-bound");
        var flow = new DurableFlowInstanceId("retention-command-bound-flow");
        await SeedTerminalFlowAsync(database.DataSource, scope, flow);
        await using (var command = database.DataSource.CreateCommand(
            """
            INSERT INTO appsurface_durable.flow_command
                (scope_id, flow_instance_id, command_id, command_type, start_idempotency_key,
                 fingerprint_schema, fingerprint_sha256, outcome, resulting_state, resulting_revision)
            VALUES
                (@scope_id, @flow_instance_id, 'retention-bound-start', 'start', 'retention-bound-start',
                 'retention-command-v1', repeat('b', 64), 'accepted', 'completed', 1);
            """))
        {
            command.Parameters.AddWithValue("scope_id", scope.Value);
            command.Parameters.AddWithValue("flow_instance_id", flow.Value);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        var client = new PostgreSqlDurableFlowRetentionClient(database.DataSource, schema);
        var assessment = await client.AssessAsync(new DurableRetentionAssessmentRequest(scope, flow, maximumClosureItems: 1));

        Assert.True(assessment.IsSuccess);
        Assert.Equal(DurableRetentionAssessmentReason.ClosureLimitExceeded, assessment.Value!.Reason);
    }

    [Fact]
    public async Task Assessment_stops_when_the_canonical_package_exceeds_the_caller_byte_bound()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var scope = new DurableScopeId("retention-byte-bound");
        var flow = new DurableFlowInstanceId("retention-byte-bound-flow");
        await SeedTerminalFlowAsync(database.DataSource, scope, flow);
        var client = new PostgreSqlDurableFlowRetentionClient(database.DataSource, schema);

        var assessment = await client.AssessAsync(new DurableRetentionAssessmentRequest(scope, flow, maximumArchiveBytes: 1));

        Assert.True(assessment.IsSuccess);
        Assert.Equal(DurableRetentionAssessmentStatus.Blocked, assessment.Value!.Status);
        Assert.Equal(DurableRetentionAssessmentReason.ArchiveLimitExceeded, assessment.Value.Reason);
    }

    [Fact]
    public async Task Assessment_classifies_flow_state_dependency_and_per_item_byte_limit()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var scope = new DurableScopeId("retention-assessment-branches");
        var flow = new DurableFlowInstanceId("retention-assessment-branches-flow");
        await SeedTerminalFlowAsync(database.DataSource, scope, flow);
        var client = new PostgreSqlDurableFlowRetentionClient(database.DataSource, schema);
        var safe = (await client.AssessAsync(new DurableRetentionAssessmentRequest(scope, flow))).Value!;
        Assert.Equal(DurableRetentionAssessmentStatus.Safe, safe.Status);
        var perItemLimit = await client.AssessAsync(new DurableRetentionAssessmentRequest(
            scope,
            flow,
            maximumArchiveBytes: checked((int)safe.ArchiveByteCount - 1)));
        Assert.Equal(DurableRetentionAssessmentReason.ArchiveLimitExceeded, perItemLimit.Value!.Reason);

        await SetFlowStateAsync(database.DataSource, scope, flow, "ready", terminal: false, suspended: false);
        var nonterminal = await client.AssessAsync(new DurableRetentionAssessmentRequest(scope, flow));
        Assert.Equal(DurableRetentionAssessmentStatus.Blocked, nonterminal.Value!.Status);
        Assert.Equal(DurableRetentionAssessmentReason.FlowNotTerminal, nonterminal.Value.Reason);

        await SetFlowStateAsync(database.DataSource, scope, flow, "suspended", terminal: false, suspended: true);
        var suspended = await client.AssessAsync(new DurableRetentionAssessmentRequest(scope, flow));
        Assert.Equal(DurableRetentionAssessmentStatus.Indeterminate, suspended.Value!.Status);
        Assert.Equal(DurableRetentionAssessmentReason.RepairRequired, suspended.Value.Reason);

        await SetFlowStateAsync(database.DataSource, scope, flow, "completed", terminal: true, suspended: false);
        await using (var dispatch = database.DataSource.CreateCommand(
            "INSERT INTO appsurface_durable.flow_dispatch (dispatch_id, scope_id, kind, flow_instance_id, due_at, state, expected_revision) VALUES (gen_random_uuid(), @scope_id, 'flow', @flow_instance_id, clock_timestamp(), 'available', 0);"))
        {
            dispatch.Parameters.AddWithValue("scope_id", scope.Value);
            dispatch.Parameters.AddWithValue("flow_instance_id", flow.Value);
            Assert.Equal(1, await dispatch.ExecuteNonQueryAsync());
        }

        var dependency = await client.AssessAsync(new DurableRetentionAssessmentRequest(scope, flow));
        Assert.Equal(DurableRetentionAssessmentStatus.Blocked, dependency.Value!.Status);
        Assert.Equal(DurableRetentionAssessmentReason.ActiveFlowDependency, dependency.Value.Reason);
    }

    [Fact]
    public async Task Terminal_flow_moves_through_verified_hold_and_idempotent_purge_lifecycle()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var scope = new DurableScopeId("retention-lifecycle");
        var flow = new DurableFlowInstanceId("retention-flow");
        await SeedTerminalFlowAsync(database.DataSource, scope, flow);
        var client = new PostgreSqlDurableFlowRetentionClient(database.DataSource, schema);

        var assessment = await client.AssessAsync(new DurableRetentionAssessmentRequest(scope, flow));
        Assert.True(assessment.IsSuccess);
        Assert.Equal(DurableRetentionAssessmentStatus.Safe, assessment.Value!.Status);
        var created = await client.CreateManifestAsync(new DurableRetentionManifestCreateRequest(
            new DurableCommandId("retention-create"), assessment.Value));
        Assert.True(created.IsSuccess);
        Assert.Equal(DurableRetentionManifestCreateOutcome.Created, created.Value!.Outcome);
        var manifest = created.Value.Manifest;

        var duplicate = await client.CreateManifestAsync(new DurableRetentionManifestCreateRequest(
            new DurableCommandId("retention-create"), assessment.Value));
        Assert.Equal(DurableRetentionManifestCreateOutcome.Duplicate, duplicate.Value!.Outcome);
        Assert.Equal(manifest.ManifestId, duplicate.Value.Manifest.ManifestId);

        var package = await client.BuildArchivePackageAsync(scope, manifest.ManifestId);
        Assert.True(package.IsSuccess);
        Assert.NotEmpty(package.Value!.Bytes.ToArray());
        Assert.Equal(manifest.ArchiveByteCount, package.Value.Bytes.Length);
        var receipt = new DurableArchiveReceiptV1(
            "retention-receipt",
            package.Value.PackageDigest,
            manifest.ClosureDigest,
            manifest.ClosureItemCount);
        var recorded = await client.RecordArchiveReceiptAsync(new DurableRetentionRecordArchiveReceiptRequest(
            scope, manifest.ManifestId, new DurableCommandId("retention-receipt"), "operator", "archive", 1, receipt));
        Assert.Equal(DurableRetentionManifestState.ArchiveReceiptRecorded, recorded.Value!.State);

        var verified = await client.VerifyArchiveAsync(new DurableRetentionVerifyArchiveRequest(
            scope, manifest.ManifestId, new DurableCommandId("retention-verify"), "operator", "verify", 2));
        Assert.Equal(DurableRetentionManifestState.Verified, verified.Value!.State);
        var staleVerification = await client.VerifyArchiveAsync(new DurableRetentionVerifyArchiveRequest(
            scope, manifest.ManifestId, new DurableCommandId("retention-stale-verify"), "operator", "verify", 2));
        Assert.False(staleVerification.IsSuccess);
        Assert.Equal(DurableProblemCodes.RetentionLifecycleConflict, staleVerification.Problem!.Code);
        var conflictingVerification = await client.VerifyArchiveAsync(new DurableRetentionVerifyArchiveRequest(
            scope, manifest.ManifestId, new DurableCommandId("retention-verify"), "operator", "changed-verify", 3));
        Assert.False(conflictingVerification.IsSuccess);
        Assert.Equal(DurableProblemCodes.CommandConflict, conflictingVerification.Problem!.Code);
        var rejectedRelease = await client.SetHoldAsync(new DurableRetentionHoldRequest(
            scope, manifest.ManifestId, new DurableCommandId("retention-early-release"), "operator", "release", 3, placeHold: false));
        Assert.False(rejectedRelease.IsSuccess);
        Assert.Equal(DurableProblemCodes.RetentionLifecycleRejected, rejectedRelease.Problem!.Code);
        var held = await client.SetHoldAsync(new DurableRetentionHoldRequest(
            scope, manifest.ManifestId, new DurableCommandId("retention-hold"), "operator", "hold", 3, placeHold: true));
        Assert.Equal(DurableRetentionManifestState.Held, held.Value!.State);
        var blocked = await client.PurgeAsync(new DurableRetentionPurgeRequest(
            scope, manifest.ManifestId, new DurableCommandId("retention-purge-blocked"), "operator", "purge", 4));
        Assert.False(blocked.IsSuccess);
        Assert.Equal(DurableProblemCodes.RetentionLifecycleRejected, blocked.Problem!.Code);

        var released = await client.SetHoldAsync(new DurableRetentionHoldRequest(
            scope, manifest.ManifestId, new DurableCommandId("retention-release"), "operator", "release", 4, placeHold: false));
        Assert.Equal(DurableRetentionManifestState.Verified, released.Value!.State);
        var purged = await client.PurgeAsync(new DurableRetentionPurgeRequest(
            scope, manifest.ManifestId, new DurableCommandId("retention-purge"), "operator", "purge", 5));
        Assert.Equal(DurableRetentionManifestState.Purged, purged.Value!.State);
        Assert.Equal(7, purged.Value.LifecycleSequence);
        var purgeDuplicate = await client.PurgeAsync(new DurableRetentionPurgeRequest(
            scope, manifest.ManifestId, new DurableCommandId("retention-purge"), "operator", "purge", 5));
        Assert.Equal(DurableRetentionMutationOutcome.Duplicate, purgeDuplicate.Value!.Outcome);
        var afterPurge = await client.VerifyArchiveAsync(new DurableRetentionVerifyArchiveRequest(
            scope, manifest.ManifestId, new DurableCommandId("retention-after-purge"), "operator", "verify", 7));
        Assert.Equal(DurableRetentionMutationOutcome.AlreadyPurged, afterPurge.Value!.Outcome);
        var repeatedPurge = await client.PurgeAsync(new DurableRetentionPurgeRequest(
            scope, manifest.ManifestId, new DurableCommandId("retention-after-purge-command"), "operator", "purge", 7));
        Assert.Equal(DurableRetentionMutationOutcome.AlreadyPurged, repeatedPurge.Value!.Outcome);
        var stored = await client.GetManifestAsync(scope, manifest.ManifestId);
        Assert.Equal(DurableRetentionManifestState.Purged, stored.Value!.State);
        var reassessment = await client.AssessAsync(new DurableRetentionAssessmentRequest(scope, flow));
        Assert.True(reassessment.IsSuccess);
        var secondManifest = await client.CreateManifestAsync(new DurableRetentionManifestCreateRequest(
            new DurableCommandId("retention-second-manifest"), reassessment.Value!));
        Assert.False(secondManifest.IsSuccess);
        Assert.Equal(DurableProblemCodes.RetentionLifecycleRejected, secondManifest.Problem!.Code);
        await using (var mutateAudit = database.DataSource.CreateCommand(
            "UPDATE appsurface_durable.flow_retention_manifest_event SET event_type = 'purged' WHERE scope_id = @scope_id;"))
        {
            mutateAudit.Parameters.AddWithValue("scope_id", scope.Value);
            await Assert.ThrowsAsync<PostgresException>(async () => await mutateAudit.ExecuteNonQueryAsync());
        }
    }

    private static async ValueTask SeedTerminalFlowAsync(NpgsqlDataSource dataSource, DurableScopeId scope, DurableFlowInstanceId flow)
    {
        await using var command = dataSource.CreateCommand(
            """
            INSERT INTO appsurface_durable.scope (scope_id) VALUES (@scope_id) ON CONFLICT (scope_id) DO NOTHING;
            INSERT INTO appsurface_durable.flow_instance
                (scope_id, flow_instance_id, flow_id, flow_version, manifest_id, authoring_model,
                 definition_fingerprint_schema, definition_fingerprint_sha256, current_node_id, state,
                 terminal_at, terminal_code, scope_generation, runtime_epoch)
            VALUES
                (@scope_id, @flow_instance_id, 'retention.flow', 'v1', 'retention-manifest', 'tests',
                 'retention-definition-v1', repeat('a', 64), 'terminal', 'completed',
                 clock_timestamp(), 'completed', 1, gen_random_uuid());
            """);
        command.Parameters.AddWithValue("scope_id", scope.Value);
        command.Parameters.AddWithValue("flow_instance_id", flow.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static async ValueTask UpdateCurrentNodeAsync(NpgsqlDataSource dataSource, DurableScopeId scope, DurableFlowInstanceId flow, string nodeId)
    {
        await using var command = dataSource.CreateCommand(
            "UPDATE appsurface_durable.flow_instance SET current_node_id = @node_id, updated_at = clock_timestamp() WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id;");
        command.Parameters.AddWithValue("node_id", nodeId);
        command.Parameters.AddWithValue("scope_id", scope.Value);
        command.Parameters.AddWithValue("flow_instance_id", flow.Value);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async ValueTask SetFlowStateAsync(NpgsqlDataSource dataSource, DurableScopeId scope, DurableFlowInstanceId flow, string state, bool terminal, bool suspended)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE appsurface_durable.flow_instance
            SET state = @state,
                terminal_at = CASE WHEN @terminal THEN clock_timestamp() ELSE NULL END,
                terminal_code = CASE WHEN @terminal THEN 'completed' ELSE NULL END,
                suspension_descriptor = CASE WHEN @suspended THEN '{}'::jsonb ELSE NULL END,
                suspended_from_state = CASE WHEN @suspended THEN 'ready' ELSE NULL END,
                updated_at = clock_timestamp()
            WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id;
            """);
        command.Parameters.AddWithValue("state", state);
        command.Parameters.AddWithValue("terminal", terminal);
        command.Parameters.AddWithValue("suspended", suspended);
        command.Parameters.AddWithValue("scope_id", scope.Value);
        command.Parameters.AddWithValue("flow_instance_id", flow.Value);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }
}
