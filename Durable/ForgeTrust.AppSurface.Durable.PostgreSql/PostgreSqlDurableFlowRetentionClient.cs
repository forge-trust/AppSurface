using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ForgeTrust.AppSurface.Durable.Provider;
using Npgsql;
using NpgsqlTypes;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>
/// Implements the verified, one-Flow retention lifecycle over a scoped PostgreSQL retention-operator connection.
/// </summary>
/// <remarks>
/// The supplied connection must use the dedicated retention-operator role described by the PostgreSQL role recipe.
/// It is not interchangeable with the runtime or dispatcher data source. The application authorizes every caller,
/// stores archive bytes externally, and decides policy cadence; this client proves only protocol correspondence.
/// </remarks>
public sealed class PostgreSqlDurableFlowRetentionClient : IDurableFlowRetentionClient
{
    private const int MaximumClosureItems = 10_000;
    private const int MaximumArchiveBytes = 64 * 1024 * 1024;
    private static readonly Uri Documentation = new("https://forge-trust.com/docs/durable/flow-retention");
    private readonly NpgsqlDataSource _dataSource;
    private readonly IDurableRuntimeSchemaManager _schemaManager;

    /// <summary>Initializes a retention client without performing database I/O or applying migrations.</summary>
    /// <param name="retentionOperatorDataSource">Dedicated scoped retention-operator data source.</param>
    /// <param name="schemaManager">A separately registered schema manager used only to fail closed on incompatibility.</param>
    public PostgreSqlDurableFlowRetentionClient(
        NpgsqlDataSource retentionOperatorDataSource,
        IDurableRuntimeSchemaManager schemaManager)
    {
        _dataSource = retentionOperatorDataSource ?? throw new ArgumentNullException(nameof(retentionOperatorDataSource));
        _schemaManager = schemaManager ?? throw new ArgumentNullException(nameof(schemaManager));
    }

