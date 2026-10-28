using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

internal static partial class LinuxEmptyObservationExecution
{
    /// <summary>Private compile-selected projections composed only from the original native holders.</summary>
    /// <remarks>
    /// Constructed once by RunAsync, never supplied by a caller. Its ordinary methods complete without
    /// native work. Selected implementations retain the original run holders, tokens, diagnostic write
    /// order and sticky failure flags. The private record callback only captures the enclosing run's
    /// current first-fault stage; it cannot start work, issue admission, release custody or renew a bound.
    /// The shared execution state machine calls these hooks at fixed positions in every image.
    /// </remarks>
    private sealed class QualifiedObservation
    {
        private readonly EvidenceNativeObservationFailureLatch _failures;
#if EVIDENCE_PRIVATE_N07
        private bool _n07FailureProjectionWriteAttempted;
#endif
#if EVIDENCE_PRIVATE_N08 || EVIDENCE_PRIVATE_N09
        private bool _cancellationProjectionWritten;
        private bool _cancellationProjectionWriteAttempted;
        private LinuxCancellationProjectionStage _cancellationProjectionStage;
#endif
#if EVIDENCE_PRIVATE_N08 || EVIDENCE_PRIVATE_N09 || EVIDENCE_PRIVATE_N16
        private bool _accountsClosedUnderCustody;
#endif
#if EVIDENCE_PRIVATE_N08 || EVIDENCE_PRIVATE_N09
        private uint _cancellationResultsGid;
#endif
#if EVIDENCE_PRIVATE_N16
        private bool _acceptedWorkProjectionWritten;
        private bool _acceptedWorkProjectionWriteAttempted;
        private uint _acceptedWorkResultsGid;
        private LinuxN16ProgressDiagnostic.Snapshot? _n16ProgressDiagnostic;
#endif

        internal QualifiedObservation(EvidenceNativeObservationFailureLatch failures) => _failures = failures;

        /// <summary>Emits private SDK cleanup progress only; returning from an attempt does not mean success.</summary>
        internal void ObserveCleanupPhase(EvidenceNativeObservationPhase phase, bool returned)
        {
#if EVIDENCE_PRIVATE_C3_SDK
            try
            {
                Console.Error.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                {
                    schema = "evidence-sdk-root-cleanup-progress-v1",
                    phase = phase.ToString(),
                    boundary = returned ? "returned" : "entered",
                    native_acceptance = false
                }));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                or InvalidOperationException or System.Text.Json.JsonException)
            { } // Optional progress never replaces the original attempt, exception or join.
#endif
        }

        internal Task PrepareSlotAsync(LinuxRunWorkspace workspace, LinuxOwnerActivation owner,
            LinuxRunAccounts accounts, CancellationToken token)
        {
#if EVIDENCE_PRIVATE_N05 || EVIDENCE_PRIVATE_N06
            return PrepareSlotActualAsync(workspace, owner, accounts, token);
#else
            return Task.CompletedTask;
#endif
        }

        internal Task ObserveLeaderExitAsync(LinuxWorkerProcess worker, CancellationToken token)
        {
#if EVIDENCE_PRIVATE_N12
            return EvidenceNativeQualification.DescendantEnabled
                ? worker.ObserveLeaderExitWithDescendantAsync(token)
                : Task.CompletedTask;
#else
            return Task.CompletedTask;
#endif
        }

#if EVIDENCE_PRIVATE_N12
        internal async Task WriteJoinedDescendantAsync(LinuxWorkerProcess worker, LinuxOwnerActivation owner)
        {
            if (!EvidenceNativeQualification.DescendantEnabled) return;
            var bytes = worker.CaptureJoinedDescendantObservation();
            owner.RequireCleanup(default);
            using var error = Console.OpenStandardError();
            await error.WriteAsync(bytes, owner.RootTeardownToken).ConfigureAwait(false);
            await error.WriteAsync(new byte[] { (byte)'\n' }, owner.RootTeardownToken).ConfigureAwait(false);
            await error.FlushAsync(owner.RootTeardownToken).ConfigureAwait(false);
            owner.RequireCleanup(default);
        }

#else
        internal Task WriteJoinedDescendantAsync(LinuxWorkerProcess worker, LinuxOwnerActivation owner) => Task.CompletedTask;
