using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Evidence.Planner;
using System.ComponentModel;

namespace ForgeTrust.AppSurface.Evidence.Cli;

/// <summary>Closed phases of the allocation/activation callback; None means it was not entered.</summary>
internal enum EvidenceAllocationPhase { None, BeforeAllocation, Allocation, BeforeActivation, Activation, Completed }

/// <summary>Closed exception categories; no exception text or runtime type names are retained.</summary>
internal enum EvidenceAllocationErrorClass { None, Unknown, OutOfMemory, Io, AccessDenied, Unsupported, Argument, Cancelled, Timeout, Admission, InvalidOperation }

/// <summary>Private diagnostic facts after joined allocation-stage failure. This record grants no authority.</summary>
/// <param name="Phase">Last callback phase.</param>
/// <param name="Operation">Last allocation operation.</param>
/// <param name="StageOutcome">Closed stage outcome.</param>
/// <param name="TerminalCode">First latched worker terminal code.</param>
/// <param name="ErrorClass">Closed exception classification.</param>
/// <param name="NativeErrno">Known direct Win32 inner errno from allocation, bounded to 1..4095; otherwise null.</param>
internal sealed record EvidenceAllocationFailureDiagnostic(EvidenceAllocationPhase Phase,
    EvidenceLinuxArtifactAllocationOperation Operation, EvidenceWorkerStageOutcome StageOutcome,
    EvidenceWorkerTerminalCode TerminalCode, EvidenceAllocationErrorClass ErrorClass, int? NativeErrno)
{
    /// <summary>Gets the fixed private diagnostic schema.</summary>
    public string Schema => "evidence-allocation-failure-v1";
}

/// <summary>Runs the first-party CLI through the same protected admission and bounded lifecycle as Aspire.</summary>
/// <remarks>The production Trusted proof allowlist is empty until full consumer acceptance; this entry cannot bypass it.</remarks>
internal static class EvidenceProtectedCliExecution
{
    /// <summary>Uses only the authenticated descriptor to select the worker mode and inputs.</summary>
    internal static async Task<EvidenceManifest> RunAsync(string controlChannel, CancellationToken cancellationToken,
        Action<EvidenceAllocationFailureDiagnostic>? diagnosticSink = null)
    {
        var worker = await EvidenceLinuxWorkerSupervisor.ConnectAsync(controlChannel, cancellationToken).ConfigureAwait(false);
        return await RunAsync(worker, EvidenceModeSelection.Select(worker.Descriptor.Mode), cancellationToken, diagnosticSink).ConfigureAwait(false);
    }

    /// <summary>Checks an explicit caller mode against the protected launcher before any callback.</summary>
    /// <remarks>Authenticated mode rejection follows the bounded owned-worker stop and exit-confirmation path before returning.</remarks>
    internal static async Task<EvidenceManifest> RunAsync(EvidenceExecutionRequest request, CancellationToken cancellationToken,
        Action<EvidenceAllocationFailureDiagnostic>? diagnosticSink = null)
    {
        var worker = await EvidenceLinuxWorkerSupervisor.ConnectAsync(request.ControlChannel, cancellationToken).ConfigureAwait(false);
        return await RunAsync(worker, request.Mode, cancellationToken, diagnosticSink).ConfigureAwait(false);
    }

