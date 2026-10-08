using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Composes the actual same-image root/worker checkpoint without a transport or owner injection seam.</summary>
/// <remarks>
/// The reserved supervisor role dispatches this internal native checkpoint candidate. It requires a
/// pre-CLR sanitized, independently terminating OS owner, retained protected inputs and atomic activation.
/// It enrolls no producers or applications and grants no Trusted claim or accepted consumer proof.
/// Every original server, worker and account operation is joined; failed preparation preserves paths and
/// reusable identities for quarantine when complete native root custody cannot be established.
/// </remarks>
internal static class LinuxEmptyObservationExecution
{
    /// <summary>Runs the genuine empty targeted Observation and returns verified data only after strict final cleanup.</summary>
    /// <param name="requestPath">The protected request selected by the authenticated OS bootstrap command.</param>
    /// <param name="token">Caller cancellation; independent cleanup uses the original owner/job reserve.</param>
    /// <returns>Detached informational Observation manifest data after normal exit and all required final closes.</returns>
    /// <exception cref="EvidenceAdmissionException">Fixed ASEVD402 for unsupported/unprivileged entry,
    /// before protected execution begins.</exception>
    /// <exception cref="EvidenceNativeObservationException">Fixed ASEVD410 with closed first-fault data when execution or cleanup fails.</exception>
    /// <exception cref="OperationCanceledException">Original caller cancellation after all cleanup attempts join.</exception>
    /// <remarks>
    /// Plan resolution precedes account creation. A successful EXIT ACK is followed by the worker's original
    /// natural exit monitor before unit stop, so root does not terminate a worker between acknowledgment and
    /// normal return. Final root custody seals all account-associated nodes before reusable identities close.
    /// Uncertain joins do not return a positive result. No recursive output deletion or per-stage deadline reset
    /// occurs; the external owner retains physical containment if managed/native code stops making progress.
    /// </remarks>
    internal static async Task<EvidenceManifest> RunAsync(string requestPath, CancellationToken token = default)
    {
        // Unsupported or unprivileged invocation rejects before any protected input, account or unit I/O.
        LinuxProtectedDeployment.RequirePlatform();
        EvidenceProtectedLaunchInput? input = null;
        LinuxSystemdBackend? backend = null;
        LinuxOwnerActivation? owner = null;
        LinuxRunAccounts? accounts = null;
        LinuxRunWorkspace? workspace = null;
        LinuxControlListener? listener = null;
        LinuxWorkerProcess? worker = null;
        LinuxEmptyObservationControlServer? server = null;
        LinuxRunWorkspace.RootCustody? custody = null;
        CancellationTokenSource? job = null;
        CancellationTokenSource? serverLifetime = null;
        Task? serverTask = null;
        EvidenceManifest? manifest = null;
        var failures = new EvidenceNativeObservationFailureLatch();
        var phase = EvidenceNativeObservationPhase.Unknown;
        void Record(Exception error)
        {
            try { failures.Capture(phase, error,
                phase is EvidenceNativeObservationPhase.ServerRun or EvidenceNativeObservationPhase.ServerCompletion
                    ? server?.FirstFailure : null,
                phase is EvidenceNativeObservationPhase.Custody or EvidenceNativeObservationPhase.CleanupCustody
                    or EvidenceNativeObservationPhase.FileVerification or EvidenceNativeObservationPhase.AccountsClose
                    ? workspace?.FirstCustodyFailure : null); }
            catch (Exception) { } // Diagnostic capture cannot replace the actual execution/cleanup error.
        }
        var failed = false;
        var cleanupFailed = false;
        var cancellationProjectionWritten = false;
        var cancellationProjectionWriteAttempted = false;
        var cancellationProjectionStage = LinuxCancellationProjectionStage.Unknown;
        var accountsClosedUnderCustody = false;
        uint cancellationResultsGid = 0;
#if EVIDENCE_PRIVATE_N16
        var acceptedWorkProjectionWritten = false;
        uint acceptedWorkResultsGid = 0;
#endif
        var cleanupToken = CancellationToken.None;
        long finalCloseStarted = 0;
        TimeSpan finalCloseAllowance = TimeSpan.Zero;
        try
        {
            phase = EvidenceNativeObservationPhase.CallerCancellation;
            token.ThrowIfCancellationRequested();
            phase = EvidenceNativeObservationPhase.ProtectedInput;
            input = EvidenceProtectedLaunchInput.Open(requestPath, token);
            phase = EvidenceNativeObservationPhase.JobDeadline;
            job = CancellationTokenSource.CreateLinkedTokenSource(token);
            job.CancelAfter(input.Remaining);
            phase = EvidenceNativeObservationPhase.BackendConnect;
            backend = await LinuxSystemdBackend.ConnectAsync(job.Token).ConfigureAwait(false);
            phase = EvidenceNativeObservationPhase.OwnerActivation;
            owner = await LinuxOwnerActivation.OpenAsync(input, backend, job.Token).ConfigureAwait(false);
            phase = EvidenceNativeObservationPhase.Plan;
            _ = EvidenceEmptyObservationPlan.FromInput(input, job.Token);
            phase = EvidenceNativeObservationPhase.AccountCreate;
            accounts = await LinuxRunAccounts.CreateAsync(owner, job.Token).ConfigureAwait(false);
            phase = EvidenceNativeObservationPhase.WorkspaceCreate;
            workspace = LinuxRunWorkspace.Create(owner, accounts, job.Token);
            phase = EvidenceNativeObservationPhase.ListenerBind;
            listener = LinuxControlListener.Bind(owner, accounts, workspace, job.Token);
            phase = EvidenceNativeObservationPhase.WorkerCreate;
            worker = LinuxWorkerProcess.Create(input, owner, accounts, workspace, listener, job.Token);
            phase = EvidenceNativeObservationPhase.WorkerStart;
            await worker.StartAsync(job.Token).ConfigureAwait(false);
            phase = EvidenceNativeObservationPhase.ServerCreate;
            server = LinuxEmptyObservationControlServer.Create(input, owner, accounts, workspace, listener, worker, job.Token);
            phase = EvidenceNativeObservationPhase.ServerLifetime;
            serverLifetime = CancellationTokenSource.CreateLinkedTokenSource(job.Token);
            phase = EvidenceNativeObservationPhase.ServerRun;
            serverTask = server.RunAsync(serverLifetime.Token);
            await serverTask.ConfigureAwait(false);
            phase = EvidenceNativeObservationPhase.ServerCompletion;
            server.RequireSuccessfulCompletion(job.Token);
            phase = EvidenceNativeObservationPhase.WorkerExit;
            await worker.WaitForExitAsync().ConfigureAwait(false);
            if (EvidenceNativeQualification.DescendantEnabled)
                await worker.ObserveLeaderExitWithDescendantAsync(job.Token).ConfigureAwait(false);
            phase = EvidenceNativeObservationPhase.WorkerStop;
            await worker.StopAndJoinAsync().ConfigureAwait(false);
            phase = EvidenceNativeObservationPhase.WorkerCompletion;
            worker.RequireSuccessfulCompletion();
            if (EvidenceNativeQualification.DescendantEnabled)
            {
                var bytes = worker.CaptureJoinedDescendantObservation();
                owner.RequireCleanup(default);
                using var error = Console.OpenStandardError();
                await error.WriteAsync(bytes, owner.RootTeardownToken).ConfigureAwait(false);
                await error.WriteAsync(new byte[] { (byte)'\n' }, owner.RootTeardownToken).ConfigureAwait(false);
                await error.FlushAsync(owner.RootTeardownToken).ConfigureAwait(false);
                owner.RequireCleanup(default);
            }
            phase = EvidenceNativeObservationPhase.BeginTeardown;
            owner.BeginRootTeardown();
            cleanupToken = owner.RootTeardownToken;
            phase = EvidenceNativeObservationPhase.Custody;
            custody = await workspace.TakeRootCustodyAsync(input, owner, accounts, worker, server, cleanupToken).ConfigureAwait(false);
            phase = EvidenceNativeObservationPhase.FileVerification;
            manifest = custody.VerifyObservationFiles(cleanupToken);
        }
        catch (Exception error) when (Recoverable(error)) { Record(error); failed = true; }
        finally
        {
            if (owner is not null)
            {
                try { phase = EvidenceNativeObservationPhase.CleanupBegin; owner.BeginRootTeardown(); cleanupToken = owner.RootTeardownToken; }
                catch (Exception error) when (Recoverable(error)) { Record(error); cleanupFailed = true; }
            }
            // Interrupt actual I/O before joining original tasks. This code runs outside every handler.
            try { phase = EvidenceNativeObservationPhase.ServerCancel; serverLifetime?.Cancel(); }
            catch (Exception error) when (Recoverable(error)) { Record(error); cleanupFailed = true; }
            if (listener is not null)
                try { phase = EvidenceNativeObservationPhase.ListenerClose; await listener.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) when (Recoverable(error)) { Record(error); cleanupFailed = true; }
            if (serverTask is not null)
                try { phase = EvidenceNativeObservationPhase.ServerJoin; await serverTask.ConfigureAwait(false); }
                catch (Exception error) when (Recoverable(error)) { Record(error); failed = true; }
            if (worker is not null)
                try { phase = EvidenceNativeObservationPhase.WorkerJoin; await worker.StopAndJoinAsync().ConfigureAwait(false); }
                catch (Exception error) when (Recoverable(error)) { Record(error); cleanupFailed = true; }
            if (EvidenceNativeQualification.ParentReplacementEnabled && failed && !cleanupFailed
                && input is not null && owner is not null && accounts is not null
                && workspace is not null && worker is not null && server is not null)
                try
                {
                    var evidence = worker.CaptureFailureSettlement(input, owner, accounts, workspace, server, cleanupToken);
                    await LinuxN07FailureEmitter.WriteAsync(evidence.Settlement, evidence.ReadyDescriptorSha256,
                        server.N07ExecutionStatus, cleanupToken).ConfigureAwait(false);
                }
                catch (Exception error) when (Recoverable(error)) { Record(error); cleanupFailed = true; }
#if EVIDENCE_PRIVATE_N10
            // Emit only after the original worker lifetime returned from its joined two-stop/finalization path.
            // The record retains sticky cancellation and reports its physical-settlement projection verbatim.
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
                catch (Exception error) when (Recoverable(error)) { Record(error); cleanupFailed = true; }
#endif
#if EVIDENCE_PRIVATE_N16
            // The negative private control exports only actual past authenticated writes and
            // original joined task/kernel data. It never replaces the worker's nonzero failure.
            if (EvidenceNativeQualification.AcceptedBlockedWorkEnabled && failed && !cleanupFailed
                && input is not null && owner is not null && accounts is not null
                && workspace is not null && worker is not null && server is not null)
                try
                {
                    var control = server.CaptureAcceptedBlockedWork(input, owner, accounts, workspace, worker, cleanupToken);
                    var kernel = worker.CaptureNegativeObservation(input, owner, accounts, workspace, server, cleanupToken);
                    acceptedWorkResultsGid = accounts.RequireControlOwnedBy(owner, cleanupToken).ResultsGid;
                    cleanupToken.ThrowIfCancellationRequested();
                    using (var stderr = Console.OpenStandardError())
                    {
                        foreach (var bytes in new[] { control, kernel.Bytes })
                        {
                            await stderr.WriteAsync(bytes.AsMemory(), cleanupToken).ConfigureAwait(false);
                            await stderr.WriteAsync(new byte[] { (byte)'\n' }, cleanupToken).ConfigureAwait(false);
                        }
                        await stderr.FlushAsync(cleanupToken).ConfigureAwait(false);
                    }
                    cleanupToken.ThrowIfCancellationRequested();
                    owner.RequireControlIdentity(cleanupToken);
                    acceptedWorkProjectionWritten = true;
                }
                catch (Exception error) when (Recoverable(error)) { Record(error); cleanupFailed = true; }
#endif
            // Private N11 failure-only data comes from the original joined holders before custody
            // and account closure. Capture failure never clears or replaces the execution failure.
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
                        await stderr.WriteAsync(bytes.AsMemory(), cleanupToken).ConfigureAwait(false);
                        await stderr.WriteAsync(new byte[] { (byte)'\n' }, cleanupToken).ConfigureAwait(false);
                        await stderr.FlushAsync(cleanupToken).ConfigureAwait(false);
                    }
                    cleanupToken.ThrowIfCancellationRequested();
                    owner.RequireControlIdentity(cleanupToken);
                }
                catch (Exception error) when (Recoverable(error)) { Record(error); cleanupFailed = true; }
            // Fixed private N04 image: copy facts only from the original joined holders, before
            // substituted-path custody can reject. This record cannot turn the failed run positive.
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
                        await stderr.WriteAsync(bytes.AsMemory(), cleanupToken).ConfigureAwait(false);
                        await stderr.WriteAsync(new byte[] { (byte)'\n' }, cleanupToken).ConfigureAwait(false);
                        await stderr.FlushAsync(cleanupToken).ConfigureAwait(false);
                    }
                    cleanupToken.ThrowIfCancellationRequested();
                    owner.RequireControlIdentity(cleanupToken);
                }
                catch (Exception error) when (Recoverable(error)) { Record(error); cleanupFailed = true; }