#endif

        internal void MarkAccountsClosedUnderCustody()
        {
#if EVIDENCE_PRIVATE_N08 || EVIDENCE_PRIVATE_N09 || EVIDENCE_PRIVATE_N16
            _accountsClosedUnderCustody = true;
#endif
        }

#if EVIDENCE_PRIVATE_N04 || EVIDENCE_PRIVATE_N05 || EVIDENCE_PRIVATE_N06 || EVIDENCE_PRIVATE_N07 || EVIDENCE_PRIVATE_N08 || EVIDENCE_PRIVATE_N09 || EVIDENCE_PRIVATE_N10 || EVIDENCE_PRIVATE_N11 || EVIDENCE_PRIVATE_N16
        internal async Task<bool> CaptureFailureAsync(bool failed, bool cleanupFailed,
            EvidenceProtectedLaunchInput? input, LinuxOwnerActivation? owner, LinuxRunAccounts? accounts,
            LinuxRunWorkspace? workspace, LinuxWorkerProcess? worker, LinuxEmptyObservationControlServer? server,
            Task? serverTask, CancellationToken cleanupToken, CancellationToken callerToken,
            Action<Exception> record)
        {
#if EVIDENCE_PRIVATE_N05 || EVIDENCE_PRIVATE_N06
            if ((failed || cleanupFailed) && input is not null && owner is not null && accounts is not null
                && workspace is not null && worker is not null && server is not null)
                try
                {
                    var kernel = worker.CaptureNegativeObservation(input, owner, accounts, workspace, server, cleanupToken);
                    var caseId = EvidenceNativeQualification.Current == EvidenceNativeQualificationKind.SymlinkWorkerOutputSlot ? "N06" : "N05";
                    var joined = LinuxSlotNegativeObservation.EncodeJoinedOutput(caseId,
                        worker.Output ?? throw Rejected());
                    var inspection = workspace.CloseAndCaptureSlotNegativeFixture(cleanupToken);
                    await WriteSlotObservationAsync([inspection, joined.Projection, kernel.Bytes, joined.Raw],
                        owner, cleanupToken).ConfigureAwait(false);
                }
                catch (Exception error) when (Recoverable(error)) { record(error); cleanupFailed = true; }
#endif
#if EVIDENCE_PRIVATE_N07
            if (EvidenceNativeQualification.ParentReplacementEnabled && failed && !cleanupFailed
                && input is not null && owner is not null && accounts is not null
                && workspace is not null && worker is not null && server is not null)
                try
                {
                    var evidence = worker.CaptureFailureSettlement(input, owner, accounts, workspace, server, cleanupToken);
                    _n07FailureProjectionWriteAttempted = true;
                    await LinuxN07FailureEmitter.WriteAsync(evidence.Settlement, evidence.ReadyDescriptorSha256,
                        server.N07ExecutionStatus, cleanupToken).ConfigureAwait(false);
                }
                catch (Exception error) when (Recoverable(error)) { record(error); cleanupFailed = true; }
            if (EvidenceNativeQualification.ParentReplacementEnabled && (failed || cleanupFailed)
                && !_n07FailureProjectionWriteAttempted
                && input is not null && owner is not null && accounts is not null
                && workspace is not null && worker is not null && server is not null)
                try
                {
                    var observation = worker.CaptureN07RejectedFrameObservation(input, owner, accounts,
                        workspace, server, cleanupToken);
                    owner.RequireControlIdentity(cleanupToken);
                    cleanupToken.ThrowIfCancellationRequested();
                    _n07FailureProjectionWriteAttempted = true;
                    await Console.Error.WriteLineAsync(System.Text.Encoding.UTF8.GetString(observation.Bytes).AsMemory(),
                        cleanupToken).ConfigureAwait(false);
                    await Console.Error.FlushAsync(cleanupToken).ConfigureAwait(false);
                    owner.RequireControlIdentity(cleanupToken);
                    cleanupToken.ThrowIfCancellationRequested();
                }
                catch (Exception error) when (Recoverable(error)) { }
#endif
#if EVIDENCE_PRIVATE_N10
            if (EvidenceNativeQualification.PendingStartRaceEnabled && failed
                && input is not null && owner is not null && accounts is not null
                && workspace is not null && worker is not null)
                try
                {
                    var observation = worker.CaptureN10PendingStartObservation(input, owner, accounts,
                        workspace, cleanupToken);
                    using var stderr = Console.OpenStandardError();
                    await stderr.WriteAsync(observation.AsMemory(), cleanupToken).ConfigureAwait(false);
                    await stderr.WriteAsync(new byte[] { (byte)'\n' }.AsMemory(), cleanupToken).ConfigureAwait(false);
                    await stderr.FlushAsync(cleanupToken).ConfigureAwait(false);
                    owner.RequireControlIdentity(cleanupToken);
                }
                catch (Exception error) when (Recoverable(error)) { record(error); cleanupFailed = true; }
#endif
#if EVIDENCE_PRIVATE_N16
            if (EvidenceNativeQualification.AcceptedBlockedWorkEnabled && failed && !cleanupFailed
                && input is not null && owner is not null && accounts is not null
                && workspace is not null && worker is not null && server is not null)
                try
                {
                    var control = server.CaptureAcceptedBlockedWork(input, owner, accounts, workspace, worker, cleanupToken);
                    var kernel = worker.CaptureNegativeObservation(input, owner, accounts, workspace, server, cleanupToken);
                    _acceptedWorkResultsGid = accounts.RequireControlOwnedBy(owner, cleanupToken).ResultsGid;
                    cleanupToken.ThrowIfCancellationRequested();
                    _acceptedWorkProjectionWriteAttempted = true;
                    using (var stderr = Console.OpenStandardError())
                    {
                        await SupervisionRecordWrite.WriteLinesAsync(stderr, [control, kernel.Bytes], cleanupToken).ConfigureAwait(false);
                    }
                    cleanupToken.ThrowIfCancellationRequested();
                    owner.RequireControlIdentity(cleanupToken);
                    _acceptedWorkProjectionWritten = true;
                }
                catch (Exception error) when (Recoverable(error)) { record(error); cleanupFailed = true; }
            if (EvidenceNativeQualification.AcceptedBlockedWorkEnabled && !callerToken.IsCancellationRequested
                && (failed || cleanupFailed)
                && !_acceptedWorkProjectionWriteAttempted && server is not null && serverTask?.IsCompleted == true)
                try
                {
                    _n16ProgressDiagnostic = server.CaptureN16ProgressDiagnostic() with
                    {
                        Client = worker?.CaptureJoinedN16ClientProgress()
                    };
                }
                catch (Exception error) when (Recoverable(error)) { }
#endif
#if EVIDENCE_PRIVATE_N11
            if (EvidenceNativeQualification.WorkerStallEnabled && (failed || cleanupFailed)
                && input is not null && owner is not null && accounts is not null
                && workspace is not null && worker is not null && server is not null && serverTask is not null)
                try
                {
                    var observation = worker.CaptureFailedSettlementObservation(input, owner, accounts,
                        workspace, server, cleanupToken);
                    var bytes = observation.Bytes;
                    if (bytes.Length is 0 or > LinuxFailedSettlementObservation.MaximumJsonBytes) throw Rejected();
                    cleanupToken.ThrowIfCancellationRequested();
                    using (var stderr = Console.OpenStandardError())
                    {
                        await SupervisionRecordWrite.WriteLinesAsync(stderr, [bytes], cleanupToken).ConfigureAwait(false);
                    }
                    cleanupToken.ThrowIfCancellationRequested();
                    owner.RequireControlIdentity(cleanupToken);
                }
                catch (Exception error) when (Recoverable(error)) { record(error); cleanupFailed = true; }
#endif
#if EVIDENCE_PRIVATE_N04
            if (EvidenceNativeQualification.PeerReplacementEnabled && (failed || cleanupFailed) && input is not null && owner is not null && accounts is not null
                && workspace is not null && worker is not null && server is not null && serverTask is not null)
                try
                {
                    var observation = worker.CaptureNegativeObservation(input, owner, accounts, workspace, server, cleanupToken);
                    var bytes = observation.Bytes;
                    if (bytes.Length is 0 or > LinuxNegativeKernelObservation.MaximumJsonBytes) throw Rejected();
                    cleanupToken.ThrowIfCancellationRequested();
                    using (var stderr = Console.OpenStandardError())
                    {
                        await SupervisionRecordWrite.WriteLinesAsync(stderr, [bytes], cleanupToken).ConfigureAwait(false);
                    }
                    cleanupToken.ThrowIfCancellationRequested();
                    owner.RequireControlIdentity(cleanupToken);
                }
                catch (Exception error) when (Recoverable(error)) { record(error); cleanupFailed = true; }
#endif
#if EVIDENCE_PRIVATE_N09
            if (input is not null && owner is not null && accounts is not null
                && workspace is not null && worker is not null && server is not null)
                try
                {
                    var allocation = server.CaptureAndCloseN09AllocationSlot(
                        input, owner, accounts, workspace, worker, cleanupToken);
                    if (allocation is not null)
                    {
                        cleanupToken.ThrowIfCancellationRequested();
                        owner.RequireControlIdentity(cleanupToken);
                        using var stderr = Console.OpenStandardError();
                        await SupervisionRecordWrite.WriteLinesAsync(stderr, [allocation], cleanupToken).ConfigureAwait(false);
                        owner.RequireControlIdentity(cleanupToken);
                    }
                }
                catch (Exception error) when (Recoverable(error)) { record(error); cleanupFailed = true; }
#endif
#if EVIDENCE_PRIVATE_N08 || EVIDENCE_PRIVATE_N09
            if (EvidenceNativeQualification.CancellationEnabled && failed && input is not null && owner is not null && accounts is not null
                && workspace is not null && worker is not null && server is not null)
                try
                {
                    _cancellationResultsGid = accounts.RequireControlOwnedBy(owner, cleanupToken).ResultsGid;
                    var observed = worker.CaptureCancellationJoinedObservation(input, owner, accounts, workspace, server,
                        cleanupToken, ref _cancellationProjectionStage);
                    _cancellationProjectionStage = LinuxCancellationProjectionStage.SignalProvenance;
                    var signal = server.CaptureCancellationSignal(input, owner, accounts, workspace, worker, cleanupToken);
                    _cancellationProjectionWriteAttempted = true;
                    _cancellationProjectionStage = LinuxCancellationProjectionStage.SignalWrite;
                    await Console.Error.WriteLineAsync(signal).ConfigureAwait(false);
                    _cancellationProjectionStage = LinuxCancellationProjectionStage.KernelWrite;
                    await Console.Error.WriteLineAsync(System.Text.Encoding.UTF8.GetString(observed.Kernel.Bytes)).ConfigureAwait(false);
                    _cancellationProjectionStage = LinuxCancellationProjectionStage.JoinedStreamWrite;
                    cleanupToken.ThrowIfCancellationRequested();
                    await Console.Error.WriteLineAsync(System.Text.Encoding.UTF8.GetString(observed.JoinedStreams).AsMemory(),
                        cleanupToken).ConfigureAwait(false);
                    _cancellationProjectionStage = LinuxCancellationProjectionStage.FinalBound;
                    cleanupToken.ThrowIfCancellationRequested();
                    _cancellationProjectionWritten = true;
                }
                catch (Exception error) when (Recoverable(error))
                {
                    record(error); cleanupFailed = true;
                    try
                    {
                        owner.RequireControlIdentity(cleanupToken);
                        var bytes = LinuxCancellationProjectionFailure.EncodeDetached(owner.RunId,
                            _cancellationProjectionStage, error);
                        using var diagnostic = Console.OpenStandardError();
                        await SupervisionRecordWrite.WriteLinesAsync(diagnostic, [bytes], cleanupToken).ConfigureAwait(false);
                        owner.RequireControlIdentity(cleanupToken);
                    }
                    catch (Exception diagnosticError) when (Recoverable(diagnosticError)) { record(diagnosticError); }
                    if (!_cancellationProjectionWriteAttempted) try
                        {
                            owner.RequireControlIdentity(cleanupToken);
                            var diagnostic = worker.CaptureFailedSettlementObservation(input, owner, accounts,
                                workspace, server, cleanupToken).Bytes;
                            _cancellationProjectionWriteAttempted = true;
                            using var stderr = Console.OpenStandardError();
                            await SupervisionRecordWrite.WriteLinesAsync(stderr, [diagnostic], cleanupToken).ConfigureAwait(false);
                            owner.RequireControlIdentity(cleanupToken);
                        }
                        catch (Exception diagnosticError) when (Recoverable(diagnosticError))
                        {
                            record(diagnosticError);
                            if (!_cancellationProjectionWriteAttempted) try
                                {
                                    owner.RequireControlIdentity(cleanupToken);
                                    var diagnostic = worker.CaptureJoinedOutputDiagnostic(owner.RunId);
                                    _cancellationProjectionWriteAttempted = true;
                                    using var stderr = Console.OpenStandardError();
                                    await SupervisionRecordWrite.WriteLinesAsync(stderr, [diagnostic], cleanupToken).ConfigureAwait(false);
                                    owner.RequireControlIdentity(cleanupToken);
                                }
                                catch (Exception outputError) when (Recoverable(outputError)) { record(outputError); }
                        }
                }
#endif
            return cleanupFailed;
        }

