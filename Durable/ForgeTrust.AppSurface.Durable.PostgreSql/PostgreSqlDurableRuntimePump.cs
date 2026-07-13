using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

internal sealed class PostgreSqlDurableRuntimePump : IDurableRuntimePump
{
    private readonly PostgreSqlDurableRuntimeRegistration _registration;
    private readonly IDurableRuntimeSchemaManager _schemaManager;
    private readonly PostgreSqlDurableWorkStore _workStore;
    private readonly PostgreSqlDurableFlowStore _flowStore;
    private readonly PostgreSqlDurableScheduleProcessor _scheduleProcessor;
    private readonly IDurableWorkRegistry _workRegistry;
    private readonly IDurableFlowRegistry _flowRegistry;
    private readonly IDurablePayloadCodecRegistry _payloadCodecs;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PostgreSqlDurableRuntimeHealth _runtimeHealth;
    private readonly SemaphoreSlim _passGate = new(1, 1);
    private int _passNumber;

    public PostgreSqlDurableRuntimePump(
        PostgreSqlDurableRuntimeRegistration registration,
        IDurableRuntimeSchemaManager schemaManager,
        PostgreSqlDurableWorkStore workStore,
        PostgreSqlDurableFlowStore flowStore,
        PostgreSqlDurableScheduleProcessor scheduleProcessor,
        IDurableWorkRegistry workRegistry,
        IDurableFlowRegistry flowRegistry,
        IDurablePayloadCodecRegistry payloadCodecs,
        IServiceScopeFactory scopeFactory,
        PostgreSqlDurableRuntimeHealth runtimeHealth)
    {
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _schemaManager = schemaManager ?? throw new ArgumentNullException(nameof(schemaManager));
        _workStore = workStore ?? throw new ArgumentNullException(nameof(workStore));
        _flowStore = flowStore ?? throw new ArgumentNullException(nameof(flowStore));
        _scheduleProcessor = scheduleProcessor ?? throw new ArgumentNullException(nameof(scheduleProcessor));
        _workRegistry = workRegistry ?? throw new ArgumentNullException(nameof(workRegistry));
        _flowRegistry = flowRegistry ?? throw new ArgumentNullException(nameof(flowRegistry));
        _payloadCodecs = payloadCodecs ?? throw new ArgumentNullException(nameof(payloadCodecs));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _runtimeHealth = runtimeHealth ?? throw new ArgumentNullException(nameof(runtimeHealth));
    }

