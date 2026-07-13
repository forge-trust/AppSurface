using System.Data;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

internal sealed record PostgreSqlScheduleProcessingRequest
{
    public PostgreSqlScheduleProcessingRequest(
        int maximumSchedules = 32,
        int maximumOccurrencesPerSchedule = 100,
        TimeSpan? evaluationTimeBudgetPerSchedule = null)
    {
        if (maximumSchedules is < 1 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSchedules));
        }

        MaximumSchedules = maximumSchedules;
        EvaluationBudget = new ScheduleEvaluationBudget(
            maximumOccurrencesPerSchedule,
            evaluationTimeBudgetPerSchedule ?? TimeSpan.FromMilliseconds(50));
    }

    public int MaximumSchedules { get; }

    public ScheduleEvaluationBudget EvaluationBudget { get; }
}

internal sealed record PostgreSqlScheduleProcessingResult(
    int Discovered,
    int Advanced,
    int Materialized,
    int Started,
    int Queued,
    int Coalesced,
    int Skipped,
    bool HasMore);

internal sealed record PostgreSqlScheduleDispatchCandidate(
    Guid DispatchId,
    DurableScopeId ScopeId,
    DurableScheduleId ScheduleId,
    DateTimeOffset DueAtUtc,
    long ExpectedRevision);

internal sealed record PostgreSqlScheduleCurrent(
    DurableScopeId ScopeId,
    DurableScheduleSnapshot Snapshot,
    DateTimeOffset AcceptedAtUtc,
    SchedulePendingOccurrence? PendingOccurrence,
    int? CatchUpRemaining,
    long ScopeGeneration,
    Guid RuntimeEpoch,
    string? CronEvaluatorVersion,
    int? CronJitterSeed,
    string? TimeZoneRulesFingerprint);

internal sealed record PostgreSqlScheduleTargetStart(
    DurableScheduleTargetKind Kind,
    string TargetId);

internal sealed class PostgreSqlDurableScheduleProcessor
{
    private readonly NpgsqlDataSource dataSource;
    private readonly IDurableWorkRegistry workRegistry;
    private readonly IDurableFlowRegistry flowRegistry;
    private readonly Guid runtimeEpoch;
    private readonly bool sendWakeNotification;

    public PostgreSqlDurableScheduleProcessor(
        NpgsqlDataSource dataSource,
        IDurableWorkRegistry workRegistry,
        IDurableFlowRegistry flowRegistry,
        Guid runtimeEpoch,
        bool sendWakeNotification = true)
    {
        this.dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        this.workRegistry = workRegistry ?? throw new ArgumentNullException(nameof(workRegistry));
        this.flowRegistry = flowRegistry ?? throw new ArgumentNullException(nameof(flowRegistry));
        if (runtimeEpoch == Guid.Empty)
        {
            throw new ArgumentException("The durable runtime epoch must not be empty.", nameof(runtimeEpoch));
        }

        this.runtimeEpoch = runtimeEpoch;
        this.sendWakeNotification = sendWakeNotification;
    }

    public async ValueTask<PostgreSqlScheduleProcessingResult> ProcessDueAsync(
        PostgreSqlScheduleProcessingRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        var resolvedRequest = request ?? new PostgreSqlScheduleProcessingRequest();
        var candidates = await DiscoverAsync(resolvedRequest.MaximumSchedules, cancellationToken).ConfigureAwait(false);
        var totals = new ProcessingCounts();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await ProcessCandidateAsync(
                candidate,
                resolvedRequest.EvaluationBudget,
                cancellationToken).ConfigureAwait(false);
            totals.Add(result);
        }

