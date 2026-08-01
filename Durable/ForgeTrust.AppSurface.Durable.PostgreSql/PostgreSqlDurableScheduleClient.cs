using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Durable;
using Npgsql;
using NpgsqlTypes;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>
/// PostgreSQL implementation of the durable Schedule client.
/// </summary>
/// <remarks>
/// This client persists Schedule command, generation, and occurrence facts but does not start a hosted loop. Use
/// <see cref="PostgreSqlDurableScheduleProcessor"/> to run one bounded, manually invoked due pass. The initial
/// implementation admits Work targets only because it can atomically compose the existing caller-owned Work writer.
/// Callers must authorize the Schedule scope before invoking this client. Configure distinct non-owner, non-
/// <c>BYPASSRLS</c> dispatcher and runtime login roles with the documented
/// <see href="https://github.com/forge-trust/AppSurface/blob/main/Durable/configure-postgresql-roles.sql">PostgreSQL role recipe</see>,
/// and apply schema version 4 before accepting Schedule work. Hosted activation is intentionally deferred; this client
/// and processor never register or start a background loop.
/// </remarks>
public sealed class PostgreSqlDurableScheduleClient : IDurableScheduleClient
{
    private readonly PostgreSqlDurableScheduleStore _store;

    /// <summary>Initializes a client for one validated PostgreSQL durable store and runtime role.</summary>
    /// <param name="dataSource">Scoped runtime-role data source.</param>
    /// <param name="workRegistry">Immutable Work registrations used to validate Work targets.</param>
    /// <param name="workOptions">Validated durable StoreId and runtime epoch.</param>
    /// <param name="scheduleOptions">Schedule runtime-role and temporal safety settings.</param>
    public PostgreSqlDurableScheduleClient(
        NpgsqlDataSource dataSource,
        IDurableWorkRegistry workRegistry,
        PostgreSqlDurableWorkOptions workOptions,
        PostgreSqlDurableScheduleOptions scheduleOptions)
    {
        _store = new PostgreSqlDurableScheduleStore(dataSource, workRegistry, workOptions, scheduleOptions);
    }

    /// <inheritdoc />
    public ValueTask<DurableOperationResult<DurableScheduleMutationResult>> CreateAsync(
        DurableScheduleCreateRequest request,
        CancellationToken cancellationToken = default) =>
        _store.CreateAsync(request, cancellationToken);

    /// <inheritdoc />
    public ValueTask<DurableOperationResult<DurableScheduleMutationResult>> UpdateAsync(
        DurableScheduleUpdateRequest request,
        CancellationToken cancellationToken = default) =>
        _store.UpdateAsync(request, cancellationToken);

    /// <inheritdoc />
    public ValueTask<DurableOperationResult<DurableScheduleMutationResult>> ApplyLifecycleCommandAsync(
        DurableScheduleCommand command,
        CancellationToken cancellationToken = default) =>
        _store.ApplyLifecycleCommandAsync(command, cancellationToken);

    /// <inheritdoc />
    public ValueTask<DurableOperationResult<DurableScheduleSnapshot>> GetAsync(
        DurableScopeId scopeId,
        DurableScheduleId scheduleId,
        CancellationToken cancellationToken = default) =>
        _store.GetAsync(scopeId, scheduleId, cancellationToken);

    /// <inheritdoc />
    public ValueTask<DurableOperationResult<DurableScheduleListResult>> ListAsync(
        DurableScheduleListRequest request,
        CancellationToken cancellationToken = default) =>
        _store.ListAsync(request, cancellationToken);

    /// <inheritdoc />
    public ValueTask<DurableOperationResult<DurableScheduleExplanation>> ExplainNextOccurrencesAsync(
        DurableScheduleExplainRequest request,
        CancellationToken cancellationToken = default) =>
        _store.ExplainAsync(request, cancellationToken);
}

internal sealed class PostgreSqlDurableScheduleStore
{
    private static readonly Uri ScheduleDocumentation =
        new("https://forge-trust.com/troubleshooting/durable-diagnostics");

    private readonly NpgsqlDataSource _dataSource;
    private readonly IDurableWorkRegistry _workRegistry;
    private readonly PostgreSqlDurableWorkOptions _workOptions;
    private readonly PostgreSqlDurableScheduleOptions _scheduleOptions;