    public async ValueTask<DurableRuntimePumpResult> RunOnceAsync(
        DurableRuntimePumpRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!await _passGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"{DurableProblemCodes.WorkerIdentityConflict}: This runtime instance already has an active bounded pump pass.");
        }

        try
        {
            return await RunOnceCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _passGate.Release();
        }
    }

    private async ValueTask<DurableRuntimePumpResult> RunOnceCoreAsync(
        DurableRuntimePumpRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _schemaManager.ValidateAsync(cancellationToken).ConfigureAwait(false);
        if (!await _runtimeHealth.TryBeginPassAsync(cancellationToken).ConfigureAwait(false))
        {
            return new DurableRuntimePumpResult(
                discovered: 0,
                claimed: 0,
                processed: 0,
                deferred: 0,
                failed: 0,
                hasMore: false,
                nextDueAtUtc: null,
                elapsed: TimeSpan.Zero);
        }

        try
        {
            return await RunPassAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                await _runtimeHealth.RecordFailedPassAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
            {
                // Preserve the processing failure. A crash-safe stale heartbeat remains visible if cleanup also fails.
            }

            throw;
        }
    }

    private async ValueTask<DurableRuntimePumpResult> RunPassAsync(
        DurableRuntimePumpRequest request,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var counts = new PumpCounts();
        var surfaces = ResolveSurfaceOrder(request.Surfaces, Interlocked.Increment(ref _passNumber));
        foreach (var surface in surfaces)
        {
            if (counts.Consumed >= request.MaximumItems || stopwatch.Elapsed >= request.TimeBudget)
            {
                counts.HasMore = true;
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var remaining = request.MaximumItems - counts.Consumed;
            switch (surface)
            {
                case DurableRuntimeSurface.Schedule:
                    await ProcessSchedulesAsync(
                        remaining,
                        request.TimeBudget,
                        stopwatch,
                        counts,
                        cancellationToken).ConfigureAwait(false);
                    break;
                case DurableRuntimeSurface.Flow:
                    await ProcessFlowsAsync(remaining, request.TimeBudget, stopwatch, counts, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case DurableRuntimeSurface.Work:
                    await ProcessWorkAsync(remaining, request.TimeBudget, stopwatch, counts, cancellationToken)
                        .ConfigureAwait(false);
                    break;
            }
        }

        stopwatch.Stop();
        var result = new DurableRuntimePumpResult(
            counts.Discovered,
            counts.Claimed,
            counts.Processed,
            counts.Deferred,
            counts.Failed,
            counts.HasMore,
            counts.NextDueAtUtc,
            stopwatch.Elapsed);
        await _runtimeHealth.RecordSuccessfulSweepAsync(result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async ValueTask ProcessSchedulesAsync(
        int maximumItems,
        TimeSpan timeBudget,
        Stopwatch stopwatch,
        PumpCounts counts,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < maximumItems; index++)
        {
            if (stopwatch.Elapsed >= timeBudget)
            {
                counts.HasMore = true;
                break;
            }

            var result = await _scheduleProcessor.ProcessDueAsync(
                new PostgreSqlScheduleProcessingRequest(
                    maximumSchedules: 1,
                    maximumOccurrencesPerSchedule: 1),
                cancellationToken).ConfigureAwait(false);
            counts.Discovered += result.Discovered;
            counts.Examined += result.Discovered;
            counts.Claimed += result.Advanced;
            counts.Processed += result.Advanced;
            counts.Deferred += result.Queued + result.Coalesced + result.Skipped;
            counts.HasMore |= result.HasMore;
            if (result.Discovered == 0)
            {
                break;
            }
        }
    }

    private async ValueTask ProcessFlowsAsync(
        int maximumItems,
        TimeSpan timeBudget,
        Stopwatch stopwatch,
        PumpCounts counts,
        CancellationToken cancellationToken)
    {
        var candidates = await _flowStore.DiscoverAsync(maximumItems, cancellationToken).ConfigureAwait(false);
        counts.Discovered += candidates.Count;
        counts.HasMore |= candidates.Count == maximumItems;
        foreach (var candidate in candidates)
        {
            if (stopwatch.Elapsed >= timeBudget)
            {
                counts.HasMore = true;
                break;
            }

            counts.Examined++;
            var result = await _flowStore.TryProcessAsync(
                candidate,
                _registration.Options.WorkerId,
                _flowRegistry,
                _payloadCodecs,
                async (transaction, scopeId, instanceId, terminalCode, callbackToken) =>
                {
                    await _scheduleProcessor.ReleaseTargetAsync(
                        transaction,
                        scopeId,
                        DurableScheduleTargetKind.Flow,
                        instanceId.Value,
                        terminalCode,
                        callbackToken).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
            switch (result.Outcome)
            {
                case PostgreSqlFlowProcessingOutcome.Applied:
                    counts.Claimed++;
                    counts.Processed++;
                    break;
                case PostgreSqlFlowProcessingOutcome.NotClaimed:
                case PostgreSqlFlowProcessingOutcome.Stale:
                case PostgreSqlFlowProcessingOutcome.RaceLost:
                    counts.Deferred++;
                    break;
                case PostgreSqlFlowProcessingOutcome.Failed:
                    counts.Failed++;
                    break;
                default:
                    throw new InvalidDataException($"Unknown PostgreSQL Flow processing outcome '{result.Outcome}'.");
            }
        }
    }

    private async ValueTask ProcessWorkAsync(
        int maximumItems,
        TimeSpan timeBudget,
        Stopwatch stopwatch,
        PumpCounts counts,
        CancellationToken cancellationToken)
    {
        var candidates = await _workStore.DiscoverAsync(maximumItems, cancellationToken).ConfigureAwait(false);
        counts.Discovered += candidates.Count;
        counts.HasMore |= candidates.Count == maximumItems;
        foreach (var candidate in candidates)
        {
            if (stopwatch.Elapsed >= timeBudget)
            {
                counts.HasMore = true;
                break;
            }

            counts.Examined++;
            var transitioned = false;
            var claim = await _workStore.TryClaimAsync(
                candidate,
                _registration.Options.WorkerId,
                cancellationToken,
                async (transaction, state, code, callbackToken) =>
                {
                    transitioned = true;
                    await ApplyClaimTransitionCallbacksAsync(
                        transaction,
                        candidate.ScopeId,
                        candidate.WorkId,
                        state,
                        code,
                        callbackToken).ConfigureAwait(false);
                }).ConfigureAwait(false);
            if (claim is null)
            {
                if (transitioned)
                {
                    counts.Failed++;
                }
                else
                {
                    counts.Deferred++;
                }

                continue;
            }

            counts.Claimed++;
            await ProcessClaimedWorkAsync(claim, counts, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask ProcessClaimedWorkAsync(
        PostgreSqlDurableWorkClaim claim,
        PumpCounts counts,
        CancellationToken cancellationToken)
    {
        DurableWorkRegistration workRegistration;
        try
        {
            workRegistration = _workRegistry.GetRequired(claim.WorkName, claim.WorkVersion);
            if (workRegistration.ProviderSafety != claim.ProviderSafety)
            {
                throw new InvalidOperationException("The persisted provider-safety snapshot does not match its registration.");
            }
        }
        catch (InvalidOperationException)
        {
            var missingContractCompletion = new PostgreSqlWorkCompletion(
                PostgreSqlWorkCompletionKind.ContractUnavailable,
                DurableProblemCodes.WorkContractUnavailable,
                "{}");
            var result = await RecordCompletionAsync(claim, missingContractCompletion, cancellationToken).ConfigureAwait(false);
            CountCompletion(result, counts);
            return;
        }

        DurableEncodedPayload? resultPayload = null;
        Exception? executionFailure = null;
        var currentClaim = claim;
        await using (var serviceScope = _scopeFactory.CreateAsyncScope())
        {
            DurablePreparedWorkInvocation prepared;
            try
            {
                prepared = workRegistration.Prepare(serviceScope.ServiceProvider, CreateClaimedWork(claim));
            }
            catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
            {
                var invalidContract = new PostgreSqlWorkCompletion(
                    PostgreSqlWorkCompletionKind.ContractUnavailable,
                    DurableProblemCodes.WorkContractUnavailable,
                    "{}");
                var invalidResult = await RecordCompletionAsync(claim, invalidContract, cancellationToken)
                    .ConfigureAwait(false);
                CountCompletion(invalidResult, counts);
                return;
            }

            var permit = await _workStore.TryAcquireEffectPermitAsync(
                claim,
                cancellationToken,
                async (transaction, state, code, callbackToken) =>
                {
                    await ApplyClaimTransitionCallbacksAsync(
                        transaction,
                        claim.ScopeId,
                        claim.WorkId,
                        state,
                        code,
                        callbackToken).ConfigureAwait(false);
                }).ConfigureAwait(false);
            if (permit is null)
            {
                counts.Deferred++;
                return;
            }

            currentClaim = permit.Claim;
            try
            {
                (resultPayload, currentClaim) = await InvokeWithLeaseRenewalAsync(
                    prepared,
                    currentClaim,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
            {
                executionFailure = exception;
            }
        }

        PostgreSqlWorkCompletion completion;
        if (executionFailure is null)
        {
            completion = new PostgreSqlWorkCompletion(
                PostgreSqlWorkCompletionKind.Succeeded,
                "completed",
                "{}",
                resultPayload);
        }
        else
        {
            completion = new PostgreSqlWorkCompletion(
                PostgreSqlWorkCompletionKind.Retry,
                DurableProblemCodes.AmbiguousExternalOutcome,
                "{}");
        }

        var completionToken = cancellationToken.IsCancellationRequested ? CancellationToken.None : cancellationToken;
        var completionResult = await RecordCompletionAsync(currentClaim, completion, completionToken).ConfigureAwait(false);
        CountCompletion(completionResult, counts);
    }

    private async ValueTask<(DurableEncodedPayload Result, PostgreSqlDurableWorkClaim Claim)> InvokeWithLeaseRenewalAsync(
        DurablePreparedWorkInvocation invocationBoundary,
        PostgreSqlDurableWorkClaim claim,
        CancellationToken cancellationToken)
    {
        using var executorStop = new CancellationTokenSource();
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, executorStop.Token);
        if (claim.CancellationRequested)
        {
            await executorStop.CancelAsync().ConfigureAwait(false);
        }

        var invocation = invocationBoundary.InvokeAsync(execution.Token).AsTask();
        var currentClaim = claim;
        var nextRenewalAtUtc = DateTimeOffset.UtcNow + currentClaim.LeaseRenewalCadence;
        var heartbeatInterval = _registration.Options.HeartbeatStaleAfter / 3;
        var nextHeartbeatAtUtc = DateTimeOffset.UtcNow + heartbeatInterval;
        try
        {
            while (!invocation.IsCompleted)
            {
                var now = DateTimeOffset.UtcNow;
                var remaining = currentClaim.LeaseExpiresAtUtc - now;
                if (remaining <= TimeSpan.Zero)
                {
                    await executorStop.CancelAsync().ConfigureAwait(false);
                    break;
                }

                var nextActionAtUtc = nextRenewalAtUtc < nextHeartbeatAtUtc
                    ? nextRenewalAtUtc
                    : nextHeartbeatAtUtc;
                if (currentClaim.LeaseExpiresAtUtc < nextActionAtUtc)
                {
                    nextActionAtUtc = currentClaim.LeaseExpiresAtUtc;
                }

                var delay = nextActionAtUtc - now;
                if (delay > TimeSpan.Zero
                    && await Task.WhenAny(invocation, Task.Delay(delay, cancellationToken)).ConfigureAwait(false) == invocation)
                {
                    break;
                }

                if (invocation.IsCompleted)
                {
                    break;
                }

                now = DateTimeOffset.UtcNow;
                if (now >= nextHeartbeatAtUtc)
                {
                    await _runtimeHealth.RecordHeartbeatAsync(cancellationToken).ConfigureAwait(false);
                    nextHeartbeatAtUtc = now + heartbeatInterval;
                }

                if (now >= nextRenewalAtUtc)
                {
                    var renewed = await _workStore.RenewLeaseAsync(currentClaim, cancellationToken).ConfigureAwait(false);
                    if (renewed is null)
                    {
                        await executorStop.CancelAsync().ConfigureAwait(false);
                        break;
                    }

                    currentClaim = renewed;
                    nextRenewalAtUtc = now + currentClaim.LeaseRenewalCadence;
                    if (currentClaim.CancellationRequested && !executorStop.IsCancellationRequested)
                    {
                        await executorStop.CancelAsync().ConfigureAwait(false);
                    }
                }
            }

            return (await invocation.ConfigureAwait(false), currentClaim);
        }
        catch
        {
            await executorStop.CancelAsync().ConfigureAwait(false);
            try
            {
                _ = await invocation.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
            {
                // Preserve the lease/heartbeat failure while still observing the provider task after cancellation.
            }

            throw;
        }
    }

    private async ValueTask<PostgreSqlWorkCompletionResult> RecordCompletionAsync(
        PostgreSqlDurableWorkClaim claim,
        PostgreSqlWorkCompletion completion,
        CancellationToken cancellationToken)
    {
        return await _workStore.RecordCompletionAsync(
            claim,
            completion,
            async (transaction, terminalState, callbackToken) =>
            {
                if (terminalState is DurableWorkState.Succeeded or DurableWorkState.SucceededAfterCancelRequested
                    && completion.Result is not null)
                {
                    await _flowStore.ResumeActivityAsync(
                        transaction,
                        claim.ScopeId,
                        claim.WorkId,
                        completion.Result,
                        callbackToken).ConfigureAwait(false);
                }
                else if (terminalState is DurableWorkState.FailedTerminal or DurableWorkState.CanceledBeforeEffect)
                {
                    var failureKind = terminalState == DurableWorkState.CanceledBeforeEffect
                        ? PostgreSqlFlowActivityFailureKind.CanceledBeforeEffect
                        : PostgreSqlFlowActivityFailureKind.FailedTerminal;
                    await _flowStore.FailActivityAsync(
                        transaction,
                        claim.ScopeId,
                        claim.WorkId,
                        failureKind,
                        completion.Code,
                        callbackToken).ConfigureAwait(false);
                }
                else if (terminalState == DurableWorkState.Suspended)
                {
                    await _flowStore.FailActivityAsync(
                        transaction,
                        claim.ScopeId,
                        claim.WorkId,
                        PostgreSqlFlowActivityFailureKind.Suspended,
                        completion.Code,
                        callbackToken).ConfigureAwait(false);
                }

                if (terminalState != DurableWorkState.Suspended)
                {
                    await _scheduleProcessor.ReleaseTargetAsync(
                        transaction,
                        claim.ScopeId,
                        DurableScheduleTargetKind.Work,
                        claim.WorkId.Value,
                        completion.Code,
                        callbackToken).ConfigureAwait(false);
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask ApplyClaimTransitionCallbacksAsync(
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableWorkId workId,
        DurableWorkState state,
        string code,
        CancellationToken cancellationToken)
    {
        if (state == DurableWorkState.Suspended)
        {
            await _flowStore.FailActivityAsync(
                transaction,
                scopeId,
                workId,
                PostgreSqlFlowActivityFailureKind.Suspended,
                code,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (state is DurableWorkState.FailedTerminal or DurableWorkState.CanceledBeforeEffect)
        {
            await _flowStore.FailActivityAsync(
                transaction,
                scopeId,
                workId,
                state == DurableWorkState.CanceledBeforeEffect
                    ? PostgreSqlFlowActivityFailureKind.CanceledBeforeEffect
                    : PostgreSqlFlowActivityFailureKind.FailedTerminal,
                code,
                cancellationToken).ConfigureAwait(false);
            await _scheduleProcessor.ReleaseTargetAsync(
                transaction,
                scopeId,
                DurableScheduleTargetKind.Work,
                workId.Value,
                code,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static DurableClaimedWork CreateClaimedWork(PostgreSqlDurableWorkClaim claim) =>
        new(
            claim.ScopeId,
            claim.WorkId,
            claim.ActivityId,
            claim.WorkName,
            claim.WorkVersion,
            claim.Payload,
            claim.ProviderSafety,
            claim.AttemptNumber,
            claim.LeaseGeneration,
            claim.ScopeGeneration,
            claim.RuntimeEpoch.ToString("D"),
            claim.ProviderKey);

    private static void CountCompletion(PostgreSqlWorkCompletionResult completion, PumpCounts counts)
    {
        switch (completion.Outcome)
        {
            case PostgreSqlWorkObservationOutcome.Applied when completion.State is
                DurableWorkState.Succeeded or DurableWorkState.SucceededAfterCancelRequested:
                counts.Processed++;
                break;
            case PostgreSqlWorkObservationOutcome.Applied when completion.State == DurableWorkState.Suspended:
                counts.Failed++;
                break;
            case PostgreSqlWorkObservationOutcome.Applied when completion.State == DurableWorkState.FailedTerminal:
                counts.Failed++;
                break;
            case PostgreSqlWorkObservationOutcome.Applied:
            case PostgreSqlWorkObservationOutcome.AlreadyTerminal:
            case PostgreSqlWorkObservationOutcome.StaleObservation:
                counts.Deferred++;
                break;
            default:
                throw new InvalidDataException($"Unknown PostgreSQL work observation outcome '{completion.Outcome}'.");
        }

        if (completion.NextDueAtUtc is { } nextDueAtUtc
            && (counts.NextDueAtUtc is null || nextDueAtUtc < counts.NextDueAtUtc))
        {
            counts.NextDueAtUtc = nextDueAtUtc;
        }
    }

    private static IReadOnlyList<DurableRuntimeSurface> ResolveSurfaceOrder(
        DurableRuntimeSurface selected,
        int passNumber)
    {
        DurableRuntimeSurface[] all =
        [
            DurableRuntimeSurface.Schedule,
            DurableRuntimeSurface.Flow,
            DurableRuntimeSurface.Work,
        ];
        var offset = Math.Abs(passNumber % all.Length);
        var ordered = new List<DurableRuntimeSurface>(all.Length);
        for (var index = 0; index < all.Length; index++)
        {
            var candidate = all[(index + offset) % all.Length];
            if ((selected & candidate) != 0)
            {
                ordered.Add(candidate);
            }
        }

        return ordered;
    }

    private sealed class PumpCounts
    {
        public int Discovered { get; set; }

        public int Claimed { get; set; }

        public int Processed { get; set; }

        public int Deferred { get; set; }

        public int Failed { get; set; }

        public int Examined { get; set; }

        public bool HasMore { get; set; }

        public DateTimeOffset? NextDueAtUtc { get; set; }

        public int Consumed => Examined;
    }
}