#if EVIDENCE_PRIVATE_N09
            // The actual workspace holder was acquired before the original SIGINT. After
            // server/worker joins, recheck the empty slot and close both retained descriptors
            // before root custody can release the generated accounts.
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
                        await stderr.WriteAsync(allocation.AsMemory(), cleanupToken).ConfigureAwait(false);
                        await stderr.WriteAsync(new byte[] { (byte)'\n' }, cleanupToken).ConfigureAwait(false);
                        await stderr.FlushAsync(cleanupToken).ConfigureAwait(false);
                        owner.RequireControlIdentity(cleanupToken);
                    }
                }
                catch (Exception error) when (Recoverable(error)) { Record(error); cleanupFailed = true; }
#endif
            // Fixed private cancellation image: original holder projection after server/native/pump joins.
            if (EvidenceNativeQualification.CancellationEnabled && failed && input is not null && owner is not null && accounts is not null
                && workspace is not null && worker is not null && server is not null)
                try
                {
                    cancellationResultsGid = accounts.ResultsGid;
                    var observed = worker.CaptureCancellationJoinedObservation(input, owner, accounts, workspace, server,
                        cleanupToken, ref cancellationProjectionStage);
                    cancellationProjectionStage = LinuxCancellationProjectionStage.SignalProvenance;
                    var signal = server.CaptureCancellationSignal(input, owner, accounts, workspace, worker, cleanupToken);
                    // Even a faulted write may have emitted a partial line. Do not append another
                    // potentially large projection once any original output write was attempted.
                    cancellationProjectionWriteAttempted = true;
                    cancellationProjectionStage = LinuxCancellationProjectionStage.SignalWrite;
                    await Console.Error.WriteLineAsync(signal).ConfigureAwait(false);
                    cancellationProjectionStage = LinuxCancellationProjectionStage.KernelWrite;
                    await Console.Error.WriteLineAsync(System.Text.Encoding.UTF8.GetString(observed.Kernel.Bytes)).ConfigureAwait(false);
                    cancellationProjectionStage = LinuxCancellationProjectionStage.JoinedStreamWrite;
                    cleanupToken.ThrowIfCancellationRequested();
                    await Console.Error.WriteLineAsync(System.Text.Encoding.UTF8.GetString(observed.JoinedStreams).AsMemory(),
                        cleanupToken).ConfigureAwait(false);
                    cancellationProjectionStage = LinuxCancellationProjectionStage.FinalBound;
                    cleanupToken.ThrowIfCancellationRequested();
                    cancellationProjectionWritten = true;
                }
                catch (Exception error) when (Recoverable(error))
                {
                    Record(error); cleanupFailed = true;
                    // This separate, bounded checkpoint explains a later capture failure without
                    // replacing the earlier server failure or any original settlement predicate.
                    try
                    {
                        owner.RequireControlIdentity(cleanupToken);
                        var bytes = LinuxCancellationProjectionFailure.EncodeDetached(owner.RunId,
                            cancellationProjectionStage, error);
                        using var diagnostic = Console.OpenStandardError();
                        await diagnostic.WriteAsync(bytes, cleanupToken).ConfigureAwait(false);
                        await diagnostic.WriteAsync(new byte[] { (byte)'\n' }, cleanupToken).ConfigureAwait(false);
                        await diagnostic.FlushAsync(cleanupToken).ConfigureAwait(false);
                        owner.RequireControlIdentity(cleanupToken);
                    }
                    catch (Exception diagnosticError) when (Recoverable(diagnosticError)) { Record(diagnosticError); }
                    // A joined nonzero worker can lack the successful negative-kernel projection.
                    // Retain the original post-pump unit/group and lifetime state if READY committed.
                    // A pre-READY or otherwise unjoined failure keeps the existing pump-only fallback.
                    // Neither diagnostic replaces the first fault or upgrades settlement/cleanup.
                    if (!cancellationProjectionWriteAttempted) try
                    {
                        owner.RequireControlIdentity(cleanupToken);
                        var diagnostic = worker.CaptureFailedSettlementObservation(input, owner, accounts,
                            workspace, server, cleanupToken).Bytes;
                        cancellationProjectionWriteAttempted = true; // A partial write forbids a second large record.
                        using var stderr = Console.OpenStandardError();
                        await stderr.WriteAsync(diagnostic, cleanupToken).ConfigureAwait(false);
                        await stderr.WriteAsync(new byte[] { (byte)'\n' }, cleanupToken).ConfigureAwait(false);
                        await stderr.FlushAsync(cleanupToken).ConfigureAwait(false);
                        owner.RequireControlIdentity(cleanupToken);
                    }
                    catch (Exception diagnosticError) when (Recoverable(diagnosticError))
                    {
                        Record(diagnosticError);
                        if (!cancellationProjectionWriteAttempted) try
                        {
                            owner.RequireControlIdentity(cleanupToken);
                            var diagnostic = worker.CaptureJoinedOutputDiagnostic(owner.RunId);
                            cancellationProjectionWriteAttempted = true;
                            using var stderr = Console.OpenStandardError();
                            await stderr.WriteAsync(diagnostic, cleanupToken).ConfigureAwait(false);
                            await stderr.WriteAsync(new byte[] { (byte)'\n' }, cleanupToken).ConfigureAwait(false);
                            await stderr.FlushAsync(cleanupToken).ConfigureAwait(false);
                            owner.RequireControlIdentity(cleanupToken);
                        }
                        catch (Exception outputError) when (Recoverable(outputError)) { Record(outputError); }
                    }
                }
            if (custody is null && input is not null && owner is not null && accounts is not null
                && workspace is not null && worker is not null && server is not null)
                try
                {
                    phase = EvidenceNativeObservationPhase.CleanupCustody;
                    custody = await workspace.TakeRootCustodyAsync(input, owner, accounts, worker, server, cleanupToken)
                        .ConfigureAwait(false);
                }
                catch (Exception error) when (Recoverable(error)) { Record(error); cleanupFailed = true; }
            if (custody is not null)
                try
                {
                    phase = EvidenceNativeObservationPhase.AccountsClose;
                    await custody.CloseAccountsAsync(cleanupToken).ConfigureAwait(false);
                    accountsClosedUnderCustody = true;
                }
                catch (Exception error) when (Recoverable(error)) { Record(error); cleanupFailed = true; }
            else if (accounts is not null)
                try { phase = EvidenceNativeObservationPhase.AccountsClose; await accounts.CloseAsync(cleanupToken).ConfigureAwait(false); }
                catch (Exception error) when (Recoverable(error)) { Record(error); cleanupFailed = true; }
            // Transfer already closes original owners. Rejoining their same tasks is harmless and ensures
            // earlier setup failures also attempt every local close, without bypassing account custody.
            if (worker is not null)
                try { phase = EvidenceNativeObservationPhase.WorkerClose; await worker.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) when (Recoverable(error)) { Record(error); cleanupFailed = true; }
            try { phase = EvidenceNativeObservationPhase.CustodyClose; custody?.Dispose(); }
            catch (Exception error) when (Recoverable(error)) { Record(error); cleanupFailed = true; }
            try { phase = EvidenceNativeObservationPhase.WorkspaceClose; workspace?.Dispose(); }
            catch (Exception error) when (Recoverable(error)) { Record(error); cleanupFailed = true; }
            if (owner is not null)
                try
                {
                    phase = EvidenceNativeObservationPhase.OwnerFinalCheck;
                    cleanupToken.ThrowIfCancellationRequested();
                    owner.RequireControlIdentity(cleanupToken);
                    finalCloseStarted = TimeProvider.System.GetTimestamp();
                    finalCloseAllowance = owner.CleanupRemaining;
                }
                catch (Exception error) when (Recoverable(error)) { Record(error); cleanupFailed = true; }
            try { phase = EvidenceNativeObservationPhase.ServerLifetimeClose; serverLifetime?.Dispose(); }
            catch (Exception error) when (Recoverable(error)) { Record(error); cleanupFailed = true; }
            try { phase = EvidenceNativeObservationPhase.JobClose; job?.Dispose(); }
            catch (Exception error) when (Recoverable(error)) { Record(error); cleanupFailed = true; }
            try { phase = EvidenceNativeObservationPhase.OwnerClose; owner?.Dispose(); }
            catch (Exception error) when (Recoverable(error)) { Record(error); cleanupFailed = true; }
            try { phase = EvidenceNativeObservationPhase.InputClose; input?.Dispose(); }
            catch (Exception error) when (Recoverable(error)) { Record(error); cleanupFailed = true; }
            try { phase = EvidenceNativeObservationPhase.BackendClose; backend?.Dispose(); }
            catch (Exception error) when (Recoverable(error)) { Record(error); cleanupFailed = true; }
            // Native owners are now closed, so bind the final publication check to the remaining
            // monotonic interval captured immediately before their closes; no new allowance is created.
            // Disposing the owner deliberately cancels borrowers; that expected cancellation is not
            // evidence of expiry. Check only the independent captured interval after those closes.
            if (owner is not null)
                try { phase = EvidenceNativeObservationPhase.FinalDeadline; RequireFinalClose(TimeProvider.System, finalCloseStarted, finalCloseAllowance); }
                catch (Exception error) when (Recoverable(error)) { Record(error); cleanupFailed = true; }
        }
        if (cleanupFailed) throw failures.Rejected();
        token.ThrowIfCancellationRequested();
        // The optional diagnostic is attached only after every original cleanup and deadline check.
        // No earlier first-fault projection can conceal a failed finalization behind this record.
        if (failed && cancellationProjectionWritten && accountsClosedUnderCustody && owner is not null)
            throw failures.Rejected(owner.RunId, cancellationResultsGid);
