using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using ForgeTrust.AppSurface.Flow;
using Npgsql;
using NpgsqlTypes;
using static ForgeTrust.AppSurface.Durable.PostgreSql.PostgreSqlDurableProtocolCodec;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>Internal settings for one-transition Flow processing.</summary>
internal sealed record PostgreSqlDurableFlowProcessorSettings
{
    internal static PostgreSqlDurableFlowProcessorSettings Default { get; } = new(TimeSpan.FromMinutes(2));

    internal PostgreSqlDurableFlowProcessorSettings(TimeSpan evaluationLease)
    {
        if (evaluationLease < TimeSpan.FromSeconds(1) || evaluationLease > TimeSpan.FromMinutes(15))
        {
            throw new ArgumentOutOfRangeException(nameof(evaluationLease));
        }

        EvaluationLease = evaluationLease;
    }

    internal TimeSpan EvaluationLease { get; }
}

/// <summary>Observes committed protocol barriers used by subprocess crash certification.</summary>
/// <remarks>
/// Observers run after the named database boundary commits and must preserve that ordering. They must not access the
/// database or initiate another Flow operation, because observers exist only to certify recovery boundaries such as a
/// deterministic subprocess termination.
/// </remarks>
internal interface IPostgreSqlDurableFlowBarrierObserver
{
    /// <summary>Observes a committed Flow protocol barrier.</summary>
    /// <param name="barrier">The stable name of the committed boundary.</param>
    /// <param name="scopeId">The scope that owns the committed Flow transition.</param>
    /// <param name="instanceId">The Flow instance that crossed the boundary.</param>
    /// <param name="revision">The committed aggregate revision.</param>
    /// <param name="traceEvidence">Value-free in-process activity evidence available at the committed boundary.</param>
    /// <param name="cancellationToken">Cancellation for observer-only work after the commit.</param>
    /// <returns>A task that completes after the observer records the barrier.</returns>
    ValueTask ObserveAsync(
        string barrier,
        DurableScopeId scopeId,
        DurableFlowInstanceId instanceId,
        long revision,
        PostgreSqlFlowTelemetryEvidence? traceEvidence,
        CancellationToken cancellationToken);
}

/// <summary>Provides the production barrier observer that preserves ordering without recording a checkpoint.</summary>
internal sealed class NoOpPostgreSqlDurableFlowBarrierObserver : IPostgreSqlDurableFlowBarrierObserver
{
    internal static NoOpPostgreSqlDurableFlowBarrierObserver Instance { get; } = new();

    private NoOpPostgreSqlDurableFlowBarrierObserver()
    {
    }

    public ValueTask ObserveAsync(
        string barrier,
        DurableScopeId scopeId,
        DurableFlowInstanceId instanceId,
        long revision,
        PostgreSqlFlowTelemetryEvidence? traceEvidence,
        CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}

/// <summary>Classifies the payload-free dispatch row that initiated Flow processing.</summary>
internal enum PostgreSqlFlowDispatchKind
{
    /// <summary>Runs the next Flow evaluation after a ready or resumed Flow transition.</summary>
    Flow = 0,

    /// <summary>Resolves a due Flow timer before the next Flow evaluation.</summary>
    Timer = 1,
}

/// <summary>Describes one payload-free Flow or timer dispatch candidate discovered by the dispatcher role.</summary>
/// <param name="DispatchId">The unique dispatch row identity.</param>
/// <param name="ScopeId">The owning durable scope.</param>
/// <param name="Kind">Whether the candidate evaluates a Flow or resolves a timer.</param>
/// <param name="InstanceId">The target Flow instance.</param>
/// <param name="TimerId">The timer identity for <see cref="PostgreSqlFlowDispatchKind.Timer"/> candidates.</param>
/// <param name="DueAtUtc">The time at which the candidate becomes eligible for processing.</param>
/// <param name="ExpectedRevision">The Flow revision that must still match when the candidate is claimed.</param>
/// <param name="Priority">The stable scheduler priority used when candidates share a due time.</param>
internal sealed record PostgreSqlFlowDispatchCandidate(
    Guid DispatchId,
    DurableScopeId ScopeId,
    PostgreSqlFlowDispatchKind Kind,
    DurableFlowInstanceId InstanceId,
    Guid? TimerId,
    DateTimeOffset DueAtUtc,
    long ExpectedRevision,
    short Priority);

/// <summary>Value-free in-process Activity evidence passed only to deterministic crash-test barriers.</summary>
internal sealed record PostgreSqlFlowTelemetryEvidence(
    string Operation,
    string TraceId,
    string SpanId,
    string? CorrelationToken,
    IReadOnlyList<PostgreSqlFlowTelemetryLink> Links);

/// <summary>Describes one causal link carried by a crash-test Activity evidence record.</summary>
internal sealed record PostgreSqlFlowTelemetryLink(string TraceId, string SpanId);

/// <summary>Describes the durable outcome of attempting to process one Flow dispatch candidate.</summary>
internal enum PostgreSqlFlowProcessingOutcome
{
    /// <summary>The candidate committed its intended transition.</summary>
    Applied = 0,

    /// <summary>The dispatcher could not claim the candidate because another worker owns or completed it.</summary>
    NotClaimed = 1,

    /// <summary>The candidate no longer matched its persisted revision, epoch, or scope fence before processing began.</summary>
    Stale = 2,

    /// <summary>The Flow committed a safety suspension instead of continuing evaluation.</summary>
    Suspended = 3,

    /// <summary>The Flow committed a terminal state.</summary>
    Terminal = 4,

    /// <summary>A competing transaction won the transition after this worker had discovered the candidate.</summary>
    RaceLost = 5,
}

/// <summary>Reports the observable result of processing a single Flow dispatch candidate.</summary>
/// <param name="Outcome">The applied, terminal, fenced, or competing-transition outcome.</param>
/// <param name="ScopeId">The scope that owns the candidate.</param>
/// <param name="InstanceId">The Flow instance considered for processing.</param>
/// <param name="State">The resulting Flow state when a durable transition was observed.</param>
/// <param name="Revision">The resulting or observed Flow aggregate revision.</param>
/// <param name="ChildWorkId">The child Work accepted by an activity transition, when one was created.</param>
/// <param name="ProblemCode">The durable safety code when the Flow was suspended or rejected.</param>
internal sealed record PostgreSqlFlowProcessingResult(
    PostgreSqlFlowProcessingOutcome Outcome,
    DurableScopeId ScopeId,
    DurableFlowInstanceId InstanceId,
    DurableFlowState? State,
    long Revision,
    DurableWorkId? ChildWorkId = null,
    string? ProblemCode = null);

/// <summary>
/// Discovers payload-free Flow/timer candidates and commits one replay-safe Flow transition at a time.
/// </summary>
/// <remarks>
/// Discovery uses only the dispatcher-role data source. Claim and mutation use only the scoped runtime-role source.
/// Evaluation runs after the claim transaction releases every database resource.
/// </remarks>
internal sealed class PostgreSqlDurableFlowProcessor
{
    private readonly NpgsqlDataSource _dispatcherDataSource;
    private readonly IDurableFlowRegistry _flowRegistry;
    private readonly IDurableWorkRegistry _workRegistry;
    private readonly IDurablePayloadCodecRegistry _payloadCodecs;
    private readonly PostgreSqlDurableFlowStore _store;
    private readonly PostgreSqlDurableFlowProcessorSettings _settings;
    private readonly IPostgreSqlDurableFlowBarrierObserver _barriers;