    /// <inheritdoc />
    public async ValueTask<DurableOperationResult<DurableRetentionAssessment>> AssessAsync(
        DurableRetentionAssessmentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _schemaManager.ValidateAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
        try
        {
            await SetScopeAsync(connection, transaction, request.ScopeId, cancellationToken).ConfigureAwait(false);
            var closure = await ReadClosureAsync(
                connection,
                transaction,
                request.ScopeId,
                request.FlowInstanceId,
                request.MaximumClosureItems,
                request.MaximumArchiveBytes,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return DurableOperationResult<DurableRetentionAssessment>.Success(closure.Assessment);
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask<DurableOperationResult<DurableRetentionManifestCreateResult>> CreateManifestAsync(
        DurableRetentionManifestCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _schemaManager.ValidateAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        try
        {
            var assessment = request.Assessment;
            await SetScopeAsync(connection, transaction, assessment.ScopeId, cancellationToken).ConfigureAwait(false);
            await LockCommandAsync(connection, transaction, assessment.ScopeId, request.CommandId, cancellationToken).ConfigureAwait(false);
            var duplicate = await ReadCommandAsync(connection, transaction, assessment.ScopeId, request.CommandId, cancellationToken).ConfigureAwait(false);
            if (duplicate is not null)
            {
                if (!FingerprintsMatch(duplicate.FingerprintSchema, duplicate.FingerprintSha256, request.Fingerprint))
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return Failure<DurableRetentionManifestCreateResult>(
                        DurableProblemCodes.CommandConflict,
                        "The retention manifest command identity was already used with different semantics.",
                        "A retry changed the frozen source facts or request fields.",
                        "Reuse the exact command or create a new command after a new assessment.",
                        request.CommandId.Value);
                }

                var existing = await ReadManifestAsync(
                    connection,
                    transaction,
                    assessment.ScopeId,
                    duplicate.ManifestId,
                    cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return DurableOperationResult<DurableRetentionManifestCreateResult>.Success(
                    new DurableRetentionManifestCreateResult(DurableRetentionManifestCreateOutcome.Duplicate, existing!));
            }

            if (await FlowAlreadyHasManifestAsync(connection, transaction, assessment.ScopeId, assessment.FlowInstanceId, cancellationToken)
                    .ConfigureAwait(false))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return Failure<DurableRetentionManifestCreateResult>(
                    DurableProblemCodes.RetentionLifecycleRejected,
                    "This Flow already has an immutable retention manifest.",
                    "A one-Flow source set is assessed and retained through at most one lifecycle manifest.",
                    "Read the existing manifest; do not create a second archive or purge decision for this Flow.",
                    assessment.FlowInstanceId.Value);
            }

            var closure = await ReadClosureAsync(
                connection,
                transaction,
                assessment.ScopeId,
                assessment.FlowInstanceId,
                MaximumClosureItems,
                MaximumArchiveBytes,
                cancellationToken).ConfigureAwait(false);
            if (!Matches(assessment, closure.Assessment))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return Failure<DurableRetentionManifestCreateResult>(
                    DurableProblemCodes.RetentionSourceChanged,
                    "The Flow source changed before its retention manifest could be frozen.",
                    "The assessment digest no longer matches the current dependency closure.",
                    "Assess the exact Flow again and create a manifest from the new result.",
                    assessment.FlowInstanceId.Value);
            }

            var manifestId = DurableRetentionManifestId.New();
            var procedure = await CreateManifestByProcedureAsync(
                connection,
                transaction,
                request,
                manifestId,
                closure.Items,
                cancellationToken).ConfigureAwait(false);
            var procedureFailure = MapManifestCreateProcedureOutcome(request, procedure.Outcome);
            if (procedureFailure is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return procedureFailure;
            }

            var persisted = await ReadManifestAsync(connection, transaction, assessment.ScopeId, manifestId, cancellationToken)
                .ConfigureAwait(false);
            if (persisted is null)
            {
                throw new InvalidOperationException("The retention manifest procedure completed without a readable manifest.");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return DurableOperationResult<DurableRetentionManifestCreateResult>.Success(
                new DurableRetentionManifestCreateResult(
                    procedure.Outcome == "duplicate" ? DurableRetentionManifestCreateOutcome.Duplicate : DurableRetentionManifestCreateOutcome.Created,
                    persisted));
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask<DurableOperationResult<DurableRetentionManifest>> GetManifestAsync(
        DurableScopeId scopeId,
        DurableRetentionManifestId manifestId,
        CancellationToken cancellationToken = default)
    {
        Require(scopeId, nameof(scopeId));
        Require(manifestId, nameof(manifestId));
        await _schemaManager.ValidateAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SetScopeAsync(connection, transaction, scopeId, cancellationToken).ConfigureAwait(false);
            var manifest = await ReadManifestAsync(connection, transaction, scopeId, manifestId, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return manifest is null
                ? Failure<DurableRetentionManifest>(
                    DurableProblemCodes.RetentionManifestNotFound,
                    "The retention manifest was not found in the authorized durable scope.",
                    "The manifest does not exist or belongs to another scope.",
                    "Verify the trusted scope and manifest identity before retrying.",
                    manifestId.Value)
                : DurableOperationResult<DurableRetentionManifest>.Success(manifest);
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask<DurableOperationResult<DurableArchivePackageV1>> BuildArchivePackageAsync(
        DurableScopeId scopeId,
        DurableRetentionManifestId manifestId,
        CancellationToken cancellationToken = default)
    {
        Require(scopeId, nameof(scopeId));
        Require(manifestId, nameof(manifestId));
        await _schemaManager.ValidateAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
        try
        {
            await SetScopeAsync(connection, transaction, scopeId, cancellationToken).ConfigureAwait(false);
            var manifest = await ReadManifestAsync(connection, transaction, scopeId, manifestId, cancellationToken)
                .ConfigureAwait(false);
            if (manifest is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return Failure<DurableArchivePackageV1>(
                    DurableProblemCodes.RetentionManifestNotFound,
                    "The retention manifest was not found in the authorized durable scope.",
                    "The manifest does not exist or belongs to another scope.",
                    "Verify the trusted scope and manifest identity before retrying.",
                    manifestId.Value);
            }

            var closure = await ReadClosureAsync(
                connection,
                transaction,
                scopeId,
                manifest.FlowInstanceId,
                MaximumClosureItems,
                MaximumArchiveBytes,
                cancellationToken).ConfigureAwait(false);
            if (!Matches(manifest, closure.Assessment))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return SourceChangedPackage(manifest);
            }

            var package = CreateArchivePackage(manifest, closure.Items);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return DurableOperationResult<DurableArchivePackageV1>.Success(package);
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public ValueTask<DurableOperationResult<DurableRetentionMutationResult>> RecordArchiveReceiptAsync(
        DurableRetentionRecordArchiveReceiptRequest request,
        CancellationToken cancellationToken = default) =>
        MutateAsync(request, "archive_receipt", cancellationToken);

    /// <inheritdoc />
    public ValueTask<DurableOperationResult<DurableRetentionMutationResult>> VerifyArchiveAsync(
        DurableRetentionVerifyArchiveRequest request,
        CancellationToken cancellationToken = default) =>
        MutateAsync(request, "verify", cancellationToken);

    /// <inheritdoc />
    public ValueTask<DurableOperationResult<DurableRetentionMutationResult>> SetHoldAsync(
        DurableRetentionHoldRequest request,
        CancellationToken cancellationToken = default) =>
        MutateAsync(request, "hold", cancellationToken);

    /// <inheritdoc />
    public ValueTask<DurableOperationResult<DurableRetentionMutationResult>> PurgeAsync(
        DurableRetentionPurgeRequest request,
        CancellationToken cancellationToken = default) =>
        MutateAsync(request, "purge", cancellationToken);

    private async ValueTask<DurableOperationResult<DurableRetentionMutationResult>> MutateAsync(
        DurableRetentionMutationRequest request,
        string operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _schemaManager.ValidateAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        try
        {
            await SetScopeAsync(connection, transaction, request.ScopeId, cancellationToken).ConfigureAwait(false);
            await LockCommandAsync(connection, transaction, request.ScopeId, request.CommandId, cancellationToken).ConfigureAwait(false);
            var duplicate = await ReadCommandAsync(connection, transaction, request.ScopeId, request.CommandId, cancellationToken).ConfigureAwait(false);
            if (duplicate is not null)
            {
                if (!FingerprintsMatch(duplicate.FingerprintSchema, duplicate.FingerprintSha256, request.Fingerprint))
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return Failure<DurableRetentionMutationResult>(
                        DurableProblemCodes.CommandConflict,
                        "The retention lifecycle command identity was already used with different semantics.",
                        "A retry changed the lifecycle request fields.",
                        "Reuse the exact command or make a new authorized lifecycle decision.",
                        request.CommandId.Value);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return DurableOperationResult<DurableRetentionMutationResult>.Success(new DurableRetentionMutationResult(
                    duplicate.ManifestId,
                    duplicate.Outcome == "already_purged" ? DurableRetentionMutationOutcome.AlreadyPurged : DurableRetentionMutationOutcome.Duplicate,
                    ParseState(duplicate.State),
                    duplicate.Sequence));
            }

            var manifest = await ReadManifestAsync(connection, transaction, request.ScopeId, request.ManifestId, cancellationToken)
                .ConfigureAwait(false);
            if (manifest is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return Failure<DurableRetentionMutationResult>(
                    DurableProblemCodes.RetentionManifestNotFound,
                    "The retention manifest was not found in the authorized durable scope.",
                    "The manifest does not exist or belongs to another scope.",
                    "Verify the trusted scope and manifest identity before retrying.",
                    request.ManifestId.Value);
            }

            if (manifest.LifecycleSequence != request.ExpectedLifecycleSequence)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return LifecycleConflict(request.ManifestId);
            }

            if (manifest.State == DurableRetentionManifestState.Purged)
            {
                var result = await ApplyLifecycleByProcedureAsync(connection, transaction, request, operation, cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return result;
            }

            return operation switch
            {
                "archive_receipt" => await RecordReceiptAsync(
                    connection, transaction, manifest, (DurableRetentionRecordArchiveReceiptRequest)request, cancellationToken).ConfigureAwait(false),
                "verify" => await VerifyAsync(
                    connection, transaction, manifest, (DurableRetentionVerifyArchiveRequest)request, cancellationToken).ConfigureAwait(false),
                "hold" => await SetHoldInternalAsync(
                    connection, transaction, manifest, (DurableRetentionHoldRequest)request, cancellationToken).ConfigureAwait(false),
                "purge" => await PurgeInternalAsync(
                    connection, transaction, manifest, (DurableRetentionPurgeRequest)request, cancellationToken).ConfigureAwait(false),
                _ => throw new InvalidOperationException($"Unknown retention operation '{operation}'."),
            };
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask<DurableOperationResult<DurableRetentionMutationResult>> RecordReceiptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableRetentionManifest manifest,
        DurableRetentionRecordArchiveReceiptRequest request,
        CancellationToken cancellationToken)
    {
        if (manifest.State != DurableRetentionManifestState.Frozen
            || !Equals(manifest.ClosureDigest, request.Receipt.ClosureDigest)
            || manifest.ClosureItemCount != request.Receipt.RecordCount)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return LifecycleRejected(request.ManifestId, "The archive receipt does not match a frozen manifest.");
        }

        var result = await ApplyLifecycleByProcedureAsync(connection, transaction, request, "archive_receipt", cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async ValueTask<DurableOperationResult<DurableRetentionMutationResult>> VerifyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableRetentionManifest manifest,
        DurableRetentionVerifyArchiveRequest request,
        CancellationToken cancellationToken)
    {
        if (manifest.State != DurableRetentionManifestState.ArchiveReceiptRecorded)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return LifecycleRejected(request.ManifestId, "Source correspondence requires a recorded archive receipt.");
        }

        var receipt = await ReadReceiptAsync(connection, transaction, request.ScopeId, request.ManifestId, cancellationToken).ConfigureAwait(false);
        var closure = await ReadClosureAsync(connection, transaction, request.ScopeId, manifest.FlowInstanceId, MaximumClosureItems, MaximumArchiveBytes, cancellationToken)
            .ConfigureAwait(false);
        if (receipt is null || !Matches(manifest, closure.Assessment))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return LifecycleRejected(request.ManifestId, "The live Flow source no longer matches the frozen manifest.");
        }

        var package = CreateArchivePackage(manifest, closure.Items);
        if (!Equals(receipt.PackageDigest, package.PackageDigest) || !Equals(receipt.ClosureDigest, manifest.ClosureDigest) || receipt.RecordCount != manifest.ClosureItemCount)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return LifecycleRejected(request.ManifestId, "The archive receipt does not match the reproducible source package.");
        }

        var result = await ApplyLifecycleByProcedureAsync(connection, transaction, request, "verify", cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async ValueTask<DurableOperationResult<DurableRetentionMutationResult>> SetHoldInternalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableRetentionManifest manifest,
        DurableRetentionHoldRequest request,
        CancellationToken cancellationToken)
    {
        var expected = request.PlaceHold ? DurableRetentionManifestState.Verified : DurableRetentionManifestState.Held;
        if (manifest.State != expected)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return LifecycleRejected(request.ManifestId, request.PlaceHold
                ? "A hold can be placed only on a verified manifest."
                : "A hold can be released only while it is active.");
        }

        var result = await ApplyLifecycleByProcedureAsync(connection, transaction, request, "hold", cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async ValueTask<DurableOperationResult<DurableRetentionMutationResult>> PurgeInternalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableRetentionManifest manifest,
        DurableRetentionPurgeRequest request,
        CancellationToken cancellationToken)
    {
        if (manifest.State != DurableRetentionManifestState.Verified)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return LifecycleRejected(request.ManifestId, "Purge requires a verified manifest with no active hold.");
        }

        var closure = await ReadClosureAsync(connection, transaction, request.ScopeId, manifest.FlowInstanceId, MaximumClosureItems, MaximumArchiveBytes, cancellationToken)
            .ConfigureAwait(false);
        if (!Matches(manifest, closure.Assessment))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return LifecycleRejected(request.ManifestId, "Purge refused because the live source no longer matches the verified manifest.");
        }

        var result = await ApplyLifecycleByProcedureAsync(connection, transaction, request, "purge", cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static async ValueTask<RetentionClosure> ReadClosureAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableFlowInstanceId flowInstanceId,
        int maximumItems,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var flowState = await ReadFlowStateAsync(connection, transaction, scopeId, flowInstanceId, cancellationToken).ConfigureAwait(false);
        if (flowState is null)
        {
            return RetentionClosure.Blocked(scopeId, flowInstanceId, DurableRetentionAssessmentReason.FlowNotFound);
        }

        if (flowState == "suspended")
        {
            return RetentionClosure.Indeterminate(scopeId, flowInstanceId, DurableRetentionAssessmentReason.RepairRequired);
        }

        if (flowState is not ("completed" or "faulted" or "canceled"))
        {
            return RetentionClosure.Blocked(scopeId, flowInstanceId, DurableRetentionAssessmentReason.FlowNotTerminal);
        }

        if (await ExistsAsync(
                connection,
                transaction,
                """
                SELECT EXISTS
                (
                    SELECT 1 FROM appsurface_durable.flow_wait
                    WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id
                      AND state IN ('active', 'suspended')
                    UNION ALL
                    SELECT 1 FROM appsurface_durable.flow_timer
                    WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id AND state = 'scheduled'
                    UNION ALL
                    SELECT 1 FROM appsurface_durable.flow_dispatch
                    WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id
                      AND state IN ('available', 'leased', 'suspended')
                );
                """,
                scopeId,
                flowInstanceId,
                cancellationToken).ConfigureAwait(false))
        {
            return RetentionClosure.Blocked(scopeId, flowInstanceId, DurableRetentionAssessmentReason.ActiveFlowDependency);
        }

        if (await ExistsAsync(
                connection,
                transaction,
                """
                SELECT EXISTS
                (
                    SELECT 1
                    FROM appsurface_durable.flow_wait AS wait
                    JOIN appsurface_durable.work AS work
                      ON work.scope_id = wait.scope_id AND work.work_id = wait.child_work_id
                    WHERE wait.scope_id = @scope_id AND wait.flow_instance_id = @flow_instance_id
                      AND wait.child_work_id IS NOT NULL
                      AND work.state NOT IN ('succeeded', 'succeeded_after_cancel_requested', 'failed', 'canceled_before_effect')
                );
                """,
                scopeId,
                flowInstanceId,
                cancellationToken).ConfigureAwait(false))
        {
            return RetentionClosure.Blocked(scopeId, flowInstanceId, DurableRetentionAssessmentReason.ActiveChildWork);
        }

        var inventory = new RetentionInventory(scopeId, flowInstanceId);
        if (inventory.ArchiveByteCount > maximumBytes)
        {
            return RetentionClosure.Blocked(scopeId, flowInstanceId, DurableRetentionAssessmentReason.ArchiveLimitExceeded);
        }

        var limit = await AddJsonItemsAsync(inventory, maximumItems, maximumBytes, connection, transaction, scopeId, flowInstanceId, 10, "flow_instance", true,
            "SELECT flow_instance_id, to_jsonb(instance)::text FROM appsurface_durable.flow_instance AS instance WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id ORDER BY flow_instance_id;", cancellationToken).ConfigureAwait(false);
        if (limit != RetentionLimit.None) return RetentionClosure.Blocked(scopeId, flowInstanceId, ToAssessmentReason(limit));
        limit = await AddJsonItemsAsync(inventory, maximumItems, maximumBytes, connection, transaction, scopeId, flowInstanceId, 20, "flow_command", false,
            "SELECT command_id, to_jsonb(command)::text FROM appsurface_durable.flow_command AS command WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id ORDER BY command_id;", cancellationToken).ConfigureAwait(false);
        if (limit != RetentionLimit.None) return RetentionClosure.Blocked(scopeId, flowInstanceId, ToAssessmentReason(limit));
        limit = await AddJsonItemsAsync(inventory, maximumItems, maximumBytes, connection, transaction, scopeId, flowInstanceId, 30, "flow_history", true,
            "SELECT event_id::text, to_jsonb(history)::text FROM appsurface_durable.flow_history AS history WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id ORDER BY aggregate_revision, event_id;", cancellationToken).ConfigureAwait(false);
        if (limit != RetentionLimit.None) return RetentionClosure.Blocked(scopeId, flowInstanceId, ToAssessmentReason(limit));
        limit = await AddJsonItemsAsync(inventory, maximumItems, maximumBytes, connection, transaction, scopeId, flowInstanceId, 40, "flow_wait", true,
            "SELECT wait_id::text, to_jsonb(wait)::text FROM appsurface_durable.flow_wait AS wait WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id ORDER BY wait_id;", cancellationToken).ConfigureAwait(false);
        if (limit != RetentionLimit.None) return RetentionClosure.Blocked(scopeId, flowInstanceId, ToAssessmentReason(limit));
        limit = await AddJsonItemsAsync(inventory, maximumItems, maximumBytes, connection, transaction, scopeId, flowInstanceId, 50, "flow_timer", true,
            "SELECT timer_id::text, to_jsonb(timer)::text FROM appsurface_durable.flow_timer AS timer WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id ORDER BY timer_id;", cancellationToken).ConfigureAwait(false);
        if (limit != RetentionLimit.None) return RetentionClosure.Blocked(scopeId, flowInstanceId, ToAssessmentReason(limit));
        limit = await AddJsonItemsAsync(inventory, maximumItems, maximumBytes, connection, transaction, scopeId, flowInstanceId, 60, "flow_dispatch", true,
            "SELECT dispatch_id::text, to_jsonb(dispatch)::text FROM appsurface_durable.flow_dispatch AS dispatch WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id ORDER BY dispatch_id;", cancellationToken).ConfigureAwait(false);
        if (limit != RetentionLimit.None) return RetentionClosure.Blocked(scopeId, flowInstanceId, ToAssessmentReason(limit));
        limit = await AddJsonItemsAsync(inventory, maximumItems, maximumBytes, connection, transaction, scopeId, flowInstanceId, 70, "work_reference", false,
            """
            SELECT work.work_id,
                   jsonb_build_object('scope_id', work.scope_id, 'work_id', work.work_id, 'state', work.state,
                       'revision', work.revision, 'terminal_at', work.terminal_at, 'terminal_code', work.terminal_code)::text
            FROM appsurface_durable.flow_wait AS wait
            JOIN appsurface_durable.work AS work
              ON work.scope_id = wait.scope_id AND work.work_id = wait.child_work_id
            WHERE wait.scope_id = @scope_id AND wait.flow_instance_id = @flow_instance_id
              AND wait.child_work_id IS NOT NULL
            ORDER BY work.work_id;
            """, cancellationToken).ConfigureAwait(false);
        if (limit != RetentionLimit.None) return RetentionClosure.Blocked(scopeId, flowInstanceId, ToAssessmentReason(limit));
        limit = await AddJsonItemsAsync(inventory, maximumItems, maximumBytes, connection, transaction, scopeId, flowInstanceId, 80, "flow_trace_context", false,
            "SELECT trace_context_id::text, to_jsonb(trace)::text FROM appsurface_durable.flow_trace_context AS trace WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id ORDER BY trace_context_id;", cancellationToken).ConfigureAwait(false);
        if (limit != RetentionLimit.None) return RetentionClosure.Blocked(scopeId, flowInstanceId, ToAssessmentReason(limit));

        var items = inventory.Items;

        var closureDigest = Digest("durable-flow-closure-v1", items);
        var watermark = Digest("durable-flow-source-watermark-v1", items);
        return RetentionClosure.Safe(scopeId, flowInstanceId, closureDigest, watermark, items, inventory.ArchiveByteCount);
    }

    private static async ValueTask<string?> ReadFlowStateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, DurableScopeId scopeId, DurableFlowInstanceId flowInstanceId, CancellationToken cancellationToken)
    {
        await using var command = CreateScopeCommand(
            "SELECT state FROM appsurface_durable.flow_instance WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id;",
            connection, transaction, scopeId, flowInstanceId);
        return (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<RetentionLimit> AddJsonItemsAsync(
        RetentionInventory inventory,
        int maximumItems,
        int maximumBytes,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableFlowInstanceId flowInstanceId,
        short rank,
        string kind,
        bool archiveable,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = CreateScopeCommand(sql, connection, transaction, scopeId, flowInstanceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (inventory.Items.Count == maximumItems)
            {
                return RetentionLimit.ItemCount;
            }

            var item = new RetentionItem(rank, kind, reader.GetString(0), Encoding.UTF8.GetBytes(reader.GetString(1)), archiveable);
            var itemBytes = ArchiveItemByteCount(item);
            if (itemBytes > maximumBytes - inventory.ArchiveByteCount)
            {
                return RetentionLimit.ArchiveBytes;
            }

            inventory.Items.Add(item);
            inventory.ArchiveByteCount += itemBytes;
        }

        return RetentionLimit.None;
    }

    private static async ValueTask<bool> ExistsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, DurableScopeId scopeId, DurableFlowInstanceId flowInstanceId, CancellationToken cancellationToken)
    {
        await using var command = CreateScopeCommand(sql, connection, transaction, scopeId, flowInstanceId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private static DurableRetentionDigest Digest(string schema, IReadOnlyList<RetentionItem> items)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, schema);
        foreach (var item in items.OrderBy(static value => value.Rank).ThenBy(static value => value.Key, StringComparer.Ordinal))
        {
            Append(hash, item.Rank);
            Append(hash, item.Kind);
            Append(hash, item.Key);
            Append(hash, item.CanonicalBytes);
            Append(hash, item.Archiveable ? 1 : 0);
        }

        return new DurableRetentionDigest(schema, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static DurableArchivePackageV1 CreateArchivePackage(DurableRetentionManifest manifest, IReadOnlyList<RetentionItem> items)
    {
        var encoded = EncodeArchivePackage(manifest, items);
        return new DurableArchivePackageV1(manifest, encoded.Bytes, encoded.Digest, items.Count);
    }

    private static EncodedArchivePackage EncodeArchivePackage(DurableRetentionManifest manifest, IReadOnlyList<RetentionItem> items)
    {
        using var stream = new MemoryStream();
        stream.Write("DFA1"u8);
        WriteString(stream, manifest.ManifestId.Value);
        WriteString(stream, manifest.ScopeId.Value);
        WriteString(stream, manifest.FlowInstanceId.Value);
        WriteString(stream, manifest.ClosureDigest.SchemaId);
        WriteString(stream, manifest.ClosureDigest.Sha256);
        WriteString(stream, manifest.SourceWatermark.SchemaId);
        WriteString(stream, manifest.SourceWatermark.Sha256);
        WriteInt32(stream, items.Count);
        foreach (var item in items.OrderBy(static value => value.Rank).ThenBy(static value => value.Key, StringComparer.Ordinal))
        {
            WriteInt32(stream, item.Rank);
            WriteString(stream, item.Kind);
            WriteString(stream, item.Key);
            WriteInt32(stream, item.Archiveable ? 1 : 0);
            WriteBytes(stream, item.CanonicalBytes);
        }

        var body = stream.ToArray();
        var bodyHash = SHA256.HashData(body);
        WriteInt32(stream, items.Count);
        WriteInt64(stream, body.Length);
        WriteBytes(stream, bodyHash);
        var bytes = stream.ToArray();
        return new EncodedArchivePackage(
            bytes,
            new DurableRetentionDigest("durable-flow-archive-v1", Convert.ToHexStringLower(SHA256.HashData(bytes))));
    }

    private static async ValueTask LockCommandAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, DurableScopeId scopeId, DurableCommandId commandId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@scope_id || ':' || @command_id, 7331));",
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("command_id", commandId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<bool> FlowAlreadyHasManifestAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, DurableScopeId scopeId, DurableFlowInstanceId flowInstanceId, CancellationToken cancellationToken)
    {
        await using var command = CreateScopeCommand(
            "SELECT EXISTS (SELECT 1 FROM appsurface_durable.flow_retention_manifest WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id);",
            connection,
            transaction,
            scopeId,
            flowInstanceId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private static async ValueTask<RetentionProcedureResult> CreateManifestByProcedureAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableRetentionManifestCreateRequest request,
        DurableRetentionManifestId manifestId,
        IReadOnlyList<RetentionItem> items,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT outcome, lifecycle_state, lifecycle_sequence
            FROM appsurface_durable.create_flow_retention_manifest(
                @scope_id, @manifest_id, @flow_instance_id, @closure_schema, @closure_sha256,
                @watermark_schema, @watermark_sha256, @item_count, @archive_byte_count, @items,
                @command_id, @fingerprint_schema, @fingerprint_sha256);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        var assessment = request.Assessment;
        command.Parameters.AddWithValue("scope_id", assessment.ScopeId.Value);
        command.Parameters.AddWithValue("manifest_id", manifestId.Value);
        command.Parameters.AddWithValue("flow_instance_id", assessment.FlowInstanceId.Value);
        command.Parameters.AddWithValue("closure_schema", assessment.ClosureDigest!.SchemaId);
        command.Parameters.AddWithValue("closure_sha256", assessment.ClosureDigest.Sha256);
        command.Parameters.AddWithValue("watermark_schema", assessment.SourceWatermark!.SchemaId);
        command.Parameters.AddWithValue("watermark_sha256", assessment.SourceWatermark.Sha256);
        command.Parameters.AddWithValue("item_count", assessment.ClosureItemCount);
        command.Parameters.AddWithValue("archive_byte_count", assessment.ArchiveByteCount);
        command.Parameters.AddWithValue("items", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(items.Select(static item => new RetentionManifestItemParameter(
            item.Rank,
            item.Kind,
            item.Key,
            Convert.ToHexStringLower(SHA256.HashData(item.CanonicalBytes)),
            item.Archiveable))));
        command.Parameters.AddWithValue("command_id", request.CommandId.Value);
        command.Parameters.AddWithValue("fingerprint_schema", request.Fingerprint.SchemaId);
        command.Parameters.AddWithValue("fingerprint_sha256", request.Fingerprint.Sha256);
        return await ReadProcedureResultAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<RetentionProcedureResult> ReadProcedureResultAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The retention procedure returned no lifecycle result.");
        }

        return new RetentionProcedureResult(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2));
    }

    private static async ValueTask<DurableOperationResult<DurableRetentionMutationResult>> ApplyLifecycleByProcedureAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableRetentionMutationRequest request,
        string operation,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT outcome, lifecycle_state, lifecycle_sequence
            FROM appsurface_durable.apply_flow_retention_lifecycle(
                @scope_id, @manifest_id, @operation, @command_id, @fingerprint_schema, @fingerprint_sha256,
                @actor_id, @reason_code, @expected_sequence, @receipt_id, @package_schema, @package_sha256,
                @closure_schema, @closure_sha256, @record_count, @place_hold);
            """;
        var receipt = (request as DurableRetentionRecordArchiveReceiptRequest)?.Receipt;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", request.ScopeId.Value);
        command.Parameters.AddWithValue("manifest_id", request.ManifestId.Value);
        command.Parameters.AddWithValue("operation", operation);
        command.Parameters.AddWithValue("command_id", request.CommandId.Value);
        command.Parameters.AddWithValue("fingerprint_schema", request.Fingerprint.SchemaId);
        command.Parameters.AddWithValue("fingerprint_sha256", request.Fingerprint.Sha256);
        command.Parameters.AddWithValue("actor_id", request.ActorId);
        command.Parameters.AddWithValue("reason_code", request.ReasonCode);
        command.Parameters.AddWithValue("expected_sequence", request.ExpectedLifecycleSequence);
        command.Parameters.AddWithValue("receipt_id", NpgsqlDbType.Text, receipt?.ReceiptId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("package_schema", NpgsqlDbType.Text, receipt?.PackageDigest.SchemaId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("package_sha256", NpgsqlDbType.Text, receipt?.PackageDigest.Sha256 ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("closure_schema", NpgsqlDbType.Text, receipt?.ClosureDigest.SchemaId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("closure_sha256", NpgsqlDbType.Text, receipt?.ClosureDigest.Sha256 ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("record_count", NpgsqlDbType.Integer, receipt?.RecordCount ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "place_hold",
            NpgsqlDbType.Boolean,
            request is DurableRetentionHoldRequest hold ? hold.PlaceHold : (object)DBNull.Value);
        var procedure = await ReadProcedureResultAsync(command, cancellationToken).ConfigureAwait(false);
        return MapLifecycleProcedureOutcome(request, procedure.Outcome, procedure.State, procedure.Sequence);
    }

    /// <summary>Maps stable manifest-create procedure rejections before attempting to read a persisted manifest.</summary>
    /// <remarks>Returns <see langword="null"/> for successful and duplicate outcomes that require a manifest read.</remarks>
    internal static DurableOperationResult<DurableRetentionManifestCreateResult>? MapManifestCreateProcedureOutcome(
        DurableRetentionManifestCreateRequest request,
        string outcome) => outcome switch
    {
        "command_conflict" => Failure<DurableRetentionManifestCreateResult>(
            DurableProblemCodes.CommandConflict,
            "The retention manifest command identity was already used with different semantics.",
            "A retry changed the frozen source facts or request fields.",
            "Reuse the exact command or create a new command after a new assessment.",
            request.CommandId.Value),
        "source_rejected" or "scope_rejected" => Failure<DurableRetentionManifestCreateResult>(
            DurableProblemCodes.RetentionSourceChanged,
            "The Flow source changed before its retention manifest could be frozen.",
            "The database procedure could no longer prove the assessed source was terminal and bounded.",
            "Assess the exact Flow again and create a manifest from the new result.",
            request.Assessment.FlowInstanceId.Value),
        "lifecycle_rejected" => Failure<DurableRetentionManifestCreateResult>(
            DurableProblemCodes.RetentionLifecycleRejected,
            "This Flow already has an immutable retention manifest.",
            "A one-Flow source set is assessed and retained through at most one lifecycle manifest.",
            "Read the existing manifest; do not create a second archive or purge decision for this Flow.",
            request.Assessment.FlowInstanceId.Value),
        _ => null,
    };

    /// <summary>Maps one stable lifecycle procedure response into the public retention mutation result.</summary>
    internal static DurableOperationResult<DurableRetentionMutationResult> MapLifecycleProcedureOutcome(
        DurableRetentionMutationRequest request,
        string outcome,
        string? state,
        long? sequence) => outcome switch
    {
        "applied" => DurableOperationResult<DurableRetentionMutationResult>.Success(new DurableRetentionMutationResult(
                request.ManifestId, DurableRetentionMutationOutcome.Applied, ParseState(state!), sequence!.Value)),
        "duplicate" => DurableOperationResult<DurableRetentionMutationResult>.Success(new DurableRetentionMutationResult(
                request.ManifestId, DurableRetentionMutationOutcome.Duplicate, ParseState(state!), sequence!.Value)),
        "already_purged" => DurableOperationResult<DurableRetentionMutationResult>.Success(new DurableRetentionMutationResult(
                request.ManifestId, DurableRetentionMutationOutcome.AlreadyPurged, DurableRetentionManifestState.Purged, sequence!.Value)),
        "command_conflict" => Failure<DurableRetentionMutationResult>(
            DurableProblemCodes.CommandConflict,
            "The retention lifecycle command identity was already used with different semantics.",
            "A retry changed the lifecycle request fields.",
            "Reuse the exact command or make a new authorized lifecycle decision.",
            request.CommandId.Value),
        "manifest_not_found" => Failure<DurableRetentionMutationResult>(
            DurableProblemCodes.RetentionManifestNotFound,
            "The retention manifest was not found in the authorized durable scope.",
            "The manifest does not exist or belongs to another scope.",
            "Verify the trusted scope and manifest identity before retrying.",
            request.ManifestId.Value),
        "lifecycle_conflict" => LifecycleConflict(request.ManifestId),
        _ => LifecycleRejected(request.ManifestId, "The database retention procedure rejected the lifecycle transition."),
    };

    private static async ValueTask<DurableRetentionManifest?> ReadManifestAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, DurableScopeId scopeId, DurableRetentionManifestId manifestId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT manifest.flow_instance_id, manifest.closure_schema, manifest.closure_sha256,
                   manifest.source_watermark_schema, manifest.source_watermark_sha256,
                   manifest.closure_item_count, manifest.archive_byte_count, manifest.created_at,
                   summary.lifecycle_state, summary.lifecycle_sequence
            FROM appsurface_durable.flow_retention_manifest AS manifest
            JOIN appsurface_durable.flow_retention_manifest_summary AS summary
              ON summary.scope_id = manifest.scope_id AND summary.retention_manifest_id = manifest.retention_manifest_id
            WHERE manifest.scope_id = @scope_id AND manifest.retention_manifest_id = @manifest_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("manifest_id", manifestId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new DurableRetentionManifest(
            manifestId,
            scopeId,
            new DurableFlowInstanceId(reader.GetString(0)),
            new DurableRetentionDigest(reader.GetString(1), reader.GetString(2)),
            new DurableRetentionDigest(reader.GetString(3), reader.GetString(4)),
            reader.GetInt32(5),
            reader.GetInt64(6),
            ParseState(reader.GetString(8)),
            reader.GetInt64(9),
            ReadUtc(reader, 7));
    }

    private static async ValueTask<DurableArchiveReceiptV1?> ReadReceiptAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, DurableScopeId scopeId, DurableRetentionManifestId manifestId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT receipt_id, receipt_package_schema, receipt_package_sha256,
                   receipt_closure_schema, receipt_closure_sha256, receipt_record_count
            FROM appsurface_durable.flow_retention_manifest_summary
            WHERE scope_id = @scope_id AND retention_manifest_id = @manifest_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("manifest_id", manifestId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.IsDBNull(0))
        {
            return null;
        }

        return new DurableArchiveReceiptV1(
            reader.GetString(0),
            new DurableRetentionDigest(reader.GetString(1), reader.GetString(2)),
            new DurableRetentionDigest(reader.GetString(3), reader.GetString(4)),
            reader.GetInt32(5));
    }

    private static async ValueTask<RetentionCommand?> ReadCommandAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, DurableScopeId scopeId, DurableCommandId commandId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT retention_manifest_id, fingerprint_schema, fingerprint_sha256, outcome, resulting_state, resulting_lifecycle_sequence
            FROM appsurface_durable.flow_retention_command
            WHERE scope_id = @scope_id AND command_id = @command_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("command_id", commandId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new RetentionCommand(new DurableRetentionManifestId(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetInt64(5))
            : null;
    }

    private static bool Matches(DurableRetentionAssessment assessment, DurableRetentionAssessment current) =>
        current.Status == DurableRetentionAssessmentStatus.Safe
        && Equals(assessment.ClosureDigest, current.ClosureDigest)
        && Equals(assessment.SourceWatermark, current.SourceWatermark)
        && assessment.ClosureItemCount == current.ClosureItemCount
        && assessment.ArchiveByteCount == current.ArchiveByteCount;

    private static bool Matches(DurableRetentionManifest manifest, DurableRetentionAssessment current) =>
        current.Status == DurableRetentionAssessmentStatus.Safe
        && Equals(manifest.ClosureDigest, current.ClosureDigest)
        && Equals(manifest.SourceWatermark, current.SourceWatermark)
        && manifest.ClosureItemCount == current.ClosureItemCount
        && manifest.ArchiveByteCount == current.ArchiveByteCount;

    private static bool FingerprintsMatch(string schema, string sha256, DurableCommandFingerprint request) =>
        string.Equals(schema, request.SchemaId, StringComparison.Ordinal) && string.Equals(sha256, request.Sha256, StringComparison.Ordinal);

    private static DurableRetentionAssessmentReason ToAssessmentReason(RetentionLimit limit) => limit switch
    {
        RetentionLimit.ItemCount => DurableRetentionAssessmentReason.ClosureLimitExceeded,
        RetentionLimit.ArchiveBytes => DurableRetentionAssessmentReason.ArchiveLimitExceeded,
        _ => throw new ArgumentOutOfRangeException(nameof(limit)),
    };

    private static DurableOperationResult<DurableArchivePackageV1> SourceChangedPackage(DurableRetentionManifest manifest) =>
        Failure<DurableArchivePackageV1>(DurableProblemCodes.RetentionSourceChanged, "The Flow source changed after its retention manifest was frozen.", "The current dependency closure does not match the immutable source watermark.", "Create a new assessment and manifest; do not archive this stale source set.", manifest.ManifestId.Value);

    private static DurableOperationResult<DurableRetentionMutationResult> LifecycleConflict(DurableRetentionManifestId manifestId) =>
        Failure<DurableRetentionMutationResult>(DurableProblemCodes.RetentionLifecycleConflict, "The retention lifecycle sequence is stale.", "Another lifecycle operation committed first.", "Read the manifest and make a new authorized decision using its current sequence.", manifestId.Value);

    private static DurableOperationResult<DurableRetentionMutationResult> LifecycleRejected(DurableRetentionManifestId manifestId, string problem) =>
        Failure<DurableRetentionMutationResult>(DurableProblemCodes.RetentionLifecycleRejected, problem, "The manifest is not in the required verified lifecycle state.", "Read the manifest and follow the assess, archive, verify, hold, and purge order.", manifestId.Value);

    private static DurableOperationResult<T> Failure<T>(string code, string problem, string cause, string fix, string correlationId)
        where T : class => DurableOperationResult<T>.Failure(new DurableProblem(code, problem, cause, fix, Documentation, correlationId));

    private static DurableRetentionManifestState ParseState(string state) => state switch
    {
        "frozen" => DurableRetentionManifestState.Frozen,
        "archive_receipt_recorded" => DurableRetentionManifestState.ArchiveReceiptRecorded,
        "verified" => DurableRetentionManifestState.Verified,
        "held" => DurableRetentionManifestState.Held,
        "purged" => DurableRetentionManifestState.Purged,
        _ => throw new InvalidDataException($"Unknown retention lifecycle state '{state}'."),
    };

    private static NpgsqlCommand CreateScopeCommand(string sql, NpgsqlConnection connection, NpgsqlTransaction transaction, DurableScopeId scopeId, DurableFlowInstanceId flowInstanceId)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("flow_instance_id", flowInstanceId.Value);
        return command;
    }

    private static void Require(DurableScopeId scopeId, string parameterName)
    {
        if (string.IsNullOrEmpty(scopeId.Value))
        {
            throw new ArgumentException("A durable scope identifier is required.", parameterName);
        }
    }

    private static void Require(DurableRetentionManifestId manifestId, string parameterName)
    {
        if (string.IsNullOrEmpty(manifestId.Value))
        {
            throw new ArgumentException("A retention manifest identifier is required.", parameterName);
        }
    }

    private static async ValueTask SetScopeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, DurableScopeId scopeId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT set_config('appsurface_durable.scope_id', @scope_id, true);", connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask TryRollbackAsync(NpgsqlTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (PostgreSqlDurableExceptionFilters.IsExpectedCleanupFailure(exception))
        {
            // Preserve the original exception.
        }
    }

    private static DateTimeOffset ReadUtc(NpgsqlDataReader reader, int ordinal) => new(reader.GetFieldValue<DateTime>(ordinal), TimeSpan.Zero);

    private static void Append(IncrementalHash hash, string value) => Append(hash, Encoding.UTF8.GetBytes(value));

    private static void Append(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void Append(IncrementalHash hash, byte[] value)
    {
        Append(hash, value.Length);
        hash.AppendData(value);
    }

    private static void WriteString(Stream stream, string value) => WriteBytes(stream, Encoding.UTF8.GetBytes(value));

    private static void WriteBytes(Stream stream, ReadOnlySpan<byte> value)
    {
        WriteInt32(stream, value.Length);
        stream.Write(value);
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteInt64(Stream stream, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static long ArchiveItemByteCount(RetentionItem item) =>
        sizeof(int)
        + EncodedStringByteCount(item.Kind)
        + EncodedStringByteCount(item.Key)
        + sizeof(int)
        + EncodedBytesByteCount(item.CanonicalBytes.Length);

    private static long ArchivePackageFixedByteCount(DurableScopeId scopeId, DurableFlowInstanceId flowInstanceId) =>
        4L
        + EncodedStringByteCount(new string('0', 32))
        + EncodedStringByteCount(scopeId.Value)
        + EncodedStringByteCount(flowInstanceId.Value)
        + EncodedStringByteCount("durable-flow-closure-v1")
        + EncodedStringByteCount(new string('0', 64))
        + EncodedStringByteCount("durable-flow-source-watermark-v1")
        + EncodedStringByteCount(new string('0', 64))
        + sizeof(int)
        + sizeof(int)
        + sizeof(long)
        + EncodedBytesByteCount(32);

    private static long EncodedStringByteCount(string value) => EncodedBytesByteCount(Encoding.UTF8.GetByteCount(value));

    private static long EncodedBytesByteCount(int length) => sizeof(int) + length;

    private sealed record RetentionItem(short Rank, string Kind, string Key, byte[] CanonicalBytes, bool Archiveable);

    private sealed record RetentionManifestItemParameter(
        [property: JsonPropertyName("rank")] short Rank,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("key")] string Key,
        [property: JsonPropertyName("sha256")] string Sha256,
        [property: JsonPropertyName("archiveable")] bool Archiveable);

    private sealed record RetentionProcedureResult(string Outcome, string? State, long? Sequence);

    private sealed class RetentionInventory
    {
        internal RetentionInventory(DurableScopeId scopeId, DurableFlowInstanceId flowInstanceId)
        {
            ArchiveByteCount = ArchivePackageFixedByteCount(scopeId, flowInstanceId);
        }

        internal List<RetentionItem> Items { get; } = [];

        internal long ArchiveByteCount { get; set; }
    }

    private enum RetentionLimit
    {
        None = 0,
        ItemCount = 1,
        ArchiveBytes = 2,
    }

    private sealed record RetentionClosure(DurableRetentionAssessment Assessment, IReadOnlyList<RetentionItem> Items)
    {
        internal static RetentionClosure Safe(DurableScopeId scopeId, DurableFlowInstanceId flowInstanceId, DurableRetentionDigest closureDigest, DurableRetentionDigest watermark, IReadOnlyList<RetentionItem> items, long archiveByteCount) =>
            new(new DurableRetentionAssessment(scopeId, flowInstanceId, DurableRetentionAssessmentStatus.Safe, DurableRetentionAssessmentReason.Safe, closureDigest, watermark, items.Count, archiveByteCount), items);

        internal static RetentionClosure Blocked(DurableScopeId scopeId, DurableFlowInstanceId flowInstanceId, DurableRetentionAssessmentReason reason) =>
            new(new DurableRetentionAssessment(scopeId, flowInstanceId, DurableRetentionAssessmentStatus.Blocked, reason, null, null, 0, 0), []);

        internal static RetentionClosure Indeterminate(DurableScopeId scopeId, DurableFlowInstanceId flowInstanceId, DurableRetentionAssessmentReason reason) =>
            new(new DurableRetentionAssessment(scopeId, flowInstanceId, DurableRetentionAssessmentStatus.Indeterminate, reason, null, null, 0, 0), []);
    }

    private sealed record RetentionCommand(DurableRetentionManifestId ManifestId, string FingerprintSchema, string FingerprintSha256, string Outcome, string State, long Sequence);

    private sealed record EncodedArchivePackage(byte[] Bytes, DurableRetentionDigest Digest);
}