#if EVIDENCE_PRIVATE_N16
        if (failed && acceptedWorkProjectionWritten && accountsClosedUnderCustody && owner is not null)
            throw failures.Rejected(owner.RunId, acceptedWorkResultsGid, LinuxNegativeCleanupKind.AcceptedBlockedWork);
#endif
        if (failed || manifest is null) throw failures.Rejected();
        return manifest;
    }

    /// <summary>Encodes fixed private cancellation cleanup data; it creates no native ownership or completion capability.</summary>
    /// <param name="generation">Original generation data; native composition supplies the actual owner generation.</param>
    /// <param name="resultsGid">Original nonroot results group ID, captured before account cleanup changes NSS.</param>
    /// <returns>Bounded JSON with no raw exception, path, message, admission or successful-run claim.</returns>
    /// <exception cref="EvidenceAdmissionException">Fixed ASEVD410 for empty generation or reserved group data.</exception>
    /// <remarks>
    /// Detached calls prove encoding only. RunAsync emits these bytes solely after the original root
    /// custody account-close task, every local-owner close and final remaining-interval check succeed.
    /// Native validation must separately bind the exact root image and process exit, require root-owned
    /// terminal filesystem custody and independently observe all generated NSS names and IDs absent.
    /// </remarks>
    internal static string EncodeCancellationCleanupDetached(Guid generation, uint resultsGid)
        => EncodeNegativeCleanupDetached(generation, resultsGid, LinuxNegativeCleanupKind.Cancellation);

    /// <summary>Encodes one closed failure-only cleanup record; detached calls establish encoding only.</summary>
    /// <param name="generation">Original owner generation data, not a caller capability.</param>
    /// <param name="resultsGid">Original nonreserved results group, copied before account deletion.</param>
    /// <param name="kind">One of the two closed negative cleanup diagnostic families.</param>
    /// <returns>Bounded fixed-schema JSON with no raw error or native authority.</returns>
    /// <exception cref="EvidenceAdmissionException">Fixed ASEVD410 for invalid generation, group or kind.</exception>
    /// <remarks>
    /// The cancellation schema remains unchanged. Accepted-work cleanup is a separate schema required
    /// in addition to its pre-custody event and kernel records. Native composition alone attaches it
    /// after strict original account/handle closure and the final captured monotonic deadline check.
    /// An expected nonzero root exit without this record cannot prove successful negative-run cleanup.
    /// </remarks>
    internal static string EncodeNegativeCleanupDetached(Guid generation, uint resultsGid, LinuxNegativeCleanupKind kind)
    {
        if (generation == Guid.Empty || resultsGid is 0 or uint.MaxValue || !Enum.IsDefined(kind)) throw Rejected();
        return System.Text.Encoding.UTF8.GetString(EvidenceCanonicalJson.Serialize(new
        {
            schema = kind == LinuxNegativeCleanupKind.Cancellation
                ? "issue779-cancellation-root-cleanup-v1" : "issue779-accepted-work-root-cleanup-v1",
            generation = generation.ToString("N"),
            results_gid = resultsGid,
            accounts_closed = true,
            root_custody_closed = true,
            original_owners_closed = true,
            observation_only = true,
            native_authority = false,
            native_acceptance = false
        }));
    }

    /// <summary>Checks captured timing data after native owners close; it neither renews a deadline nor issues authority.</summary>
    /// <remarks>
    /// Native composition supplies the original monotonic clock and remaining interval immediately before
    /// closing its owner. Deliberate owner disposal cancellation is not an expiry signal. Portable clock
    /// controls cannot establish native completion, account release, admission or a successful run.
    /// </remarks>
    internal static void RequireFinalClose(TimeProvider clock, long startedAt, TimeSpan remaining,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (clock is null || remaining <= TimeSpan.Zero || remaining > TimeSpan.FromSeconds(660)) throw Rejected();
        var elapsed = clock.GetElapsedTime(startedAt);
        if (elapsed < TimeSpan.Zero || elapsed >= remaining) throw Rejected();
        token.ThrowIfCancellationRequested();
    }

    private static bool Recoverable(Exception error) =>
        error is not (OutOfMemoryException or StackOverflowException or AccessViolationException);
    private static EvidenceAdmissionException Rejected() =>
        new("ASEVD410", "The protected empty Observation execution or final cleanup could not be established.");
}