    internal PostgreSqlDurableFlowProcessor(
        NpgsqlDataSource dispatcherDataSource,
        NpgsqlDataSource scopedRuntimeDataSource,
        IDurableFlowRegistry flowRegistry,
        IDurableWorkRegistry workRegistry,
        IDurablePayloadCodecRegistry payloadCodecs,
        PostgreSqlDurableWorkOptions options,
        PostgreSqlDurableFlowProcessorSettings? settings = null,
        IPostgreSqlDurableFlowBarrierObserver? barriers = null)
    {
        _dispatcherDataSource = dispatcherDataSource ?? throw new ArgumentNullException(nameof(dispatcherDataSource));
        ArgumentNullException.ThrowIfNull(scopedRuntimeDataSource);
        _flowRegistry = flowRegistry ?? throw new ArgumentNullException(nameof(flowRegistry));
        _workRegistry = workRegistry ?? throw new ArgumentNullException(nameof(workRegistry));
        _payloadCodecs = payloadCodecs ?? throw new ArgumentNullException(nameof(payloadCodecs));
        ArgumentNullException.ThrowIfNull(options);
        _settings = settings ?? PostgreSqlDurableFlowProcessorSettings.Default;
        _barriers = barriers ?? NoOpPostgreSqlDurableFlowBarrierObserver.Instance;
        _store = new PostgreSqlDurableFlowStore(scopedRuntimeDataSource, options);
    }

    internal async ValueTask<IReadOnlyList<PostgreSqlFlowDispatchCandidate>> DiscoverAsync(
        int maximumCandidates = 100,
        CancellationToken cancellationToken = default)
    {
        if (maximumCandidates is < 1 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCandidates));
        }

