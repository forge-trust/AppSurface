using Microsoft.Extensions.Logging;

namespace ForgeTrust.AppSurface.Durable.Provider;

/// <summary>Implements one passive, concurrent-safe lifecycle over health and authoritative admission.</summary>
/// <param name="health">Concurrency-safe current assessment reader, with a singleton-compatible lifetime.</param>
/// <param name="admission">Authoritative nonwaiting pump admission; never replaced by a service semaphore.</param>
/// <param name="clock">Host clock used independently for each invocation's original monotonic budget.</param>
/// <param name="logger">Host-configured safe structured observation sink.</param>
internal sealed class DurableExternalActivationService(
    IDurableRuntimeHealth health,
    IDurableRuntimePumpAdmission admission,
    TimeProvider clock,
    ILogger<DurableExternalActivationService> logger) : IDurableExternalActivationService
{
    /// <inheritdoc />
    public async ValueTask<DurableExternalActivationResult> ActivateAsync(
        DurableExternalActivationRequest request,
        CancellationToken callerCancellation = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        long started;
        try
        {
            started = clock.GetTimestamp();
        }
        catch (Exception exception) when (ExternalActivationValidation.IsNonfatal(exception))
        {
            var failedInvocation = new DurableExternalActivationInvocation(clock, request.RequestBudget, callerCancellation, null);
            var failure = exception is OperationCanceledException && callerCancellation.IsCancellationRequested
                ? failedInvocation.Result(DurableExternalActivationOutcomeKind.CanceledBeforeAdmission, null)
                : Failure(failedInvocation);
            failedInvocation.Finish(failure, logger);
            return failure;
        }

        var invocation = new DurableExternalActivationInvocation(clock, request.RequestBudget, callerCancellation, started);
        DurableExternalActivationResult? result = null;
        Exception? primaryException = null;
        try
        {
            invocation.Start();
            result = invocation.PreAdmissionCancellation();
            if (result is not null)
            {
                return result;
            }

            var snapshot = await health.GetAsync(invocation.Token).ConfigureAwait(false);
            if (snapshot is not null && Enum.IsDefined(snapshot.State))
            {
                invocation.ObservedHealthState = snapshot.State;
                if (snapshot.State == DurableRuntimeHealthState.Stale && ExternalActivationValidation.IsStaleCode(snapshot.ProblemCode))
                {
                    invocation.ObservationCode = snapshot.ProblemCode;
                }
            }

            result = invocation.PreAdmissionCancellation();
            if (result is not null)
            {
                return result;
            }

            ExternalActivationValidation.ValidateHealth(snapshot!);
            if (snapshot!.State == DurableRuntimeHealthState.Unavailable)
            {
                result = invocation.Result(DurableExternalActivationOutcomeKind.Unavailable, snapshot.ProblemCode);
            }
            else if (!snapshot.CanEnableActivation)
            {
                result = invocation.Result(DurableExternalActivationOutcomeKind.Incompatible, snapshot.ProblemCode);
            }
            else if (snapshot.State == DurableRuntimeHealthState.Draining)
            {
                result = invocation.Result(DurableExternalActivationOutcomeKind.Draining, null);
            }

            // Recheck at every pre-invocation return; a compatible observation is never cached admission authority.
            result = invocation.PreAdmissionCancellation() ?? result;
            if (result is not null)
            {
                return result;
            }

            invocation.MarkPumpInvoked();
            var attempt = await admission.TryRunOnceAsync(request.PumpRequest, invocation.Token).ConfigureAwait(false);
            ArgumentNullException.ThrowIfNull(attempt);
            result = attempt.Kind switch
            {
                DurableRuntimePumpAttemptKind.Completed => invocation.Result(
                    DurableExternalActivationOutcomeKind.Completed, invocation.ObservationCode, attempt.Result),
                DurableRuntimePumpAttemptKind.Refused => invocation.Result(DurableExternalActivationOutcomeKind.Busy, invocation.ObservationCode),
                DurableRuntimePumpAttemptKind.Unavailable or DurableRuntimePumpAttemptKind.Incompatible => invocation.Result(
                    DurableExternalActivationOutcomeKind.PumpFailed, attempt.ProblemCode),
                _ => throw new InvalidDataException("The provider returned an undefined admission outcome."),
            };
            if (result.Kind == DurableExternalActivationOutcomeKind.Completed)
            {
                invocation.MarkCompleted();
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            try
            {
                result = invocation.Phase == ActivationPhase.PreAdmission
                    ? invocation.PreAdmissionCancellation() ?? Failure(invocation)
                    : invocation.CallerCanceled || invocation.BudgetCanceled
                        ? invocation.Result(DurableExternalActivationOutcomeKind.PumpCanceled, invocation.ObservationCode)
                        : Failure(invocation);
            }
            catch (Exception exception) when (ExternalActivationValidation.IsNonfatal(exception))
            {
                result = Failure(invocation);
            }
            catch (Exception exception)
            {
                primaryException = exception;
                throw;
            }
            return result;
        }
        catch (Exception exception) when (ExternalActivationValidation.IsNonfatal(exception))
        {
            result = Failure(invocation);
            return result;
        }
        catch (Exception exception)
        {
            primaryException = exception;
            throw;
        }
        finally
        {
            invocation.Finish(result, logger, primaryException);
        }
    }

    /// <summary>Maps unexpected failures by the invocation boundary, replacing advisory diagnostics with ASDUR407.</summary>
    private static DurableExternalActivationResult Failure(DurableExternalActivationInvocation invocation) => invocation.Result(
        invocation.Phase == ActivationPhase.PreAdmission
            ? DurableExternalActivationOutcomeKind.ActivationFailed
            : DurableExternalActivationOutcomeKind.PumpFailed,
        DurableProblemCodes.ExternalActivationFailed);
}