#else
        internal Task<bool> CaptureFailureAsync(bool failed, bool cleanupFailed,
            EvidenceProtectedLaunchInput? input, LinuxOwnerActivation? owner, LinuxRunAccounts? accounts,
            LinuxRunWorkspace? workspace, LinuxWorkerProcess? worker, LinuxEmptyObservationControlServer? server,
            Task? serverTask, CancellationToken cleanupToken, CancellationToken callerToken,
            Action<Exception> record) => Task.FromResult(cleanupFailed);
#endif

        internal void ThrowCleanupFailure(bool cleanupFailed)
        {
            if (!cleanupFailed) return;
#if EVIDENCE_PRIVATE_N16
            throw _failures.RejectedWithN16Progress(_n16ProgressDiagnostic);
#else
            throw _failures.Rejected();
#endif
        }

        internal void ThrowFinalProjection(bool failed, EvidenceManifest? manifest, LinuxOwnerActivation? owner)
        {
#if EVIDENCE_PRIVATE_N08 || EVIDENCE_PRIVATE_N09
            if (failed && _cancellationProjectionWritten && _accountsClosedUnderCustody && owner is not null)
                throw _failures.Rejected(owner.RunId, _cancellationResultsGid);
#endif
#if EVIDENCE_PRIVATE_N16
            if (failed && _acceptedWorkProjectionWritten && _accountsClosedUnderCustody && owner is not null)
                throw _failures.RejectedWithN16Progress(_n16ProgressDiagnostic, owner.RunId, _acceptedWorkResultsGid,
                    LinuxNegativeCleanupKind.AcceptedBlockedWork);
            if (failed || manifest is null)
                throw _failures.RejectedWithN16Progress(_n16ProgressDiagnostic);
#endif
        }