    internal PostgreSqlDurableScheduleStore(
        NpgsqlDataSource dataSource,
        IDurableWorkRegistry workRegistry,
        PostgreSqlDurableWorkOptions workOptions,
        PostgreSqlDurableScheduleOptions scheduleOptions)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _workRegistry = workRegistry ?? throw new ArgumentNullException(nameof(workRegistry));
        _workOptions = workOptions ?? throw new ArgumentNullException(nameof(workOptions));
        _scheduleOptions = scheduleOptions ?? throw new ArgumentNullException(nameof(scheduleOptions));
    }

    internal async ValueTask<DurableOperationResult<DurableScheduleMutationResult>> CreateAsync(
        DurableScheduleCreateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!TryValidateTarget(request.Target, request.ScheduleId.Value, out var target, out var failure))
        {
            return failure!;
        }

        if (!TryDescribeSchedule(request.Schedule, out var definition, out failure))
        {
            return failure!;
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var scopeGeneration = await ValidateStoreSetScopeAndReadActiveScopeAsync(
                connection,
                transaction,
                request.ScopeId,
                createScope: true,
                lockScope: true,
                cancellationToken).ConfigureAwait(false);
            if (scopeGeneration is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return Failure<DurableScheduleMutationResult>(
                    DurableScheduleProblemCodes.AccessDenied,
                    "The durable scope is disabled.",
                    "Schedule mutation is fenced while the authoritative scope is disabled.",
                    "Use the authorized scope recovery path before creating a Schedule.",
                    request.ScheduleId.Value);
            }

            var existingLookup = await FindCommandAsync(
                connection,
                transaction,
                request.ScopeId,
                request.CommandId,
                request.IdempotencyKey,
                cancellationToken).ConfigureAwait(false);
            if (existingLookup.HasConflictingIdentities)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return CommandConflict<DurableScheduleMutationResult>(request.CommandId.Value);
            }

            var existing = existingLookup.Command;
            if (existing is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return existing.Fingerprint.Compare(request.Fingerprint) == DurableCommandFingerprintMatch.Exact
                    ? DurableOperationResult<DurableScheduleMutationResult>.Success(existing.ToDuplicate())
                    : CommandConflict<DurableScheduleMutationResult>(request.CommandId.Value);
            }

            if (await ScheduleExistsAsync(connection, transaction, request.ScopeId, request.ScheduleId, cancellationToken).ConfigureAwait(false))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return Failure<DurableScheduleMutationResult>(
                    DurableScheduleProblemCodes.ScheduleInvalid,
                    "The Schedule identity already exists in this durable scope.",
                    "Schedule identities are durable audit keys and cannot be created twice, even after deletion.",
                    "Use a new Schedule identity, or update the existing active or paused Schedule with its current revision.",
                    request.ScheduleId.Value);
            }

            var acceptedAtUtc = await CaptureAcceptedAtUtcAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            var cursorUtc = InitialCursor(request.Schedule, acceptedAtUtc);
            var nextDueUtc = FirstDue(request.Schedule, acceptedAtUtc);
            await InsertDefinitionAsync(
                connection,
                transaction,
                request.ScopeId,
                request.ScheduleId,
                request.DisplayName,
                generation: 1,
                revision: 1,
                acceptedAtUtc,
                cursorUtc,
                nextDueUtc,
                scopeGeneration.Value,
                state: "active",
                cancellationToken).ConfigureAwait(false);
            await InsertGenerationAsync(
                connection,
                transaction,
                request.ScopeId,
                request.ScheduleId,
                generation: 1,
                acceptedAtUtc,
                definition!,
                target!,
                cancellationToken).ConfigureAwait(false);
            await InsertCommandAsync(
                connection,
                transaction,
                request.ScopeId,
                request.CommandId,
                request.IdempotencyKey,
                request.ScheduleId,
                "create",
                request.Fingerprint,
                "created",
                generation: 1,
                revision: 1,
                acceptedAtUtc,
                cancellationToken).ConfigureAwait(false);
            await InsertHistoryAsync(
                connection,
                transaction,
                request.ScopeId,
                request.ScheduleId,
                generation: 1,
                occurrenceId: null,
                eventType: "created",
                cancellationToken).ConfigureAwait(false);
            await UpsertDispatchAsync(
                connection,
                transaction,
                request.ScopeId,
                request.ScheduleId,
                revision: 1,
                nextDueUtc,
                state: "available",
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return DurableOperationResult<DurableScheduleMutationResult>.Success(new DurableScheduleMutationResult(
                request.ScheduleId,
                request.CommandId,
                DurableScheduleMutationCode.Created,
                generation: 1,
                revision: 1,
                acceptedAtUtc));
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    internal async ValueTask<DurableOperationResult<DurableScheduleMutationResult>> UpdateAsync(
        DurableScheduleUpdateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!TryValidateTarget(request.Target, request.ScheduleId.Value, out var target, out var failure)
            || !TryDescribeSchedule(request.Schedule, out var definition, out failure))
        {
            return failure!;
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var scopeGeneration = await ValidateStoreSetScopeAndReadActiveScopeAsync(
                connection,
                transaction,
                request.ScopeId,
                createScope: false,
                lockScope: true,
                cancellationToken).ConfigureAwait(false);
            if (scopeGeneration is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return Failure<DurableScheduleMutationResult>(
                    DurableScheduleProblemCodes.AccessDenied,
                    "The durable scope is disabled or absent.",
                    "Schedule mutation requires an active authoritative scope.",
                    "Use an active authorized scope before updating a Schedule.",
                    request.ScheduleId.Value);
            }

            var existingLookup = await FindCommandAsync(
                connection,
                transaction,
                request.ScopeId,
                request.CommandId,
                idempotencyKey: null,
                cancellationToken).ConfigureAwait(false);
            if (existingLookup.HasConflictingIdentities)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return CommandConflict<DurableScheduleMutationResult>(request.CommandId.Value);
            }

            var existing = existingLookup.Command;
            if (existing is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return existing.Fingerprint.Compare(request.Fingerprint) == DurableCommandFingerprintMatch.Exact
                    ? DurableOperationResult<DurableScheduleMutationResult>.Success(existing.ToDuplicate())
                    : CommandConflict<DurableScheduleMutationResult>(request.CommandId.Value);
            }

            var current = await LockDefinitionAsync(connection, transaction, request.ScopeId, request.ScheduleId, cancellationToken)
                .ConfigureAwait(false);
            if (current is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return NotFound<DurableScheduleMutationResult>(request.ScheduleId.Value);
            }

            if (current.Revision != request.ExpectedRevision)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return RevisionConflict<DurableScheduleMutationResult>(request.ScheduleId.Value);
            }

            if (current.State == "deleted")
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return Failure<DurableScheduleMutationResult>(
                    DurableScheduleProblemCodes.ScheduleInvalid,
                    "A deleted Schedule cannot be updated or revived.",
                    "Deletion is terminal for the Schedule identity and preserves its immutable audit history.",
                    "Create a new Schedule identity for the corrected definition.",
                    request.ScheduleId.Value);
            }

            if (current.State == "suspended")
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return Failure<DurableScheduleMutationResult>(
                    DurableScheduleProblemCodes.ScheduleInvalid,
                    "A suspended Schedule cannot be updated or revived.",
                    "A safety fence suspended this Schedule, so a definition update must not clear its durable recovery evidence.",
                    "Use ReleaseAfterRecovery when it is admitted for the fence, or create a new Schedule identity after resolving the cause.",
                    request.ScheduleId.Value);
            }

            var generation = checked(current.Generation + 1);
            var revision = checked(current.Revision + 1);
            var acceptedAtUtc = await CaptureAcceptedAtUtcAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            var cursorUtc = InitialCursor(request.Schedule, acceptedAtUtc);
            var nextDueUtc = FirstDue(request.Schedule, acceptedAtUtc);
            const string updateSql = """
                UPDATE appsurface_durable.schedule_definition
                SET display_name = @display_name,
                    state = @state,
                    active_generation = @generation,
                    revision = @revision,
                    accepted_at_utc = @accepted_at_utc,
                    cursor_utc = @cursor_utc,
                    next_due_utc = @next_due_utc,
                    scope_generation = @scope_generation,
                    runtime_epoch = @runtime_epoch,
                    suspension_code = NULL,
                    updated_at = clock_timestamp()
                WHERE scope_id = @scope_id AND schedule_id = @schedule_id AND revision = @expected_revision;
                """;
            await using (var update = new NpgsqlCommand(updateSql, connection, transaction))
            {
                AddDefinitionParameters(
                    update,
                    request.ScopeId,
                    request.ScheduleId,
                    request.DisplayName,
                    generation,
                    revision,
                    acceptedAtUtc,
                    cursorUtc,
                    nextDueUtc,
                    scopeGeneration.Value);
                update.Parameters.AddWithValue("state", current.State);
                update.Parameters.AddWithValue("expected_revision", request.ExpectedRevision);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new InvalidOperationException("The Schedule revision changed while its authoritative row was locked.");
                }
            }

            await InsertGenerationAsync(connection, transaction, request.ScopeId, request.ScheduleId, generation, acceptedAtUtc, definition!, target!, cancellationToken)
                .ConfigureAwait(false);
            await SupersedePendingOccurrencesAsync(connection, transaction, request.ScopeId, request.ScheduleId, current.Generation, cancellationToken)
                .ConfigureAwait(false);
            await InsertCommandAsync(connection, transaction, request.ScopeId, request.CommandId, null, request.ScheduleId, "update", request.Fingerprint, "updated", generation, revision, acceptedAtUtc, cancellationToken)
                .ConfigureAwait(false);
            await InsertHistoryAsync(connection, transaction, request.ScopeId, request.ScheduleId, generation, null, "updated", cancellationToken).ConfigureAwait(false);
            await UpsertDispatchAsync(
                connection,
                transaction,
                request.ScopeId,
                request.ScheduleId,
                revision,
                nextDueUtc,
                current.State == "paused" ? "suspended" : "available",
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return DurableOperationResult<DurableScheduleMutationResult>.Success(new DurableScheduleMutationResult(
                request.ScheduleId,
                request.CommandId,
                DurableScheduleMutationCode.Updated,
                generation,
                revision,
                acceptedAtUtc));
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    internal async ValueTask<DurableOperationResult<DurableScheduleMutationResult>> ApplyLifecycleCommandAsync(
        DurableScheduleCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var scopeGeneration = await ValidateStoreSetScopeAndReadActiveScopeAsync(
                connection,
                transaction,
                command.ScopeId,
                createScope: false,
                lockScope: true,
                cancellationToken).ConfigureAwait(false);
            if (scopeGeneration is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return Failure<DurableScheduleMutationResult>(
                    DurableScheduleProblemCodes.AccessDenied,
                    "The durable scope is disabled or absent.",
                    "Schedule lifecycle commands require an active authoritative scope.",
                    "Use the authorized scope recovery path before changing the Schedule.",
                    command.ScheduleId.Value);
            }

            var existingLookup = await FindCommandAsync(connection, transaction, command.ScopeId, command.CommandId, null, cancellationToken).ConfigureAwait(false);
            if (existingLookup.HasConflictingIdentities)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return CommandConflict<DurableScheduleMutationResult>(command.CommandId.Value);
            }

            var existing = existingLookup.Command;
            if (existing is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return existing.Fingerprint.Compare(command.Fingerprint) == DurableCommandFingerprintMatch.Exact
                    ? DurableOperationResult<DurableScheduleMutationResult>.Success(existing.ToDuplicate())
                    : CommandConflict<DurableScheduleMutationResult>(command.CommandId.Value);
            }

            var current = await LockDefinitionAsync(connection, transaction, command.ScopeId, command.ScheduleId, cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return NotFound<DurableScheduleMutationResult>(command.ScheduleId.Value);
            }

            if (current.Revision != command.ExpectedRevision)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return RevisionConflict<DurableScheduleMutationResult>(command.ScheduleId.Value);
            }

            var transition = DescribeLifecycleTransition(command.Kind, current);
            var revision = transition.ChangesDefinition ? checked(current.Revision + 1) : current.Revision;
            var acceptedAtUtc = await CaptureAcceptedAtUtcAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            if (transition.ChangesDefinition)
            {
                var rebindRecoveryFence = transition.Code == DurableScheduleMutationCode.RecoveryReleased;
                const string sql = """
                    UPDATE appsurface_durable.schedule_definition
                    SET state = @state,
                        revision = @revision,
                        runtime_epoch = @runtime_epoch,
                        scope_generation = @scope_generation,
                        suspension_code = NULL,
                        updated_at = clock_timestamp()
                    WHERE scope_id = @scope_id AND schedule_id = @schedule_id AND revision = @expected_revision;
                    """;
                await using var update = new NpgsqlCommand(sql, connection, transaction);
                update.Parameters.AddWithValue("state", transition.State);
                update.Parameters.AddWithValue("revision", revision);
                update.Parameters.AddWithValue(
                    "runtime_epoch",
                    rebindRecoveryFence ? _workOptions.RuntimeEpoch : current.RuntimeEpoch);
                update.Parameters.AddWithValue(
                    "scope_generation",
                    rebindRecoveryFence ? scopeGeneration.Value : current.ScopeGeneration);
                update.Parameters.AddWithValue("scope_id", command.ScopeId.Value);
                update.Parameters.AddWithValue("schedule_id", command.ScheduleId.Value);
                update.Parameters.AddWithValue("expected_revision", command.ExpectedRevision);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new InvalidOperationException("The Schedule revision changed while its authoritative row was locked.");
                }
            }

            if (transition.Code == DurableScheduleMutationCode.Deleted)
            {
                await SupersedePendingOccurrencesAsync(connection, transaction, command.ScopeId, command.ScheduleId, current.Generation, cancellationToken).ConfigureAwait(false);
            }

            await InsertCommandAsync(
                connection,
                transaction,
                command.ScopeId,
                command.CommandId,
                null,
                command.ScheduleId,
                ToCommandKind(command.Kind),
                command.Fingerprint,
                transition.Outcome,
                current.Generation,
                revision,
                acceptedAtUtc,
                cancellationToken).ConfigureAwait(false);
            await InsertHistoryAsync(
                connection,
                transaction,
                command.ScopeId,
                command.ScheduleId,
                current.Generation,
                occurrenceId: null,
                eventType: transition.Outcome,
                actorId: command.ActorId,
                reasonCode: command.ReasonCode,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (transition.ChangesDefinition)
            {
                await SetDispatchStateAsync(connection, transaction, command.ScopeId, command.ScheduleId, revision, transition.DispatchState!, cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return DurableOperationResult<DurableScheduleMutationResult>.Success(new DurableScheduleMutationResult(
                command.ScheduleId,
                command.CommandId,
                transition.Code,
                current.Generation,
                revision,
                acceptedAtUtc));
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    internal async ValueTask<DurableOperationResult<DurableScheduleSnapshot>> GetAsync(
        DurableScopeId scopeId,
        DurableScheduleId scheduleId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var scopeGeneration = await ValidateStoreSetScopeAndReadActiveScopeAsync(
                connection,
                transaction,
                scopeId,
                createScope: false,
                lockScope: false,
                cancellationToken).ConfigureAwait(false);
            if (scopeGeneration is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return NotFound<DurableScheduleSnapshot>(scheduleId.Value);
            }

            var stored = await ReadScheduleAsync(connection, transaction, scopeId, scheduleId, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return stored is null
                ? NotFound<DurableScheduleSnapshot>(scheduleId.Value)
                : DurableOperationResult<DurableScheduleSnapshot>.Success(stored.ToSnapshot());
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    internal async ValueTask<DurableOperationResult<DurableScheduleListResult>> ListAsync(
        DurableScheduleListRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var continuation = DecodeContinuation(request.ContinuationToken);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var scopeGeneration = await ValidateStoreSetScopeAndReadActiveScopeAsync(
                connection,
                transaction,
                request.ScopeId,
                createScope: false,
                lockScope: false,
                cancellationToken).ConfigureAwait(false);
            if (scopeGeneration is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return DurableOperationResult<DurableScheduleListResult>.Success(new DurableScheduleListResult([], null));
            }

            const string sql = """
                SELECT d.schedule_id, d.display_name, d.state, d.active_generation, d.revision, d.next_due_utc, d.runtime_epoch,
                       g.schedule_kind, g.overlap_kind, g.overlap_limit, g.misfire_kind, g.misfire_limit,
                       g.target_kind, g.target_name, g.target_version, g.target_provider_safety
                FROM appsurface_durable.schedule_definition AS d
                JOIN appsurface_durable.schedule_generation AS g
                  ON g.scope_id = d.scope_id
                 AND g.schedule_id = d.schedule_id
                 AND g.generation = d.active_generation
                WHERE d.scope_id = @scope_id
                  AND (@state IS NULL OR d.state = @state)
                  AND (@recovery IS NULL OR (d.runtime_epoch <> @runtime_epoch AND d.state <> 'deleted') = @recovery)
                  AND (@after_schedule_id IS NULL OR d.schedule_id > @after_schedule_id)
                ORDER BY d.schedule_id
                LIMIT @limit;
                """;
            var schedules = new List<StoredScheduleList>();
            await using (var command = new NpgsqlCommand(sql, connection, transaction))
            {
                command.Parameters.AddWithValue("scope_id", request.ScopeId.Value);
                command.Parameters.Add(new NpgsqlParameter("state", NpgsqlDbType.Text)
                {
                    Value = request.State is null ? DBNull.Value : ToState(request.State.Value),
                });
                command.Parameters.Add(new NpgsqlParameter("recovery", NpgsqlDbType.Boolean)
                {
                    Value = request.RequiresRecoveryRelease is null ? DBNull.Value : request.RequiresRecoveryRelease.Value,
                });
                command.Parameters.AddWithValue("runtime_epoch", _workOptions.RuntimeEpoch);
                command.Parameters.Add(new NpgsqlParameter("after_schedule_id", NpgsqlDbType.Text)
                {
                    Value = (object?)continuation ?? DBNull.Value,
                });
                command.Parameters.AddWithValue("limit", request.PageSize + 1);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    schedules.Add(ReadStoredScheduleList(reader));
                }
            }

            var hasMore = schedules.Count > request.PageSize;
            if (hasMore)
            {
                schedules.RemoveAt(schedules.Count - 1);
            }

            var items = schedules.Select(schedule => schedule.ToListItem(_workOptions.RuntimeEpoch)).ToArray();

            var next = hasMore && schedules.Count > 0 ? EncodeContinuation(schedules[^1].ScheduleId.Value) : null;
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return DurableOperationResult<DurableScheduleListResult>.Success(new DurableScheduleListResult(items, next));
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    internal ValueTask<DurableOperationResult<DurableScheduleExplanation>> ExplainAsync(
        DurableScheduleExplainRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Schedule is DurableCronSchedule)
        {
            return ValueTask.FromResult(Failure<DurableScheduleExplanation>(
                DurableScheduleProblemCodes.DialectUnsupported,
                "CronosV1 evaluation is not admitted by the Work-first Schedule provider.",
                "Cron requires a pinned evaluator and time-zone rules fingerprint that are not available in Gate A.",
                "Use At, After, or Every until the Cron evaluator compatibility gate is implemented.",
                request.ScheduleId.Value));
        }

        var anchor = request.AnchorUtc;
        var values = new List<DateTimeOffset>(request.OccurrenceCount);
        switch (request.Schedule)
        {
            case DurableAtSchedule at:
                if (at.AtUtc >= anchor)
                {
                    values.Add(at.AtUtc);
                }

                break;
            case DurableAfterSchedule after:
                values.Add(anchor + after.Delay);
                break;
            case DurableEverySchedule every:
                {
                    var effectiveAnchor = every.AnchorUtc ?? anchor;
                    var next = NextAfter(effectiveAnchor, every.Interval, anchor);
                    for (var index = 0; index < request.OccurrenceCount; index++)
                    {
                        values.Add(next + TimeSpan.FromTicks(checked(every.Interval.Ticks * index)));
                    }

                    break;
                }
            default:
                return ValueTask.FromResult(Failure<DurableScheduleExplanation>(
                    DurableScheduleProblemCodes.ScheduleInvalid,
                    "The Schedule shape is not recognized by this provider.",
                    "The request used an unsupported persisted Schedule kind.",
                    "Use one of the documented Schedule factory methods.",
                    request.ScheduleId.Value));
        }

        var notes = request.Schedule switch
        {
            DurableAfterSchedule => new[] { "After is anchored to the provider acceptance transaction timestamp." },
            DurableEverySchedule { AnchorUtc: null } => new[] { "Every uses the provider acceptance transaction timestamp when no anchor is supplied." },
            _ => Array.Empty<string>(),
        };
        return ValueTask.FromResult(DurableOperationResult<DurableScheduleExplanation>.Success(new DurableScheduleExplanation(
            request.ScheduleId,
            request.Schedule.Kind,
            request.Schedule.OverlapPolicy,
            request.Schedule.MisfirePolicy,
            values,
            notes: notes)));
    }

    internal async ValueTask<ScheduleProcessOutcome> ProcessClaimAsync(
        ScheduleDispatchClaim claim,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var scopeGeneration = await ValidateStoreSetScopeAndReadActiveScopeAsync(
                connection,
                transaction,
                claim.ScopeId,
                createScope: false,
                lockScope: true,
                cancellationToken).ConfigureAwait(false);
            if (scopeGeneration is null)
            {
                await SetDispatchStateAsync(connection, transaction, claim.ScopeId, claim.ScheduleId, claim.DispatchRevision, "suspended", cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return ScheduleProcessOutcome.Suspended;
            }

            var processing = await ReadProcessingScheduleAsync(connection, transaction, claim.ScopeId, claim.ScheduleId, cancellationToken).ConfigureAwait(false);
            if (processing is null || processing.Revision != claim.DispatchRevision)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return ScheduleProcessOutcome.None;
            }

            if (processing.State is "deleted" or "paused" or "suspended")
            {
                var state = processing.State == "deleted" ? "terminal" : "suspended";
                await SetDispatchStateAsync(connection, transaction, claim.ScopeId, claim.ScheduleId, claim.DispatchRevision, state, cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return processing.State == "suspended" ? ScheduleProcessOutcome.Suspended : ScheduleProcessOutcome.None;
            }

            if (processing.RuntimeEpoch != _workOptions.RuntimeEpoch || processing.ScopeGeneration != scopeGeneration.Value)
            {
                await SuspendForRecoveryFenceAsync(connection, transaction, processing, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return ScheduleProcessOutcome.Suspended;
            }

            var cutoffUtc = await CaptureCutoffUtcAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            if (cutoffUtc - processing.CursorUtc > _scheduleOptions.MaximumClockAdvance)
            {
                await SuspendForClockAnomalyAsync(connection, transaction, processing, cutoffUtc, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return ScheduleProcessOutcome.Suspended;
            }

            var pendingCoalesced = processing.Schedule is DurableEverySchedule
                ? await ReadPendingCoalescedOccurrenceAsync(connection, transaction, processing, cancellationToken).ConfigureAwait(false)
                : null;
            var hasOccupiedQueueOneSlot = processing.Schedule is DurableEverySchedule
                && await HasNonTerminalMaterializedWorkAsync(connection, transaction, processing, cancellationToken).ConfigureAwait(false);
            var materializedWorkTargets = 0;
            if (pendingCoalesced is not null && !hasOccupiedQueueOneSlot)
            {
                await MaterializeWorkAsync(
                    connection,
                    transaction,
                    processing,
                    pendingCoalesced.OccurrenceId,
                    cancellationToken).ConfigureAwait(false);
                materializedWorkTargets = 1;
                hasOccupiedQueueOneSlot = true;
                pendingCoalesced = null;
            }

            if (cutoffUtc <= processing.CursorUtc)
            {
                await SetDispatchDueAsync(
                    connection,
                    transaction,
                    claim.ScopeId,
                    claim.ScheduleId,
                    processing.Revision,
                    processing.NextDueUtc,
                    "available",
                    cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return materializedWorkTargets == 0 ? ScheduleProcessOutcome.None : ScheduleProcessOutcome.WorkOnly;
            }

            if (!TryEvaluateDue(processing.Schedule, processing.AcceptedAtUtc, processing.CursorUtc, cutoffUtc, out var due))
            {
                await SetDispatchDueAsync(
                    connection,
                    transaction,
                    claim.ScopeId,
                    claim.ScheduleId,
                    processing.Revision,
                    processing.NextDueUtc,
                    "available",
                    cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return materializedWorkTargets == 0 ? ScheduleProcessOutcome.None : ScheduleProcessOutcome.WorkOnly;
            }

            if (hasOccupiedQueueOneSlot)
            {
                var coalesced = await InsertOrExtendCoalescedOccurrenceAsync(
                    connection,
                    transaction,
                    processing,
                    pendingCoalesced,
                    due,
                    cancellationToken).ConfigureAwait(false);
                var coalescedRevision = checked(processing.Revision + 1);
                await AdvanceCursorAsync(
                    connection,
                    transaction,
                    processing,
                    coalescedRevision,
                    due.LastUtc,
                    due.NextDueUtc,
                    cancellationToken).ConfigureAwait(false);
                await InsertHistoryAsync(
                    connection,
                    transaction,
                    claim.ScopeId,
                    claim.ScheduleId,
                    processing.Generation,
                    coalesced.OccurrenceId,
                    "occurrence-coalesced",
                    cancellationToken).ConfigureAwait(false);
                await SetDispatchDueAsync(
                    connection,
                    transaction,
                    claim.ScopeId,
                    claim.ScheduleId,
                    coalescedRevision,
                    due.NextDueUtc,
                    "available",
                    cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new ScheduleProcessOutcome(coalesced.Inserted ? 1 : 0, materializedWorkTargets, 0);
            }

            var occurrenceId = CreateOccurrenceIdentity(processing, due);
            var inserted = await InsertOccurrenceAsync(connection, transaction, processing, occurrenceId, due, cancellationToken).ConfigureAwait(false);
            var revision = checked(processing.Revision + 1);
            await AdvanceCursorAsync(
                connection,
                transaction,
                processing,
                revision,
                due.LastUtc,
                processing.Schedule is DurableAtSchedule or DurableAfterSchedule ? null : due.NextDueUtc,
                cancellationToken).ConfigureAwait(false);
            await InsertHistoryAsync(connection, transaction, claim.ScopeId, claim.ScheduleId, processing.Generation, occurrenceId, "occurrence-recorded", cancellationToken)
                .ConfigureAwait(false);

            if (processing.Target.TargetKind != "work")
            {
                await SuspendForUnsupportedTargetAsync(connection, transaction, processing, revision, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return ScheduleProcessOutcome.Suspended;
            }

            await MaterializeWorkAsync(connection, transaction, processing, occurrenceId, cancellationToken).ConfigureAwait(false);
            var dispatchState = processing.Schedule is DurableAtSchedule or DurableAfterSchedule ? "terminal" : "available";
            await SetDispatchDueAsync(
                connection,
                transaction,
                claim.ScopeId,
                claim.ScheduleId,
                revision,
                due.NextDueUtc,
                dispatchState,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return inserted ? ScheduleProcessOutcome.OccurrenceAndWork : ScheduleProcessOutcome.WorkOnly;
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    private static async ValueTask<DateTimeOffset> CaptureCutoffUtcAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT clock_timestamp();", connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("PostgreSQL did not return a Schedule processing cutoff.");
        }

        return reader.GetFieldValue<DateTimeOffset>(0);
    }

    private async ValueTask<ProcessingSchedule?> ReadProcessingScheduleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableScheduleId scheduleId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT d.state, d.active_generation, d.revision, d.cursor_utc, d.next_due_utc, d.accepted_at_utc,
                   d.scope_generation, d.runtime_epoch,
                   g.schedule_kind, g.at_utc, g.delay_interval, g.interval_value, g.anchor_utc,
                   g.overlap_kind, g.overlap_limit, g.misfire_kind, g.misfire_limit,
                   g.target_kind, g.target_name, g.target_version, g.target_contract_id, g.target_schema_version,
                   g.target_classification, g.target_retention, g.target_payload, g.target_sha256, g.target_provider_safety
            FROM appsurface_durable.schedule_definition AS d
            JOIN appsurface_durable.schedule_generation AS g
              ON g.scope_id = d.scope_id
             AND g.schedule_id = d.schedule_id
             AND g.generation = d.active_generation
            WHERE d.scope_id = @scope_id AND d.schedule_id = @schedule_id
            FOR UPDATE OF d;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("schedule_id", scheduleId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var source = new StoredSchedule(
            scheduleId,
            null,
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
            reader.GetGuid(7),
            reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
            reader.IsDBNull(10) ? null : reader.GetFieldValue<TimeSpan>(10),
            reader.IsDBNull(11) ? null : reader.GetFieldValue<TimeSpan>(11),
            reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12),
            reader.GetString(13),
            reader.GetInt32(14),
            reader.GetString(15),
            reader.GetInt32(16),
            reader.GetString(17),
            reader.GetString(18),
            reader.GetString(19),
            reader.GetString(20),
            reader.GetString(21),
            reader.GetString(22),
            reader.GetString(23),
            reader.GetFieldValue<byte[]>(24),
            reader.GetString(25),
            reader.IsDBNull(26) ? null : reader.GetString(26),
            null,
            null,
            null,
            null);
        return new ProcessingSchedule(
            scopeId,
            scheduleId,
            source.State,
            source.Generation,
            source.Revision,
            reader.GetFieldValue<DateTimeOffset>(3),
            source.NextDueUtc ?? DateTimeOffset.MaxValue,
            reader.GetFieldValue<DateTimeOffset>(5),
            reader.GetInt64(6),
            source.RuntimeEpoch,
            source.ToSchedule(),
            source);
    }

    private static bool TryEvaluateDue(
        DurableSchedule schedule,
        DateTimeOffset acceptedAtUtc,
        DateTimeOffset cursorUtc,
        DateTimeOffset cutoffUtc,
        out ScheduleDueRange due)
    {
        switch (schedule)
        {
            case DurableAtSchedule at when at.AtUtc > cursorUtc && at.AtUtc <= cutoffUtc:
                due = new ScheduleDueRange(at.AtUtc, at.AtUtc, DateTimeOffset.MaxValue, "nominal");
                return true;
            case DurableAfterSchedule after:
                {
                    var nominal = acceptedAtUtc + after.Delay;
                    if (nominal > cursorUtc && nominal <= cutoffUtc)
                    {
                        due = new ScheduleDueRange(nominal, nominal, DateTimeOffset.MaxValue, "nominal");
                        return true;
                    }

                    break;
                }
            case DurableEverySchedule every:
                {
                    var anchor = every.AnchorUtc ?? acceptedAtUtc;
                    var first = NextAfter(anchor, every.Interval, cursorUtc);
                    if (first <= cutoffUtc)
                    {
                        var count = (long)Math.Floor((cutoffUtc - first).Ticks / (double)every.Interval.Ticks);
                        var last = first + TimeSpan.FromTicks(checked(every.Interval.Ticks * count));
                        due = new ScheduleDueRange(first, last, last + every.Interval, first == last ? "nominal" : "recovery");
                        return true;
                    }

                    break;
                }
        }

        due = default;
        return false;
    }

    private static DateTimeOffset NextAfter(DateTimeOffset anchorUtc, TimeSpan interval, DateTimeOffset valueUtc)
    {
        var firstOccurrenceUtc = anchorUtc + interval;
        if (valueUtc < firstOccurrenceUtc)
        {
            return firstOccurrenceUtc;
        }

        var elapsedTicks = valueUtc.UtcTicks - anchorUtc.UtcTicks;
        var steps = checked((elapsedTicks / interval.Ticks) + 1);
        return anchorUtc + TimeSpan.FromTicks(checked(interval.Ticks * steps));
    }

    private static string CreateOccurrenceIdentity(ProcessingSchedule processing, ScheduleDueRange due) => StableIdentity(
        "schedule-occurrence",
        processing.ScopeId.Value,
        processing.ScheduleId.Value,
        processing.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture),
        due.Kind,
        due.FirstUtc.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static async ValueTask<bool> HasNonTerminalMaterializedWorkAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ProcessingSchedule processing,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT EXISTS
            (
                SELECT 1
                FROM appsurface_durable.schedule_occurrence AS occurrence
                JOIN appsurface_durable.work AS work
                  ON work.scope_id = occurrence.scope_id
                 AND work.work_id = occurrence.target_id
                WHERE occurrence.scope_id = @scope_id
                  AND occurrence.schedule_id = @schedule_id
                  AND occurrence.state = 'materialized'
                  AND occurrence.target_kind = 'work'
                  AND work.state NOT IN
                      ('succeeded', 'succeeded_after_cancel_requested', 'failed', 'canceled_before_effect')
            );
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", processing.ScopeId.Value);
        command.Parameters.AddWithValue("schedule_id", processing.ScheduleId.Value);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private static async ValueTask<PendingScheduleOccurrence?> ReadPendingCoalescedOccurrenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ProcessingSchedule processing,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT occurrence_id, first_nominal_utc, last_nominal_utc
            FROM appsurface_durable.schedule_occurrence
            WHERE scope_id = @scope_id
              AND schedule_id = @schedule_id
              AND generation = @generation
              AND occurrence_kind = 'coalesced'
              AND state = 'pending'
            FOR UPDATE;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", processing.ScopeId.Value);
        command.Parameters.AddWithValue("schedule_id", processing.ScheduleId.Value);
        command.Parameters.AddWithValue("generation", processing.Generation);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var pending = new PendingScheduleOccurrence(
            reader.GetString(0),
            reader.GetFieldValue<DateTimeOffset>(1),
            reader.GetFieldValue<DateTimeOffset>(2));
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("QueueOne Schedule state contains more than one pending coalesced occurrence.");
        }

        return pending;
    }

    private static async ValueTask<CoalescedOccurrence> InsertOrExtendCoalescedOccurrenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ProcessingSchedule processing,
        PendingScheduleOccurrence? pending,
        ScheduleDueRange due,
        CancellationToken cancellationToken)
    {
        if (pending is null)
        {
            var coalesced = due with { Kind = "coalesced" };
            var occurrenceId = CreateOccurrenceIdentity(processing, coalesced);
            var inserted = await InsertOccurrenceAsync(connection, transaction, processing, occurrenceId, coalesced, cancellationToken)
                .ConfigureAwait(false);
            if (!inserted)
            {
                throw new InvalidOperationException("The QueueOne Schedule coalesced occurrence was not inserted exactly once.");
            }

            return new CoalescedOccurrence(occurrenceId, true);
        }

        const string sql = """
            UPDATE appsurface_durable.schedule_occurrence
            SET last_nominal_utc = GREATEST(last_nominal_utc, @last_nominal_utc),
                updated_at = clock_timestamp()
            WHERE scope_id = @scope_id
              AND schedule_id = @schedule_id
              AND generation = @generation
              AND occurrence_id = @occurrence_id
              AND occurrence_kind = 'coalesced'
              AND state = 'pending';
            """;
        await using var update = new NpgsqlCommand(sql, connection, transaction);
        update.Parameters.AddWithValue("scope_id", processing.ScopeId.Value);
        update.Parameters.AddWithValue("schedule_id", processing.ScheduleId.Value);
        update.Parameters.AddWithValue("generation", processing.Generation);
        update.Parameters.AddWithValue("occurrence_id", pending.OccurrenceId);
        update.Parameters.AddWithValue("last_nominal_utc", due.LastUtc.UtcDateTime);
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("The QueueOne Schedule coalesced occurrence changed while it was locked.");
        }

        return new CoalescedOccurrence(pending.OccurrenceId, false);
    }

    private async ValueTask MaterializeWorkAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ProcessingSchedule processing,
        string occurrenceId,
        CancellationToken cancellationToken)
    {
        var target = processing.Target.ToTargetSnapshot();
        var providerSafety = target.ProviderSafety
            ?? throw new InvalidOperationException("A persisted Work Schedule target omitted its provider safety mode.");
        var targetCommandId = new DurableCommandId(StableIdentity("schedule-work-command", occurrenceId));
        var targetIdempotencyKey = StableIdentity("schedule-work-idempotency", occurrenceId);
        var request = new DurableWorkRequest(
            processing.ScopeId,
            targetCommandId,
            targetIdempotencyKey,
            target.RegisteredName,
            target.RegisteredVersion,
            target.Input,
            providerSafety);
        var writer = new PostgreSqlDurableWorkTransactionWriter(_dataSource, _workRegistry, _workOptions);
        var acceptance = await writer.EnqueueAsync(transaction, request, cancellationToken).ConfigureAwait(false);
        if (!acceptance.IsSuccess)
        {
            throw new InvalidOperationException(
                $"The Schedule Work bridge rejected occurrence '{occurrenceId}' with problem '{acceptance.Problem!.Code}'.");
        }

        await LinkWorkOccurrenceAsync(
            connection,
            transaction,
            processing,
            occurrenceId,
            acceptance.Value!,
            targetCommandId,
            targetIdempotencyKey,
            cancellationToken).ConfigureAwait(false);
        await InsertHistoryAsync(
            connection,
            transaction,
            processing.ScopeId,
            processing.ScheduleId,
            processing.Generation,
            occurrenceId,
            "work-materialized",
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<bool> InsertOccurrenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ProcessingSchedule processing,
        string occurrenceId,
        ScheduleDueRange due,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO appsurface_durable.schedule_occurrence
                (scope_id, schedule_id, generation, occurrence_id, occurrence_kind, first_nominal_utc, last_nominal_utc, state)
            VALUES
                (@scope_id, @schedule_id, @generation, @occurrence_id, @occurrence_kind, @first_nominal_utc, @last_nominal_utc, 'pending')
            ON CONFLICT (scope_id, schedule_id, generation, occurrence_kind, first_nominal_utc) DO NOTHING
            RETURNING occurrence_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", processing.ScopeId.Value);
        command.Parameters.AddWithValue("schedule_id", processing.ScheduleId.Value);
        command.Parameters.AddWithValue("generation", processing.Generation);
        command.Parameters.AddWithValue("occurrence_id", occurrenceId);
        command.Parameters.AddWithValue("occurrence_kind", due.Kind);
        command.Parameters.AddWithValue("first_nominal_utc", due.FirstUtc.UtcDateTime);
        command.Parameters.AddWithValue("last_nominal_utc", due.LastUtc.UtcDateTime);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string;
    }

    private static async ValueTask AdvanceCursorAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ProcessingSchedule processing,
        long revision,
        DateTimeOffset cursorUtc,
        DateTimeOffset? nextDueUtc,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE appsurface_durable.schedule_definition
            SET cursor_utc = @cursor_utc,
                next_due_utc = @next_due_utc,
                revision = @revision,
                updated_at = clock_timestamp()
            WHERE scope_id = @scope_id AND schedule_id = @schedule_id AND revision = @expected_revision;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", processing.ScopeId.Value);
        command.Parameters.AddWithValue("schedule_id", processing.ScheduleId.Value);
        command.Parameters.AddWithValue("cursor_utc", cursorUtc.UtcDateTime);
        command.Parameters.Add(new NpgsqlParameter("next_due_utc", NpgsqlDbType.TimestampTz)
        {
            Value = nextDueUtc is null ? DBNull.Value : nextDueUtc.Value.UtcDateTime,
        });
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue("expected_revision", processing.Revision);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("The Schedule revision changed while a due occurrence was being recorded.");
        }
    }

    private static async ValueTask LinkWorkOccurrenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ProcessingSchedule processing,
        string occurrenceId,
        DurableWorkAcceptance acceptance,
        DurableCommandId commandId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE appsurface_durable.schedule_occurrence
            SET state = 'materialized',
                target_kind = 'work',
                target_id = @target_id,
                target_command_id = @target_command_id,
                target_idempotency_key = @target_idempotency_key,
                updated_at = clock_timestamp()
            WHERE scope_id = @scope_id
              AND schedule_id = @schedule_id
              AND generation = @generation
              AND occurrence_id = @occurrence_id
              AND target_id IS NULL;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", processing.ScopeId.Value);
        command.Parameters.AddWithValue("schedule_id", processing.ScheduleId.Value);
        command.Parameters.AddWithValue("generation", processing.Generation);
        command.Parameters.AddWithValue("occurrence_id", occurrenceId);
        command.Parameters.AddWithValue("target_id", acceptance.WorkId.Value);
        command.Parameters.AddWithValue("target_command_id", commandId.Value);
        command.Parameters.AddWithValue("target_idempotency_key", idempotencyKey);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("The Schedule occurrence already has a different target link.");
        }
    }

    private static async ValueTask SetDispatchDueAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableScheduleId scheduleId,
        long revision,
        DateTimeOffset dueAtUtc,
        string state,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE appsurface_durable.schedule_dispatch
            SET dispatch_revision = @revision,
                due_at = @due_at,
                state = @state,
                lease_owner = NULL,
                lease_expires_at = NULL,
                updated_at = clock_timestamp()
            WHERE scope_id = @scope_id AND schedule_id = @schedule_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("schedule_id", scheduleId.Value);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue("due_at", dueAtUtc.UtcDateTime);
        command.Parameters.AddWithValue("state", state);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("The Schedule dispatch row was missing while committing a due pass.");
        }
    }

    private static async ValueTask SuspendForUnsupportedTargetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ProcessingSchedule processing,
        long revision,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE appsurface_durable.schedule_definition
            SET state = 'suspended',
                revision = @revision,
                suspension_code = @suspension_code,
                updated_at = clock_timestamp()
            WHERE scope_id = @scope_id AND schedule_id = @schedule_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", processing.ScopeId.Value);
        command.Parameters.AddWithValue("schedule_id", processing.ScheduleId.Value);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue("suspension_code", DurableScheduleProblemCodes.DialectUnsupported);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await SetDispatchStateAsync(connection, transaction, processing.ScopeId, processing.ScheduleId, revision, "suspended", cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask SuspendForClockAnomalyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ProcessingSchedule processing,
        DateTimeOffset cutoffUtc,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE appsurface_durable.schedule_definition
            SET state = 'suspended',
                revision = @revision,
                suspension_code = @suspension_code,
                updated_at = clock_timestamp()
            WHERE scope_id = @scope_id AND schedule_id = @schedule_id;
            """;
        var revision = checked(processing.Revision + 1);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", processing.ScopeId.Value);
        command.Parameters.AddWithValue("schedule_id", processing.ScheduleId.Value);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue("suspension_code", DurableScheduleProblemCodes.EvaluationChanged);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await InsertHistoryAsync(connection, transaction, processing.ScopeId, processing.ScheduleId, processing.Generation, null, "clock-anomaly-suspended", cancellationToken)
            .ConfigureAwait(false);
        await SetDispatchStateAsync(connection, transaction, processing.ScopeId, processing.ScheduleId, revision, "suspended", cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask SuspendForRecoveryFenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ProcessingSchedule processing,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE appsurface_durable.schedule_definition
            SET state = 'suspended',
                revision = @revision,
                suspension_code = @suspension_code,
                updated_at = clock_timestamp()
            WHERE scope_id = @scope_id AND schedule_id = @schedule_id;
            """;
        var revision = checked(processing.Revision + 1);
        var suspensionCode = processing.RuntimeEpoch != _workOptions.RuntimeEpoch
            ? DurableProblemCodes.RecoveryEpochRequired
            : DurableProblemCodes.ScopeGenerationConflict;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", processing.ScopeId.Value);
        command.Parameters.AddWithValue("schedule_id", processing.ScheduleId.Value);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue("suspension_code", suspensionCode);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await InsertHistoryAsync(
            connection,
            transaction,
            processing.ScopeId,
            processing.ScheduleId,
            processing.Generation,
            null,
            "recovery-fence-suspended",
            cancellationToken).ConfigureAwait(false);
        await SetDispatchStateAsync(connection, transaction, processing.ScopeId, processing.ScheduleId, revision, "suspended", cancellationToken)
            .ConfigureAwait(false);
    }

    private static string StableIdentity(string prefix, params string[] values)
    {
        var data = Encoding.UTF8.GetBytes(string.Join("\n", values));
        return $"{prefix}-{Convert.ToHexStringLower(SHA256.HashData(data))}";
    }

    private static string EncodeContinuation(string scheduleId)
    {
        var json = JsonSerializer.Serialize(new ScheduleContinuationToken(1, scheduleId));
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string? DecodeContinuation(string? token)
    {
        if (token is null)
        {
            return null;
        }

        try
        {
            var padded = token.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight((padded.Length + 3) / 4 * 4, '=');
            var decoded = JsonSerializer.Deserialize<ScheduleContinuationToken>(
                Encoding.UTF8.GetString(Convert.FromBase64String(padded)));
            if (decoded is null || decoded.Version != 1)
            {
                throw new FormatException();
            }

            return new DurableScheduleId(decoded.ScheduleId).Value;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException)
        {
            throw new ArgumentException(
                "The Schedule continuation token is malformed or uses an unknown version.",
                nameof(token),
                exception);
        }
    }

    private sealed record ScheduleContinuationToken(int Version, string ScheduleId);

    internal async ValueTask<long?> ValidateStoreSetScopeAndReadActiveScopeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        bool createScope,
        bool lockScope,
        CancellationToken cancellationToken)
    {
        await AssertRuntimeRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        const string metadataSql = """
            SELECT store_id, active_runtime_epoch, schema_version
            FROM appsurface_durable.store_metadata
            WHERE singleton;
            """;
        await using (var metadata = new NpgsqlCommand(metadataSql, connection, transaction))
        await using (var reader = await metadata.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("Durable store metadata is missing.");
            }

            var storeId = reader.GetGuid(0);
            var epoch = reader.IsDBNull(1) ? (Guid?)null : reader.GetGuid(1);
            var schemaVersion = reader.GetInt32(2);
            if (schemaVersion < 4)
            {
                throw new InvalidOperationException($"{DurableProblemCodes.SchemaUpgradeRequired}: PostgreSQL durable Schedule requires schema version 4.");
            }

            if (storeId != _workOptions.ExpectedStoreId)
            {
                throw new InvalidOperationException($"{DurableProblemCodes.StoreIdentityMismatch}: The configured durable store identity does not match PostgreSQL.");
            }

            if (epoch != _workOptions.RuntimeEpoch)
            {
                throw new InvalidOperationException($"{DurableProblemCodes.RecoveryEpochRequired}: The configured runtime epoch is not active.");
            }
        }

        await using (var scope = new NpgsqlCommand(
                         "SELECT set_config('appsurface_durable.scope_id', @scope_id, true);",
                         connection,
                         transaction))
        {
            scope.Parameters.AddWithValue("scope_id", scopeId.Value);
            await scope.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (createScope)
        {
            await using var insert = new NpgsqlCommand(
                "INSERT INTO appsurface_durable.scope (scope_id) VALUES (@scope_id) ON CONFLICT (scope_id) DO NOTHING;",
                connection,
                transaction);
            insert.Parameters.AddWithValue("scope_id", scopeId.Value);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var scopeSql = lockScope
            ? "SELECT generation, state FROM appsurface_durable.scope WHERE scope_id = @scope_id FOR UPDATE;"
            : "SELECT generation, state FROM appsurface_durable.scope WHERE scope_id = @scope_id;";
        await using var selectScope = new NpgsqlCommand(
            scopeSql,
            connection,
            transaction);
        selectScope.Parameters.AddWithValue("scope_id", scopeId.Value);
        await using var scopeReader = await selectScope.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await scopeReader.ReadAsync(cancellationToken).ConfigureAwait(false) || scopeReader.GetString(1) != "active")
        {
            return null;
        }

        return scopeReader.GetInt64(0);
    }

    private async ValueTask AssertRuntimeRoleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT current_user = @runtime_role;", connection, transaction);
        command.Parameters.AddWithValue("runtime_role", _scheduleOptions.RuntimeRole);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not bool matches || !matches)
        {
            throw new InvalidOperationException(
                $"{DurableScheduleProblemCodes.AccessDenied}: Schedule scoped processing requires PostgreSQL role '{_scheduleOptions.RuntimeRole}'.");
        }
    }

    private static async ValueTask<DateTimeOffset> CaptureAcceptedAtUtcAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string sql = "WITH accepted AS (SELECT transaction_timestamp() AS accepted_at_utc) SELECT accepted_at_utc FROM accepted;";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("PostgreSQL did not return the Schedule acceptance timestamp.");
        }

        return reader.GetFieldValue<DateTimeOffset>(0);
    }

    private static async ValueTask<ExistingCommandLookup> FindCommandAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableCommandId commandId,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT command_id, schedule_id, request_fingerprint_schema, request_fingerprint_sha256,
                   resulting_generation, resulting_revision, accepted_at_utc
            FROM appsurface_durable.schedule_command
            WHERE scope_id = @scope_id
              AND (command_id = @command_id OR (@idempotency_key IS NOT NULL AND idempotency_key = @idempotency_key))
            ORDER BY command_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("command_id", commandId.Value);
        command.Parameters.Add(new NpgsqlParameter("idempotency_key", NpgsqlDbType.Text)
        {
            Value = (object?)idempotencyKey ?? DBNull.Value,
        });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        ExistingCommand? first = null;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var candidate = new ExistingCommand(
                new DurableCommandId(reader.GetString(0)),
                new DurableScheduleId(reader.GetString(1)),
                new DurableCommandFingerprint(reader.GetString(2), reader.GetString(3)),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetFieldValue<DateTimeOffset>(6));
            if (first is not null && candidate.CommandId != first.CommandId)
            {
                return new ExistingCommandLookup(null, HasConflictingIdentities: true);
            }

            first ??= candidate;
        }

        return new ExistingCommandLookup(first, HasConflictingIdentities: false);
    }

    private static async ValueTask<DefinitionLock?> LockDefinitionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableScheduleId scheduleId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT active_generation, revision, scope_generation, state, runtime_epoch, suspension_code
            FROM appsurface_durable.schedule_definition
            WHERE scope_id = @scope_id AND schedule_id = @schedule_id
            FOR UPDATE;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("schedule_id", scheduleId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new DefinitionLock(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetString(3),
                reader.GetGuid(4),
                reader.IsDBNull(5) ? null : reader.GetString(5))
            : null;
    }

    private async ValueTask<StoredSchedule?> ReadScheduleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableScheduleId scheduleId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT d.schedule_id, d.display_name, d.state, d.active_generation, d.revision, d.next_due_utc, d.runtime_epoch,
                   g.schedule_kind, g.at_utc, g.delay_interval, g.interval_value, g.anchor_utc,
                   g.overlap_kind, g.overlap_limit, g.misfire_kind, g.misfire_limit,
                   g.target_kind, g.target_name, g.target_version, g.target_contract_id, g.target_schema_version,
                   g.target_classification, g.target_retention, g.target_payload, g.target_sha256, g.target_provider_safety,
                   g.cron_expression, g.cron_time_zone, g.cron_dialect, g.cron_grammar
            FROM appsurface_durable.schedule_definition AS d
            JOIN appsurface_durable.schedule_generation AS g
              ON g.scope_id = d.scope_id
             AND g.schedule_id = d.schedule_id
             AND g.generation = d.active_generation
            WHERE d.scope_id = @scope_id AND d.schedule_id = @schedule_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("schedule_id", scheduleId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return ReadStoredSchedule(reader);
    }

    private static StoredSchedule ReadStoredSchedule(NpgsqlDataReader reader) => new(
            new DurableScheduleId(reader.GetString(0)),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
            reader.GetGuid(6),
            reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
            reader.IsDBNull(9) ? null : reader.GetFieldValue<TimeSpan>(9),
            reader.IsDBNull(10) ? null : reader.GetFieldValue<TimeSpan>(10),
            reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11),
            reader.GetString(12),
            reader.GetInt32(13),
            reader.GetString(14),
            reader.GetInt32(15),
            reader.GetString(16),
            reader.GetString(17),
            reader.GetString(18),
            reader.GetString(19),
            reader.GetString(20),
            reader.GetString(21),
            reader.GetString(22),
            reader.GetFieldValue<byte[]>(23),
            reader.GetString(24),
            reader.IsDBNull(25) ? null : reader.GetString(25),
            reader.IsDBNull(26) ? null : reader.GetString(26),
            reader.IsDBNull(27) ? null : reader.GetString(27),
            reader.IsDBNull(28) ? null : reader.GetString(28),
            reader.IsDBNull(29) ? null : reader.GetString(29));

    private static StoredScheduleList ReadStoredScheduleList(NpgsqlDataReader reader) => new(
        new DurableScheduleId(reader.GetString(0)),
        reader.IsDBNull(1) ? null : reader.GetString(1),
        reader.GetString(2),
        reader.GetInt64(3),
        reader.GetInt64(4),
        reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
        reader.GetGuid(6),
        Enum.Parse<DurableScheduleKind>(reader.GetString(7), ignoreCase: true),
        reader.GetString(8),
        reader.GetInt32(9),
        reader.GetString(10),
        reader.GetInt32(11),
        Enum.Parse<DurableScheduleTargetKind>(reader.GetString(12), ignoreCase: true),
        reader.GetString(13),
        reader.GetString(14),
        reader.IsDBNull(15) ? null : reader.GetString(15));

    private static async ValueTask<bool> ScheduleExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableScheduleId scheduleId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT EXISTS
            (
                SELECT 1
                FROM appsurface_durable.schedule_definition
                WHERE scope_id = @scope_id AND schedule_id = @schedule_id
            );
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("schedule_id", scheduleId.Value);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private async ValueTask InsertDefinitionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableScheduleId scheduleId,
        string? displayName,
        long generation,
        long revision,
        DateTimeOffset acceptedAtUtc,
        DateTimeOffset cursorUtc,
        DateTimeOffset nextDueUtc,
        long scopeGeneration,
        string state,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO appsurface_durable.schedule_definition
                (scope_id, schedule_id, display_name, state, active_generation, revision, accepted_at_utc, cursor_utc,
                 next_due_utc, scope_generation, runtime_epoch)
            VALUES
                (@scope_id, @schedule_id, @display_name, @state, @generation, @revision, @accepted_at_utc, @cursor_utc,
                 @next_due_utc, @scope_generation, @runtime_epoch);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddDefinitionParameters(command, scopeId, scheduleId, displayName, generation, revision, acceptedAtUtc, cursorUtc, nextDueUtc, scopeGeneration);
        command.Parameters.AddWithValue("state", state);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask InsertGenerationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableScheduleId scheduleId,
        long generation,
        DateTimeOffset acceptedAtUtc,
        ScheduleDefinition definition,
        ScheduleTargetData target,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO appsurface_durable.schedule_generation
                (scope_id, schedule_id, generation, accepted_at_utc, schedule_kind, at_utc, delay_interval, interval_value,
                 anchor_utc, cron_expression, cron_time_zone, cron_dialect, cron_grammar, overlap_kind, overlap_limit,
                 misfire_kind, misfire_limit, target_kind, target_name, target_version, target_contract_id,
                 target_schema_version, target_classification, target_retention, target_payload, target_sha256,
                 target_provider_safety)
            VALUES
                (@scope_id, @schedule_id, @generation, @accepted_at_utc, @schedule_kind, @at_utc, @delay_interval,
                 @interval_value, @anchor_utc, @cron_expression, @cron_time_zone, @cron_dialect, @cron_grammar,
                 @overlap_kind, @overlap_limit, @misfire_kind, @misfire_limit, @target_kind, @target_name, @target_version,
                 @target_contract_id, @target_schema_version, @target_classification, @target_retention, @target_payload,
                 @target_sha256, @target_provider_safety);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("schedule_id", scheduleId.Value);
        command.Parameters.AddWithValue("generation", generation);
        command.Parameters.AddWithValue("accepted_at_utc", acceptedAtUtc.UtcDateTime);
        command.Parameters.AddWithValue("schedule_kind", definition.Kind);
        command.Parameters.Add(new NpgsqlParameter("at_utc", NpgsqlDbType.TimestampTz)
        {
            Value = definition.AtUtc is null ? DBNull.Value : definition.AtUtc.Value.UtcDateTime,
        });
        command.Parameters.Add(new NpgsqlParameter("delay_interval", NpgsqlDbType.Interval)
        {
            Value = (object?)definition.Delay ?? DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("interval_value", NpgsqlDbType.Interval)
        {
            Value = (object?)definition.Interval ?? DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("anchor_utc", NpgsqlDbType.TimestampTz)
        {
            Value = definition.AnchorUtc is null ? DBNull.Value : definition.AnchorUtc.Value.UtcDateTime,
        });
        command.Parameters.Add(new NpgsqlParameter("cron_expression", NpgsqlDbType.Text)
        {
            Value = (object?)definition.CronExpression ?? DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("cron_time_zone", NpgsqlDbType.Text)
        {
            Value = (object?)definition.CronTimeZone ?? DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("cron_dialect", NpgsqlDbType.Text)
        {
            Value = (object?)definition.CronDialect ?? DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("cron_grammar", NpgsqlDbType.Text)
        {
            Value = (object?)definition.CronGrammar ?? DBNull.Value,
        });
        command.Parameters.AddWithValue("overlap_kind", definition.OverlapKind);
        command.Parameters.AddWithValue("overlap_limit", definition.OverlapLimit);
        command.Parameters.AddWithValue("misfire_kind", definition.MisfireKind);
        command.Parameters.AddWithValue("misfire_limit", definition.MisfireLimit);
        command.Parameters.AddWithValue("target_kind", target.Kind);
        command.Parameters.AddWithValue("target_name", target.Name);
        command.Parameters.AddWithValue("target_version", target.Version);
        command.Parameters.AddWithValue("target_contract_id", target.Payload.ContractName);
        command.Parameters.AddWithValue("target_schema_version", target.Payload.ContractVersion);
        command.Parameters.AddWithValue("target_classification", ToClassification(target.Payload.Classification));
        command.Parameters.AddWithValue("target_retention", target.Payload.RetentionPolicyId);
        command.Parameters.AddWithValue("target_payload", target.Payload.Content.ToArray());
        command.Parameters.AddWithValue("target_sha256", target.Payload.Sha256);
        command.Parameters.Add(new NpgsqlParameter("target_provider_safety", NpgsqlDbType.Text)
        {
            Value = target.ProviderSafety is null ? DBNull.Value : ToProviderSafety(target.ProviderSafety.Value),
        });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask InsertCommandAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableCommandId commandId,
        string? idempotencyKey,
        DurableScheduleId scheduleId,
        string commandKind,
        DurableCommandFingerprint fingerprint,
        string outcome,
        long generation,
        long revision,
        DateTimeOffset acceptedAtUtc,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO appsurface_durable.schedule_command
                (scope_id, command_id, idempotency_key, schedule_id, command_kind, request_fingerprint_schema,
                 request_fingerprint_sha256, outcome, resulting_generation, resulting_revision, accepted_at_utc)
            VALUES
                (@scope_id, @command_id, @idempotency_key, @schedule_id, @command_kind, @fingerprint_schema,
                 @fingerprint_sha256, @outcome, @generation, @revision, @accepted_at_utc);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("command_id", commandId.Value);
        command.Parameters.Add(new NpgsqlParameter("idempotency_key", NpgsqlDbType.Text)
        {
            Value = (object?)idempotencyKey ?? DBNull.Value,
        });
        command.Parameters.AddWithValue("schedule_id", scheduleId.Value);
        command.Parameters.AddWithValue("command_kind", commandKind);
        command.Parameters.AddWithValue("fingerprint_schema", fingerprint.SchemaId);
        command.Parameters.AddWithValue("fingerprint_sha256", fingerprint.Sha256);
        command.Parameters.AddWithValue("outcome", outcome);
        command.Parameters.AddWithValue("generation", generation);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue("accepted_at_utc", acceptedAtUtc.UtcDateTime);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask InsertHistoryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableScheduleId scheduleId,
        long generation,
        string? occurrenceId,
        string eventType,
        CancellationToken cancellationToken,
        string? actorId = null,
        string? reasonCode = null)
    {
        const string sql = """
            INSERT INTO appsurface_durable.schedule_history
                (scope_id, schedule_id, generation, occurrence_id, event_type, details)
            VALUES
                (@scope_id, @schedule_id, @generation, @occurrence_id, @event_type,
                 CASE WHEN @actor_id IS NULL THEN '{}'::jsonb
                      ELSE jsonb_build_object('actor_id', @actor_id, 'reason_code', @reason_code)
                 END);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("schedule_id", scheduleId.Value);
        command.Parameters.AddWithValue("generation", generation);
        command.Parameters.Add(new NpgsqlParameter("occurrence_id", NpgsqlDbType.Text)
        {
            Value = (object?)occurrenceId ?? DBNull.Value,
        });
        command.Parameters.AddWithValue("event_type", eventType);
        command.Parameters.Add(new NpgsqlParameter("actor_id", NpgsqlDbType.Text)
        {
            Value = (object?)actorId ?? DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("reason_code", NpgsqlDbType.Text)
        {
            Value = (object?)reasonCode ?? DBNull.Value,
        });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask UpsertDispatchAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableScheduleId scheduleId,
        long revision,
        DateTimeOffset dueAtUtc,
        string state,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO appsurface_durable.schedule_dispatch
                (scope_id, schedule_id, dispatch_revision, due_at, state)
            VALUES (@scope_id, @schedule_id, @revision, @due_at, @state)
            ON CONFLICT (scope_id, schedule_id) DO UPDATE
            SET dispatch_revision = EXCLUDED.dispatch_revision,
                due_at = EXCLUDED.due_at,
                state = EXCLUDED.state,
                lease_owner = NULL,
                lease_expires_at = NULL,
                updated_at = clock_timestamp();
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("schedule_id", scheduleId.Value);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue("due_at", dueAtUtc.UtcDateTime);
        command.Parameters.AddWithValue("state", state);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask SetDispatchStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableScheduleId scheduleId,
        long revision,
        string state,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE appsurface_durable.schedule_dispatch
            SET dispatch_revision = @revision,
                state = @state,
                lease_owner = NULL,
                lease_expires_at = NULL,
                updated_at = clock_timestamp()
            WHERE scope_id = @scope_id AND schedule_id = @schedule_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("schedule_id", scheduleId.Value);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue("state", state);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("The Schedule dispatch row was missing during lifecycle mutation.");
        }
    }

    private static async ValueTask SupersedePendingOccurrencesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableScheduleId scheduleId,
        long generation,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE appsurface_durable.schedule_occurrence
            SET state = 'superseded', updated_at = clock_timestamp()
            WHERE scope_id = @scope_id
              AND schedule_id = @schedule_id
              AND generation = @generation
              AND state IN ('pending', 'claimed');
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("schedule_id", scheduleId.Value);
        command.Parameters.AddWithValue("generation", generation);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private void AddDefinitionParameters(
        NpgsqlCommand command,
        DurableScopeId scopeId,
        DurableScheduleId scheduleId,
        string? displayName,
        long generation,
        long revision,
        DateTimeOffset acceptedAtUtc,
        DateTimeOffset cursorUtc,
        DateTimeOffset nextDueUtc,
        long scopeGeneration)
    {
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("schedule_id", scheduleId.Value);
        command.Parameters.Add(new NpgsqlParameter("display_name", NpgsqlDbType.Text)
        {
            Value = (object?)displayName ?? DBNull.Value,
        });
        command.Parameters.AddWithValue("generation", generation);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue("accepted_at_utc", acceptedAtUtc.UtcDateTime);
        command.Parameters.AddWithValue("cursor_utc", cursorUtc.UtcDateTime);
        command.Parameters.AddWithValue("next_due_utc", nextDueUtc.UtcDateTime);
        command.Parameters.AddWithValue("scope_generation", scopeGeneration);
        command.Parameters.AddWithValue("runtime_epoch", _workOptions.RuntimeEpoch);
    }

    private static bool TryDescribeSchedule(
        DurableSchedule schedule,
        out ScheduleDefinition? definition,
        out DurableOperationResult<DurableScheduleMutationResult>? failure)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        definition = schedule switch
        {
            DurableAtSchedule at => new ScheduleDefinition("at", at.AtUtc, null, null, null, null, null, null, null, schedule),
            DurableAfterSchedule after => new ScheduleDefinition("after", null, after.Delay, null, null, null, null, null, null, schedule),
            DurableEverySchedule every => new ScheduleDefinition("every", null, null, every.Interval, every.AnchorUtc, null, null, null, null, schedule),
            DurableCronSchedule cron => new ScheduleDefinition(
                "cron", null, null, null, null, cron.Expression, cron.IanaTimeZoneId, ToCronDialect(cron.Dialect), ToCronGrammar(cron.Grammar), schedule),
            _ => null,
        };
        if (definition is null)
        {
            failure = Failure<DurableScheduleMutationResult>(
                DurableScheduleProblemCodes.ScheduleInvalid,
                "The Schedule shape is not recognized by the PostgreSQL provider.",
                "The request did not use one of the supported durable Schedule shapes.",
                "Construct the Schedule through DurableSchedule.At, After, Every, or Cron.",
                "schedule");
            return false;
        }

        if (schedule is DurableCronSchedule)
        {
            failure = Failure<DurableScheduleMutationResult>(
                DurableScheduleProblemCodes.DialectUnsupported,
                "CronosV1 is not admitted by the Work-first PostgreSQL Schedule provider.",
                "Cron evaluation requires pinned evaluator and time-zone rule compatibility evidence.",
                "Use At, After, or Every until the Cron compatibility gate is implemented.",
                "schedule");
            return false;
        }

        if (schedule.OverlapPolicy.Kind != ScheduleOverlapPolicyKind.QueueOne
            || schedule.OverlapPolicy.MaximumConcurrentRuns != 1
            || schedule.MisfirePolicy.Kind != ScheduleMisfirePolicyKind.RunOnce
            || schedule.MisfirePolicy.MaximumOccurrences != 1)
        {
            failure = Failure<DurableScheduleMutationResult>(
                DurableScheduleProblemCodes.ScheduleInvalid,
                "This Work-first Schedule provider only admits QueueOne overlap and RunOnce misfire policies.",
                "Skip, bounded concurrency, and catch-up policies require occurrence-state semantics that are not implemented by Gate A.",
                "Use the default QueueOne and RunOnce policies until the policy compatibility gate is implemented.",
                "schedule");
            return false;
        }

        failure = null;
        return true;
    }

    private LifecycleTransition DescribeLifecycleTransition(DurableScheduleCommandKind kind, DefinitionLock current) => kind switch
    {
        DurableScheduleCommandKind.Pause when current.State == "active" =>
            new("paused", "paused", DurableScheduleMutationCode.Paused, "suspended", true),
        DurableScheduleCommandKind.Resume when current.State == "paused" =>
            new("active", "resumed", DurableScheduleMutationCode.Resumed, "available", true),
        DurableScheduleCommandKind.Delete when current.State != "deleted" =>
            new("deleted", "deleted", DurableScheduleMutationCode.Deleted, "terminal", true),
        DurableScheduleCommandKind.ReleaseAfterRecovery
            when current.State == "suspended"
                 && current.RuntimeEpoch != _workOptions.RuntimeEpoch
                 && current.SuspensionCode != DurableScheduleProblemCodes.EvaluationChanged =>
            new("active", "recovery_released", DurableScheduleMutationCode.RecoveryReleased, "available", true),
        DurableScheduleCommandKind.Pause
            or DurableScheduleCommandKind.Resume
            or DurableScheduleCommandKind.Delete
            or DurableScheduleCommandKind.ReleaseAfterRecovery =>
            new(current.State, "unchanged", DurableScheduleMutationCode.Unchanged, null, false),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private bool TryValidateTarget(
        DurableScheduleTarget target,
        string correlationId,
        out ScheduleTargetData? data,
        out DurableOperationResult<DurableScheduleMutationResult>? failure)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Kind != DurableScheduleTargetKind.Work)
        {
            data = null;
            failure = Failure<DurableScheduleMutationResult>(
                DurableScheduleProblemCodes.ScheduleInvalid,
                "Flow Schedule targets are deferred until their atomic start seam is proven.",
                "The current Flow client owns its transaction and cannot atomically link a Schedule occurrence.",
                "Use a registered Work target for the Work-first provider path.",
                correlationId);
            return false;
        }

        try
        {
            var registration = _workRegistry.GetRequired(target.RegisteredName, target.RegisteredVersion);
            _ = registration.WorkCodec.DecodeObject(target.EncodedInput);
            data = new ScheduleTargetData(
                "work",
                target.RegisteredName,
                target.RegisteredVersion,
                target.EncodedInput,
                registration.ProviderSafety);
            failure = null;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            data = null;
            failure = Failure<DurableScheduleMutationResult>(
                DurableScheduleProblemCodes.ScheduleInvalid,
                "The Schedule Work target is not a registered durable Work contract.",
                "The target name, version, codec, payload, or provider-safety snapshot did not match the Work registry.",
                "Register the exact Work contract and create the target with its matching codec.",
                correlationId);
            return false;
        }
    }

    private static DateTimeOffset InitialCursor(DurableSchedule schedule, DateTimeOffset acceptedAtUtc) => schedule switch
    {
        // PostgreSQL timestamp values are stored at microsecond precision. Leave a millisecond of durable room before an
        // At instant so the strict (cursor, cutoff] evaluator cannot collapse both values onto the same stored tick.
        DurableAtSchedule at => at.AtUtc - TimeSpan.FromMilliseconds(1),
        _ => acceptedAtUtc,
    };

    private static DateTimeOffset FirstDue(DurableSchedule schedule, DateTimeOffset acceptedAtUtc) => schedule switch
    {
        DurableAtSchedule at => at.AtUtc,
        DurableAfterSchedule after => acceptedAtUtc + after.Delay,
        DurableEverySchedule every => (every.AnchorUtc ?? acceptedAtUtc) + every.Interval,
        _ => acceptedAtUtc,
    };

    private static string ToCommandKind(DurableScheduleCommandKind kind) => kind switch
    {
        DurableScheduleCommandKind.Pause => "pause",
        DurableScheduleCommandKind.Resume => "resume",
        DurableScheduleCommandKind.Delete => "delete",
        DurableScheduleCommandKind.ReleaseAfterRecovery => "recovery_release",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string ToState(DurableScheduleState state) => state switch
    {
        DurableScheduleState.Active => "active",
        DurableScheduleState.Paused => "paused",
        DurableScheduleState.Deleted => "deleted",
        DurableScheduleState.Suspended => "suspended",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private static DurableScheduleState FromState(string state) => state switch
    {
        "active" => DurableScheduleState.Active,
        "paused" => DurableScheduleState.Paused,
        "deleted" => DurableScheduleState.Deleted,
        "suspended" => DurableScheduleState.Suspended,
        _ => throw new InvalidOperationException("The persisted Schedule state is malformed."),
    };

    private static string ToClassification(DurableDataClassification classification) => classification switch
    {
        DurableDataClassification.Operational => "operational",
        DurableDataClassification.ApprovedApplication => "approved_application",
        _ => throw new ArgumentOutOfRangeException(nameof(classification)),
    };

    private static DurableDataClassification FromClassification(string classification) => classification switch
    {
        "operational" => DurableDataClassification.Operational,
        "approved_application" => DurableDataClassification.ApprovedApplication,
        _ => throw new InvalidOperationException("The persisted Schedule target classification is malformed."),
    };

    private static string ToProviderSafety(DurableProviderSafety safety) => safety switch
    {
        DurableProviderSafety.Idempotent => "idempotent",
        DurableProviderSafety.ProviderKeyed => "provider_keyed",
        DurableProviderSafety.ReconcileBeforeRetry => "reconcile_before_retry",
        DurableProviderSafety.ManualResolution => "manual_resolution",
        _ => throw new ArgumentOutOfRangeException(nameof(safety)),
    };

    private static DurableProviderSafety FromProviderSafety(string safety) => safety switch
    {
        "idempotent" => DurableProviderSafety.Idempotent,
        "provider_keyed" => DurableProviderSafety.ProviderKeyed,
        "reconcile_before_retry" => DurableProviderSafety.ReconcileBeforeRetry,
        "manual_resolution" => DurableProviderSafety.ManualResolution,
        _ => throw new InvalidOperationException("The persisted Schedule Work safety mode is malformed."),
    };

    private static string ToCronDialect(CronDialect dialect) => dialect switch
    {
        CronDialect.CronosV1 => "cronos_v1",
        _ => throw new ArgumentOutOfRangeException(nameof(dialect)),
    };

    private static string ToCronGrammar(CronGrammar grammar) => grammar switch
    {
        CronGrammar.Standard => "standard",
        CronGrammar.IncludeSeconds => "include_seconds",
        _ => throw new ArgumentOutOfRangeException(nameof(grammar)),
    };

    private static ScheduleOverlapPolicy FromOverlap(string kind, int limit) => kind switch
    {
        "queue_one" when limit == 1 => ScheduleOverlapPolicy.QueueOne,
        "skip" when limit == 1 => ScheduleOverlapPolicy.Skip,
        "allow_concurrent" => ScheduleOverlapPolicy.AllowConcurrent(limit),
        _ => throw new InvalidOperationException("The persisted Schedule overlap policy is malformed."),
    };

    private static ScheduleMisfirePolicy FromMisfire(string kind, int limit) => kind switch
    {
        "run_once" when limit == 1 => ScheduleMisfirePolicy.RunOnce,
        "skip" when limit == 0 => ScheduleMisfirePolicy.Skip,
        "catch_up" => ScheduleMisfirePolicy.CatchUp(limit),
        _ => throw new InvalidOperationException("The persisted Schedule misfire policy is malformed."),
    };

    private static DurableOperationResult<T> Failure<T>(string code, string problem, string cause, string fix, string correlationId)
        where T : class => DurableOperationResult<T>.Failure(new DurableProblem(code, problem, cause, fix, ScheduleDocumentation, correlationId));

    private static DurableOperationResult<T> NotFound<T>(string correlationId)
        where T : class => Failure<T>(
            DurableScheduleProblemCodes.ScheduleNotFound,
            "The authorized scope does not contain the requested Schedule.",
            "No Schedule row matched the trusted scope and Schedule identity.",
            "Reload the authorized Schedule inventory and use an identity from that scope.",
            correlationId);

    private static DurableOperationResult<T> RevisionConflict<T>(string correlationId)
        where T : class => Failure<T>(
            DurableScheduleProblemCodes.RevisionConflict,
            "The Schedule revision did not match authoritative state.",
            "Another mutation committed before this optimistic-concurrency command.",
            "Reload the Schedule snapshot and retry only the intended mutation with its current revision.",
            correlationId);

    private static DurableOperationResult<T> CommandConflict<T>(string correlationId)
        where T : class => Failure<T>(
            DurableScheduleProblemCodes.CommandConflict,
            "The Schedule command identity was already used for different content.",
            "A retry changed its target, definition, lifecycle request, or optimistic revision.",
            "Retry the exact original command or use a new command identity for the changed intent.",
            correlationId);

    private static async ValueTask TryRollbackAsync(NpgsqlTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (PostgreSqlDurableExceptionFilters.IsExpectedCleanupFailure(exception))
        {
            // Preserve the original database or transport failure; disposal owns transaction cleanup.
        }
    }

    private sealed record ExistingCommand(
        DurableCommandId CommandId,
        DurableScheduleId ScheduleId,
        DurableCommandFingerprint Fingerprint,
        long Generation,
        long Revision,
        DateTimeOffset AcceptedAtUtc)
    {
        internal DurableScheduleMutationResult ToDuplicate() => new(
            ScheduleId,
            CommandId,
            DurableScheduleMutationCode.Duplicate,
            Generation,
            Revision,
            AcceptedAtUtc);
    }

    private sealed record ExistingCommandLookup(ExistingCommand? Command, bool HasConflictingIdentities);

    private sealed record DefinitionLock(
        long Generation,
        long Revision,
        long ScopeGeneration,
        string State,
        Guid RuntimeEpoch,
        string? SuspensionCode);

    private sealed record LifecycleTransition(
        string State,
        string Outcome,
        DurableScheduleMutationCode Code,
        string? DispatchState,
        bool ChangesDefinition);

    private sealed record ScheduleDefinition(
        string Kind,
        DateTimeOffset? AtUtc,
        TimeSpan? Delay,
        TimeSpan? Interval,
        DateTimeOffset? AnchorUtc,
        string? CronExpression,
        string? CronTimeZone,
        string? CronDialect,
        string? CronGrammar,
        DurableSchedule Source)
    {
        internal string OverlapKind => Source.OverlapPolicy.Kind switch
        {
            ScheduleOverlapPolicyKind.QueueOne => "queue_one",
            ScheduleOverlapPolicyKind.Skip => "skip",
            ScheduleOverlapPolicyKind.AllowConcurrent => "allow_concurrent",
            _ => throw new ArgumentOutOfRangeException(nameof(Source.OverlapPolicy.Kind)),
        };

        internal int OverlapLimit => Source.OverlapPolicy.MaximumConcurrentRuns;

        internal string MisfireKind => Source.MisfirePolicy.Kind switch
        {
            ScheduleMisfirePolicyKind.RunOnce => "run_once",
            ScheduleMisfirePolicyKind.Skip => "skip",
            ScheduleMisfirePolicyKind.CatchUp => "catch_up",
            _ => throw new ArgumentOutOfRangeException(nameof(Source.MisfirePolicy.Kind)),
        };

        internal int MisfireLimit => Source.MisfirePolicy.MaximumOccurrences;
    }

    private sealed record ScheduleTargetData(
        string Kind,
        string Name,
        string Version,
        DurableEncodedPayload Payload,
        DurableProviderSafety? ProviderSafety);

    private sealed record StoredSchedule(
        DurableScheduleId ScheduleId,
        string? DisplayName,
        string State,
        long Generation,
        long Revision,
        DateTimeOffset? NextDueUtc,
        Guid RuntimeEpoch,
        string Kind,
        DateTimeOffset? AtUtc,
        TimeSpan? Delay,
        TimeSpan? Interval,
        DateTimeOffset? AnchorUtc,
        string OverlapKind,
        int OverlapLimit,
        string MisfireKind,
        int MisfireLimit,
        string TargetKind,
        string TargetName,
        string TargetVersion,
        string TargetContractId,
        string TargetSchemaVersion,
        string TargetClassification,
        string TargetRetention,
        byte[] TargetPayload,
        string TargetSha256,
        string? TargetProviderSafety,
        string? CronExpression,
        string? CronTimeZone,
        string? CronDialect,
        string? CronGrammar)
    {
        internal DurableSchedule ToSchedule()
        {
            DurableSchedule schedule = Kind switch
            {
                "at" => DurableSchedule.At(AtUtc ?? throw new InvalidOperationException("Persisted At Schedule is malformed.")),
                "after" => DurableSchedule.After(Delay ?? throw new InvalidOperationException("Persisted After Schedule is malformed.")),
                "every" => DurableSchedule.Every(
                    Interval ?? throw new InvalidOperationException("Persisted Every Schedule is malformed."),
                    AnchorUtc),
                "cron" => DurableSchedule.Cron(
                    CronExpression ?? throw new InvalidOperationException("Persisted Cron Schedule is malformed."),
                    CronTimeZone ?? throw new InvalidOperationException("Persisted Cron Schedule is malformed."),
                    CronGrammar == "include_seconds"
                        ? global::ForgeTrust.AppSurface.Durable.CronGrammar.IncludeSeconds
                        : global::ForgeTrust.AppSurface.Durable.CronGrammar.Standard),
                _ => throw new InvalidOperationException("The persisted Schedule kind is malformed."),
            };
            return schedule.WithOverlap(FromOverlap(OverlapKind, OverlapLimit)).WithMisfire(FromMisfire(MisfireKind, MisfireLimit));
        }

        internal DurableScheduleTargetSnapshot ToTargetSnapshot()
        {
            var payload = new DurableEncodedPayload(
                TargetContractId,
                TargetSchemaVersion,
                FromClassification(TargetClassification),
                TargetPayload,
                TargetRetention);
            if (!string.Equals(payload.Sha256, TargetSha256, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The persisted Schedule target payload hash is malformed.");
            }

            return new DurableScheduleTargetSnapshot(
                TargetKind == "work" ? DurableScheduleTargetKind.Work : DurableScheduleTargetKind.Flow,
                TargetName,
                TargetVersion,
                payload,
                TargetProviderSafety is null ? null : FromProviderSafety(TargetProviderSafety));
        }

        internal DurableScheduleSnapshot ToSnapshot() => new(
            ScheduleId,
            DisplayName,
            FromState(State),
            Generation,
            Revision,
            ToSchedule(),
            ToTargetSnapshot(),
            NextDueUtc);

        internal DurableScheduleListItem ToListItem(Guid activeRuntimeEpoch)
        {
            var schedule = ToSchedule();
            return new DurableScheduleListItem(
                ScheduleId,
                DisplayName,
                FromState(State),
                Generation,
                Revision,
                schedule.Kind,
                schedule.OverlapPolicy,
                schedule.MisfirePolicy,
                TargetKind == "work" ? DurableScheduleTargetKind.Work : DurableScheduleTargetKind.Flow,
                TargetName,
                TargetVersion,
                TargetProviderSafety is null ? null : FromProviderSafety(TargetProviderSafety),
                NextDueUtc,
                State != "deleted" && RuntimeEpoch != activeRuntimeEpoch);
        }
    }

    private sealed record StoredScheduleList(
        DurableScheduleId ScheduleId,
        string? DisplayName,
        string State,
        long Generation,
        long Revision,
        DateTimeOffset? NextDueUtc,
        Guid RuntimeEpoch,
        DurableScheduleKind ScheduleKind,
        string OverlapKind,
        int OverlapLimit,
        string MisfireKind,
        int MisfireLimit,
        DurableScheduleTargetKind TargetKind,
        string TargetName,
        string TargetVersion,
        string? TargetProviderSafety)
    {
        internal DurableScheduleListItem ToListItem(Guid activeRuntimeEpoch) => new(
            ScheduleId,
            DisplayName,
            FromState(State),
            Generation,
            Revision,
            ScheduleKind,
            FromOverlap(OverlapKind, OverlapLimit),
            FromMisfire(MisfireKind, MisfireLimit),
            TargetKind,
            TargetName,
            TargetVersion,
            TargetProviderSafety is null ? null : FromProviderSafety(TargetProviderSafety),
            NextDueUtc,
            State != "deleted" && RuntimeEpoch != activeRuntimeEpoch);
    }

    private sealed record ProcessingSchedule(
        DurableScopeId ScopeId,
        DurableScheduleId ScheduleId,
        string State,
        long Generation,
        long Revision,
        DateTimeOffset CursorUtc,
        DateTimeOffset NextDueUtc,
        DateTimeOffset AcceptedAtUtc,
        long ScopeGeneration,
        Guid RuntimeEpoch,
        DurableSchedule Schedule,
        StoredSchedule Target);

    private readonly record struct ScheduleDueRange(
        DateTimeOffset FirstUtc,
        DateTimeOffset LastUtc,
        DateTimeOffset NextDueUtc,
        string Kind);

    private sealed record PendingScheduleOccurrence(
        string OccurrenceId,
        DateTimeOffset FirstNominalUtc,
        DateTimeOffset LastNominalUtc);

    private sealed record CoalescedOccurrence(string OccurrenceId, bool Inserted);
}

internal sealed record ScheduleDispatchClaim(
    DurableScopeId ScopeId,
    DurableScheduleId ScheduleId,
    long DispatchRevision);

internal sealed record ScheduleProcessOutcome(int RecordedOccurrences, int MaterializedWorkTargets, int SuspendedSchedules)
{
    internal static ScheduleProcessOutcome None { get; } = new(0, 0, 0);
    internal static ScheduleProcessOutcome OccurrenceOnly { get; } = new(1, 0, 0);
    internal static ScheduleProcessOutcome WorkOnly { get; } = new(0, 1, 0);
    internal static ScheduleProcessOutcome OccurrenceAndWork { get; } = new(1, 1, 0);
    internal static ScheduleProcessOutcome Suspended { get; } = new(0, 0, 1);
}