        return new PostgreSqlScheduleProcessingResult(
            candidates.Count,
            totals.Advanced,
            totals.Materialized,
            totals.Started,
            totals.Queued,
            totals.Coalesced,
            totals.Skipped,
            candidates.Count == resolvedRequest.MaximumSchedules);
    }

    public async ValueTask<bool> ReleaseTargetAsync(
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableScheduleTargetKind targetKind,
        string targetId,
        string terminalCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var connection = transaction.Connection
            ?? throw new InvalidOperationException("The supplied schedule release transaction is not active.");
        if (connection.State != System.Data.ConnectionState.Open)
        {
            throw new InvalidOperationException("The supplied schedule release transaction connection must be open.");
        }

        if (string.IsNullOrWhiteSpace(targetId) || targetId.Length > 200)
        {
            throw new ArgumentException("Target id must contain 1 to 200 characters.", nameof(targetId));
        }

        if (string.IsNullOrWhiteSpace(terminalCode) || terminalCode.Length > 120)
        {
            throw new ArgumentException("Terminal code must contain 1 to 120 characters.", nameof(terminalCode));
        }

        await PostgreSqlDurableEpochFence.EnsureCurrentAsync(
            connection, transaction, runtimeEpoch, cancellationToken).ConfigureAwait(false);
        await PostgreSqlScheduleStorage.SetScopeAsync(
            connection, transaction, scopeId, cancellationToken).ConfigureAwait(false);
        var scope = await PostgreSqlScheduleStorage.LockScopeAsync(
            connection, transaction, scopeId, cancellationToken).ConfigureAwait(false);
        if (scope is null)
        {
            return false;
        }

        var slot = await LockRunSlotAsync(
            connection, transaction, scopeId, targetKind, targetId, cancellationToken).ConfigureAwait(false);
        if (slot is null)
        {
            return false;
        }

        var current = await LockCurrentAsync(
            connection,
            transaction,
            scopeId,
            slot.ScheduleId,
            expectedRevision: null,
            cancellationToken,
            requireActiveScope: false).ConfigureAwait(false)
            ?? throw new InvalidDataException("An active schedule run slot has no owning schedule.");
        var activeRuns = await CountActiveRunsAsync(
            connection, transaction, scopeId, slot.ScheduleId, cancellationToken).ConfigureAwait(false);
        var state = new ScheduleRunSlotState(activeRuns, current.PendingOccurrence);
        var decision = ScheduleRunSlotPolicyCalculator.Release(
            state,
            current.Snapshot.State,
            current.Snapshot.Generation,
            current.Snapshot.Schedule.OverlapPolicy);

        await MarkSlotReleasedAsync(
            connection, transaction, slot, terminalCode, cancellationToken).ConfigureAwait(false);
        if (current.RuntimeEpoch != runtimeEpoch)
        {
            await FenceReleasedTargetForRecoveryAsync(
                connection,
                transaction,
                current,
                cancellationToken).ConfigureAwait(false);
            return true;
        }

        var started = 0;
        if (scope.IsActive
            && scope.Generation == current.ScopeGeneration
            && current.Snapshot.State == DurableScheduleState.Active
            && decision.OccurrenceToStart is not null)
        {
            var occurrenceId = await FindPendingOccurrenceIdAsync(
                connection,
                transaction,
                scopeId,
                slot.ScheduleId,
                decision.OccurrenceToStart,
                cancellationToken).ConfigureAwait(false);
            await StartOccurrenceAsync(
                connection,
                transaction,
                current,
                occurrenceId,
                decision.OccurrenceToStart,
                cancellationToken).ConfigureAwait(false);
            started = 1;
        }

        var newRevision = checked(current.Snapshot.Revision + 1);
        await UpdateCurrentAfterPolicyAsync(
            connection,
            transaction,
            current,
            nextNominalDueUtc: current.Snapshot.NextOccurrenceUtc,
            decision.State.PendingOccurrence,
            current.CatchUpRemaining,
            newRevision,
            cancellationToken).ConfigureAwait(false);
        await InsertProcessorHistoryAsync(
            connection,
            transaction,
            current,
            newRevision,
            started == 1 ? "slot_released_pending_started" : "slot_released",
            nominalDueUtc: null,
            cancellationToken).ConfigureAwait(false);
        await PostgreSqlDurableScheduleClient.UpsertDispatchAsync(
            connection,
            transaction,
            scopeId,
            slot.ScheduleId,
            current.Snapshot.NextOccurrenceUtc,
            current.Snapshot.State switch
            {
                DurableScheduleState.Active => "active",
                DurableScheduleState.Deleted => "deleted",
                _ => "paused",
            },
            newRevision,
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async ValueTask FenceReleasedTargetForRecoveryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostgreSqlScheduleCurrent current,
        CancellationToken cancellationToken)
    {
        var revision = checked(current.Snapshot.Revision + 1);
        const string sql = """
            UPDATE appsurface_durable.schedule_current
            SET suspended_from_state = CASE
                    WHEN state IN ('active', 'paused') THEN state
                    ELSE suspended_from_state
                END,
                suspension_code = CASE
                    WHEN state IN ('active', 'paused') THEN @recovery_code
                    ELSE suspension_code
                END,
                state = CASE WHEN state IN ('active', 'paused') THEN 'suspended' ELSE state END,
                revision = @revision,
                updated_at = clock_timestamp()
            WHERE scope_id = @scope_id
              AND schedule_id = @schedule_id
              AND revision = @expected_revision;

            UPDATE appsurface_durable.dispatch
            SET state = CASE WHEN @schedule_state = 'deleted' THEN 'terminal' ELSE 'suspended' END,
                expected_revision = @revision,
                updated_at = clock_timestamp()
            WHERE scope_id = @scope_id
              AND aggregate_kind = 'schedule'
              AND aggregate_id = @schedule_id;

            INSERT INTO appsurface_durable.schedule_history
                (scope_id, schedule_id, aggregate_revision, schedule_generation, event_type, details)
            VALUES
                (@scope_id, @schedule_id, @revision, @generation, 'slot_released_recovery_fenced',
                 jsonb_build_object('code', @recovery_code));
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", current.ScopeId.Value);
        command.Parameters.AddWithValue("schedule_id", current.Snapshot.ScheduleId.Value);
        command.Parameters.AddWithValue("expected_revision", current.Snapshot.Revision);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue("generation", current.Snapshot.Generation);
        command.Parameters.AddWithValue("schedule_state", PostgreSqlScheduleStorage.FormatState(current.Snapshot.State));
        command.Parameters.AddWithValue("recovery_code", DurableProblemCodes.RecoveryEpochRequired);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 3)
        {
            throw new DBConcurrencyException(
                "The old-epoch schedule changed while its terminal target was being recovery-fenced.");
        }
    }

    private async ValueTask<IReadOnlyList<PostgreSqlScheduleDispatchCandidate>> DiscoverAsync(
        int maximumSchedules,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT dispatch_id, scope_id, aggregate_id, due_at, expected_revision
            FROM appsurface_durable.dispatch
            WHERE aggregate_kind = 'schedule'
              AND state = 'available'
              AND due_at <= clock_timestamp()
            ORDER BY due_at, dispatch_id
            LIMIT @maximum_schedules;
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("maximum_schedules", maximumSchedules);
        var candidates = new List<PostgreSqlScheduleDispatchCandidate>(maximumSchedules);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            candidates.Add(new PostgreSqlScheduleDispatchCandidate(
                reader.GetGuid(0),
                new DurableScopeId(reader.GetString(1)),
                new DurableScheduleId(reader.GetString(2)),
                PostgreSqlScheduleStorage.ReadUtc(reader, 3),
                reader.GetInt64(4)));
        }

        return candidates;
    }

    private async ValueTask<ProcessingCounts> ProcessCandidateAsync(
        PostgreSqlScheduleDispatchCandidate candidate,
        ScheduleEvaluationBudget budget,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PostgreSqlDurableEpochFence.EnsureCurrentAsync(
                connection, transaction, runtimeEpoch, cancellationToken).ConfigureAwait(false);
            await PostgreSqlScheduleStorage.SetScopeAsync(
                connection, transaction, candidate.ScopeId, cancellationToken).ConfigureAwait(false);
            var current = await LockCurrentAsync(
                connection,
                transaction,
                candidate.ScopeId,
                candidate.ScheduleId,
                candidate.ExpectedRevision,
                cancellationToken).ConfigureAwait(false);
            if (current is null || current.Snapshot.State != DurableScheduleState.Active)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new ProcessingCounts();
            }

            if (current.RuntimeEpoch != runtimeEpoch)
            {
                await SuspendScheduleAsync(
                    connection,
                    transaction,
                    candidate,
                    current,
                    "recovery_epoch_mismatch",
                    DurableProblemCodes.RecoveryEpochRequired,
                    cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new ProcessingCounts { Advanced = 1 };
            }

            IScheduleOccurrenceCalculator calculator;
            try
            {
                calculator = ScheduleOccurrenceCalculatorFactory.Create(
                    candidate.ScheduleId,
                    current.Snapshot.Schedule,
                    current.AcceptedAtUtc);
                ValidateTargetRegistration(current.Snapshot.Target);
            }
            catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
            {
                await SuspendScheduleAsync(
                    connection,
                    transaction,
                    candidate,
                    current,
                    "schedule_definition_unavailable",
                    DurableScheduleProblemCodes.ScheduleInvalid,
                    cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new ProcessingCounts { Advanced = 1, Skipped = 1 };
            }
            if (!HasCompatibleRuntimePins(current, calculator))
            {
                await SuspendScheduleAsync(
                    connection,
                    transaction,
                    candidate,
                    current,
                    "schedule_evaluator_changed",
                    DurableScheduleProblemCodes.EvaluationChanged,
                    cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new ProcessingCounts { Advanced = 1 };
            }

            var now = await PostgreSqlScheduleStorage.ReadTransactionTimeAsync(
                connection, transaction, cancellationToken).ConfigureAwait(false);
            var activeRuns = await CountActiveRunsAsync(
                connection, transaction, candidate.ScopeId, candidate.ScheduleId, cancellationToken).ConfigureAwait(false);
            var slotState = new ScheduleRunSlotState(activeRuns, current.PendingOccurrence);
            var counts = new ProcessingCounts();
            slotState = await ActivatePendingAsync(
                connection, transaction, current, slotState, counts, cancellationToken).ConfigureAwait(false);

            var configuredMisfire = current.Snapshot.Schedule.MisfirePolicy;
            var catchUpRemaining = configuredMisfire.Kind == ScheduleMisfirePolicyKind.CatchUp
                ? current.CatchUpRemaining ?? configuredMisfire.MaximumOccurrences
                : (int?)null;
            var effectiveMisfire = catchUpRemaining is { } remaining
                ? ScheduleMisfirePolicy.CatchUp(remaining)
                : configuredMisfire;
            var recovery = new ScheduleMisfireRecoveryCalculator().Calculate(
                calculator,
                current.Snapshot.NextOccurrenceUtc,
                now,
                effectiveMisfire,
                budget);
            var nextCatchUpRemaining = recovery.ContinuationRequired && catchUpRemaining is { } remainingCount
                ? remainingCount - recovery.Occurrences.Count
                : (int?)null;
            foreach (var window in recovery.Occurrences)
            {
                var occurrence = new ScheduleGenerationOccurrence(current.Snapshot.Generation, window);
                var occurrenceId = await InsertOccurrenceAsync(
                    connection, transaction, candidate.ScopeId, candidate.ScheduleId, occurrence, cancellationToken)
                    .ConfigureAwait(false);
                counts.Materialized++;
                var decision = ScheduleRunSlotPolicyCalculator.ApplyOccurrence(
                    slotState,
                    current.Snapshot.State,
                    current.Snapshot.Generation,
                    current.Snapshot.Schedule.OverlapPolicy,
                    occurrence);
                slotState = await ApplyDecisionAsync(
                    connection,
                    transaction,
                    current,
                    occurrenceId,
                    occurrence,
                    decision,
                    counts,
                    cancellationToken).ConfigureAwait(false);
            }

            var changed = counts.Materialized > 0
                || counts.Started > 0
                || current.Snapshot.NextOccurrenceUtc != recovery.NextNominalDueUtc
                || current.PendingOccurrence != slotState.PendingOccurrence
                || current.CatchUpRemaining != nextCatchUpRemaining;
            if (!changed)
            {
                await PostgreSqlDurableScheduleClient.UpsertDispatchAsync(
                    connection,
                    transaction,
                    candidate.ScopeId,
                    candidate.ScheduleId,
                    recovery.NextNominalDueUtc,
                    "active",
                    current.Snapshot.Revision,
                    cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return counts;
            }

            var revision = checked(current.Snapshot.Revision + 1);
            await UpdateCurrentAfterPolicyAsync(
                connection,
                transaction,
                current,
                recovery.NextNominalDueUtc,
                slotState.PendingOccurrence,
                nextCatchUpRemaining,
                revision,
                cancellationToken).ConfigureAwait(false);
            await InsertProcessorHistoryAsync(
                connection,
                transaction,
                current,
                revision,
                "occurrences_materialized",
                recovery.Occurrences.FirstOrDefault()?.NominalDueUtc,
                cancellationToken).ConfigureAwait(false);
            await PostgreSqlDurableScheduleClient.UpsertDispatchAsync(
                connection,
                transaction,
                candidate.ScopeId,
                candidate.ScheduleId,
                recovery.ContinuationRequired ? now : recovery.NextNominalDueUtc,
                "active",
                revision,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            counts.Advanced++;
            return counts;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static bool HasCompatibleRuntimePins(
        PostgreSqlScheduleCurrent current,
        IScheduleOccurrenceCalculator calculator)
    {
        if (calculator is not CronosV1ScheduleCalculator cronos)
        {
            return true;
        }

        return string.Equals(
                current.CronEvaluatorVersion,
                CronosV1ScheduleCalculator.EvaluatorVersion,
                StringComparison.Ordinal)
            && current.CronJitterSeed == cronos.JitterSeed
            && string.Equals(
                current.TimeZoneRulesFingerprint,
                cronos.TimeZoneRulesFingerprint,
                StringComparison.Ordinal);
    }

    private void ValidateTargetRegistration(DurableScheduleTargetSnapshot target)
    {
        if (target.Kind == DurableScheduleTargetKind.Work)
        {
            var registration = workRegistry.GetRequired(target.RegisteredName, target.RegisteredVersion);
            if (target.ProviderSafety != registration.ProviderSafety
                || !MatchesCodec(target.Input, registration.WorkCodec))
            {
                throw new InvalidDataException(
                    "Persisted schedule work target no longer matches its immutable registration.");
            }

            _ = registration.WorkCodec.DecodeObject(target.Input);
            return;
        }

        var flowRegistration = flowRegistry.GetRequired(target.RegisteredName, target.RegisteredVersion);
        if (!MatchesCodec(target.Input, flowRegistration.ContextCodec))
        {
            throw new InvalidDataException(
                "Persisted schedule Flow target no longer matches its immutable registration.");
        }

        _ = flowRegistration.ContextCodec.DecodeObject(target.Input);
    }

    private static async ValueTask SuspendScheduleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostgreSqlScheduleDispatchCandidate candidate,
        PostgreSqlScheduleCurrent current,
        string eventType,
        string code,
        CancellationToken cancellationToken)
    {
        var nextRevision = checked(current.Snapshot.Revision + 1);
        var recoverableEpochFence = string.Equals(
            code,
            DurableProblemCodes.RecoveryEpochRequired,
            StringComparison.Ordinal);
        const string sql = """
            UPDATE appsurface_durable.schedule_current
            SET state = 'suspended',
                suspended_from_state = CASE WHEN @recoverable THEN state ELSE NULL END,
                suspension_code = @code,
                pending_generation = CASE WHEN @recoverable THEN pending_generation ELSE NULL END,
                pending_nominal_due_utc = CASE WHEN @recoverable THEN pending_nominal_due_utc ELSE NULL END,
                pending_covered_through_utc = CASE WHEN @recoverable THEN pending_covered_through_utc ELSE NULL END,
                pending_covered_occurrence_count = CASE WHEN @recoverable THEN pending_covered_occurrence_count ELSE NULL END,
                catch_up_remaining = CASE WHEN @recoverable THEN catch_up_remaining ELSE NULL END,
                revision = @next_revision,
                updated_at = clock_timestamp()
            WHERE scope_id = @scope_id
              AND schedule_id = @schedule_id
              AND revision = @expected_revision;

            UPDATE appsurface_durable.dispatch
            SET state = 'suspended',
                expected_revision = @next_revision,
                updated_at = clock_timestamp()
            WHERE dispatch_id = @dispatch_id
              AND scope_id = @scope_id
              AND aggregate_kind = 'schedule'
              AND aggregate_id = @schedule_id;

            INSERT INTO appsurface_durable.schedule_history
                (scope_id, schedule_id, aggregate_revision, schedule_generation, event_type, details)
            VALUES
                (@scope_id, @schedule_id, @next_revision, @generation, @event_type,
                 jsonb_build_object('code', @code));
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", candidate.ScopeId.Value);
        command.Parameters.AddWithValue("schedule_id", candidate.ScheduleId.Value);
        command.Parameters.AddWithValue("dispatch_id", candidate.DispatchId);
        command.Parameters.AddWithValue("expected_revision", current.Snapshot.Revision);
        command.Parameters.AddWithValue("next_revision", nextRevision);
        command.Parameters.AddWithValue("generation", current.Snapshot.Generation);
        command.Parameters.AddWithValue("event_type", eventType);
        command.Parameters.AddWithValue("code", code);
        command.Parameters.AddWithValue("recoverable", recoverableEpochFence);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 3)
        {
            throw new DBConcurrencyException("The schedule changed while recovery fencing was being recorded.");
        }

        if (recoverableEpochFence)
        {
            return;
        }

        const string invalidateSql = """
            UPDATE appsurface_durable.schedule_occurrence
            SET state = 'invalidated',
                resolved_at = clock_timestamp()
            WHERE scope_id = @scope_id
              AND schedule_id = @schedule_id
              AND state IN ('ready', 'queued', 'coalesced');
            """;
        await using var invalidate = new NpgsqlCommand(invalidateSql, connection, transaction);
        invalidate.Parameters.AddWithValue("scope_id", candidate.ScopeId.Value);
        invalidate.Parameters.AddWithValue("schedule_id", candidate.ScheduleId.Value);
        await invalidate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ScheduleRunSlotState> ActivatePendingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostgreSqlScheduleCurrent current,
        ScheduleRunSlotState slotState,
        ProcessingCounts counts,
        CancellationToken cancellationToken)
    {
        var decision = ScheduleRunSlotPolicyCalculator.ActivatePending(
            slotState,
            current.Snapshot.State,
            current.Snapshot.Generation,
            current.Snapshot.Schedule.OverlapPolicy);
        if (decision.OccurrenceToStart is not null)
        {
            var occurrenceId = await FindPendingOccurrenceIdAsync(
                connection,
                transaction,
                current.ScopeId,
                current.Snapshot.ScheduleId,
                decision.OccurrenceToStart,
                cancellationToken).ConfigureAwait(false);
            await StartOccurrenceAsync(
                connection,
                transaction,
                current,
                occurrenceId,
                decision.OccurrenceToStart,
                cancellationToken).ConfigureAwait(false);
            counts.Started++;
        }
        else if (decision.Kind == ScheduleRunSlotDecisionKind.ReleasedAndDiscardedPending
                 && current.PendingOccurrence is not null)
        {
            await InvalidatePendingOccurrenceAsync(
                connection, transaction, current, cancellationToken).ConfigureAwait(false);
        }

        return decision.State;
    }

    private async ValueTask<ScheduleRunSlotState> ApplyDecisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostgreSqlScheduleCurrent current,
        Guid occurrenceId,
        ScheduleGenerationOccurrence occurrence,
        ScheduleRunSlotDecision decision,
        ProcessingCounts counts,
        CancellationToken cancellationToken)
    {
        switch (decision.Kind)
        {
            case ScheduleRunSlotDecisionKind.StartNow:
                await StartOccurrenceAsync(
                    connection, transaction, current, occurrenceId, occurrence, cancellationToken).ConfigureAwait(false);
                counts.Started++;
                break;
            case ScheduleRunSlotDecisionKind.Queued:
            case ScheduleRunSlotDecisionKind.HeldPaused:
                await MarkOccurrenceAsync(
                    connection, transaction, occurrenceId, "queued", resolved: false, cancellationToken).ConfigureAwait(false);
                counts.Queued++;
                break;
            case ScheduleRunSlotDecisionKind.Coalesced:
                await MarkOccurrenceAsync(
                    connection, transaction, occurrenceId, "coalesced", resolved: true, cancellationToken).ConfigureAwait(false);
                await UpdateQueuedOccurrenceWindowAsync(
                    connection,
                    transaction,
                    current.ScopeId,
                    current.Snapshot.ScheduleId,
                    decision.State.PendingOccurrence!,
                    cancellationToken).ConfigureAwait(false);
                counts.Coalesced++;
                break;
            case ScheduleRunSlotDecisionKind.SkippedOverlap:
            case ScheduleRunSlotDecisionKind.InvalidatedGeneration:
            case ScheduleRunSlotDecisionKind.InvalidatedDeleted:
            case ScheduleRunSlotDecisionKind.InvalidatedSuspended:
                await MarkOccurrenceAsync(
                    connection,
                    transaction,
                    occurrenceId,
                    decision.Kind == ScheduleRunSlotDecisionKind.SkippedOverlap ? "skipped" : "invalidated",
                    resolved: true,
                    cancellationToken).ConfigureAwait(false);
                counts.Skipped++;
                break;
            default:
                throw new InvalidOperationException($"Unexpected occurrence policy decision '{decision.Kind}'.");
        }

        return decision.State;
    }

    private async ValueTask<PostgreSqlScheduleTargetStart> StartOccurrenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostgreSqlScheduleCurrent current,
        Guid occurrenceId,
        ScheduleGenerationOccurrence occurrence,
        CancellationToken cancellationToken)
    {
        var target = current.Snapshot.Target;
        var identity = CreateTargetIdentity(
            current.ScopeId,
            current.Snapshot.ScheduleId,
            occurrence.Generation,
            occurrence.Window.NominalDueUtc);
        PostgreSqlScheduleTargetStart started;
        if (target.Kind == DurableScheduleTargetKind.Work)
        {
            var registration = workRegistry.GetRequired(target.RegisteredName, target.RegisteredVersion);
            if (target.ProviderSafety != registration.ProviderSafety
                || !MatchesCodec(target.Input, registration.WorkCodec))
            {
                throw new InvalidDataException("Persisted schedule work target no longer matches its immutable registration.");
            }

            var request = new DurableWorkRequest(
                current.ScopeId,
                new DurableCommandId("schedule-" + identity),
                "schedule-" + identity,
                target.RegisteredName,
                target.RegisteredVersion,
                target.Input,
                registration.ProviderSafety);
            var result = await PostgreSqlDurableWorkStore.AcceptAsync(
                transaction,
                request,
                runtimeEpoch,
                expectedStoreId: null,
                sendWakeNotification: sendWakeNotification,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess || result.Value is null)
            {
                throw new InvalidOperationException(
                    $"Scheduled durable work could not be accepted: {result.Problem?.Code ?? "unknown"}.");
            }

            started = new PostgreSqlScheduleTargetStart(DurableScheduleTargetKind.Work, result.Value.WorkId.Value);
        }
        else
        {
            var registration = flowRegistry.GetRequired(target.RegisteredName, target.RegisteredVersion);
            if (!MatchesCodec(target.Input, registration.ContextCodec))
            {
                throw new InvalidDataException("Persisted schedule Flow target no longer matches its immutable registration.");
            }

            var instanceId = new DurableFlowInstanceId("scheduled-flow-" + identity);
            var request = new DurableFlowStartRequest(
                current.ScopeId,
                new DurableCommandId("schedule-" + identity),
                "schedule-" + identity,
                instanceId,
                target.RegisteredName,
                target.RegisteredVersion,
                target.Input);
            var result = await PostgreSqlDurableFlowStore.StartAsync(
                transaction,
                request,
                registration,
                runtimeEpoch,
                sendWakeNotification,
                cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess || result.Value is null)
            {
                throw new InvalidOperationException(
                    $"Scheduled durable Flow could not be accepted: {result.Problem?.Code ?? "unknown"}.");
            }

            started = new PostgreSqlScheduleTargetStart(DurableScheduleTargetKind.Flow, instanceId.Value);
        }

        await MarkOccurrenceStartedAsync(
            connection, transaction, occurrenceId, started, cancellationToken).ConfigureAwait(false);
        await InsertRunSlotAsync(
            connection,
            transaction,
            current,
            occurrenceId,
            occurrence.Generation,
            started,
            cancellationToken).ConfigureAwait(false);
        return started;
    }

    private static bool MatchesCodec(DurableEncodedPayload payload, IDurablePayloadCodec codec) =>
        string.Equals(payload.ContractName, codec.ContractName, StringComparison.Ordinal)
        && string.Equals(payload.ContractVersion, codec.ContractVersion, StringComparison.Ordinal)
        && payload.Classification == codec.Classification
        && string.Equals(payload.RetentionPolicyId, codec.RetentionPolicyId, StringComparison.Ordinal);

    private static string CreateTargetIdentity(
        DurableScopeId scopeId,
        DurableScheduleId scheduleId,
        long generation,
        DateTimeOffset nominalDueUtc)
    {
        var input = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{scopeId.Value}\n{scheduleId.Value}\n{generation}\n{nominalDueUtc.UtcTicks}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }

    private static async ValueTask<Guid> InsertOccurrenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableScheduleId scheduleId,
        ScheduleGenerationOccurrence occurrence,
        CancellationToken cancellationToken)
    {
        var occurrenceId = Guid.NewGuid();
        const string sql = """
            INSERT INTO appsurface_durable.schedule_occurrence
                (occurrence_id, scope_id, schedule_id, schedule_generation, nominal_due_utc,
                 covered_through_utc, covered_occurrence_count, is_recovery, state)
            VALUES
                (@occurrence_id, @scope_id, @schedule_id, @generation, @nominal_due_utc,
                 @covered_through_utc, @covered_count, @is_recovery, 'ready')
            ON CONFLICT (scope_id, schedule_id, schedule_generation, nominal_due_utc) DO NOTHING
            RETURNING occurrence_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("occurrence_id", occurrenceId);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("schedule_id", scheduleId.Value);
        command.Parameters.AddWithValue("generation", occurrence.Generation);
        command.Parameters.AddWithValue("nominal_due_utc", occurrence.Window.NominalDueUtc);
        command.Parameters.AddWithValue("covered_through_utc", occurrence.Window.CoveredThroughUtc);
        PostgreSqlScheduleStorage.AddNullable(
            command,
            "covered_count",
            NpgsqlTypes.NpgsqlDbType.Bigint,
            occurrence.Window.CoveredOccurrenceCount);
        command.Parameters.AddWithValue("is_recovery", occurrence.Window.IsRecovery);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is Guid inserted
            ? inserted
            : throw new InvalidOperationException("The authoritative schedule cursor attempted to rematerialize an occurrence.");
    }

    private static async ValueTask MarkOccurrenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid occurrenceId,
        string state,
        bool resolved,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE appsurface_durable.schedule_occurrence
            SET state = @state,
                resolved_at = CASE WHEN @resolved THEN clock_timestamp() ELSE NULL END
            WHERE occurrence_id = @occurrence_id AND state = 'ready';
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("state", state);
        command.Parameters.AddWithValue("resolved", resolved);
        command.Parameters.AddWithValue("occurrence_id", occurrenceId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new DBConcurrencyException("The schedule occurrence changed before its policy decision was recorded.");
        }
    }

    private static async ValueTask MarkOccurrenceStartedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid occurrenceId,
        PostgreSqlScheduleTargetStart target,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE appsurface_durable.schedule_occurrence
            SET state = 'started',
                target_kind = @target_kind,
                target_id = @target_id,
                started_at = clock_timestamp()
            WHERE occurrence_id = @occurrence_id
              AND state IN ('ready', 'queued');
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("target_kind", PostgreSqlScheduleStorage.FormatTargetKind(target.Kind));
        command.Parameters.AddWithValue("target_id", target.TargetId);
        command.Parameters.AddWithValue("occurrence_id", occurrenceId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new DBConcurrencyException("The queued schedule occurrence changed before target acceptance.");
        }
    }

    private static async ValueTask InsertRunSlotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostgreSqlScheduleCurrent current,
        Guid occurrenceId,
        long generation,
        PostgreSqlScheduleTargetStart target,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO appsurface_durable.schedule_run_slot
                (scope_id, schedule_id, occurrence_id, schedule_generation, target_kind, target_id, state)
            VALUES
                (@scope_id, @schedule_id, @occurrence_id, @generation, @target_kind, @target_id, 'active');
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", current.ScopeId.Value);
        command.Parameters.AddWithValue("schedule_id", current.Snapshot.ScheduleId.Value);
        command.Parameters.AddWithValue("occurrence_id", occurrenceId);
        command.Parameters.AddWithValue("generation", generation);
        command.Parameters.AddWithValue("target_kind", PostgreSqlScheduleStorage.FormatTargetKind(target.Kind));
        command.Parameters.AddWithValue("target_id", target.TargetId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask UpdateQueuedOccurrenceWindowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableScheduleId scheduleId,
        SchedulePendingOccurrence pending,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE appsurface_durable.schedule_occurrence
            SET covered_through_utc = @covered_through_utc,
                covered_occurrence_count = @covered_count
            WHERE scope_id = @scope_id
              AND schedule_id = @schedule_id
              AND schedule_generation = @generation
              AND nominal_due_utc = @nominal_due_utc
              AND state = 'queued';
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("schedule_id", scheduleId.Value);
        command.Parameters.AddWithValue("generation", pending.Generation);
        command.Parameters.AddWithValue("nominal_due_utc", pending.NominalDueUtc);
        command.Parameters.AddWithValue("covered_through_utc", pending.CoalescedThroughUtc);
        PostgreSqlScheduleStorage.AddNullable(
            command, "covered_count", NpgsqlTypes.NpgsqlDbType.Bigint, pending.CoveredOccurrenceCount);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidDataException("The schedule pending cursor has no queued occurrence ledger row.");
        }
    }

    private static async ValueTask<Guid> FindPendingOccurrenceIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableScheduleId scheduleId,
        ScheduleGenerationOccurrence occurrence,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT occurrence_id
            FROM appsurface_durable.schedule_occurrence
            WHERE scope_id = @scope_id
              AND schedule_id = @schedule_id
              AND schedule_generation = @generation
              AND nominal_due_utc = @nominal_due_utc
              AND state = 'queued'
            FOR UPDATE;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("schedule_id", scheduleId.Value);
        command.Parameters.AddWithValue("generation", occurrence.Generation);
        command.Parameters.AddWithValue("nominal_due_utc", occurrence.Window.NominalDueUtc);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is Guid occurrenceId
            ? occurrenceId
            : throw new InvalidDataException("The schedule pending cursor has no queued occurrence ledger row.");
    }

    private static async ValueTask InvalidatePendingOccurrenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostgreSqlScheduleCurrent current,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE appsurface_durable.schedule_occurrence
            SET state = 'invalidated', resolved_at = clock_timestamp()
            WHERE scope_id = @scope_id
              AND schedule_id = @schedule_id
              AND schedule_generation = @generation
              AND nominal_due_utc = @nominal_due_utc
              AND state = 'queued';
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", current.ScopeId.Value);
        command.Parameters.AddWithValue("schedule_id", current.Snapshot.ScheduleId.Value);
        command.Parameters.AddWithValue("generation", current.PendingOccurrence!.Generation);
        command.Parameters.AddWithValue("nominal_due_utc", current.PendingOccurrence.NominalDueUtc);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<int> CountActiveRunsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableScheduleId scheduleId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT count(*)::integer
            FROM appsurface_durable.schedule_run_slot
            WHERE scope_id = @scope_id
              AND schedule_id = @schedule_id
              AND state = 'active';
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("schedule_id", scheduleId.Value);
        return (int)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("PostgreSQL did not return the active schedule slot count."));
    }

    private static async ValueTask<PostgreSqlScheduleCurrent?> LockCurrentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableScheduleId scheduleId,
        long? expectedRevision,
        CancellationToken cancellationToken,
        bool requireActiveScope = true)
    {
        long? activeScopeGeneration = null;
        if (requireActiveScope)
        {
            activeScopeGeneration = await PostgreSqlScheduleStorage.EnsureActiveScopeAsync(
                connection,
                transaction,
                scopeId,
                createIfMissing: false,
                cancellationToken).ConfigureAwait(false);
            if (activeScopeGeneration is null)
            {
                return null;
            }
        }

        var sql = $"""
            SELECT {PostgreSqlScheduleStorage.SnapshotColumns},
                   current.accepted_at,
                   current.pending_generation,
                   current.pending_nominal_due_utc,
                   current.pending_covered_through_utc,
                   current.pending_covered_occurrence_count,
                   current.catch_up_remaining,
                   current.scope_generation,
                   current.runtime_epoch,
                   current.cron_evaluator_version,
                   current.cron_jitter_seed,
                   current.time_zone_rules_fingerprint
            FROM appsurface_durable.schedule_current AS current
            WHERE current.scope_id = @scope_id
              AND current.schedule_id = @schedule_id
              AND (@expected_revision IS NULL OR current.revision = @expected_revision)
              AND (@active_scope_generation IS NULL OR current.scope_generation = @active_scope_generation)
            FOR UPDATE OF current;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("schedule_id", scheduleId.Value);
        PostgreSqlScheduleStorage.AddNullable(
            command, "expected_revision", NpgsqlTypes.NpgsqlDbType.Bigint, expectedRevision);
        PostgreSqlScheduleStorage.AddNullable(
            command,
            "active_scope_generation",
            NpgsqlTypes.NpgsqlDbType.Bigint,
            activeScopeGeneration);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var snapshot = PostgreSqlScheduleStorage.ReadSnapshot(reader);
        SchedulePendingOccurrence? pending = null;
        if (!reader.IsDBNull(30))
        {
            pending = new SchedulePendingOccurrence(
                reader.GetInt64(30),
                PostgreSqlScheduleStorage.ReadUtc(reader, 31),
                PostgreSqlScheduleStorage.ReadUtc(reader, 32),
                reader.IsDBNull(33) ? null : reader.GetInt64(33));
        }

        return new PostgreSqlScheduleCurrent(
            scopeId,
            snapshot,
            PostgreSqlScheduleStorage.ReadUtc(reader, 29),
            pending,
            reader.IsDBNull(34) ? null : reader.GetInt32(34),
            reader.GetInt64(35),
            reader.GetGuid(36),
            reader.IsDBNull(37) ? null : reader.GetString(37),
            reader.IsDBNull(38) ? null : reader.GetInt32(38),
            reader.IsDBNull(39) ? null : reader.GetString(39));
    }

    private static async ValueTask UpdateCurrentAfterPolicyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostgreSqlScheduleCurrent current,
        DateTimeOffset? nextNominalDueUtc,
        SchedulePendingOccurrence? pending,
        int? catchUpRemaining,
        long revision,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE appsurface_durable.schedule_current
            SET next_nominal_due_utc = @next_nominal_due_utc,
                pending_generation = @pending_generation,
                pending_nominal_due_utc = @pending_nominal_due_utc,
                pending_covered_through_utc = @pending_covered_through_utc,
                pending_covered_occurrence_count = @pending_covered_count,
                catch_up_remaining = @catch_up_remaining,
                revision = @revision,
                updated_at = clock_timestamp()
            WHERE scope_id = @scope_id
              AND schedule_id = @schedule_id
              AND revision = @expected_revision;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", current.ScopeId.Value);
        command.Parameters.AddWithValue("schedule_id", current.Snapshot.ScheduleId.Value);
        command.Parameters.AddWithValue("expected_revision", current.Snapshot.Revision);
        command.Parameters.AddWithValue("revision", revision);
        PostgreSqlScheduleStorage.AddNullable(
            command, "next_nominal_due_utc", NpgsqlTypes.NpgsqlDbType.TimestampTz, nextNominalDueUtc);
        PostgreSqlScheduleStorage.AddNullable(
            command, "pending_generation", NpgsqlTypes.NpgsqlDbType.Bigint, pending?.Generation);
        PostgreSqlScheduleStorage.AddNullable(
            command, "pending_nominal_due_utc", NpgsqlTypes.NpgsqlDbType.TimestampTz, pending?.NominalDueUtc);
        PostgreSqlScheduleStorage.AddNullable(
            command, "pending_covered_through_utc", NpgsqlTypes.NpgsqlDbType.TimestampTz, pending?.CoalescedThroughUtc);
        PostgreSqlScheduleStorage.AddNullable(
            command, "pending_covered_count", NpgsqlTypes.NpgsqlDbType.Bigint, pending?.CoveredOccurrenceCount);
        PostgreSqlScheduleStorage.AddNullable(
            command, "catch_up_remaining", NpgsqlTypes.NpgsqlDbType.Integer, catchUpRemaining);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new DBConcurrencyException("The schedule revision changed during occurrence materialization.");
        }
    }

    private static async ValueTask InsertProcessorHistoryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostgreSqlScheduleCurrent current,
        long revision,
        string eventType,
        DateTimeOffset? nominalDueUtc,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO appsurface_durable.schedule_history
                (scope_id, schedule_id, aggregate_revision, schedule_generation, event_type, nominal_due_utc)
            VALUES
                (@scope_id, @schedule_id, @revision, @generation, @event_type, @nominal_due_utc);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", current.ScopeId.Value);
        command.Parameters.AddWithValue("schedule_id", current.Snapshot.ScheduleId.Value);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue("generation", current.Snapshot.Generation);
        command.Parameters.AddWithValue("event_type", eventType);
        PostgreSqlScheduleStorage.AddNullable(
            command, "nominal_due_utc", NpgsqlTypes.NpgsqlDbType.TimestampTz, nominalDueUtc);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<PostgreSqlScheduleRunSlot?> LockRunSlotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableScheduleTargetKind targetKind,
        string targetId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT schedule_id, occurrence_id, schedule_generation, target_kind, target_id
            FROM appsurface_durable.schedule_run_slot
            WHERE scope_id = @scope_id
              AND target_kind = @target_kind
              AND target_id = @target_id
              AND state = 'active'
            FOR UPDATE;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("target_kind", PostgreSqlScheduleStorage.FormatTargetKind(targetKind));
        command.Parameters.AddWithValue("target_id", targetId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new PostgreSqlScheduleRunSlot(
                scopeId,
                new DurableScheduleId(reader.GetString(0)),
                reader.GetGuid(1),
                reader.GetInt64(2),
                PostgreSqlScheduleStorage.ParseTargetKind(reader.GetString(3)),
                reader.GetString(4))
            : null;
    }

    private static async ValueTask MarkSlotReleasedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostgreSqlScheduleRunSlot slot,
        string terminalCode,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE appsurface_durable.schedule_run_slot
            SET state = 'released', released_at = clock_timestamp(), terminal_code = @terminal_code
            WHERE scope_id = @scope_id
              AND schedule_id = @schedule_id
              AND occurrence_id = @occurrence_id
              AND state = 'active';

            UPDATE appsurface_durable.schedule_occurrence
            SET state = 'terminal', resolved_at = clock_timestamp()
            WHERE occurrence_id = @occurrence_id AND state = 'started';
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", slot.ScopeId.Value);
        command.Parameters.AddWithValue("schedule_id", slot.ScheduleId.Value);
        command.Parameters.AddWithValue("occurrence_id", slot.OccurrenceId);
        command.Parameters.AddWithValue("terminal_code", terminalCode);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed class ProcessingCounts
    {
        public int Advanced { get; set; }

        public int Materialized { get; set; }

        public int Started { get; set; }

        public int Queued { get; set; }

        public int Coalesced { get; set; }

        public int Skipped { get; set; }

        public void Add(ProcessingCounts other)
        {
            Advanced += other.Advanced;
            Materialized += other.Materialized;
            Started += other.Started;
            Queued += other.Queued;
            Coalesced += other.Coalesced;
            Skipped += other.Skipped;
        }
    }
}

internal sealed record PostgreSqlScheduleRunSlot(
    DurableScopeId ScopeId,
    DurableScheduleId ScheduleId,
    Guid OccurrenceId,
    long Generation,
    DurableScheduleTargetKind TargetKind,
    string TargetId);
