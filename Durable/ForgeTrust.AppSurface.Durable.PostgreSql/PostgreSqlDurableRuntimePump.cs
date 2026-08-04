using System.Diagnostics;
using ForgeTrust.AppSurface.Durable.Provider;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>Runs one provider-backed PostgreSQL Pass through Work, Flow, and Schedule Turns.</summary>
/// <remarks>
/// One pass is deliberately sequential and process-local. PostgreSQL retains all authoritative discovery, claim,
/// lease, permit, completion, schedule, scope, and epoch decisions. The internal execution boundary is intentionally
/// uninstrumented so #685 can attach Activity and ActivityLink behavior without taking ownership of this lifecycle.
/// </remarks>
internal sealed class PostgreSqlDurableRuntimePump : IDurableRuntimePump
{
    private readonly PostgreSqlDurableRuntimeRegistration _registration;
    private readonly IDurableRuntimeSchemaManager _schemaManager;
    private readonly PostgreSqlDurableRuntimeHealth _runtimeHealth;
    private readonly PostgreSqlDurableWorkStore _workStore;
    private readonly PostgreSqlDurableFlowProcessor _flowProcessor;
    private readonly PostgreSqlDurableScheduleProcessor _scheduleProcessor;
    private readonly IDurableWorkRegistry _workRegistry;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDurableRuntimeExecutionBoundary _executionBoundary;
    private readonly DurableRuntimeAdmissionGate _admission;
    private readonly DurableRuntimeTurnScheduler _turnScheduler = new();
    private readonly SemaphoreSlim _passGate = new(1, 1);

    internal PostgreSqlDurableRuntimePump(
        PostgreSqlDurableRuntimeRegistration registration,
        IDurableRuntimeSchemaManager schemaManager,
        PostgreSqlDurableRuntimeHealth runtimeHealth,
        PostgreSqlDurableWorkStore workStore,
        PostgreSqlDurableFlowProcessor flowProcessor,
        PostgreSqlDurableScheduleProcessor scheduleProcessor,
        IDurableWorkRegistry workRegistry,
        IServiceScopeFactory scopeFactory,
        IDurableRuntimeExecutionBoundary executionBoundary,
        DurableRuntimeAdmissionGate admission)
    {
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _schemaManager = schemaManager ?? throw new ArgumentNullException(nameof(schemaManager));
        _runtimeHealth = runtimeHealth ?? throw new ArgumentNullException(nameof(runtimeHealth));
        _workStore = workStore ?? throw new ArgumentNullException(nameof(workStore));
        _flowProcessor = flowProcessor ?? throw new ArgumentNullException(nameof(flowProcessor));
        _scheduleProcessor = scheduleProcessor ?? throw new ArgumentNullException(nameof(scheduleProcessor));
        _workRegistry = workRegistry ?? throw new ArgumentNullException(nameof(workRegistry));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _executionBoundary = executionBoundary ?? throw new ArgumentNullException(nameof(executionBoundary));
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
    }