#if EVIDENCE_PRIVATE_N05 || EVIDENCE_PRIVATE_N06
        private static async Task PrepareSlotActualAsync(LinuxRunWorkspace workspace, LinuxOwnerActivation owner,
            LinuxRunAccounts accounts, CancellationToken token)
        {
            var slotSetup = workspace.PrepareSlotNegativeFixture(owner, accounts, token);
            await WriteSlotObservationAsync([slotSetup], owner, token).ConfigureAwait(false);
        }

        private static async Task WriteSlotObservationAsync(byte[][] records, LinuxOwnerActivation owner, CancellationToken token)
        {
            owner.RequireControlIdentity(token);
            using var stderr = Console.OpenStandardError();
            foreach (var record in records)
            {
                token.ThrowIfCancellationRequested();
                await stderr.WriteAsync(record.AsMemory(), token).ConfigureAwait(false);
                await stderr.WriteAsync(new byte[] { (byte)'\n' }.AsMemory(), token).ConfigureAwait(false);
            }
            await stderr.FlushAsync(token).ConfigureAwait(false);
            owner.RequireControlIdentity(token);
        }
#endif

        /// <summary>Runs fixed reference-denial controls only in the authentic private C3 owner graph.</summary>
        /// <remarks>Ordinary images have no probe or callback; no owner is fabricated or accepted from metadata.</remarks>
        internal void CheckOwnerReferenceGuards(EvidenceProtectedLaunchInput input, LinuxOwnerActivation owner,
            LinuxRunAccounts accounts, LinuxRunWorkspace workspace, LinuxProducerWorkspace? producerWorkspace,
            LinuxApplicationWorkspace? applicationWorkspace, LinuxWorkerProcess worker,
            LinuxEmptyObservationControlServer server, LinuxControlListener listener, CancellationToken token)
        {
#if EVIDENCE_PRIVATE_C3
            LinuxWorkerMeasurement.CheckOwnerReferenceGuards(input, owner, accounts, workspace,
                producerWorkspace, applicationWorkspace, worker, server, listener, token);
#endif
        }

    }
}
