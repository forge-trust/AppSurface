using System.Diagnostics;
using System.Runtime.ExceptionServices;
using ForgeTrust.AppSurface.Durable.Provider;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>Runs one provider-backed PostgreSQL Pass through Work, Flow, and Schedule Turns.</summary>
/// <remarks>
/// One pass is deliberately sequential and process-local. PostgreSQL retains all authoritative discovery, claim,
/// lease, permit, completion, schedule, scope, and epoch decisions. The internal execution boundary is intentionally
/// uninstrumented so #685 can attach Activity and ActivityLink behavior without taking ownership of this lifecycle.
/// </remarks>
internal sealed partial class PostgreSqlDurableRuntimePump : IDurableRuntimePump, IDurableRuntimePumpAdmission
{
    private const string LocalOverlapMessage =
        "ASDUR405: This runtime instance already has an active Pass.";
    private readonly PostgreSqlDurableRuntimeRegistration _registration;
    private readonly IDurableRuntimeSchemaManager _schemaManager;
    private readonly PostgreSqlDurableRuntimeHealth _runtimeHealth;
    private readonly PostgreSqlDurableWorkStore _workStore;
    private readonly PostgreSqlDurableFlowProcessor _flowProcessor;
    private readonly PostgreSqlDurableScheduleProcessor _scheduleProcessor;
    private readonly IDurableWorkRegistry _workRegistry;
    private readonly PostgreSqlDurableWorkContractSelection _workContractSelection;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDurableRuntimeExecutionBoundary _executionBoundary;
    private readonly DurableRuntimeAdmissionGate _admission;
    private readonly ILogger<PostgreSqlDurableRuntimePump> _logger;
    private readonly PostgreSqlDurablePassExecutor _passExecutor;
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
        PostgreSqlDurableWorkContractSelection workContractSelection,
        IServiceScopeFactory scopeFactory,
        IDurableRuntimeExecutionBoundary executionBoundary,
        DurableRuntimeAdmissionGate admission)
        : this(
            registration,
            schemaManager,
            runtimeHealth,
            workStore,
            flowProcessor,
            scheduleProcessor,
            workRegistry,
            workContractSelection,
            scopeFactory,
            executionBoundary,
            admission,
            NullLogger<PostgreSqlDurableRuntimePump>.Instance,
            passExecutor: null)
    {
    }

    /// <summary>
    /// Initializes the one pump implementation and its internal sole execution-boundary seam.
    /// </summary>
    internal PostgreSqlDurableRuntimePump(
        PostgreSqlDurableRuntimeRegistration registration,
        IDurableRuntimeSchemaManager schemaManager,
        PostgreSqlDurableRuntimeHealth runtimeHealth,
        PostgreSqlDurableWorkStore workStore,
        PostgreSqlDurableFlowProcessor flowProcessor,
        PostgreSqlDurableScheduleProcessor scheduleProcessor,
        IDurableWorkRegistry workRegistry,
        PostgreSqlDurableWorkContractSelection workContractSelection,
        IServiceScopeFactory scopeFactory,
        IDurableRuntimeExecutionBoundary executionBoundary,
        DurableRuntimeAdmissionGate admission,
        ILogger<PostgreSqlDurableRuntimePump> logger,
        PostgreSqlDurablePassExecutor? passExecutor)
    {
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _schemaManager = schemaManager ?? throw new ArgumentNullException(nameof(schemaManager));
        _runtimeHealth = runtimeHealth ?? throw new ArgumentNullException(nameof(runtimeHealth));
        _workStore = workStore ?? throw new ArgumentNullException(nameof(workStore));
        _flowProcessor = flowProcessor ?? throw new ArgumentNullException(nameof(flowProcessor));
        _scheduleProcessor = scheduleProcessor ?? throw new ArgumentNullException(nameof(scheduleProcessor));
        _workRegistry = workRegistry ?? throw new ArgumentNullException(nameof(workRegistry));
        _workContractSelection = workContractSelection ?? throw new ArgumentNullException(nameof(workContractSelection));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _executionBoundary = executionBoundary ?? throw new ArgumentNullException(nameof(executionBoundary));
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _passExecutor = passExecutor ?? RunPassAsync;
    }

    public async ValueTask<DurableRuntimePumpResult> RunOnceAsync(
        DurableRuntimePumpRequest request,
        CancellationToken cancellationToken = default)
    {
        var outcome = await RunAttemptAsync(request, cancellationToken).ConfigureAwait(false);
        switch (outcome.Kind)
        {
            case PostgreSqlDurablePumpOutcomeKind.Completed:
                return outcome.Result!;
            case PostgreSqlDurablePumpOutcomeKind.Refused:
                if (outcome.Refusal is PostgreSqlDurablePumpRefusal.LocalPassOverlap
                    or PostgreSqlDurablePumpRefusal.LostWorkerGeneration)
                {
                    outcome.LegacyException!.Throw();
                }

                return EmptyResult();
            case PostgreSqlDurablePumpOutcomeKind.Unavailable:
            case PostgreSqlDurablePumpOutcomeKind.Incompatible:
                outcome.LegacyException!.Throw();
                break;
        }

        throw new InvalidDataException($"Unknown durable pump outcome '{outcome.Kind}'.");
    }

    public async ValueTask<DurableRuntimePumpAttempt> TryRunOnceAsync(
        DurableRuntimePumpRequest request,
        CancellationToken cancellationToken = default)
    {
        var outcome = await RunAttemptAsync(request, cancellationToken).ConfigureAwait(false);
        return outcome.Kind switch
        {
            PostgreSqlDurablePumpOutcomeKind.Completed => new DurableRuntimePumpAttempt(
                DurableRuntimePumpAttemptKind.Completed,
                outcome.Result,
                problemCode: null),
            PostgreSqlDurablePumpOutcomeKind.Refused => new DurableRuntimePumpAttempt(
                DurableRuntimePumpAttemptKind.Refused,
                result: null,
                problemCode: null),
            PostgreSqlDurablePumpOutcomeKind.Unavailable => new DurableRuntimePumpAttempt(
                DurableRuntimePumpAttemptKind.Unavailable,
                result: null,
                outcome.ProblemCode),
            PostgreSqlDurablePumpOutcomeKind.Incompatible => new DurableRuntimePumpAttempt(
                DurableRuntimePumpAttemptKind.Incompatible,
                result: null,
                outcome.ProblemCode),
            _ => throw new InvalidDataException($"Unknown durable pump outcome '{outcome.Kind}'."),
        };
    }

    /// <summary>Runs the sole private admission and execution state machine shared by both public projections.</summary>
    private async ValueTask<PostgreSqlDurablePumpOutcome> RunAttemptAsync(
        DurableRuntimePumpRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var phase = PostgreSqlDurablePumpPhase.LocalSlotPending;
        if (!await _passGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            var legacyException = new InvalidOperationException(LocalOverlapMessage);
            LogRefusalDebug(
                PostgreSqlDurablePumpRefusal.LocalPassOverlap,
                phase,
                PostgreSqlDurableDiagnostics.OperationalAssessmentTroubleshooting);
            return PostgreSqlDurablePumpOutcome.Refused(
                PostgreSqlDurablePumpRefusal.LocalPassOverlap,
                ExceptionDispatchInfo.Capture(legacyException));
        }

        try
        {
            phase = PostgreSqlDurablePumpPhase.ProcessAdmission;
            if (!_admission.TryEnter())
            {
                LogRefusalDebug(
                    PostgreSqlDurablePumpRefusal.ProcessAdmissionClosed,
                    phase,
                    PostgreSqlDurableDiagnostics.OperationalAssessmentTroubleshooting);
                return PostgreSqlDurablePumpOutcome.Refused(
                    PostgreSqlDurablePumpRefusal.ProcessAdmissionClosed);
            }

            phase = PostgreSqlDurablePumpPhase.StoreAdmission;
            try
            {
                await _schemaManager.ValidateAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
            {
                var classified = ClassifyPreExecutionFailure(
                    PostgreSqlDurableControlPlaneOperation.SchemaAdmission,
                    phase,
                    exception,
                    cancellationToken);
                if (classified is { } outcome)
                {
                    return outcome;
                }

                throw;
            }

            PostgreSqlDurableStoreAdmission storeAdmission;
            try
            {
                storeAdmission = await _runtimeHealth.TryBeginPassWithOutcomeAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
            {
                if (PostgreSqlDurableAdmissionFailureContext.TakeIndeterminate(exception))
                {
                    await TryRecordFailedPassAsync(phase).ConfigureAwait(false);
                }

                var classified = ClassifyPreExecutionFailure(
                    PostgreSqlDurableControlPlaneOperation.RuntimeAdmission,
                    phase,
                    exception,
                    cancellationToken);
                if (classified is { } outcome)
                {
                    return outcome;
                }

                throw;
            }

            switch (storeAdmission.Kind)
            {
                case PostgreSqlDurableStoreAdmissionKind.Admitted:
                    break;
                case PostgreSqlDurableStoreAdmissionKind.Draining:
                    LogRefusalDebug(
                        PostgreSqlDurablePumpRefusal.Draining,
                        phase,
                        PostgreSqlDurableDiagnostics.OperationalAssessmentTroubleshooting);
                    return PostgreSqlDurablePumpOutcome.Refused(
                        PostgreSqlDurablePumpRefusal.Draining);
                case PostgreSqlDurableStoreAdmissionKind.StorePassActive:
                    LogRefusalDebug(
                        PostgreSqlDurablePumpRefusal.StorePassActive,
                        phase,
                        PostgreSqlDurableDiagnostics.OperationalAssessmentTroubleshooting);
                    return PostgreSqlDurablePumpOutcome.Refused(
                        PostgreSqlDurablePumpRefusal.StorePassActive);
                case PostgreSqlDurableStoreAdmissionKind.LostWorkerGeneration:
                    LogRefusalWarning(
                        PostgreSqlDurablePumpRefusal.LostWorkerGeneration,
                        phase,
                        PostgreSqlDurableDiagnostics.OperationalAssessmentTroubleshooting);
                    return PostgreSqlDurablePumpOutcome.Refused(
                        PostgreSqlDurablePumpRefusal.LostWorkerGeneration,
                        storeAdmission.LegacyException);
                case PostgreSqlDurableStoreAdmissionKind.EpochMismatch:
                    return PostgreSqlDurablePumpOutcome.Incompatible(
                        DurableProblemCodes.RecoveryEpochRequired,
                        storeAdmission.LegacyException!);
                default:
                    throw new InvalidDataException(
                        $"Unknown durable store admission result '{storeAdmission.Kind}'.");
            }

            DurableRuntimePumpResult result;
            phase = PostgreSqlDurablePumpPhase.Executing;
            try
            {
                result = await _passExecutor(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
            {
                PostgreSqlDurablePumpFailureContext.Mark(exception, phase);
                await TryRecordFailedPassAsync(phase).ConfigureAwait(false);
                throw;
            }

            phase = PostgreSqlDurablePumpPhase.ProviderReturned;
            using var finalizationCancellation = new CancellationTokenSource(
                _registration.Options.ShutdownReserve);
            phase = PostgreSqlDurablePumpPhase.Finalizing;
            try
            {
                await _runtimeHealth.RecordSuccessfulSweepAsync(
                    result,
                    finalizationCancellation.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
            {
                PostgreSqlDurablePumpFailureContext.Mark(exception, phase);
                await TryRecordFailedPassAsync(phase).ConfigureAwait(false);
                throw;
            }

            phase = PostgreSqlDurablePumpPhase.Completed;
            return PostgreSqlDurablePumpOutcome.Completed(result);
        }
        finally
        {
            _passGate.Release();
        }
    }

    /// <summary>Classifies only a failure that occurred before the execution boundary.</summary>
    private PostgreSqlDurablePumpOutcome? ClassifyPreExecutionFailure(
        PostgreSqlDurableControlPlaneOperation operation,
        PostgreSqlDurablePumpPhase phase,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var classification = PostgreSqlDurableFailureClassifier.Classify(
            operation,
            exception,
            cancellationToken);
        switch (classification.Disposition)
        {
            case PostgreSqlDurableFailureDisposition.Propagate:
                return null;
            case PostgreSqlDurableFailureDisposition.Unavailable:
                LogUnavailable(
                    operation,
                    phase,
                    classification.UnavailableCause!.Value,
                    DurableProblemCodes.StoreUnavailable,
                    PostgreSqlDurableDiagnostics.OperationalAssessmentTroubleshooting);
                return PostgreSqlDurablePumpOutcome.Unavailable(exception);
            case PostgreSqlDurableFailureDisposition.Incompatible:
                return PostgreSqlDurablePumpOutcome.Incompatible(
                    classification.ProblemCode!,
                    ExceptionDispatchInfo.Capture(exception));
            default:
                throw new InvalidDataException(
                    $"Unknown durable failure classification '{classification.Disposition}'.");
        }
    }

    /// <summary>
    /// Makes one fresh, bounded ownership-scoped cleanup attempt without replacing the original outcome.
    /// </summary>
    private async ValueTask TryRecordFailedPassAsync(PostgreSqlDurablePumpPhase originalPhase)
    {
        using var cleanupCancellation = new CancellationTokenSource(
            _registration.Options.ShutdownReserve);
        try
        {
            await _runtimeHealth.RecordFailedPassAsync(cleanupCancellation.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
        {
            LogCleanupFailure(
                originalPhase,
                PostgreSqlDurableDiagnostics.OperationalAssessmentTroubleshooting);
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
        if (_workContractSelection.IsEmpty)
        {
            return TurnOutcome.Empty;
        }

        var candidate = (await _workStore.DiscoverAsync(_workContractSelection, 1, cancellationToken).ConfigureAwait(false)).FirstOrDefault();
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

        DurableEncodedWorkExit? exit = null;
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
                (exit, currentClaim) = await InvokeWithLeaseAndHeartbeatAsync(invocation, currentClaim, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
            {
                failure = exception;
            }
        }

        var completion = failure is null
            ? TranslateExit(exit!)
            : new PostgreSqlWorkCompletion(
                PostgreSqlWorkCompletionKind.AmbiguousExternalOutcome,
                DurableProblemCodes.AmbiguousExternalOutcome,
                "{}");
        // Once a permit has committed, cancellation records the normal ambiguous-outcome path rather than inventing
        // terminal provider truth solely to meet a host deadline.
        return await CompleteAsync(currentClaim, completion, counts, CancellationToken.None).ConfigureAwait(false);
    }

    private async ValueTask<(DurableEncodedWorkExit Exit, PostgreSqlDurableWorkClaim Claim)> InvokeWithLeaseAndHeartbeatAsync(
        DurablePreparedWorkInvocation invocation,
        PostgreSqlDurableWorkClaim claim,
        CancellationToken cancellationToken)
    {
        using var executorStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var running = _executionBoundary.InvokeExitAsync(invocation, executorStop.Token).AsTask();
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
            case PostgreSqlWorkObservationOutcome.Applied:
                // Any applied non-success state, including canceled-before-effect and suspension, is committed but not processed.
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

    private static PostgreSqlWorkCompletion TranslateExit(DurableEncodedWorkExit exit)
    {
        ArgumentNullException.ThrowIfNull(exit);
        return exit.Kind switch
        {
            DurableWorkExitKind.Succeeded => new PostgreSqlWorkCompletion(
                PostgreSqlWorkCompletionKind.Succeeded,
                "completed",
                "{}",
                exit.Result!),
            DurableWorkExitKind.RetryBeforeEffect => new PostgreSqlWorkCompletion(
                PostgreSqlWorkCompletionKind.ProvenNoEffect,
                exit.Code!,
                "{}"),
            DurableWorkExitKind.FailedTerminal => new PostgreSqlWorkCompletion(
                PostgreSqlWorkCompletionKind.FailedTerminal,
                exit.Code!,
                "{}"),
            // Encoded exits use private constructors and validated factories; unknown values remain conservative.
            _ => new PostgreSqlWorkCompletion(
                PostgreSqlWorkCompletionKind.AmbiguousExternalOutcome,
                exit.Code!,
                "{}"),
        };
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
        return result.RecordedOccurrences == 0
            && result.MaterializedWorkTargets == 0
            && result.SuspendedSchedules == 0
            ? TurnOutcome.Deferred
            : TurnOutcome.Committed;
    }

    [LoggerMessage(
        EventId = 4111,
        Level = LogLevel.Warning,
        Message = "{ProblemCode} durable PostgreSQL control-plane operation {Operation} at phase {Phase} was unavailable due to {Cause}. See {TroubleshootingAnchor}.")]
    private partial void LogUnavailable(
        PostgreSqlDurableControlPlaneOperation operation,
        PostgreSqlDurablePumpPhase phase,
        PostgreSqlDurableUnavailableCause cause,
        string problemCode,
        string troubleshootingAnchor);

    [LoggerMessage(
        EventId = 4112,
        Level = LogLevel.Debug,
        Message = "Durable PostgreSQL pump admission was refused due to {Cause} at phase {Phase}. See {TroubleshootingAnchor}.")]
    private partial void LogRefusalDebug(
        PostgreSqlDurablePumpRefusal cause,
        PostgreSqlDurablePumpPhase phase,
        string troubleshootingAnchor);

    [LoggerMessage(
        EventId = 4113,
        Level = LogLevel.Warning,
        Message = "Durable PostgreSQL pump admission was refused due to {Cause} at phase {Phase}. See {TroubleshootingAnchor}.")]
    private partial void LogRefusalWarning(
        PostgreSqlDurablePumpRefusal cause,
        PostgreSqlDurablePumpPhase phase,
        string troubleshootingAnchor);

    [LoggerMessage(
        EventId = 4114,
        Level = LogLevel.Warning,
        Message = "Durable PostgreSQL failed-pass cleanup did not complete after phase {OriginalPhase}; stale takeover remains authoritative. See {TroubleshootingAnchor}.")]
    private partial void LogCleanupFailure(
        PostgreSqlDurablePumpPhase originalPhase,
        string troubleshootingAnchor);

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