        const string sql = """
            SELECT dispatch_id, scope_id, kind, flow_instance_id, timer_id, due_at, expected_revision, priority
            FROM appsurface_durable.flow_dispatch
            WHERE state IN ('available', 'leased') AND due_at <= clock_timestamp()
            ORDER BY due_at, priority DESC, dispatch_id
            LIMIT @maximum_candidates;
            """;
        await using var connection = await _dispatcherDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("maximum_candidates", maximumCandidates);
        var candidates = new List<PostgreSqlFlowDispatchCandidate>(maximumCandidates);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            candidates.Add(new PostgreSqlFlowDispatchCandidate(
                reader.GetGuid(0),
                new DurableScopeId(reader.GetString(1)),
                reader.GetString(2) == "flow" ? PostgreSqlFlowDispatchKind.Flow : PostgreSqlFlowDispatchKind.Timer,
                new DurableFlowInstanceId(reader.GetString(3)),
                reader.IsDBNull(4) ? null : reader.GetGuid(4),
                reader.GetFieldValue<DateTimeOffset>(5),
                reader.GetInt64(6),
                reader.GetInt16(7)));
        }

        return candidates;
    }

    internal async ValueTask<PostgreSqlFlowProcessingResult> TryProcessAsync(
        PostgreSqlFlowDispatchCandidate candidate,
        string workerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (string.IsNullOrWhiteSpace(workerId) || workerId.Length > 200)
        {
            throw new ArgumentException("The Flow worker identity must contain 1 to 200 characters.", nameof(workerId));
        }
        if (candidate.Kind == PostgreSqlFlowDispatchKind.Timer)
        {
            var timerTrace = await _store.ReadTimerTraceContextAsync(candidate, cancellationToken).ConfigureAwait(false);
            DurableTraceDiagnostics.Report(timerTrace.DiagnosticCode);
            using var timerActivityScope = DurableTraceActivity.StartRoot(
                "appsurface.durable.flow.timer",
                ActivityKind.Consumer,
                timerTrace.Context);
            var timerActivity = timerActivityScope.Activity;
            var timerExecutionTrace = DurableTraceContext.CaptureExecution(timerActivity, timerTrace);
            if (timerActivity is not null)
            {
                DurableTraceDiagnostics.Report(timerExecutionTrace.DiagnosticCode);
            }

            var timerResult = await _store.TryResolveTimerAsync(
                candidate,
                timerExecutionTrace.Context,
                cancellationToken).ConfigureAwait(false);
            if (timerResult.Outcome == PostgreSqlFlowProcessingOutcome.Applied)
            {
                await _barriers.ObserveAsync(
                    "flow.timer-resolution.committed",
                    timerResult.ScopeId,
                    timerResult.InstanceId,
                    timerResult.Revision,
                    CreateTelemetryEvidence(
                        timerActivity,
                        "appsurface.durable.flow.timer",
                        timerExecutionTrace.Context),
                    cancellationToken).ConfigureAwait(false);
            }

            DurableTraceTelemetry.Apply(
                timerActivity,
                "timer",
                "timer",
                timerResult.State?.ToString().ToLowerInvariant() ?? "unknown",
                timerResult.Outcome.ToString().ToLowerInvariant(),
                timerExecutionTrace.Context?.CorrelationToken ?? Guid.Empty,
                timerExecutionTrace.Status);
            return timerResult;
        }

        var claim = await _store.TryClaimFlowAsync(
            candidate,
            workerId,
            _settings.EvaluationLease,
            cancellationToken).ConfigureAwait(false);
        if (claim is null)
        {
            return new PostgreSqlFlowProcessingResult(
                PostgreSqlFlowProcessingOutcome.NotClaimed,
                candidate.ScopeId,
                candidate.InstanceId,
                null,
                candidate.ExpectedRevision);
        }

        await _barriers.ObserveAsync(
            "flow.claim.committed",
            claim.ScopeId,
            claim.InstanceId,
            claim.Revision,
            traceEvidence: null,
            cancellationToken).ConfigureAwait(false);

        DurableTraceDiagnostics.Report(claim.TraceContext.DiagnosticCode);
        using var activityScope = DurableTraceActivity.StartRoot(
            "appsurface.durable.flow.execute",
            ActivityKind.Consumer,
            claim.TraceContext.Context);
        var activity = activityScope.Activity;
        var executionTrace = DurableTraceContext.CaptureExecution(activity, claim.TraceContext);
        if (activity is not null)
        {
            DurableTraceDiagnostics.Report(executionTrace.DiagnosticCode);
        }

        DurableFlowEvaluationResult decision;
        try
        {
            var registration = _flowRegistry.GetRequired(claim.FlowId, claim.FlowVersion);
            if (!string.Equals(registration.AuthoringModel, claim.AuthoringModel, StringComparison.Ordinal)
                || !string.Equals(registration.ImplementationVersion, claim.ManifestId, StringComparison.Ordinal)
                || !string.Equals(registration.DefinitionFingerprint, claim.DefinitionFingerprint, StringComparison.Ordinal))
            {
                var suspended = await _store.SuspendClaimAsync(
                    claim,
                    "flow.manifest_incompatible",
                    cancellationToken).ConfigureAwait(false);
                DurableTraceTelemetry.Apply(
                    activity,
                    "flow",
                    "claim",
                    "suspended",
                    "manifest_incompatible",
                    executionTrace.Context?.CorrelationToken ?? Guid.Empty,
                    executionTrace.Status);
                return suspended;
            }

            decision = await registration.EvaluateAsync(
                new DurableFlowEvaluationInput(
                    claim.CurrentNodeId,
                    claim.Context,
                    claim.ResumeEventName,
                    claim.ResumeEventPayload,
                    claim.ResumeEventIsTimeout,
                    claim.ActivityCallsiteId,
                    claim.ActivityResult),
                _payloadCodecs,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException
            and not OutOfMemoryException
            and not StackOverflowException)
        {
            var suspended = await _store.SuspendClaimAsync(
                claim,
                "flow.evaluation_failed",
                cancellationToken).ConfigureAwait(false);
            DurableTraceTelemetry.Apply(
                activity,
                "flow",
                "claim",
                "suspended",
                "evaluation_failed",
                executionTrace.Context?.CorrelationToken ?? Guid.Empty,
                executionTrace.Status);
            return suspended;
        }

        await _barriers.ObserveAsync(
            "flow.evaluation.completed",
            claim.ScopeId,
            claim.InstanceId,
            claim.Revision,
            CreateTelemetryEvidence(activity, "appsurface.durable.flow.execute", executionTrace.Context),
            cancellationToken).ConfigureAwait(false);
        PostgreSqlFlowProcessingResult result;
        try
        {
            result = await _store.CommitDecisionAsync(
                claim,
                decision,
                _workRegistry,
                executionTrace.Context,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or ArgumentException)
        {
            var suspended = await _store.SuspendClaimAsync(
                claim,
                "flow.evaluation_invalid",
                cancellationToken).ConfigureAwait(false);
            DurableTraceTelemetry.Apply(
                activity,
                "flow",
                "claim",
                "suspended",
                "evaluation_invalid",
                executionTrace.Context?.CorrelationToken ?? Guid.Empty,
                executionTrace.Status);
            return suspended;
        }

        await _barriers.ObserveAsync(
            "flow.decision.committed",
            result.ScopeId,
            result.InstanceId,
            result.Revision,
            CreateTelemetryEvidence(activity, "appsurface.durable.flow.execute", executionTrace.Context),
            cancellationToken).ConfigureAwait(false);
        DurableTraceTelemetry.Apply(
            activity,
            "flow",
            "claim",
            result.State?.ToString().ToLowerInvariant() ?? "unknown",
            result.Outcome.ToString().ToLowerInvariant(),
            executionTrace.Context?.CorrelationToken ?? Guid.Empty,
            executionTrace.Status);
        return result;
    }

    private static PostgreSqlFlowTelemetryEvidence? CreateTelemetryEvidence(
        Activity? activity,
        string operation,
        DurableTraceContext? fallback)
    {
        if (activity is not null)
        {
            return new PostgreSqlFlowTelemetryEvidence(
                activity.OperationName,
                activity.TraceId.ToHexString(),
                activity.SpanId.ToHexString(),
                fallback?.CorrelationToken.ToString("D"),
                activity.Links
                    .Select(link => new PostgreSqlFlowTelemetryLink(
                        link.Context.TraceId.ToHexString(),
                        link.Context.SpanId.ToHexString()))
                    .ToArray());
        }

        return fallback is null
            ? null
            : new PostgreSqlFlowTelemetryEvidence(
                operation,
                fallback.TraceId,
                fallback.SpanId,
                fallback.CorrelationToken.ToString("D"),
                [new PostgreSqlFlowTelemetryLink(fallback.TraceId, fallback.SpanId)]);
    }
}

internal sealed partial class PostgreSqlDurableFlowStore
{
    internal ValueTask<PostgreSqlFlowProcessingResult> CommitDecisionAsync(
        PostgreSqlFlowClaim claim,
        DurableFlowEvaluationResult decision,
        IDurableWorkRegistry workRegistry,
        CancellationToken cancellationToken) =>
        CommitDecisionAsync(claim, decision, workRegistry, executionTraceContext: null, cancellationToken);

    internal async ValueTask<PostgreSqlFlowClaim?> TryClaimFlowAsync(
        PostgreSqlFlowDispatchCandidate candidate,
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var generation = await ValidateStoreSetScopeAndLockActiveScopeAsync(
                connection, transaction, candidate.ScopeId, createIfMissing: false, cancellationToken).ConfigureAwait(false);
            if (generation is null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            const string lockSql = """
                SELECT flow.flow_id, flow.flow_version, flow.manifest_id, flow.authoring_model,
                       flow.definition_fingerprint_sha256, flow.current_node_id, flow.state,
                       flow.context_contract_id, flow.context_schema_version, flow.context_payload,
                       flow.context_classification, flow.context_retention,
                       flow.resume_event_name, flow.resume_event_is_timeout,
                       flow.resume_event_contract_id, flow.resume_event_schema_version, flow.resume_event_payload,
                       flow.resume_event_classification, flow.resume_event_retention,
                       flow.activity_callsite_id, flow.activity_result_contract_id,
                       flow.activity_result_schema_version, flow.activity_result_payload,
                       flow.activity_result_classification, flow.activity_result_retention,
                       flow.revision, flow.scope_generation, flow.runtime_epoch, flow.lease_generation,
                       (flow.lease_expires_at IS NOT NULL AND flow.lease_expires_at <= clock_timestamp()) AS lease_expired,
                       dispatch.state, dispatch.expected_revision,
                       flow.context_sha256, flow.resume_event_sha256, flow.activity_result_sha256,
                       trace.traceparent, trace.tracestate
                FROM appsurface_durable.flow_instance AS flow
                JOIN appsurface_durable.flow_dispatch AS dispatch
                 ON dispatch.scope_id = flow.scope_id
                 AND dispatch.flow_instance_id = flow.flow_instance_id
                 AND dispatch.kind = 'flow'
                LEFT JOIN appsurface_durable.flow_trace_context AS trace
                  ON trace.scope_id = flow.scope_id AND trace.trace_context_id = flow.trace_context_id
                WHERE flow.scope_id = @scope_id AND flow.flow_instance_id = @flow_instance_id
                  AND dispatch.dispatch_id = @dispatch_id
                FOR UPDATE OF flow, dispatch;
                """;
            await using var command = new NpgsqlCommand(lockSql, connection, transaction);
            command.Parameters.AddWithValue("scope_id", candidate.ScopeId.Value);
            command.Parameters.AddWithValue("flow_instance_id", candidate.InstanceId.Value);
            command.Parameters.AddWithValue("dispatch_id", candidate.DispatchId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                await reader.DisposeAsync().ConfigureAwait(false);
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            var state = reader.GetString(6);
            var revision = reader.GetInt64(25);
            var leaseExpired = reader.GetBoolean(29);
            var dispatchState = reader.GetString(30);
            var dispatchRevision = reader.GetInt64(31);
            if (dispatchRevision != candidate.ExpectedRevision
                || revision != candidate.ExpectedRevision
                || dispatchState is not ("available" or "leased")
                || (state != "ready" && !(state == "evaluating" && leaseExpired)))
            {
                await reader.DisposeAsync().ConfigureAwait(false);
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            var context = ReadPayload(reader, 7, 8, 9, 10, 11);
            var resumePayload = ReadNullablePayload(reader, 14, 15, 16, 17, 18);
            var activityPayload = ReadNullablePayload(reader, 20, 21, 22, 23, 24);
            ValidatePayloadHash(context, reader, 32, "Flow context");
            ValidateNullablePayloadHash(resumePayload, reader, 33, "Flow event");
            ValidateNullablePayloadHash(activityPayload, reader, 34, "Flow activity result");
            var traceContext = reader.IsDBNull(35)
                ? DurableTraceContextCapture.Absent
                : DurableTraceContext.Parse(
                    reader.GetString(35),
                    reader.IsDBNull(36) ? null : reader.GetString(36));
            var claim = new PostgreSqlFlowClaim(
                candidate.DispatchId,
                candidate.ScopeId,
                candidate.InstanceId,
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                context,
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.GetBoolean(13),
                resumePayload,
                reader.IsDBNull(19) ? null : reader.GetString(19),
                activityPayload,
                traceContext,
                checked(revision + 1),
                reader.GetInt64(28) + 1,
                generation.Value,
                _runtimeEpoch,
                workerId);
            await reader.DisposeAsync().ConfigureAwait(false);

            const string updateSql = """
                UPDATE appsurface_durable.flow_instance
                SET state = 'evaluating', revision = @revision, lease_generation = @lease_generation,
                    lease_owner = @lease_owner, lease_started_at = clock_timestamp(),
                    lease_expires_at = clock_timestamp() + @lease_duration,
                    updated_at = clock_timestamp()
                WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id
                  AND revision = @prior_revision;

                UPDATE appsurface_durable.flow_dispatch
                SET state = 'leased', due_at = clock_timestamp() + @lease_duration,
                    expected_revision = @revision, updated_at = clock_timestamp()
                WHERE dispatch_id = @dispatch_id;

                INSERT INTO appsurface_durable.flow_history
                    (scope_id, flow_instance_id, aggregate_revision, node_id, transition_kind)
                VALUES
                    (@scope_id, @flow_instance_id, @revision, @node_id, 'evaluation_claimed');
                """;
            await using var update = new NpgsqlCommand(updateSql, connection, transaction);
            update.Parameters.AddWithValue("revision", claim.Revision);
            update.Parameters.AddWithValue("lease_generation", claim.LeaseGeneration);
            update.Parameters.AddWithValue("lease_owner", workerId);
            update.Parameters.AddWithValue("lease_duration", leaseDuration);
            update.Parameters.AddWithValue("scope_id", candidate.ScopeId.Value);
            update.Parameters.AddWithValue("flow_instance_id", candidate.InstanceId.Value);
            update.Parameters.AddWithValue("prior_revision", revision);
            update.Parameters.AddWithValue("dispatch_id", candidate.DispatchId);
            update.Parameters.AddWithValue("node_id", claim.CurrentNodeId);
            var affected = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (affected != 3)
            {
                throw new InvalidOperationException($"Flow claim expected three writes but PostgreSQL reported {affected}.");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return claim;
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    internal async ValueTask<PostgreSqlFlowProcessingResult> SuspendClaimAsync(
        PostgreSqlFlowClaim claim,
        string problemCode,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _ = await ValidateStoreSetScopeAndLockActiveScopeAsync(
                connection, transaction, claim.ScopeId, createIfMissing: false, cancellationToken).ConfigureAwait(false);
            var revision = checked(claim.Revision + 1);
            const string sql = """
                UPDATE appsurface_durable.flow_instance
                -- An evaluation claim is a temporary leased state. Releasing this safety suspension must return
                -- to ready, the last dispatchable state, rather than a lease-free evaluating row.
                SET state = 'suspended', suspended_from_state = 'ready',
                    suspension_descriptor = jsonb_build_object('code', @problem_code, 'source', 'evaluation'),
                    revision = @revision, lease_owner = NULL, lease_started_at = NULL, lease_expires_at = NULL,
                    updated_at = clock_timestamp()
                WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id
                  AND state = 'evaluating' AND revision = @claim_revision
                  AND lease_generation = @lease_generation AND lease_owner = @lease_owner
                  AND runtime_epoch = @runtime_epoch AND scope_generation = @scope_generation;

                UPDATE appsurface_durable.flow_dispatch
                SET state = 'suspended', expected_revision = @revision, updated_at = clock_timestamp()
                WHERE dispatch_id = @dispatch_id AND expected_revision = @claim_revision;

                INSERT INTO appsurface_durable.flow_history
                    (scope_id, flow_instance_id, aggregate_revision, node_id, transition_kind, details)
                SELECT @scope_id, @flow_instance_id, @revision, @node_id, 'evaluation_suspended',
                       jsonb_build_object('problem_code', @problem_code)
                WHERE EXISTS
                (
                    SELECT 1 FROM appsurface_durable.flow_instance
                    WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id AND revision = @revision
                );
                """;
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            AddClaimParameters(command, claim);
            command.Parameters.AddWithValue("revision", revision);
            command.Parameters.AddWithValue("problem_code", problemCode);
            command.Parameters.AddWithValue("node_id", claim.CurrentNodeId);
            var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (affected == 0)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new PostgreSqlFlowProcessingResult(
                    PostgreSqlFlowProcessingOutcome.Stale,
                    claim.ScopeId,
                    claim.InstanceId,
                    null,
                    claim.Revision);
            }
            if (affected != 3)
            {
                throw new InvalidOperationException(
                    $"Flow suspension expected three writes but PostgreSQL reported {affected}.");
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new PostgreSqlFlowProcessingResult(
                PostgreSqlFlowProcessingOutcome.Suspended,
                claim.ScopeId,
                claim.InstanceId,
                DurableFlowState.Suspended,
                revision,
                ProblemCode: problemCode);
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    internal async ValueTask<PostgreSqlFlowProcessingResult> CommitDecisionAsync(
        PostgreSqlFlowClaim claim,
        DurableFlowEvaluationResult decision,
        IDurableWorkRegistry workRegistry,
        DurableTraceContext? executionTraceContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decision);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _ = await ValidateStoreSetScopeAndLockActiveScopeAsync(
                connection, transaction, claim.ScopeId, createIfMissing: false, cancellationToken).ConfigureAwait(false);
            DurableWorkId? childWorkId = null;
            string? activityIdentity = null;
            if (decision.Kind == FlowTransitionKind.Activity)
            {
                var activity = decision.Activity
                    ?? throw new InvalidOperationException("An activity transition did not contain an activity command.");
                var registration = workRegistry.GetRequired(activity.WorkName, activity.WorkVersion);
                if (registration.ProviderSafety != activity.ProviderSafety)
                {
                    throw new InvalidOperationException("Flow activity provider safety differs from its Work registration.");
                }

                _ = registration.WorkCodec.DecodeObject(activity.Work);
                activityIdentity = ComputeActivityIdentity(claim, decision);
                var request = new DurableWorkRequest(
                    claim.ScopeId,
                    new DurableCommandId("flow-work-command-v1-" + activityIdentity),
                    "flow-work-idempotency-v1-" + activityIdentity,
                    activity.WorkName,
                    activity.WorkVersion,
                    activity.Work,
                    activity.ProviderSafety,
                    DurableWorkRetryPolicy.Default);
                var acceptance = await PostgreSqlDurableWorkStore.AcceptFlowChildAsync(
                    transaction,
                    request,
                    _runtimeEpoch,
                    _expectedStoreId,
                    _sendWakeNotification,
                    "flow-activity-v1-" + activityIdentity,
                    cancellationToken).ConfigureAwait(false);
                if (!acceptance.IsSuccess)
                {
                    throw new InvalidOperationException(
                        acceptance.Problem?.Problem ?? "Flow child Work acceptance failed.");
                }

                childWorkId = acceptance.Value!.WorkId;
            }

            var current = await LockCurrentAsync(
                connection, transaction, claim.ScopeId, claim.InstanceId, cancellationToken).ConfigureAwait(false);
            if (current is null
                || current.State != "evaluating"
                || current.Revision != claim.Revision
                || current.LeaseGeneration != claim.LeaseGeneration)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new PostgreSqlFlowProcessingResult(
                    PostgreSqlFlowProcessingOutcome.Stale,
                    claim.ScopeId,
                    claim.InstanceId,
                    current?.PublicState,
                    current?.Revision ?? claim.Revision);
            }

            return await CommitDecisionCoreAsync(
                connection,
                transaction,
                claim,
                decision,
                childWorkId,
                workRegistry,
                activityIdentity,
                executionTraceContext,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask<PostgreSqlFlowProcessingResult> CommitDecisionCoreAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostgreSqlFlowClaim claim,
        DurableFlowEvaluationResult decision,
        DurableWorkId? childWorkId,
        IDurableWorkRegistry workRegistry,
        string? activityIdentity,
        DurableTraceContext? executionTraceContext,
        CancellationToken cancellationToken)
    {
        var revision = checked(claim.Revision + 1);
        var nextState = decision.Kind switch
        {
            FlowTransitionKind.Next => "ready",
            FlowTransitionKind.Wait => "waiting_event",
            FlowTransitionKind.TimedOut or FlowTransitionKind.Complete => "completed",
            FlowTransitionKind.Fault => "faulted",
            FlowTransitionKind.Activity => "waiting_activity",
            _ => throw new ArgumentOutOfRangeException(nameof(decision)),
        };
        var terminal = nextState is "completed" or "faulted";
        var terminalCode = nextState switch
        {
            "completed" => decision.Kind == FlowTransitionKind.TimedOut ? "timed_out" : "completed",
            "faulted" => decision.Fault?.Code ?? "flow.faulted",
            _ => null,
        };
        var nextNode = decision.NextNodeId ?? decision.NodeId;
        var context = decision.Context ?? claim.Context;
        var waitId = decision.Kind is FlowTransitionKind.Wait or FlowTransitionKind.Activity ? Guid.NewGuid() : (Guid?)null;
        var timerId = decision.Timeout is null ? (Guid?)null : Guid.NewGuid();

        const string updateSql = """
            UPDATE appsurface_durable.flow_instance
            SET state = @state, current_node_id = @current_node_id, revision = @revision,
                context_contract_id = @context_contract_id, context_schema_version = @context_schema_version,
                context_codec_id = @context_codec_id, context_payload = @context_payload,
                context_sha256 = @context_sha256, context_classification = @context_classification,
                context_retention = @context_retention,
                resume_event_name = NULL, resume_event_is_timeout = false,
                resume_event_contract_id = NULL, resume_event_schema_version = NULL,
                resume_event_codec_id = NULL, resume_event_payload = NULL, resume_event_sha256 = NULL,
                resume_event_classification = NULL, resume_event_retention = NULL,
                activity_callsite_id = NULL, activity_result_contract_id = NULL,
                activity_result_schema_version = NULL, activity_result_codec_id = NULL,
                activity_result_payload = NULL, activity_result_sha256 = NULL,
                activity_result_classification = NULL, activity_result_retention = NULL,
                terminal_at = CASE WHEN @terminal THEN clock_timestamp() ELSE NULL END,
                terminal_code = @terminal_code,
                lease_owner = NULL, lease_started_at = NULL, lease_expires_at = NULL,
                updated_at = clock_timestamp()
            WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id
              AND state = 'evaluating' AND revision = @claim_revision
              AND lease_generation = @lease_generation AND lease_owner = @lease_owner
              AND runtime_epoch = @runtime_epoch AND scope_generation = @scope_generation;

            UPDATE appsurface_durable.flow_dispatch
            SET state = CASE WHEN @state = 'ready' THEN 'available' ELSE 'terminal' END,
                due_at = clock_timestamp(), expected_revision = @revision, updated_at = clock_timestamp()
            WHERE dispatch_id = @dispatch_id AND expected_revision = @claim_revision;
            """;
        await using (var update = new NpgsqlCommand(updateSql, connection, transaction))
        {
            AddClaimParameters(update, claim);
            update.Parameters.AddWithValue("state", nextState);
            update.Parameters.AddWithValue("current_node_id", nextNode);
            update.Parameters.AddWithValue("revision", revision);
            AddPayloadParameters(update, "context", context);
            update.Parameters.AddWithValue("terminal", terminal);
            update.Parameters.Add(new NpgsqlParameter("terminal_code", NpgsqlDbType.Text)
            {
                Value = terminalCode ?? (object)DBNull.Value,
            });
            var affected = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (affected != 2)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new PostgreSqlFlowProcessingResult(
                    PostgreSqlFlowProcessingOutcome.Stale,
                    claim.ScopeId,
                    claim.InstanceId,
                    null,
                    claim.Revision);
            }
        }

        if (decision.Kind == FlowTransitionKind.Wait)
        {
            await InsertEventWaitAsync(
                connection, transaction, claim, decision, revision, waitId!.Value, timerId, cancellationToken)
                .ConfigureAwait(false);
        }
        else if (decision.Kind == FlowTransitionKind.Activity)
        {
            await InsertActivityWaitAsync(
                connection,
                transaction,
                claim,
                decision,
                revision,
                waitId!.Value,
                childWorkId!.Value,
                workRegistry,
                cancellationToken).ConfigureAwait(false);
        }

        await AppendHistoryAsync(
            connection,
            transaction,
            new CurrentFlowRow(
                claim.ScopeId,
                claim.InstanceId,
                claim.FlowId,
                claim.FlowVersion,
                claim.ManifestId,
                claim.AuthoringModel,
                claim.DefinitionFingerprint,
                nextNode,
                nextState,
                null,
                revision,
                claim.RuntimeEpoch,
                claim.ScopeGeneration,
                claim.LeaseGeneration),
            "decision_" + decision.Kind.ToString().ToLowerInvariant(),
            commandId: null,
            cancellationToken).ConfigureAwait(false);
        var trace = await InsertTraceContextAsync(
            connection,
            transaction,
            claim.ScopeId,
            claim.InstanceId,
            executionTraceContext,
            decision.Kind == FlowTransitionKind.Activity ? "activity_scheduled" : "evaluation_committed",
            cancellationToken).ConfigureAwait(false);
        await AttachTraceContextAsync(
            connection,
            transaction,
            claim.ScopeId,
            claim.InstanceId,
            trace,
            commandId: null,
            revision,
            waitId,
            timerId,
            childWorkId,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PostgreSqlFlowProcessingResult(
            terminal ? PostgreSqlFlowProcessingOutcome.Terminal : PostgreSqlFlowProcessingOutcome.Applied,
            claim.ScopeId,
            claim.InstanceId,
            ParseState(nextState),
            revision,
            childWorkId);
    }

    private static async ValueTask InsertEventWaitAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostgreSqlFlowClaim claim,
        DurableFlowEvaluationResult decision,
        long revision,
        Guid waitId,
        Guid? timerId,
        CancellationToken cancellationToken)
    {
        var contract = decision.EventContract
            ?? throw new InvalidOperationException("A Flow event wait did not contain an event contract.");
        const string sql = """
            INSERT INTO appsurface_durable.flow_wait
            (
                wait_id, scope_id, flow_instance_id, kind, state, registered_revision,
                event_name, event_payload_required, event_contract_id, event_schema_version,
                event_classification, event_retention
            )
            VALUES
            (
                @wait_id, @scope_id, @flow_instance_id, 'event', 'active', @revision,
                @event_name, @payload_required, @event_contract_id, @event_schema_version,
                @event_classification, @event_retention
            );
            """;
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            command.Parameters.AddWithValue("wait_id", waitId);
            command.Parameters.AddWithValue("scope_id", claim.ScopeId.Value);
            command.Parameters.AddWithValue("flow_instance_id", claim.InstanceId.Value);
            command.Parameters.AddWithValue("revision", revision);
            command.Parameters.AddWithValue("event_name", decision.EventName!);
            command.Parameters.AddWithValue("payload_required", contract.PayloadRequired);
            command.Parameters.Add(new NpgsqlParameter("event_contract_id", NpgsqlDbType.Text)
            {
                Value = contract.ContractName ?? (object)DBNull.Value,
            });
            command.Parameters.Add(new NpgsqlParameter("event_schema_version", NpgsqlDbType.Text)
            {
                Value = contract.ContractVersion ?? (object)DBNull.Value,
            });
            command.Parameters.Add(new NpgsqlParameter("event_classification", NpgsqlDbType.Text)
            {
                Value = contract.Classification is null
                    ? DBNull.Value
                    : FormatClassification(contract.Classification.Value),
            });
            command.Parameters.Add(new NpgsqlParameter("event_retention", NpgsqlDbType.Text)
            {
                Value = contract.RetentionPolicyId ?? (object)DBNull.Value,
            });
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("Flow event wait was not inserted exactly once.");
            }
        }

        if (timerId is null)
        {
            return;
        }

        const string timerSql = """
            INSERT INTO appsurface_durable.flow_timer
                (timer_id, scope_id, flow_instance_id, wait_id, registered_revision, due_at, state)
            VALUES
                (@timer_id, @scope_id, @flow_instance_id, @wait_id, @revision,
                 clock_timestamp() + @duration, 'scheduled');

            INSERT INTO appsurface_durable.flow_dispatch
                (dispatch_id, scope_id, kind, flow_instance_id, timer_id, due_at, state, expected_revision, priority)
            VALUES
                (@dispatch_id, @scope_id, 'timer', @flow_instance_id, @timer_id,
                 clock_timestamp() + @duration, 'available', @revision, 0);
            """;
        await using var timer = new NpgsqlCommand(timerSql, connection, transaction);
        timer.Parameters.AddWithValue("timer_id", timerId.Value);
        timer.Parameters.AddWithValue("dispatch_id", Guid.NewGuid());
        timer.Parameters.AddWithValue("scope_id", claim.ScopeId.Value);
        timer.Parameters.AddWithValue("flow_instance_id", claim.InstanceId.Value);
        timer.Parameters.AddWithValue("wait_id", waitId);
        timer.Parameters.AddWithValue("revision", revision);
        timer.Parameters.AddWithValue("duration", decision.Timeout!.Duration);
        if (await timer.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 2)
        {
            throw new InvalidOperationException("Flow timer and dispatch were not inserted exactly once.");
        }
    }

    private async ValueTask InsertActivityWaitAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostgreSqlFlowClaim claim,
        DurableFlowEvaluationResult decision,
        long revision,
        Guid waitId,
        DurableWorkId childWorkId,
        IDurableWorkRegistry workRegistry,
        CancellationToken cancellationToken)
    {
        var activity = decision.Activity
            ?? throw new InvalidOperationException("An activity transition did not contain an activity command.");
        var resultExpectation = activity.ResultExpectation
            ?? DurableActivityResultExpectation.From(
                workRegistry.GetRequired(activity.WorkName, activity.WorkVersion).ResultCodec);
        const string sql = """
            INSERT INTO appsurface_durable.flow_wait
            (
                wait_id, scope_id, flow_instance_id, kind, state, registered_revision,
                callsite_id, child_work_id, result_contract_version,
                result_contract_id, result_schema_version, result_codec_id,
                result_classification, result_retention
            )
            VALUES
            (
                @wait_id, @scope_id, @flow_instance_id, 'activity', 'active', @revision,
                @callsite_id, @child_work_id, @result_contract_version,
                @result_contract_id, @result_schema_version, @result_codec_id,
                @result_classification, @result_retention
            );
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("wait_id", waitId);
        command.Parameters.AddWithValue("scope_id", claim.ScopeId.Value);
        command.Parameters.AddWithValue("flow_instance_id", claim.InstanceId.Value);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue("callsite_id", activity.CallsiteId);
        command.Parameters.AddWithValue("child_work_id", childWorkId.Value);
        command.Parameters.AddWithValue(
            "result_contract_version",
            activity.ResultContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("result_contract_id", resultExpectation.ContractId);
        command.Parameters.AddWithValue("result_schema_version", resultExpectation.SchemaVersion);
        command.Parameters.AddWithValue("result_codec_id", resultExpectation.CodecId);
        command.Parameters.AddWithValue("result_classification", FormatClassification(resultExpectation.Classification));
        command.Parameters.AddWithValue("result_retention", resultExpectation.RetentionPolicyId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("Flow activity wait was not inserted exactly once.");
        }
    }

    internal async ValueTask<DurableTraceContextCapture> ReadTimerTraceContextAsync(
        PostgreSqlFlowDispatchCandidate candidate,
        CancellationToken cancellationToken)
    {
        if (candidate.TimerId is null)
        {
            return DurableTraceContextCapture.Absent;
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await ValidateStoreSetScopeAndLockActiveScopeAsync(
                    connection,
                    transaction,
                    candidate.ScopeId,
                    createIfMissing: false,
                    cancellationToken).ConfigureAwait(false) is null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return DurableTraceContextCapture.Absent;
            }

            const string sql = """
                SELECT trace.traceparent, trace.tracestate
                FROM appsurface_durable.flow_timer AS timer
                LEFT JOIN appsurface_durable.flow_trace_context AS trace
                  ON trace.scope_id = timer.scope_id AND trace.trace_context_id = timer.trace_context_id
                WHERE timer.scope_id = @scope_id AND timer.flow_instance_id = @flow_instance_id
                  AND timer.timer_id = @timer_id;
                """;
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("scope_id", candidate.ScopeId.Value);
            command.Parameters.AddWithValue("flow_instance_id", candidate.InstanceId.Value);
            command.Parameters.AddWithValue("timer_id", candidate.TimerId.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var capture = await reader.ReadAsync(cancellationToken).ConfigureAwait(false) && !reader.IsDBNull(0)
                ? DurableTraceContext.Parse(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1))
                : DurableTraceContextCapture.Absent;
            await reader.DisposeAsync().ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return capture;
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    internal ValueTask<PostgreSqlFlowProcessingResult> TryResolveTimerAsync(
        PostgreSqlFlowDispatchCandidate candidate,
        CancellationToken cancellationToken) =>
        TryResolveTimerAsync(candidate, executionTraceContext: null, cancellationToken);

    internal async ValueTask<PostgreSqlFlowProcessingResult> TryResolveTimerAsync(
        PostgreSqlFlowDispatchCandidate candidate,
        DurableTraceContext? executionTraceContext,
        CancellationToken cancellationToken)
    {
        if (candidate.TimerId is null)
        {
            throw new ArgumentException("A timer candidate must carry a timer identity.", nameof(candidate));
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var scopeGeneration = await ValidateStoreSetScopeAndLockActiveScopeAsync(
                connection, transaction, candidate.ScopeId, createIfMissing: false, cancellationToken).ConfigureAwait(false);
            if (scopeGeneration is null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new PostgreSqlFlowProcessingResult(
                    PostgreSqlFlowProcessingOutcome.RaceLost,
                    candidate.ScopeId,
                    candidate.InstanceId,
                    null,
                    candidate.ExpectedRevision);
            }
            var current = await LockCurrentAsync(
                connection, transaction, candidate.ScopeId, candidate.InstanceId, cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new PostgreSqlFlowProcessingResult(
                    PostgreSqlFlowProcessingOutcome.RaceLost,
                    candidate.ScopeId,
                    candidate.InstanceId,
                    null,
                    candidate.ExpectedRevision);
            }

            const string lockSql = """
                SELECT timer.wait_id, timer.state, timer.registered_revision,
                       timer.due_at <= clock_timestamp() AS is_due,
                       wait.state, wait.event_name
                FROM appsurface_durable.flow_timer AS timer
                JOIN appsurface_durable.flow_wait AS wait
                  ON wait.scope_id = timer.scope_id AND wait.flow_instance_id = timer.flow_instance_id
                 AND wait.wait_id = timer.wait_id
                WHERE timer.scope_id = @scope_id AND timer.flow_instance_id = @flow_instance_id
                  AND timer.timer_id = @timer_id
                FOR UPDATE OF timer, wait;
                """;
            await using var command = new NpgsqlCommand(lockSql, connection, transaction);
            command.Parameters.AddWithValue("scope_id", candidate.ScopeId.Value);
            command.Parameters.AddWithValue("flow_instance_id", candidate.InstanceId.Value);
            command.Parameters.AddWithValue("timer_id", candidate.TimerId.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                await reader.DisposeAsync().ConfigureAwait(false);
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new PostgreSqlFlowProcessingResult(
                    PostgreSqlFlowProcessingOutcome.RaceLost,
                    candidate.ScopeId,
                    candidate.InstanceId,
                    current.PublicState,
                    current.Revision);
            }

            var waitId = reader.GetGuid(0);
            var timerState = reader.GetString(1);
            var registeredRevision = reader.GetInt64(2);
            var isDue = reader.GetBoolean(3);
            var waitState = reader.GetString(4);
            var eventName = reader.GetString(5);
            await reader.DisposeAsync().ConfigureAwait(false);
            if (timerState != "scheduled"
                || waitState != "active"
                || current.Revision != registeredRevision
                || !isDue)
            {
                await SupersedeTimerAsync(connection, transaction, candidate, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new PostgreSqlFlowProcessingResult(
                    PostgreSqlFlowProcessingOutcome.RaceLost,
                    candidate.ScopeId,
                    candidate.InstanceId,
                    current.PublicState,
                    current.Revision);
            }

            var revision = checked(current.Revision + 1);
            const string resolveSql = """
                WITH fired_timer AS
                (
                UPDATE appsurface_durable.flow_timer
                SET state = 'fired', resolved_at = clock_timestamp(), updated_at = clock_timestamp()
                WHERE timer_id = @timer_id AND state = 'scheduled'
                RETURNING wait_id
                ),
                resolved_wait AS
                (
                UPDATE appsurface_durable.flow_wait
                SET state = 'timer_won', resolved_revision = @revision,
                    resolved_at = clock_timestamp(), updated_at = clock_timestamp()
                WHERE wait_id = @wait_id AND state = 'active'
                  AND EXISTS (SELECT 1 FROM fired_timer)
                RETURNING wait_id
                ),
                terminal_timer_dispatch AS
                (
                UPDATE appsurface_durable.flow_dispatch
                SET state = 'terminal', updated_at = clock_timestamp()
                WHERE dispatch_id = @dispatch_id
                  AND EXISTS (SELECT 1 FROM resolved_wait)
                RETURNING dispatch_id
                ),
                projected_flow AS
                (
                UPDATE appsurface_durable.flow_instance
                SET state = 'ready', revision = @revision, resume_event_name = @event_name,
                    resume_event_is_timeout = true, updated_at = clock_timestamp()
                WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id
                  AND revision = @prior_revision
                  AND EXISTS (SELECT 1 FROM terminal_timer_dispatch)
                RETURNING revision
                ),
                projected_flow_dispatch AS
                (
                UPDATE appsurface_durable.flow_dispatch
                SET state = 'available', due_at = clock_timestamp(), expected_revision = @revision,
                    updated_at = clock_timestamp()
                WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id AND kind = 'flow'
                  AND EXISTS (SELECT 1 FROM projected_flow)
                RETURNING dispatch_id
                )
                SELECT
                    (SELECT count(*) FROM fired_timer),
                    (SELECT count(*) FROM resolved_wait),
                    (SELECT count(*) FROM terminal_timer_dispatch),
                    (SELECT count(*) FROM projected_flow),
                    (SELECT count(*) FROM projected_flow_dispatch);
                """;
            await using var resolve = new NpgsqlCommand(resolveSql, connection, transaction);
            resolve.Parameters.AddWithValue("timer_id", candidate.TimerId.Value);
            resolve.Parameters.AddWithValue("wait_id", waitId);
            resolve.Parameters.AddWithValue("dispatch_id", candidate.DispatchId);
            resolve.Parameters.AddWithValue("revision", revision);
            resolve.Parameters.AddWithValue("event_name", eventName);
            resolve.Parameters.AddWithValue("scope_id", candidate.ScopeId.Value);
            resolve.Parameters.AddWithValue("flow_instance_id", candidate.InstanceId.Value);
            resolve.Parameters.AddWithValue("prior_revision", current.Revision);
            await using (var result = await resolve.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!await result.ReadAsync(cancellationToken).ConfigureAwait(false)
                    || result.GetInt64(0) != 1
                    || result.GetInt64(1) != 1
                    || result.GetInt64(2) != 1
                    || result.GetInt64(3) != 1
                    || result.GetInt64(4) != 1)
                {
                    throw new InvalidOperationException(
                        "Timer resolution did not project the timer, wait, dispatches, and parent Flow exactly once.");
                }
            }
            await AppendHistoryAsync(
                connection, transaction, current with { Revision = revision, State = "ready" },
                "timer_won", commandId: null, cancellationToken).ConfigureAwait(false);
            var trace = await InsertTraceContextAsync(
                connection,
                transaction,
                candidate.ScopeId,
                candidate.InstanceId,
                executionTraceContext,
                "timer_winner",
                cancellationToken).ConfigureAwait(false);
            await AttachTraceContextAsync(
                connection,
                transaction,
                candidate.ScopeId,
                candidate.InstanceId,
                trace,
                commandId: null,
                revision,
                waitId,
                candidate.TimerId,
                workId: null,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new PostgreSqlFlowProcessingResult(
                PostgreSqlFlowProcessingOutcome.Applied,
                candidate.ScopeId,
                candidate.InstanceId,
                DurableFlowState.Ready,
                revision);
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    private static async ValueTask SupersedeTimerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostgreSqlFlowDispatchCandidate candidate,
        CancellationToken cancellationToken)
    {
        const string sql = """
            WITH superseded_timer AS
            (
            UPDATE appsurface_durable.flow_timer
            SET state = 'superseded', resolved_at = clock_timestamp(), updated_at = clock_timestamp()
            WHERE timer_id = @timer_id AND state = 'scheduled'
            RETURNING timer_id
            ),
            terminal_dispatch AS
            (
            UPDATE appsurface_durable.flow_dispatch
            SET state = 'terminal', updated_at = clock_timestamp()
            WHERE dispatch_id = @dispatch_id
            RETURNING dispatch_id
            )
            SELECT
                (SELECT count(*) FROM superseded_timer),
                (SELECT count(*) FROM terminal_dispatch);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("timer_id", candidate.TimerId!.Value);
        command.Parameters.AddWithValue("dispatch_id", candidate.DispatchId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            || reader.GetInt64(0) > 1
            || reader.GetInt64(1) != 1)
        {
            throw new InvalidOperationException(
                "Timer race loss did not retain exactly one terminal dispatch observation.");
        }
    }

    private static DurableEncodedPayload ReadPayload(
        NpgsqlDataReader reader,
        int contractOrdinal,
        int versionOrdinal,
        int payloadOrdinal,
        int classificationOrdinal,
        int retentionOrdinal) =>
        new(
            reader.GetString(contractOrdinal),
            reader.GetString(versionOrdinal),
            ParseClassification(reader.GetString(classificationOrdinal)),
            reader.GetFieldValue<byte[]>(payloadOrdinal),
            reader.GetString(retentionOrdinal));

    private static DurableEncodedPayload? ReadNullablePayload(
        NpgsqlDataReader reader,
        int contractOrdinal,
        int versionOrdinal,
        int payloadOrdinal,
        int classificationOrdinal,
        int retentionOrdinal) =>
        reader.IsDBNull(payloadOrdinal)
            ? null
            : ReadPayload(
                reader,
                contractOrdinal,
                versionOrdinal,
                payloadOrdinal,
                classificationOrdinal,
                retentionOrdinal);

    private static void ValidateNullablePayloadHash(
        DurableEncodedPayload? payload,
        NpgsqlDataReader reader,
        int hashOrdinal,
        string payloadKind)
    {
        if (payload is null)
        {
            if (!reader.IsDBNull(hashOrdinal))
            {
                throw new InvalidDataException($"{payloadKind} has a hash without persisted payload bytes.");
            }

            return;
        }

        ValidatePayloadHash(payload, reader, hashOrdinal, payloadKind);
    }

    private static void ValidatePayloadHash(
        DurableEncodedPayload payload,
        NpgsqlDataReader reader,
        int hashOrdinal,
        string payloadKind)
    {
        if (reader.IsDBNull(hashOrdinal))
        {
            throw new InvalidDataException($"{payloadKind} is missing its persisted SHA-256.");
        }

        var persisted = reader.GetFieldValue<byte[]>(hashOrdinal);
        var computed = Convert.FromHexString(payload.Sha256);
        if (!CryptographicOperations.FixedTimeEquals(persisted, computed))
        {
            throw new InvalidDataException($"{payloadKind} failed persisted SHA-256 verification.");
        }
    }

    private static DurableDataClassification ParseClassification(string value) => value switch
    {
        "operational" => DurableDataClassification.Operational,
        "approved_application" => DurableDataClassification.ApprovedApplication,
        _ => throw new InvalidOperationException($"Unknown durable payload classification '{value}'."),
    };

    private static string ComputeActivityIdentity(PostgreSqlFlowClaim claim, DurableFlowEvaluationResult decision)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[sizeof(int)];
        foreach (var value in new[]
        {
            "appsurface.durable.flow.activity.v1",
            claim.ScopeId.Value,
            claim.InstanceId.Value,
            claim.FlowId,
            claim.FlowVersion,
            claim.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            decision.NodeId,
            decision.Activity!.CallsiteId,
            decision.Activity.WorkName,
            decision.Activity.WorkVersion,
            decision.Activity.Work.Sha256,
        })
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void AddClaimParameters(NpgsqlCommand command, PostgreSqlFlowClaim claim)
    {
        command.Parameters.AddWithValue("scope_id", claim.ScopeId.Value);
        command.Parameters.AddWithValue("flow_instance_id", claim.InstanceId.Value);
        command.Parameters.AddWithValue("claim_revision", claim.Revision);
        command.Parameters.AddWithValue("lease_generation", claim.LeaseGeneration);
        command.Parameters.AddWithValue("lease_owner", claim.LeaseOwner);
        command.Parameters.AddWithValue("runtime_epoch", claim.RuntimeEpoch);
        command.Parameters.AddWithValue("scope_generation", claim.ScopeGeneration);
        command.Parameters.AddWithValue("dispatch_id", claim.DispatchId);
    }
}

internal sealed record PostgreSqlFlowClaim(
    Guid DispatchId,
    DurableScopeId ScopeId,
    DurableFlowInstanceId InstanceId,
    string FlowId,
    string FlowVersion,
    string ManifestId,
    string AuthoringModel,
    string DefinitionFingerprint,
    string CurrentNodeId,
    DurableEncodedPayload Context,
    string? ResumeEventName,
    bool ResumeEventIsTimeout,
    DurableEncodedPayload? ResumeEventPayload,
    string? ActivityCallsiteId,
    DurableEncodedPayload? ActivityResult,
    DurableTraceContextCapture TraceContext,
    long Revision,
    long LeaseGeneration,
    long ScopeGeneration,
    Guid RuntimeEpoch,
    string LeaseOwner);