    private static async Task<EvidenceManifest> RunAsync(EvidenceLinuxWorkerSupervisor worker, EvidenceExecutionMode mode,
        CancellationToken callerCancellation, Action<EvidenceAllocationFailureDiagnostic>? diagnosticSink)
    {
        var descriptor = worker.Descriptor;
        var clock = TimeProvider.System;
        var collectionLimit = TimeSpan.FromSeconds(descriptor.CollectionSeconds);
        var execution = new EvidenceWorkerExecution(worker, clock, worker.JobRemaining,
            TimeSpan.FromSeconds(descriptor.CleanupSeconds), TimeSpan.FromSeconds(descriptor.StoppingSeconds),
            collectionReserve: collectionLimit);
        EvidenceLinuxArtifactRoot? root = null;
        EvidenceAdmissionResult? admission = null;
        try
        {
            if (EvidenceModeSelection.Select(descriptor.Mode) != mode)
                throw new EvidenceAdmissionException("ASEVD401", "The caller mode conflicts with the protected launcher.");

            var resolved = await execution.ExecuteAsync(EvidenceRunStage.Admission,
                TimeSpan.FromSeconds(descriptor.AdmissionSeconds),
                async token => await EvidenceProtectedWorkerInputs.ResolveAsync(descriptor, token).ConfigureAwait(false),
                callerCancellation).ConfigureAwait(false);
            if (resolved.Outcome != EvidenceWorkerStageOutcome.Passed)
                throw execution.TerminalException as EvidenceAdmissionException
                    ?? new EvidenceAdmissionException("ASEVD403", "Protected input resolution failed.");
            var inputs = resolved.Value;
            var plan = inputs.Plan;
            // Parsing application metadata does not enroll a registration. Resolve the same
            // compile-owned definition used by Aspire before admission or application I/O.
            var application = descriptor.Application is null ? null
                : EvidenceClosedApplicationCatalogue.Resolve(inputs.Policy, plan, descriptor);
            int? applicationStartSeconds = application is null ? null
                : Math.Min(descriptor.StartSeconds, application.Capabilities.StartSeconds);
            var stages = CreateDeclaredStages(plan.Profile, descriptor.AdmissionSeconds, applicationStartSeconds);
            if (!EvidenceRunTimeBudget.TryCreateFromAllowance(clock, worker.JobRemaining, stages,
                TimeSpan.FromSeconds(descriptor.CollectionSeconds), TimeSpan.FromSeconds(descriptor.CleanupSeconds),
                TimeSpan.FromSeconds(descriptor.StoppingSeconds), out var budget))
                throw new EvidenceAdmissionException("ASEVD421", "Declared work and collection/cleanup reserves exceed the protected job budget.");
            var context = EvidenceProtectedWorkerInputs.CreateContext(descriptor, inputs.Policy, plan);
            if (!budget!.TryBeginNextStage(callerCancellation, out var stage))
                throw new EvidenceAdmissionException("ASEVD421", "The protected admission deadline reserve is exhausted.");
            var admitted = await execution.ExecuteAsync(EvidenceRunStage.Admission, stage!.Duration,
                token => EvidenceAdmission.AdmitAsync(mode, plan, context, worker,
                    new EvidenceProtectedWorkerInputs.RegisteredVerifier(worker, context.ExpectedAssertion!), token), callerCancellation).ConfigureAwait(false);
            budget.CompleteCurrentStage();
            if (admitted.Outcome != EvidenceWorkerStageOutcome.Passed || admitted.Value is null)
                throw execution.TerminalException as EvidenceAdmissionException ?? new EvidenceAdmissionException("ASEVD407", "Protected execution admission failed.");
            admission = admitted.Value;
            if (!budget.TryBeginNextStage(callerCancellation, out stage))
                throw new EvidenceAdmissionException("ASEVD421", "The protected allocation reserve is exhausted.");
            var phase = EvidenceAllocationPhase.None;
            var operation = EvidenceLinuxArtifactAllocationOperation.None;
            var allocated = await execution.ExecuteAsync(EvidenceRunStage.Admission, stage!.Duration,
                token =>
                {
                    phase = EvidenceAllocationPhase.BeforeAllocation;
                    token.ThrowIfCancellationRequested();
                    phase = EvidenceAllocationPhase.Allocation;
                    var allocatedRoot = EvidenceLinuxArtifactRoot.Allocate(descriptor.OutputParent, descriptor.OutputParentIdentity,
                        descriptor.OutputSlot, descriptor.WorkerUid, descriptor.WorkerGid, out operation);
                    root = allocatedRoot; // Retain ownership even if cancellation wins before the callback returns.
                    phase = EvidenceAllocationPhase.BeforeActivation;
                    token.ThrowIfCancellationRequested();
                    phase = EvidenceAllocationPhase.Activation;
                    admission.Activate(allocatedRoot.Identity.ToString());
                    phase = EvidenceAllocationPhase.Completed;
                    return ValueTask.FromResult(allocatedRoot);
                }, callerCancellation).ConfigureAwait(false);
            budget.CompleteCurrentStage();
            if (allocated.Outcome != EvidenceWorkerStageOutcome.Passed)
            {
                admission.LatchFailure();
                ReportAllocationFailure(execution, allocated.Outcome, phase, operation, diagnosticSink);
                throw new EvidenceAdmissionException("ASEVD409", "Fresh output allocation or activation failed.");
            }

            IReadOnlyList<EvidenceResourceResult> resourceResults = Array.Empty<EvidenceResourceResult>();
            if (application is not null)
                resourceResults = await RunApplicationStagesAsync(worker, admission, plan, execution, budget,
                    application, clock, callerCancellation).ConfigureAwait(false);

            var results = new List<EvidenceProducerResult>();
            var writers = new List<EvidenceArtifactWriter>();
            var artifactQuota = EvidenceRunByteQuota.CreateArtifact(onExceeded: () =>
            {
                admission.LatchFailure();
                execution.LatchFailure();
            });
            var processOutputQuota = EvidenceRunByteQuota.CreateProcessOutput(onExceeded: () =>
            {
                admission.LatchFailure();
                execution.LatchFailure();
            });
            foreach (var producer in plan.Profile.Producers)
            {
                if (!budget.TryBeginNextStage(callerCancellation, out stage))
                {
                    admission.LatchFailure();
                    await execution.RequestTerminalStopAsync(EvidenceWorkerTerminalCode.DeadlineExceeded).ConfigureAwait(false);
                    break;
                }

                var writer = new EvidenceArtifactWriter(producer, root!, artifactQuota, execution, admission);
                writers.Add(writer);
                var coverage = EvidenceRestrictedCoverageProducerFactory.Create(producer);
                var produced = await execution.ExecuteAsync(EvidenceRunStage.Producer, stage!.Duration,
                    async token =>
                    {
                        using var lease = writer.BindRestrictedProducerLease(worker, admission, plan, inputs.DiffBytes,
                            processOutputQuota, execution, token);
                        return await coverage.ProduceAsync(new EvidenceProducerContext(plan, producer, clock, writer), token).ConfigureAwait(false);
                    }, callerCancellation).ConfigureAwait(false);
                budget.CompleteCurrentStage();
                if (produced.Outcome != EvidenceWorkerStageOutcome.Passed || produced.Value is null)
                {
                    admission.LatchFailure();
                    break;
                }
                results.Add(produced.Value);
                if (produced.Value.Outcome != EvidenceProducerOutcome.Passed)
                {
                    admission.LatchFailure();
                    await execution.RequestTerminalStopAsync(EvidenceWorkerTerminalCode.StageFailed).ConfigureAwait(false);
                    break;
                }
            }

            budget.TryAbandonStagesAndBeginCleanup(stageClosed: true);
            _ = await execution.StopAndDisposeAsync().ConfigureAwait(false);
            var cleanup = execution.CleanupCompleted;
            var ownedWorkStopped = execution.OwnWorkStopped;
            if (execution.TerminalCode != EvidenceWorkerTerminalCode.None) admission.LatchFailure();
            if (!budget.CompleteCleanup() || !budget.TryBeginCollection())
                throw new EvidenceAdmissionException("ASEVD421", "The protected final collection reserve is exhausted.");
            var collected = await execution.CollectAsync(budget.CollectionRemaining, async token =>
            {
                var artifactsVerified = true;
                foreach (var writer in writers)
                    artifactsVerified &= await writer.VerifyWrittenArtifactsAsync(token).ConfigureAwait(false);
                admission.Complete(ownedWorkStopped, artifactsVerified, cleanup);
                var manifest = EvidenceManifestBuilder.Build(plan, results, admission, resourceResults, metrics: new EvidenceExecutionMetrics(
                    ResourceReadinessMilliseconds: resourceResults.Sum(static result => result.ElapsedMilliseconds),
                    CleanupCompleted: cleanup,
                    TerminalFailureCode: execution.TerminalCode != EvidenceWorkerTerminalCode.None
                        ? execution.TerminalCode.ToString()
                        : artifactsVerified ? null : nameof(EvidenceWorkerTerminalCode.StageFailed)));
                // All final output uses retained descriptors with exclusive names, after stopped work and cleanup.
                await WriteAndVerifyAsync(root!, "evidence-plan.json", EvidenceCanonicalJson.Serialize(plan), token).ConfigureAwait(false);
                await WriteAndVerifyAsync(root!, "evidence-manifest.json", EvidenceCanonicalJson.Serialize(manifest), token).ConfigureAwait(false);
                await WriteAndVerifyAsync(root!, "evidence-summary.json", EvidenceCanonicalJson.Serialize(new
                {
                    manifest.Mode, manifest.ClaimKind, manifest.Eligibility, manifest.ExecutionVerdict,
                    manifest.EnvelopeStatus, Procedure = "registered-protected-producer", SandboxAttestation = false,
                }), token).ConfigureAwait(false);
                await root!.DisposeAsync().ConfigureAwait(false);
                root = null;
                return manifest;
            }, CancellationToken.None).ConfigureAwait(false);
            budget.CompleteCollection();
            if (collected.Outcome != EvidenceWorkerStageOutcome.Passed || collected.Value is null)
                throw new EvidenceAdmissionException("ASEVD410", "Final collection failed; output remains ineligible for publication.");
            using var finalDeadline = new CancellationTokenSource(worker.JobRemaining < TimeSpan.FromSeconds(descriptor.StoppingSeconds)
                ? worker.JobRemaining : TimeSpan.FromSeconds(descriptor.StoppingSeconds));
            await worker.CompleteWorkerAsync(finalDeadline.Token).ConfigureAwait(false);
            return collected.Value;
        }
        catch
        {
            admission?.LatchFailure();
            await execution.StopAndDisposeAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            // Production FailFast never unwinds this finally; a live callback cannot race handle disposal.
            if (root is not null && execution.OwnWorkStopped) await root.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Lists serial CLI work before any allocation, startup or producer callback is admitted.</summary>
    /// <param name="profile">Protected resolved resource and producer declarations.</param>
    /// <param name="admissionSeconds">Admission and fresh allocation limits, charged separately.</param>
    /// <param name="applicationStartSeconds">Resolved application startup limit; null retains the v1 producer-only schedule.</param>
    /// <returns>A copied read-only schedule; this metadata grants no registration or execution authority.</returns>
    /// <remarks>The existing run budget validates every bound and preserves collection/cleanup reserves.</remarks>
    internal static IReadOnlyList<EvidenceRunStageDeadline> CreateDeclaredStages(EvidenceProfile profile,
        int admissionSeconds, int? applicationStartSeconds)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var stages = new List<EvidenceRunStageDeadline>
        {
            new(EvidenceRunStage.Admission, TimeSpan.FromSeconds(admissionSeconds)),
            new(EvidenceRunStage.Admission, TimeSpan.FromSeconds(admissionSeconds)),
        };
        if (applicationStartSeconds is { } startSeconds)
        {
            stages.Add(new(EvidenceRunStage.Start, TimeSpan.FromSeconds(startSeconds)));
            stages.AddRange(profile.Resources.Select(static resource =>
                new EvidenceRunStageDeadline(EvidenceRunStage.Resource, TimeSpan.FromSeconds(resource.DeadlineSeconds))));
        }
        stages.AddRange(profile.Producers.Select(static producer =>
            new EvidenceRunStageDeadline(EvidenceRunStage.Producer, TimeSpan.FromSeconds(producer.TimeoutSeconds))));
        return Array.AsReadOnly(stages.ToArray());
    }

    /// <summary>Collects copied readiness metadata only after authenticated application and resource stages pass.</summary>
    /// <param name="worker">Actual authenticated supervisor that owns the pending application lease.</param>
    /// <param name="admission">Active admission for the exact captured plan.</param>
    /// <param name="plan">Resolved resource declarations, in their serial budget order.</param>
    /// <param name="execution">Existing stage and owned-work lifecycle.</param>
    /// <param name="budget">Declared startup, readiness, producer and cleanup reserves.</param>
    /// <param name="application">Compile-owned application resolved before admission.</param>
    /// <param name="clock">The lifecycle's monotonic clock used to measure each readiness wait.</param>
    /// <param name="callerCancellation">Original caller cancellation, linked by each tracked stage.</param>
    /// <returns>A defensive read-only list; empty when the resolved application declares no resources.</returns>
    /// <remarks>Failed stages return no Ready result. Metadata never substitutes for root ACK validation or active admission.</remarks>
    private static async Task<IReadOnlyList<EvidenceResourceResult>> RunApplicationStagesAsync(EvidenceLinuxWorkerSupervisor worker,
        EvidenceAdmissionResult admission, EvidencePlan plan, EvidenceWorkerExecution execution,
        EvidenceRunTimeBudget budget, EvidenceClosedApplicationDefinition application,
        TimeProvider clock, CancellationToken callerCancellation)
    {
        if (!budget.TryBeginNextStage(callerCancellation, out var stage) || stage!.Stage != EvidenceRunStage.Start)
            throw new EvidenceAdmissionException("ASEVD421", "The protected application startup reserve is exhausted.");
        var started = await execution.ExecuteAsync(EvidenceRunStage.Start, stage.Duration, async token =>
        {
            admission.ValidateActive(plan);
            // The supervisor claims and retains the pending attempt before root I/O.
            // Existing stop/wait owns its physical exit even if this await fails.
            var receipt = await worker.StartApplicationAsync(application.Id,
                EvidenceClosedApplicationCatalogue.ComputeEntryDigest(application), token).ConfigureAwait(false);
            admission.ValidateActive(plan);
            return receipt;
        }, callerCancellation).ConfigureAwait(false);
        budget.CompleteCurrentStage();
        if (started.Outcome != EvidenceWorkerStageOutcome.Passed || started.Value is null)
            throw new EvidenceAdmissionException("ASEVD410", "Protected application startup did not complete.");

        IReadOnlyList<EvidenceResourceResult> results = Array.Empty<EvidenceResourceResult>();
        foreach (var resource in plan.Profile.Resources)
        {
            if (!budget.TryBeginNextStage(callerCancellation, out stage) || stage!.Stage != EvidenceRunStage.Resource)
                throw new EvidenceAdmissionException("ASEVD421", "The protected resource readiness reserve is exhausted.");
            var readinessStarted = clock.GetTimestamp();
            var ready = await execution.ExecuteAsync(EvidenceRunStage.Resource, stage.Duration, async token =>
            {
                admission.ValidateActive(plan);
                var receipt = await worker.WaitForApplicationResourceAsync(started.Value.LeaseId,
                    resource.Id, token).ConfigureAwait(false);
                admission.ValidateActive(plan);
                return receipt;
            }, callerCancellation).ConfigureAwait(false);
            budget.CompleteCurrentStage();
            if (ready.Outcome != EvidenceWorkerStageOutcome.Passed || ready.Value is null)
                throw new EvidenceAdmissionException("ASEVD410", "Protected resource readiness did not complete.");
            admission.ValidateActive(plan);
            results = AppendResourceReadinessResult(results, resource.Id, ready.Outcome, ready.Value, clock, readinessStarted);
        }
        return results;
    }

    /// <summary>Copies completed resource metadata and appends one successful stage's measured readiness result.</summary>
    /// <param name="completed">Previously completed results; the returned list never shares its mutable backing storage.</param>
    /// <param name="resourceId">Exact resource identifier from the resolved plan.</param>
    /// <param name="outcome">Actual tracked stage outcome; every non-Passed outcome rejects.</param>
    /// <param name="receipt">Typed resource receipt returned by the supervisor; null or a different resource rejects.</param>
    /// <param name="clock">The same monotonic clock as the lifecycle.</param>
    /// <param name="startedTimestamp">Timestamp taken immediately before the tracked resource wait.</param>
    /// <returns>A copied read-only list with one Ready result and nonnegative whole elapsed milliseconds.</returns>
    /// <remarks>
    /// This internal data projection grants no readiness authority. Production calls it only after the supervisor
    /// validates the root ACK and admission is rechecked; constructing equivalent metadata cannot replace either step.
    /// The measured interval excludes application startup and contributes to the manifest's cumulative readiness metric.
    /// </remarks>
    /// <exception cref="EvidenceAdmissionException">ASEVD410 when the stage has no matching successful receipt.</exception>
    internal static IReadOnlyList<EvidenceResourceResult> AppendResourceReadinessResult(
        IReadOnlyList<EvidenceResourceResult> completed, string resourceId, EvidenceWorkerStageOutcome outcome,
        EvidenceLinuxApplicationResourceReceipt? receipt, TimeProvider clock, long startedTimestamp)
    {
        ArgumentNullException.ThrowIfNull(completed);
        ArgumentNullException.ThrowIfNull(clock);
        if (outcome != EvidenceWorkerStageOutcome.Passed || receipt is null
            || !string.Equals(receipt.ResourceId, resourceId, StringComparison.Ordinal))
            throw new EvidenceAdmissionException("ASEVD410", "Protected resource readiness did not complete.");
        var elapsedMilliseconds = (long)Math.Max(0, clock.GetElapsedTime(startedTimestamp).TotalMilliseconds);
        EvidenceResourceResult[] snapshot = [.. completed,
            new EvidenceResourceResult(resourceId, EvidenceResourceOutcome.Ready, elapsedMilliseconds)];
        return Array.AsReadOnly(snapshot);
    }

    /// <summary>Reports only a failed allocation stage after owned exit; any optional sink failure is ignored.</summary>
    internal static void ReportAllocationFailure(EvidenceWorkerExecution execution, EvidenceWorkerStageOutcome outcome,
        EvidenceAllocationPhase phase, EvidenceLinuxArtifactAllocationOperation operation,
        Action<EvidenceAllocationFailureDiagnostic>? diagnosticSink)
    {
        if (diagnosticSink is null || outcome == EvidenceWorkerStageOutcome.Passed || !execution.OwnWorkStopped) return;
        try
        {
            diagnosticSink(CreateAllocationFailureDiagnostic(phase, operation, outcome, execution.TerminalCode, execution.TerminalException));
        }
        catch (Exception) { } // A diagnostic channel cannot replace the original ASEVD409 failure.
    }

    /// <summary>Maps internal failure facts to closed values, discarding all arbitrary exception content.</summary>
    internal static EvidenceAllocationFailureDiagnostic CreateAllocationFailureDiagnostic(EvidenceAllocationPhase phase,
        EvidenceLinuxArtifactAllocationOperation operation, EvidenceWorkerStageOutcome outcome,
        EvidenceWorkerTerminalCode terminalCode, Exception? error)
    {
        phase = Enum.IsDefined(phase) ? phase : EvidenceAllocationPhase.None;
        operation = Enum.IsDefined(operation) ? operation : EvidenceLinuxArtifactAllocationOperation.None;
        outcome = Enum.IsDefined(outcome) ? outcome : EvidenceWorkerStageOutcome.Failed;
        terminalCode = Enum.IsDefined(terminalCode) ? terminalCode : EvidenceWorkerTerminalCode.StageFailed;
        var errorClass = error switch
        {
            null => EvidenceAllocationErrorClass.None,
            OutOfMemoryException => EvidenceAllocationErrorClass.OutOfMemory,
            EvidenceAdmissionException => EvidenceAllocationErrorClass.Admission,
            IOException => EvidenceAllocationErrorClass.Io,
            UnauthorizedAccessException => EvidenceAllocationErrorClass.AccessDenied,
            PlatformNotSupportedException or NotSupportedException => EvidenceAllocationErrorClass.Unsupported,
            ArgumentException => EvidenceAllocationErrorClass.Argument,
            OperationCanceledException => EvidenceAllocationErrorClass.Cancelled,
            TimeoutException => EvidenceAllocationErrorClass.Timeout,
            InvalidOperationException => EvidenceAllocationErrorClass.InvalidOperation,
            _ => EvidenceAllocationErrorClass.Unknown,
        };
        var allocationOperation = phase == EvidenceAllocationPhase.Allocation
            && operation is not (EvidenceLinuxArtifactAllocationOperation.None or EvidenceLinuxArtifactAllocationOperation.ValidateArguments
                or EvidenceLinuxArtifactAllocationOperation.CheckPlatform or EvidenceLinuxArtifactAllocationOperation.Completed);
        int? errno = allocationOperation && error is IOException { InnerException: Win32Exception native }
            && native.NativeErrorCode is >= 1 and <= 4095 ? native.NativeErrorCode : null;
        if (allocationOperation && operation is (EvidenceLinuxArtifactAllocationOperation.OpenFilesystemRoot
            or EvidenceLinuxArtifactAllocationOperation.OpenParent or EvidenceLinuxArtifactAllocationOperation.CheckParentName
            or EvidenceLinuxArtifactAllocationOperation.OpenSlot or EvidenceLinuxArtifactAllocationOperation.CheckSlotName
            or EvidenceLinuxArtifactAllocationOperation.RecheckParentName)
            && error is PlatformNotSupportedException { InnerException: Win32Exception unsupported }
            && unsupported.NativeErrorCode is 1 or 22 or 38 or 95)
            errno = unsupported.NativeErrorCode;
        return new(phase, operation, outcome, terminalCode, errorClass, errno);
    }

    private static async ValueTask WriteAndVerifyAsync(EvidenceLinuxArtifactRoot root, string path, byte[] bytes,
        CancellationToken cancellationToken)
    {
        await root.WriteAsync(path, bytes, cancellationToken).ConfigureAwait(false);
        await root.VerifyAsync(path, bytes.Length, EvidenceDigest.Sha256(bytes), cancellationToken).ConfigureAwait(false);
    }
}
