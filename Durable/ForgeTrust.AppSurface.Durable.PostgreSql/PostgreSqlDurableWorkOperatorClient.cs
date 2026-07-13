using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

internal sealed class PostgreSqlDurableWorkOperatorClient : IDurableWorkOperatorClient
{
    private static readonly Uri OperatorDocumentation = new("https://appsurface.dev/docs/durable/operations");
    private readonly PostgreSqlDurableRuntimeRegistration _runtime;
    private readonly IDurableRuntimeSchemaManager _schemaManager;
    private readonly IDurableWorkRegistry _workRegistry;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PostgreSqlDurableFlowStore _flowStore;
    private readonly PostgreSqlDurableScheduleProcessor _scheduleProcessor;

    public PostgreSqlDurableWorkOperatorClient(
        PostgreSqlDurableRuntimeRegistration runtime,
        IDurableRuntimeSchemaManager schemaManager,
        IDurableWorkRegistry workRegistry,
        IServiceScopeFactory scopeFactory,
        PostgreSqlDurableFlowStore flowStore,
        PostgreSqlDurableScheduleProcessor scheduleProcessor)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _schemaManager = schemaManager ?? throw new ArgumentNullException(nameof(schemaManager));
        _workRegistry = workRegistry ?? throw new ArgumentNullException(nameof(workRegistry));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _flowStore = flowStore ?? throw new ArgumentNullException(nameof(flowStore));
        _scheduleProcessor = scheduleProcessor ?? throw new ArgumentNullException(nameof(scheduleProcessor));
    }

    public async ValueTask<DurableOperationResult<DurableWorkOperatorResult>> ReconcileAsync(
        DurableWorkReconcileRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var command = new OperatorCommand(
            request.ScopeId,
            request.WorkId,
            request.CommandId,
            "reconcile",
            request.ActorId,
            request.ReasonCode,
            request.ExpectedRevision,
            ResultIdentity: null);
        try
        {
            await _schemaManager.ValidateAsync(cancellationToken).ConfigureAwait(false);
            var begun = await BeginReconciliationAsync(command, cancellationToken).ConfigureAwait(false);
            if (begun.Duplicate is not null)
            {
                return Success(begun.Duplicate);
            }

            var work = begun.Work ?? throw new InvalidDataException("Reconciliation did not return authoritative work.");
            DurableEncodedEffectReconciliation reconciliation;
            await using (var scope = _scopeFactory.CreateAsyncScope())
            {
                try
                {
                    reconciliation = await work.Registration.ReconcileAsync(
                        scope.ServiceProvider,
                        work.ClaimedWork,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
                {
                    reconciliation = new DurableEncodedEffectReconciliation(
                        DurableEffectReconciliationKind.Unknown,
                        result: null);
                }
            }

            return Success(await CompleteReconciliationAsync(
                command,
                work.ReconcilingRevision,
                reconciliation,
                cancellationToken).ConfigureAwait(false));
        }
        catch (OperatorRejectedException exception)
        {
            return DurableOperationResult<DurableWorkOperatorResult>.Failure(exception.Problem);
        }
    }

    public async ValueTask<DurableOperationResult<DurableWorkOperatorResult>> ResolveAsync(
        DurableWorkManualResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var resultIdentity = request.Result is null
            ? request.Resolution.ToString()
            : $"{request.Resolution}|{request.Result.ContractName}|{request.Result.ContractVersion}|{request.Result.Classification}|{request.Result.RetentionPolicyId}|{request.Result.Sha256}";
        var command = new OperatorCommand(
            request.ScopeId,
            request.WorkId,
            request.CommandId,
            "manual_resolve",
            request.ActorId,
            request.ReasonCode,
            request.ExpectedRevision,
            resultIdentity);
        try
        {
            await _schemaManager.ValidateAsync(cancellationToken).ConfigureAwait(false);
            return Success(await ResolveManualAsync(command, request, cancellationToken).ConfigureAwait(false));
        }
        catch (OperatorRejectedException exception)
        {
            return DurableOperationResult<DurableWorkOperatorResult>.Failure(exception.Problem);
        }
    }

    public async ValueTask<DurableOperationResult<DurableWorkOperatorResult>> RetrySafeAsync(
        DurableWorkRetrySafeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var command = new OperatorCommand(
            request.ScopeId,
            request.WorkId,
            request.CommandId,
            "retry_safe",
            request.ActorId,
            request.ReasonCode,
            request.ExpectedRevision,
            ResultIdentity: null);
        try
        {
            await _schemaManager.ValidateAsync(cancellationToken).ConfigureAwait(false);
            return Success(await ReleaseSafeRetryAsync(command, cancellationToken).ConfigureAwait(false));
        }
        catch (OperatorRejectedException exception)
        {
            return DurableOperationResult<DurableWorkOperatorResult>.Failure(exception.Problem);
        }
    }

    public async ValueTask<DurableOperationResult<DurableWorkOperatorResult>> ReleaseAfterRecoveryAsync(
        DurableWorkRecoveryReleaseRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var command = new OperatorCommand(
            request.ScopeId,
            request.WorkId,
            request.CommandId,
            "recovery_release",
            request.ActorId,
            request.ReasonCode,
            request.ExpectedRevision,
            ResultIdentity: null);
        try
        {
            await _schemaManager.ValidateAsync(cancellationToken).ConfigureAwait(false);
            return Success(await ReleaseRecoveryAsync(command, cancellationToken).ConfigureAwait(false));
        }
        catch (OperatorRejectedException exception)
        {
            return DurableOperationResult<DurableWorkOperatorResult>.Failure(exception.Problem);
        }
    }

    private async ValueTask<BeginReconciliationResult> BeginReconciliationAsync(
        OperatorCommand request,
        CancellationToken cancellationToken)
    {
        var fingerprint = ComputeFingerprint(request);
        await using var connection = await _runtime.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PostgreSqlDurableEpochFence.EnsureCurrentAsync(
                connection, transaction, _runtime.RuntimeEpoch, cancellationToken).ConfigureAwait(false);
            await PostgreSqlScheduleStorage.SetScopeAsync(
                connection, transaction, request.ScopeId, cancellationToken).ConfigureAwait(false);
            var existing = await ReadCommandAsync(connection, transaction, request, fingerprint, cancellationToken)
                .ConfigureAwait(false);
            if (existing?.CompletedResult is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new BeginReconciliationResult(existing.CompletedResult, null);
            }

            var scope = await PostgreSqlScheduleStorage.LockScopeAsync(
                connection, transaction, request.ScopeId, cancellationToken).ConfigureAwait(false);
            if (scope is null)
            {
                throw Reject(DurableProblemCodes.WorkNotFound, "The owning durable scope does not exist.", request);
            }

            var work = await LockWorkAsync(connection, transaction, request, cancellationToken).ConfigureAwait(false)
                ?? throw Reject(DurableProblemCodes.WorkNotFound, "The work aggregate was not found in the authorized scope.", request);
            var registration = RequireRegistration(work, request);
            if (registration.ProviderSafety != DurableProviderSafety.ReconcileBeforeRetry || !registration.CanReconcile)
            {
                throw Reject(
                    DurableProblemCodes.OperatorTransitionRejected,
                    "Only ReconcileBeforeRetry work with a registered side-effect-free reconciler can be reconciled.",
                    request);
            }

            long reconcilingRevision;
            if (existing is null)
            {
                if (work.Revision != request.ExpectedRevision || work.State != "suspended_reconciliation_required")
                {
                    throw Reject(
                        work.Revision != request.ExpectedRevision
                            ? DurableProblemCodes.WorkRevisionConflict
                            : DurableProblemCodes.OperatorTransitionRejected,
                        "The work is not at the expected reconciliation-required revision.",
                        request);
                }

                reconcilingRevision = checked(work.Revision + 1);
                await UpdateWorkStateAsync(
                    connection,
                    transaction,
                    request,
                    "reconciling",
                    "operator_reconciling",
                    reconcilingRevision,
                    dueNow: false,
                    runtimeEpoch: null,
                    scopeGeneration: null,
                    result: null,
                    terminal: false,
                    cancellationToken).ConfigureAwait(false);
                await InsertStartedCommandAsync(
                    connection, transaction, request, fingerprint, cancellationToken).ConfigureAwait(false);
                await InsertHistoryAsync(
                    connection,
                    transaction,
                    request,
                    work,
                    reconcilingRevision,
                    "operator_reconciliation_started",
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (work.State != "reconciling")
                {
                    throw Reject(
                        DurableProblemCodes.OperatorCommandInProgress,
                        "The persisted reconciliation command cannot resume because work left its fenced state.",
                        request);
                }

                reconcilingRevision = work.Revision;
            }

            var claimed = CreateClaimedWork(work);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new BeginReconciliationResult(
                null,
                new ReconcilingWork(registration, claimed, reconcilingRevision));
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask<DurableWorkOperatorResult> CompleteReconciliationAsync(
        OperatorCommand request,
        long reconcilingRevision,
        DurableEncodedEffectReconciliation reconciliation,
        CancellationToken cancellationToken)
    {
        await using var connection = await _runtime.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PostgreSqlDurableEpochFence.EnsureCurrentAsync(
                connection, transaction, _runtime.RuntimeEpoch, cancellationToken).ConfigureAwait(false);
            await PostgreSqlScheduleStorage.SetScopeAsync(
                connection, transaction, request.ScopeId, cancellationToken).ConfigureAwait(false);
            var scope = await PostgreSqlScheduleStorage.LockScopeAsync(
                connection, transaction, request.ScopeId, cancellationToken).ConfigureAwait(false)
                ?? throw Reject(DurableProblemCodes.WorkNotFound, "The owning scope disappeared during reconciliation.", request);
            var work = await LockWorkAsync(connection, transaction, request, cancellationToken).ConfigureAwait(false)
                ?? throw Reject(DurableProblemCodes.WorkNotFound, "The work disappeared during reconciliation.", request);
            if (work.State != "reconciling" || work.Revision < reconcilingRevision)
            {
                throw Reject(
                    DurableProblemCodes.WorkRevisionConflict,
                    "The work changed while the side-effect-free reconciler was reading provider state.",
                    request);
            }

            var registration = RequireRegistration(work, request);
            if (reconciliation.Result is not null)
            {
                _ = registration.ResultCodec.DecodeObject(reconciliation.Result);
            }

            var nextRevision = checked(work.Revision + 1);
            var canceled = work.CancellationRequested || !scope.IsActive;
            var state = reconciliation.Kind switch
            {
                DurableEffectReconciliationKind.Applied when work.CancellationRequested => "succeeded_after_cancel_requested",
                DurableEffectReconciliationKind.Applied => "succeeded",
                DurableEffectReconciliationKind.NotApplied when canceled => "canceled_before_effect",
                DurableEffectReconciliationKind.NotApplied => "retry_wait",
                DurableEffectReconciliationKind.Unknown => "suspended_reconciliation_required",
                _ => throw new ArgumentOutOfRangeException(nameof(reconciliation)),
            };
            var code = reconciliation.Kind switch
            {
                DurableEffectReconciliationKind.Applied => "operator_reconciled_applied",
                DurableEffectReconciliationKind.NotApplied => "operator_reconciled_not_applied",
                DurableEffectReconciliationKind.Unknown => "operator_reconciliation_unknown",
                _ => throw new ArgumentOutOfRangeException(nameof(reconciliation)),
            };
            await UpdateWorkStateAsync(
                connection,
                transaction,
                request,
                state,
                code,
                nextRevision,
                dueNow: state == "retry_wait",
                runtimeEpoch: null,
                scopeGeneration: null,
                reconciliation.Result,
                terminal: state is "succeeded" or "succeeded_after_cancel_requested" or "canceled_before_effect",
                cancellationToken).ConfigureAwait(false);
            await UpdatePermitAsync(
                connection,
                transaction,
                request,
                reconciliation.Kind switch
                {
                    DurableEffectReconciliationKind.Applied => "known_succeeded",
                    DurableEffectReconciliationKind.NotApplied => "proven_no_effect",
                    _ => "ambiguous",
                },
                cancellationToken).ConfigureAwait(false);
            await UpdateDispatchAsync(
                connection, transaction, request, state, nextRevision, cancellationToken).ConfigureAwait(false);
            await CompleteCommandAsync(
                connection, transaction, request, state, nextRevision, cancellationToken).ConfigureAwait(false);
            await InsertHistoryAsync(
                connection,
                transaction,
                request,
                work,
                nextRevision,
                code,
                cancellationToken).ConfigureAwait(false);
            await ApplyTerminalCallbacksAsync(
                transaction,
                request,
                state,
                code,
                reconciliation.Result,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new DurableWorkOperatorResult(
                request.WorkId,
                DurableWorkOperatorOutcome.Applied,
                ParseState(state),
                nextRevision);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask<DurableWorkOperatorResult> ResolveManualAsync(
        OperatorCommand command,
        DurableWorkManualResolutionRequest request,
        CancellationToken cancellationToken)
    {
        var fingerprint = ComputeFingerprint(command);
        await using var connection = await _runtime.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PostgreSqlDurableEpochFence.EnsureCurrentAsync(
                connection, transaction, _runtime.RuntimeEpoch, cancellationToken).ConfigureAwait(false);
            await PostgreSqlScheduleStorage.SetScopeAsync(
                connection, transaction, command.ScopeId, cancellationToken).ConfigureAwait(false);
            var existing = await ReadCommandAsync(connection, transaction, command, fingerprint, cancellationToken)
                .ConfigureAwait(false);
            if (existing?.CompletedResult is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return existing.CompletedResult;
            }

            var scope = await PostgreSqlScheduleStorage.LockScopeAsync(
                connection, transaction, command.ScopeId, cancellationToken).ConfigureAwait(false)
                ?? throw Reject(DurableProblemCodes.WorkNotFound, "The owning durable scope does not exist.", command);
            var work = await LockWorkAsync(connection, transaction, command, cancellationToken).ConfigureAwait(false)
                ?? throw Reject(DurableProblemCodes.WorkNotFound, "The work aggregate was not found.", command);
            var manualResolutionRequired =
                work.State == "suspended_manual_resolution"
                && work.ProviderSafety == DurableProviderSafety.ManualResolution;
            var canceledReplaySafeAmbiguity =
                work.State == "suspended_ambiguous_external_outcome"
                && work.CancellationRequested
                && work.ProviderSafety is DurableProviderSafety.Idempotent or DurableProviderSafety.ProviderKeyed;
            if (work.Revision != command.ExpectedRevision
                || (!manualResolutionRequired && !canceledReplaySafeAmbiguity))
            {
                throw Reject(
                    work.Revision != command.ExpectedRevision
                        ? DurableProblemCodes.WorkRevisionConflict
                        : DurableProblemCodes.OperatorTransitionRejected,
                    "Resolution requires either ManualResolution work or canceled replay-safe ambiguity at its expected suspended revision.",
                    command);
            }

            if (!await HasAmbiguousPermitAsync(
                    connection, transaction, command, cancellationToken).ConfigureAwait(false))
            {
                throw Reject(
                    DurableProblemCodes.OperatorProofRequired,
                    "Resolution requires matching ambiguous effect evidence; recovery-only rows must be released through recovery.",
                    command);
            }

            var registration = RequireRegistration(work, command);
            if (request.Result is not null)
            {
                _ = registration.ResultCodec.DecodeObject(request.Result);
            }

            var nextRevision = checked(work.Revision + 1);
            var canceled = work.CancellationRequested || !scope.IsActive;
            var state = request.Resolution switch
            {
                DurableManualResolutionKind.Applied when work.CancellationRequested => "succeeded_after_cancel_requested",
                DurableManualResolutionKind.Applied => "succeeded",
                DurableManualResolutionKind.ProvenNotApplied when canceled => "canceled_before_effect",
                DurableManualResolutionKind.ProvenNotApplied => "retry_wait",
                _ => throw new ArgumentOutOfRangeException(nameof(request)),
            };
            var code = request.Resolution == DurableManualResolutionKind.Applied
                ? "operator_manual_applied"
                : "operator_manual_proven_not_applied";
            await UpdateWorkStateAsync(
                connection,
                transaction,
                command,
                state,
                code,
                nextRevision,
                dueNow: state == "retry_wait",
                runtimeEpoch: null,
                scopeGeneration: null,
                request.Result,
                terminal: state != "retry_wait",
                cancellationToken).ConfigureAwait(false);
            await UpdatePermitAsync(
                connection,
                transaction,
                command,
                request.Resolution == DurableManualResolutionKind.Applied ? "known_succeeded" : "proven_no_effect",
                cancellationToken).ConfigureAwait(false);
            await UpdateDispatchAsync(
                connection, transaction, command, state, nextRevision, cancellationToken).ConfigureAwait(false);
            await InsertCompletedCommandAsync(
                connection, transaction, command, fingerprint, state, nextRevision, cancellationToken).ConfigureAwait(false);
            await InsertHistoryAsync(
                connection, transaction, command, work, nextRevision, code, cancellationToken).ConfigureAwait(false);
            await ApplyTerminalCallbacksAsync(
                transaction, command, state, code, request.Result, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new DurableWorkOperatorResult(
                command.WorkId,
                DurableWorkOperatorOutcome.Applied,
                ParseState(state),
                nextRevision);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask<DurableWorkOperatorResult> ReleaseSafeRetryAsync(
        OperatorCommand request,
        CancellationToken cancellationToken)
    {
        var fingerprint = ComputeFingerprint(request);
        await using var connection = await _runtime.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PostgreSqlDurableEpochFence.EnsureCurrentAsync(
                connection, transaction, _runtime.RuntimeEpoch, cancellationToken).ConfigureAwait(false);
            await PostgreSqlScheduleStorage.SetScopeAsync(
                connection, transaction, request.ScopeId, cancellationToken).ConfigureAwait(false);
            var existing = await ReadCommandAsync(connection, transaction, request, fingerprint, cancellationToken)
                .ConfigureAwait(false);
            if (existing?.CompletedResult is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return existing.CompletedResult;
            }

            var scope = await PostgreSqlScheduleStorage.LockScopeAsync(
                connection, transaction, request.ScopeId, cancellationToken).ConfigureAwait(false)
                ?? throw Reject(DurableProblemCodes.WorkNotFound, "The owning durable scope does not exist.", request);
            if (!scope.IsActive)
            {
                throw Reject(DurableProblemCodes.ScopeDisabled, "Disabled scopes cannot release work for retry.", request);
            }

            var work = await LockWorkAsync(connection, transaction, request, cancellationToken).ConfigureAwait(false)
                ?? throw Reject(DurableProblemCodes.WorkNotFound, "The work aggregate was not found.", request);
            if (work.Revision != request.ExpectedRevision || !work.State.StartsWith("suspended_", StringComparison.Ordinal))
            {
                throw Reject(
                    work.Revision != request.ExpectedRevision
                        ? DurableProblemCodes.WorkRevisionConflict
                        : DurableProblemCodes.OperatorTransitionRejected,
                    "Safe retry release requires the expected suspended work revision.",
                    request);
            }

            _ = RequireRegistration(work, request);
            var hasAmbiguousPermit = await HasAmbiguousPermitAsync(
                connection, transaction, request, cancellationToken).ConfigureAwait(false);
            if (hasAmbiguousPermit
                && work.ProviderSafety is not DurableProviderSafety.Idempotent and not DurableProviderSafety.ProviderKeyed)
            {
                throw Reject(
                    DurableProblemCodes.OperatorProofRequired,
                    "An unsafe provider permit remains ambiguous; reconcile or resolve it before retry.",
                    request);
            }

            var nextRevision = checked(work.Revision + 1);
            const string state = "retry_wait";
            const string code = "operator_retry_released";
            await UpdateWorkStateAsync(
                connection,
                transaction,
                request,
                state,
                code,
                nextRevision,
                dueNow: true,
                _runtime.RuntimeEpoch,
                scope.Generation,
                result: null,
                terminal: false,
                cancellationToken,
                clearCancellation: true).ConfigureAwait(false);
            await UpdateDispatchAsync(
                connection, transaction, request, state, nextRevision, cancellationToken).ConfigureAwait(false);
            await InsertCompletedCommandAsync(
                connection, transaction, request, fingerprint, state, nextRevision, cancellationToken).ConfigureAwait(false);
            await InsertHistoryAsync(
                connection, transaction, request, work, nextRevision, code, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new DurableWorkOperatorResult(
                request.WorkId,
                DurableWorkOperatorOutcome.Applied,
                DurableWorkState.Ready,
                nextRevision);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask<DurableWorkOperatorResult> ReleaseRecoveryAsync(
        OperatorCommand request,
        CancellationToken cancellationToken)
    {
        var fingerprint = ComputeFingerprint(request);
        await using var connection = await _runtime.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PostgreSqlDurableEpochFence.EnsureCurrentAsync(
                connection, transaction, _runtime.RuntimeEpoch, cancellationToken).ConfigureAwait(false);
            await PostgreSqlScheduleStorage.SetScopeAsync(
                connection, transaction, request.ScopeId, cancellationToken).ConfigureAwait(false);
            var existing = await ReadCommandAsync(connection, transaction, request, fingerprint, cancellationToken)
                .ConfigureAwait(false);
            if (existing?.CompletedResult is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return existing.CompletedResult;
            }

            var scope = await PostgreSqlScheduleStorage.LockScopeAsync(
                connection, transaction, request.ScopeId, cancellationToken).ConfigureAwait(false)
                ?? throw Reject(DurableProblemCodes.WorkNotFound, "The owning durable scope does not exist.", request);
            if (!scope.IsActive)
            {
                throw Reject(DurableProblemCodes.ScopeDisabled, "Disabled scopes cannot release recovery-fenced work.", request);
            }

            var work = await LockWorkAsync(connection, transaction, request, cancellationToken).ConfigureAwait(false)
                ?? throw Reject(DurableProblemCodes.WorkNotFound, "The work aggregate was not found.", request);
            if (work.Revision != request.ExpectedRevision
                || work.RuntimeEpoch == _runtime.RuntimeEpoch
                || IsTerminalState(work.State))
            {
                throw Reject(
                    work.Revision != request.ExpectedRevision
                        ? DurableProblemCodes.WorkRevisionConflict
                        : DurableProblemCodes.OperatorTransitionRejected,
                    "Recovery release requires an exact-revision nonterminal work aggregate fenced by an older runtime epoch.",
                    request);
            }

            _ = RequireRegistration(work, request);
            var hasAmbiguousPermit = await HasAmbiguousPermitAsync(
                connection, transaction, request, cancellationToken).ConfigureAwait(false);
            var state = ResolveRecoveryReleaseState(work, hasAmbiguousPermit);
            var nextRevision = checked(work.Revision + 1);
            const string code = "operator_recovery_released";
            await UpdateWorkStateAsync(
                connection,
                transaction,
                request,
                state,
                code,
                nextRevision,
                dueNow: false,
                _runtime.RuntimeEpoch,
                scope.Generation,
                result: null,
                terminal: state == "canceled_before_effect",
                cancellationToken).ConfigureAwait(false);
            await UpdateDispatchAsync(
                connection,
                transaction,
                request,
                state,
                nextRevision,
                cancellationToken,
                dueNow: false).ConfigureAwait(false);
            await InsertCompletedCommandAsync(
                connection, transaction, request, fingerprint, state, nextRevision, cancellationToken).ConfigureAwait(false);
            await InsertHistoryAsync(
                connection, transaction, request, work, nextRevision, code, cancellationToken).ConfigureAwait(false);
            if (state == "canceled_before_effect")
            {
                await ApplyTerminalCallbacksAsync(
                    transaction,
                    request,
                    state,
                    code,
                    result: null,
                    cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new DurableWorkOperatorResult(
                request.WorkId,
                DurableWorkOperatorOutcome.Applied,
                ParseState(state),
                nextRevision);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private DurableWorkRegistration RequireRegistration(OperatorWorkRow work, OperatorCommand request)
    {
        DurableWorkRegistration registration;
        try
        {
            registration = _workRegistry.GetRequired(work.WorkName, work.WorkVersion);
            if (registration.ProviderSafety != work.ProviderSafety)
            {
                throw new InvalidOperationException("Provider-safety manifest mismatch.");
            }

            _ = registration.WorkCodec.DecodeObject(work.Payload);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Text.Json.JsonException)
        {
            throw Reject(
                DurableProblemCodes.WorkContractUnavailable,
                "The exact historical work registration and payload policy must be restored before operator release.",
                request);
        }

        return registration;
    }

    private async ValueTask ApplyTerminalCallbacksAsync(
        NpgsqlTransaction transaction,
        OperatorCommand request,
        string state,
        string code,
        DurableEncodedPayload? result,
        CancellationToken cancellationToken)
    {
        if (state is "succeeded" or "succeeded_after_cancel_requested" && result is not null)
        {
            await _flowStore.ResumeActivityAsync(
                transaction, request.ScopeId, request.WorkId, result, cancellationToken).ConfigureAwait(false);
        }
        else if (state == "canceled_before_effect")
        {
            await _flowStore.FailActivityAsync(
                transaction,
                request.ScopeId,
                request.WorkId,
                PostgreSqlFlowActivityFailureKind.CanceledBeforeEffect,
                code,
                cancellationToken).ConfigureAwait(false);
        }

        if (state is "succeeded" or "succeeded_after_cancel_requested" or "canceled_before_effect")
        {
            await _scheduleProcessor.ReleaseTargetAsync(
                transaction,
                request.ScopeId,
                DurableScheduleTargetKind.Work,
                request.WorkId.Value,
                code,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask<OperatorWorkRow?> LockWorkAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OperatorCommand request,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT state, revision, work_name, work_version, provider_safety, activity_id,
                   contract_id, payload_schema_version, payload_classification, payload_retention,
                   payload, payload_sha256, attempt_number, lease_generation, scope_generation,
                   runtime_epoch, provider_key, cancellation_requested_at IS NOT NULL, due_at, terminal_code
            FROM appsurface_durable.work
            WHERE scope_id = @scope_id AND work_id = @work_id
            FOR UPDATE;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddIdentity(command, request);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var payload = new DurableEncodedPayload(
            reader.GetString(6),
            reader.GetString(7),
            ParseClassification(reader.GetString(8)),
            reader.GetFieldValue<byte[]>(10),
            reader.GetString(9));
        if (!reader.GetFieldValue<byte[]>(11).AsSpan().SequenceEqual(Convert.FromHexString(payload.Sha256)))
        {
            throw new InvalidDataException("The operator work payload hash does not match its authoritative bytes.");
        }

        return new OperatorWorkRow(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetString(2),
            reader.GetString(3),
            ParseProviderSafety(reader.GetString(4)),
            reader.GetString(5),
            payload,
            reader.GetInt32(12),
            reader.GetInt64(13),
            reader.GetInt64(14),
            reader.GetGuid(15),
            reader.GetString(16),
            reader.GetBoolean(17),
            ReadUtc(reader, 18),
            reader.IsDBNull(19) ? null : reader.GetString(19))
        {
            ScopeId = request.ScopeId,
            WorkId = request.WorkId,
        };
    }

    private static DurableClaimedWork CreateClaimedWork(OperatorWorkRow work)
    {
        if (work.AttemptNumber < 1 || work.LeaseGeneration < 1)
        {
            throw new InvalidDataException("Reconciliation requires a previously permitted work attempt.");
        }

        return new DurableClaimedWork(
            work.ScopeId,
            work.WorkId,
            work.ActivityId,
            work.WorkName,
            work.WorkVersion,
            work.Payload,
            work.ProviderSafety,
            work.AttemptNumber,
            work.LeaseGeneration,
            work.ScopeGeneration,
            work.RuntimeEpoch.ToString("D"),
            work.ProviderKey);
    }

    private static async ValueTask<ExistingOperatorCommand?> ReadCommandAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OperatorCommand request,
        byte[] fingerprint,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT work_id, command_type, request_sha256, status, resulting_state, resulting_revision
            FROM appsurface_durable.work_operator_command
            WHERE scope_id = @scope_id AND command_id = @command_id
            FOR UPDATE;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddIdentity(command, request);
        command.Parameters.AddWithValue("command_id", request.CommandId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        if (!string.Equals(reader.GetString(0), request.WorkId.Value, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(1), request.CommandType, StringComparison.Ordinal)
            || !reader.GetFieldValue<byte[]>(2).AsSpan().SequenceEqual(fingerprint))
        {
            throw Reject(
                DurableProblemCodes.CommandConflict,
                "The operator command identity was already used for different semantic content.",
                request);
        }

        var status = reader.GetString(3);
        DurableWorkOperatorResult? completed = null;
        if (status == "completed")
        {
            completed = new DurableWorkOperatorResult(
                request.WorkId,
                DurableWorkOperatorOutcome.Duplicate,
                ParseState(reader.GetString(4)),
                reader.GetInt64(5));
        }

        return new ExistingOperatorCommand(status, completed);
    }

    private static async ValueTask UpdateWorkStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OperatorCommand request,
        string state,
        string code,
        long revision,
        bool dueNow,
        Guid? runtimeEpoch,
        long? scopeGeneration,
        DurableEncodedPayload? result,
        bool terminal,
        CancellationToken cancellationToken,
        bool clearCancellation = false)
    {
        const string sql = """
            UPDATE appsurface_durable.work
            SET state = @state,
                revision = @revision,
                terminal_code = @code,
                due_at = CASE WHEN @due_now THEN clock_timestamp() ELSE due_at END,
                cancellation_requested_at = CASE
                    WHEN @clear_cancellation THEN NULL
                    ELSE cancellation_requested_at
                END,
                runtime_epoch = COALESCE(@runtime_epoch, runtime_epoch),
                scope_generation = COALESCE(@scope_generation, scope_generation),
                terminal_at = CASE WHEN @terminal THEN clock_timestamp() ELSE NULL END,
                lease_owner = NULL,
                lease_started_at = NULL,
                lease_expires_at = NULL,
                result_contract_id = @result_contract_id,
                result_schema_version = @result_schema_version,
                result_codec_id = @result_codec_id,
                result_classification = @result_classification,
                result_retention_policy_id = @result_retention_policy_id,
                result_payload = @result_payload,
                result_sha256 = @result_sha256,
                updated_at = clock_timestamp()
            WHERE scope_id = @scope_id AND work_id = @work_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddIdentity(command, request);
        command.Parameters.AddWithValue("state", state);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue("code", code);
        command.Parameters.AddWithValue("due_now", dueNow);
        command.Parameters.AddWithValue("clear_cancellation", clearCancellation);
        AddNullable(command, "runtime_epoch", NpgsqlDbType.Uuid, runtimeEpoch);
        AddNullable(command, "scope_generation", NpgsqlDbType.Bigint, scopeGeneration);
        command.Parameters.AddWithValue("terminal", terminal);
        AddResultParameters(command, result);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new DBConcurrencyException("The operator work row changed while holding its mutation lock.");
        }
    }

    private static async ValueTask UpdatePermitAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OperatorCommand request,
        string status,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE appsurface_durable.effect_permit
            SET status = @status,
                observed_at = clock_timestamp(),
                details = jsonb_build_object('operator_command_id', @command_id)
            WHERE scope_id = @scope_id
              AND work_id = @work_id
              AND status IN ('granted', 'ambiguous');
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddIdentity(command, request);
        command.Parameters.AddWithValue("command_id", request.CommandId.Value);
        command.Parameters.AddWithValue("status", status);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) < 1)
        {
            throw new InvalidDataException(
                "The operator proof did not match any unresolved authoritative effect permit.");
        }
    }

    private static async ValueTask<bool> HasAmbiguousPermitAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OperatorCommand request,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT EXISTS
            (
                SELECT 1
                FROM appsurface_durable.effect_permit
                WHERE scope_id = @scope_id
                  AND work_id = @work_id
                  AND status IN ('granted', 'ambiguous')
            );
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddIdentity(command, request);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("PostgreSQL did not return effect-permit ambiguity state."));
    }

    private static async ValueTask UpdateDispatchAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OperatorCommand request,
        string workState,
        long revision,
        CancellationToken cancellationToken,
        bool dueNow = true)
    {
        var dispatchState = workState switch
        {
            "retry_wait" => "available",
            "succeeded" or "succeeded_after_cancel_requested" or "canceled_before_effect" => "terminal",
            _ => "suspended",
        };
        const string sql = """
            UPDATE appsurface_durable.dispatch
            SET state = @state,
                due_at = CASE WHEN @state = 'available' AND @due_now THEN clock_timestamp() ELSE due_at END,
                expected_revision = @revision,
                updated_at = clock_timestamp()
            WHERE scope_id = @scope_id
              AND aggregate_kind = 'work'
              AND aggregate_id = @work_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddIdentity(command, request);
        command.Parameters.AddWithValue("state", dispatchState);
        command.Parameters.AddWithValue("due_now", dueNow);
        command.Parameters.AddWithValue("revision", revision);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidDataException("The operator work aggregate has no authoritative dispatch row.");
        }
    }

    private static async ValueTask InsertStartedCommandAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OperatorCommand request,
        byte[] fingerprint,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO appsurface_durable.work_operator_command
                (scope_id, work_id, command_id, command_type, actor_id, reason_code, request_sha256, status)
            VALUES
                (@scope_id, @work_id, @command_id, @command_type, @actor_id, @reason_code, @fingerprint, 'started');
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddCommandParameters(command, request, fingerprint);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask InsertCompletedCommandAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OperatorCommand request,
        byte[] fingerprint,
        string state,
        long revision,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO appsurface_durable.work_operator_command
                (scope_id, work_id, command_id, command_type, actor_id, reason_code, request_sha256,
                 status, resulting_state, resulting_revision, completed_at)
            VALUES
                (@scope_id, @work_id, @command_id, @command_type, @actor_id, @reason_code, @fingerprint,
                 'completed', @state, @revision, clock_timestamp());
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddCommandParameters(command, request, fingerprint);
        command.Parameters.AddWithValue("state", state);
        command.Parameters.AddWithValue("revision", revision);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask CompleteCommandAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OperatorCommand request,
        string state,
        long revision,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE appsurface_durable.work_operator_command
            SET status = 'completed',
                resulting_state = @state,
                resulting_revision = @revision,
                completed_at = clock_timestamp()
            WHERE scope_id = @scope_id
              AND command_id = @command_id
              AND work_id = @work_id
              AND status = 'started';
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddIdentity(command, request);
        command.Parameters.AddWithValue("command_id", request.CommandId.Value);
        command.Parameters.AddWithValue("state", state);
        command.Parameters.AddWithValue("revision", revision);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new DBConcurrencyException("The reconciliation command was not in its started state.");
        }
    }

    private static async ValueTask InsertHistoryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OperatorCommand request,
        OperatorWorkRow work,
        long revision,
        string eventType,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO appsurface_durable.work_history
                (scope_id, work_id, aggregate_revision, event_type, command_id, actor_id, reason_code,
                 attempt_number, lease_generation, scope_generation, runtime_epoch, details)
            VALUES
                (@scope_id, @work_id, @revision, @event_type, @command_id, @actor_id, @reason_code,
                 @attempt_number, @lease_generation, @scope_generation, @runtime_epoch, '{}'::jsonb);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddIdentity(command, request);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue("event_type", eventType);
        command.Parameters.AddWithValue("command_id", request.CommandId.Value);
        command.Parameters.AddWithValue("actor_id", request.ActorId);
        command.Parameters.AddWithValue("reason_code", request.ReasonCode);
        command.Parameters.AddWithValue("attempt_number", work.AttemptNumber);
        command.Parameters.AddWithValue("lease_generation", work.LeaseGeneration);
        command.Parameters.AddWithValue("scope_generation", work.ScopeGeneration);
        command.Parameters.AddWithValue("runtime_epoch", work.RuntimeEpoch);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddCommandParameters(NpgsqlCommand command, OperatorCommand request, byte[] fingerprint)
    {
        AddIdentity(command, request);
        command.Parameters.AddWithValue("command_id", request.CommandId.Value);
        command.Parameters.AddWithValue("command_type", request.CommandType);
        command.Parameters.AddWithValue("actor_id", request.ActorId);
        command.Parameters.AddWithValue("reason_code", request.ReasonCode);
        command.Parameters.AddWithValue("fingerprint", fingerprint);
    }

    private static void AddIdentity(NpgsqlCommand command, OperatorCommand request)
    {
        command.Parameters.AddWithValue("scope_id", request.ScopeId.Value);
        command.Parameters.AddWithValue("work_id", request.WorkId.Value);
    }

    private static void AddResultParameters(NpgsqlCommand command, DurableEncodedPayload? result)
    {
        AddNullable(command, "result_contract_id", NpgsqlDbType.Text, result?.ContractName);
        AddNullable(command, "result_schema_version", NpgsqlDbType.Text, result?.ContractVersion);
        AddNullable(
            command,
            "result_codec_id",
            NpgsqlDbType.Text,
            result is null ? null : $"{result.ContractName}@{result.ContractVersion}");
        AddNullable(
            command,
            "result_classification",
            NpgsqlDbType.Text,
            result is null ? null : FormatClassification(result.Classification));
        AddNullable(command, "result_retention_policy_id", NpgsqlDbType.Text, result?.RetentionPolicyId);
        AddNullable(command, "result_payload", NpgsqlDbType.Bytea, result?.Content.ToArray());
        AddNullable(
            command,
            "result_sha256",
            NpgsqlDbType.Bytea,
            result is null ? null : Convert.FromHexString(result.Sha256));
    }

    private static void AddNullable(NpgsqlCommand command, string name, NpgsqlDbType type, object? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value ?? DBNull.Value });

    private static byte[] ComputeFingerprint(OperatorCommand request)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        Write(writer, request.CommandType);
        Write(writer, request.ScopeId.Value);
        Write(writer, request.WorkId.Value);
        Write(writer, request.ActorId);
        Write(writer, request.ReasonCode);
        writer.Write(request.ExpectedRevision);
        Write(writer, request.ResultIdentity ?? string.Empty);
        writer.Flush();
        return SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)));
    }

    private static void Write(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static DurableOperationResult<DurableWorkOperatorResult> Success(DurableWorkOperatorResult result) =>
        DurableOperationResult<DurableWorkOperatorResult>.Success(result);

    private static OperatorRejectedException Reject(string code, string problem, OperatorCommand request) =>
        new(new DurableProblem(
            code,
            problem,
            "Authoritative work state, provider proof, registration, scope lifecycle, or command identity rejected the requested transition.",
            "Reload the safe work snapshot and use reconciliation, manual proof, safe retry, or recovery release appropriate to its current state.",
            OperatorDocumentation,
            request.CommandId.Value));

    private static DurableWorkState ParseState(string state) => state switch
    {
        "pending" or "retry_wait" => DurableWorkState.Ready,
        "leased" or "effect_permitted" => DurableWorkState.Claimed,
        "cancel_pending" => DurableWorkState.CancelPending,
        "succeeded" => DurableWorkState.Succeeded,
        "succeeded_after_cancel_requested" => DurableWorkState.SucceededAfterCancelRequested,
        "failed" => DurableWorkState.FailedTerminal,
        "canceled_before_effect" => DurableWorkState.CanceledBeforeEffect,
        "reconciling" or
        "suspended_ambiguous_external_outcome" or
        "suspended_reconciliation_required" or
        "suspended_manual_resolution" or
        "suspended_contract_unavailable" => DurableWorkState.Suspended,
        _ => throw new InvalidDataException($"Unknown operator work state '{state}'."),
    };

    private static string ResolveRecoveryReleaseState(OperatorWorkRow work, bool hasAmbiguousPermit)
    {
        if (!hasAmbiguousPermit)
        {
            return work.CancellationRequested ? "canceled_before_effect" : "retry_wait";
        }

        return work.ProviderSafety switch
        {
            DurableProviderSafety.ReconcileBeforeRetry => "suspended_reconciliation_required",
            DurableProviderSafety.ManualResolution => "suspended_manual_resolution",
            DurableProviderSafety.Idempotent or DurableProviderSafety.ProviderKeyed when work.CancellationRequested =>
                "suspended_ambiguous_external_outcome",
            DurableProviderSafety.Idempotent or DurableProviderSafety.ProviderKeyed => "retry_wait",
            _ => throw new InvalidDataException($"Unknown provider safety '{work.ProviderSafety}'."),
        };
    }

    private static bool IsTerminalState(string state) => state is
        "succeeded" or
        "succeeded_after_cancel_requested" or
        "failed" or
        "canceled_before_effect";

    private static DurableProviderSafety ParseProviderSafety(string value) => value switch
    {
        "idempotent" => DurableProviderSafety.Idempotent,
        "provider_keyed" => DurableProviderSafety.ProviderKeyed,
        "reconcile_before_retry" => DurableProviderSafety.ReconcileBeforeRetry,
        "manual_resolution" => DurableProviderSafety.ManualResolution,
        _ => throw new InvalidDataException($"Unknown provider safety '{value}'."),
    };

    private static DurableDataClassification ParseClassification(string value) => value switch
    {
        "operational" => DurableDataClassification.Operational,
        "approved_application" => DurableDataClassification.ApprovedApplication,
        _ => throw new InvalidDataException($"Unknown data classification '{value}'."),
    };

    private static string FormatClassification(DurableDataClassification value) => value switch
    {
        DurableDataClassification.Operational => "operational",
        DurableDataClassification.ApprovedApplication => "approved_application",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static DateTimeOffset ReadUtc(NpgsqlDataReader reader, int ordinal) =>
        new(reader.GetFieldValue<DateTime>(ordinal), TimeSpan.Zero);

    private sealed record OperatorCommand(
        DurableScopeId ScopeId,
        DurableWorkId WorkId,
        DurableCommandId CommandId,
        string CommandType,
        string ActorId,
        string ReasonCode,
        long ExpectedRevision,
        string? ResultIdentity);

    private sealed record OperatorWorkRow(
        string State,
        long Revision,
        string WorkName,
        string WorkVersion,
        DurableProviderSafety ProviderSafety,
        string ActivityId,
        DurableEncodedPayload Payload,
        int AttemptNumber,
        long LeaseGeneration,
        long ScopeGeneration,
        Guid RuntimeEpoch,
        string ProviderKey,
        bool CancellationRequested,
        DateTimeOffset DueAtUtc,
        string? TerminalCode)
    {
        internal DurableScopeId ScopeId { get; init; }
        internal DurableWorkId WorkId { get; init; }
    }

    private sealed record ExistingOperatorCommand(
        string Status,
        DurableWorkOperatorResult? CompletedResult);

    private sealed record ReconcilingWork(
        DurableWorkRegistration Registration,
        DurableClaimedWork ClaimedWork,
        long ReconcilingRevision);

    private sealed record BeginReconciliationResult(
        DurableWorkOperatorResult? Duplicate,
        ReconcilingWork? Work);

    private sealed class OperatorRejectedException(DurableProblem problem) : Exception(problem.Problem)
    {
        internal DurableProblem Problem { get; } = problem;
    }
}