    public async ValueTask<DurableRuntimePumpResult> RunOnceAsync(
        DurableRuntimePumpRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await _passGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"{DurableProblemCodes.WorkerIdentityConflict}: This runtime instance already has an active Pass.");
        }

        try
        {
            // Check admission only after this instance owns its one pass slot. A caller that was waiting while
            // ApplicationStopping closed the gate cannot start a late Pass once the earlier one returns.
            if (!_admission.TryEnter())
            {
                return EmptyResult();
            }

            await _schemaManager.ValidateAsync(cancellationToken).ConfigureAwait(false);
            if (!await _runtimeHealth.TryBeginPassAsync(cancellationToken).ConfigureAwait(false))
            {
                return EmptyResult();
            }

            try
            {
                var result = await RunPassAsync(request, cancellationToken).ConfigureAwait(false);
                await _runtimeHealth.RecordSuccessfulSweepAsync(result, cancellationToken).ConfigureAwait(false);
                return result;
            }
            catch
            {
                try
                {
                    await _runtimeHealth.RecordFailedPassAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
                {
                    // The original pump failure remains authoritative; a later worker observes a stale heartbeat.
                }

                throw;
            }
        }
        finally
        {
            _passGate.Release();
        }
    }

    private async ValueTask<DurableRuntimePumpResult> RunPassAsync(
        DurableRuntimePumpRequest request,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var counts = new Counts();
        var withoutCommittedTurn = 0;
        while (counts.Turns < request.MaximumItems && stopwatch.Elapsed < request.TimeBudget)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var surface = _turnScheduler.Next(request.Surfaces);
            var outcome = await ProcessOneTurnAsync(surface, counts, cancellationToken).ConfigureAwait(false);
            if (outcome.CommittedTurn)
            {
                counts.Turns++;
                withoutCommittedTurn = 0;
            }
            else
            {
                // Empty or deferred surfaces rotate immediately and do not consume the item budget. One full round
                // without a committed Turn is quiescent enough to return to the host's poll/wake wait.
                withoutCommittedTurn++;
                if (withoutCommittedTurn >= CountSelectedSurfaces(request.Surfaces))
                {
                    break;
                }
            }
        }

        stopwatch.Stop();
        var budgetExhausted = counts.Turns == request.MaximumItems || stopwatch.Elapsed >= request.TimeBudget;
        return new DurableRuntimePumpResult(
            counts.Discovered,
            counts.Claimed,
            counts.Processed,
            counts.Deferred,
            counts.Failed,
            hasMore: budgetExhausted,
            nextDueAtUtc: null,
            stopwatch.Elapsed);
    }

    private async ValueTask<TurnOutcome> ProcessOneTurnAsync(
        DurableRuntimeSurface surface,
        Counts counts,
        CancellationToken cancellationToken) => surface switch
        {
            DurableRuntimeSurface.Work => await ProcessWorkTurnAsync(counts, cancellationToken).ConfigureAwait(false),
            DurableRuntimeSurface.Flow => await ProcessFlowTurnAsync(counts, cancellationToken).ConfigureAwait(false),
            DurableRuntimeSurface.Schedule => await ProcessScheduleTurnAsync(counts, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidDataException($"Unknown durable runtime surface '{surface}'."),
        };

    private async ValueTask<TurnOutcome> ProcessWorkTurnAsync(Counts counts, CancellationToken cancellationToken)
    {
        var candidate = (await _workStore.DiscoverAsync(1, cancellationToken).ConfigureAwait(false)).FirstOrDefault();
        if (candidate is null)
        {
            return TurnOutcome.Empty;
        }

        counts.Discovered++;
        DurableWorkState? transition = null;
        var claim = await _workStore.TryClaimAsync(
            candidate,
            _registration.Options.WorkerId,
            cancellationToken,
            (_, state, _, _) =>
            {
                transition = state;
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);
        if (claim is null)
        {
            if (transition is null)
            {
                counts.Deferred++;
                return TurnOutcome.Deferred;
            }

            if (transition == DurableWorkState.Suspended)
            {
                counts.Failed++;
            }
            else
            {
                counts.Processed++;
            }

            return TurnOutcome.Committed;
        }

        counts.Claimed++;
        return await ProcessClaimedWorkAsync(claim, counts, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<TurnOutcome> ProcessClaimedWorkAsync(
        PostgreSqlDurableWorkClaim claim,
        Counts counts,
        CancellationToken cancellationToken)
    {
        DurableWorkRegistration registration;
        try
        {
            registration = _workRegistry.GetRequired(claim.WorkName, claim.WorkVersion);
            if (registration.ProviderSafety != claim.ProviderSafety)
            {
                throw new InvalidOperationException("The persisted provider-safety snapshot does not match its registration.");
            }
        }
        catch (InvalidOperationException)
        {
            return await CompleteAsync(
                claim,
                new PostgreSqlWorkCompletion(
                    PostgreSqlWorkCompletionKind.ContractUnavailable,
                    DurableProblemCodes.WorkContractUnavailable,
                    "{}"),
                counts,
                cancellationToken).ConfigureAwait(false);
        }

        DurableEncodedPayload? result = null;
        Exception? failure = null;
        var currentClaim = claim;
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            DurablePreparedWorkInvocation invocation;
            try
            {
                invocation = DurableProviderWorkAdapter.Prepare(registration, scope.ServiceProvider, claim.ToProviderClaim());
            }
            catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
            {
                return await CompleteAsync(
                    claim,
                    new PostgreSqlWorkCompletion(
                        PostgreSqlWorkCompletionKind.ContractUnavailable,
                        DurableProblemCodes.WorkContractUnavailable,
                        "{}"),
                    counts,
                    cancellationToken).ConfigureAwait(false);
            }

            DurableWorkState? prePermitTransition = null;
            var permit = await _workStore.TryAcquireEffectPermitAsync(
                currentClaim,
                cancellationToken,
                (_, state, _, _) =>
                {
                    prePermitTransition = state;
                    return ValueTask.CompletedTask;
                }).ConfigureAwait(false);
            if (permit is null)
            {
                if (prePermitTransition is null)
                {
                    counts.Deferred++;
                    return TurnOutcome.Deferred;
                }

                if (prePermitTransition == DurableWorkState.Suspended)
                {
                    counts.Failed++;
                }
                else
                {
                    counts.Processed++;
                }

                return TurnOutcome.Committed;
            }

            currentClaim = permit.Claim;
            try
            {
                (result, currentClaim) = await InvokeWithLeaseAndHeartbeatAsync(invocation, currentClaim, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
            {
                failure = exception;
            }
        }

        var completion = failure is null
            ? new PostgreSqlWorkCompletion(PostgreSqlWorkCompletionKind.Succeeded, "completed", "{}", result)
            : new PostgreSqlWorkCompletion(
                PostgreSqlWorkCompletionKind.AmbiguousExternalOutcome,
                DurableProblemCodes.AmbiguousExternalOutcome,
                "{}");
        // Once a permit has committed, cancellation records the normal ambiguous-outcome path rather than inventing
        // terminal provider truth solely to meet a host deadline.
        return await CompleteAsync(currentClaim, completion, counts, CancellationToken.None).ConfigureAwait(false);
    }

    private async ValueTask<(DurableEncodedPayload Result, PostgreSqlDurableWorkClaim Claim)> InvokeWithLeaseAndHeartbeatAsync(
        DurablePreparedWorkInvocation invocation,
        PostgreSqlDurableWorkClaim claim,
        CancellationToken cancellationToken)
    {
        using var executorStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var running = _executionBoundary.InvokeAsync(invocation, executorStop.Token).AsTask();
        var current = claim;
        var heartbeatInterval = _registration.Options.HeartbeatStaleAfter / 3;
        var nextHeartbeat = DateTimeOffset.UtcNow + heartbeatInterval;
        var nextRenewal = DateTimeOffset.UtcNow + current.LeaseRenewalCadence;
        try
        {
            while (!running.IsCompleted)
            {
                var now = DateTimeOffset.UtcNow;
                if (current.LeaseExpiresAtUtc <= now)
                {
                    await executorStop.CancelAsync().ConfigureAwait(false);
                    break;
                }

                var next = Min(current.LeaseExpiresAtUtc, Min(nextHeartbeat, nextRenewal));
                var delay = next - now;
                if (delay > TimeSpan.Zero
                    && await Task.WhenAny(running, Task.Delay(delay, cancellationToken)).ConfigureAwait(false) == running)
                {
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();

                if (running.IsCompleted)
                {
                    break;
                }

                now = DateTimeOffset.UtcNow;
                if (now >= nextHeartbeat)
                {
                    await _runtimeHealth.RecordHeartbeatAsync(cancellationToken).ConfigureAwait(false);
                    nextHeartbeat = now + heartbeatInterval;
                }

                if (now >= nextRenewal)
                {
                    var renewed = await _workStore.RenewLeaseAsync(current, cancellationToken).ConfigureAwait(false);
                    if (renewed is null)
                    {
                        await executorStop.CancelAsync().ConfigureAwait(false);
                        break;
                    }

                    current = renewed;
                    nextRenewal = DateTimeOffset.UtcNow + current.LeaseRenewalCadence;
                    if (current.CancellationRequested)
                    {
                        await executorStop.CancelAsync().ConfigureAwait(false);
                    }
                }
            }

            return (await running.ConfigureAwait(false), current);
        }
        catch
        {
            await executorStop.CancelAsync().ConfigureAwait(false);
            try
            {
                _ = await running.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
            {
                // The permit/error path retains recovery authority; preserve the original runtime failure.
            }

            throw;
        }
    }

    private async ValueTask<TurnOutcome> CompleteAsync(
        PostgreSqlDurableWorkClaim claim,
        PostgreSqlWorkCompletion completion,
        Counts counts,
        CancellationToken cancellationToken)
    {
        var result = await _workStore.RecordCompletionAsync(claim, completion, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        switch (result.Outcome)
        {
            case PostgreSqlWorkObservationOutcome.Applied when result.State is
                DurableWorkState.Succeeded or DurableWorkState.SucceededAfterCancelRequested:
                counts.Processed++;
                return TurnOutcome.Committed;
            case PostgreSqlWorkObservationOutcome.Applied when result.State == DurableWorkState.Suspended:
                counts.Failed++;
                return TurnOutcome.Committed;
            case PostgreSqlWorkObservationOutcome.Applied:
                counts.Failed++;
                return TurnOutcome.Committed;
            case PostgreSqlWorkObservationOutcome.AlreadyTerminal:
            case PostgreSqlWorkObservationOutcome.StaleObservation:
                counts.Deferred++;
                return TurnOutcome.Deferred;
            default:
                throw new InvalidDataException($"Unknown PostgreSQL Work observation outcome '{result.Outcome}'.");
        }
    }

    private async ValueTask<TurnOutcome> ProcessFlowTurnAsync(Counts counts, CancellationToken cancellationToken)
    {
        var candidate = (await _flowProcessor.DiscoverAsync(1, cancellationToken).ConfigureAwait(false)).FirstOrDefault();
        if (candidate is null)
        {
            return TurnOutcome.Empty;
        }

        counts.Discovered++;
        var result = await _flowProcessor.TryProcessAsync(candidate, _registration.Options.WorkerId, cancellationToken)
            .ConfigureAwait(false);
        switch (result.Outcome)
        {
            case PostgreSqlFlowProcessingOutcome.Applied:
            case PostgreSqlFlowProcessingOutcome.Terminal:
                counts.Claimed++;
                counts.Processed++;
                return TurnOutcome.Committed;
            case PostgreSqlFlowProcessingOutcome.Suspended:
                counts.Claimed++;
                counts.Failed++;
                return TurnOutcome.Committed;
            case PostgreSqlFlowProcessingOutcome.NotClaimed:
            case PostgreSqlFlowProcessingOutcome.Stale:
            case PostgreSqlFlowProcessingOutcome.RaceLost:
                counts.Deferred++;
                return TurnOutcome.Deferred;
            default:
                throw new InvalidDataException($"Unknown PostgreSQL Flow outcome '{result.Outcome}'.");
        }
    }

    private async ValueTask<TurnOutcome> ProcessScheduleTurnAsync(Counts counts, CancellationToken cancellationToken)
    {
        var result = await _scheduleProcessor.ProcessDueAsync(
            new PostgreSqlDurableScheduleProcessRequest(_registration.Options.WorkerId, maximumSchedules: 1),
            cancellationToken).ConfigureAwait(false);
        if (result.ClaimedSchedules == 0)
        {
            return TurnOutcome.Empty;
        }

        counts.Discovered += result.ClaimedSchedules;
        counts.Claimed += result.ClaimedSchedules;
        counts.Processed += result.RecordedOccurrences + result.MaterializedWorkTargets;
        counts.Failed += result.SuspendedSchedules;
        return result.RecordedOccurrences == 0 && result.MaterializedWorkTargets == 0
            ? TurnOutcome.Deferred
            : TurnOutcome.Committed;
    }

    private static int CountSelectedSurfaces(DurableRuntimeSurface selected) =>
        ((selected & DurableRuntimeSurface.Work) != 0 ? 1 : 0)
        + ((selected & DurableRuntimeSurface.Flow) != 0 ? 1 : 0)
        + ((selected & DurableRuntimeSurface.Schedule) != 0 ? 1 : 0);

    private static DurableRuntimePumpResult EmptyResult() => new(0, 0, 0, 0, 0, false, null, TimeSpan.Zero);

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left < right ? left : right;

    private sealed class Counts
    {
        internal int Discovered { get; set; }

        internal int Claimed { get; set; }

        internal int Processed { get; set; }

        internal int Deferred { get; set; }

        internal int Failed { get; set; }

        internal int Turns { get; set; }
    }

    private readonly record struct TurnOutcome(bool CommittedTurn)
    {
        internal static TurnOutcome Empty => new(false);

        internal static TurnOutcome Deferred => new(false);

        internal static TurnOutcome Committed => new(true);
    }
}
