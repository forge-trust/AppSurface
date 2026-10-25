using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Composes the actual same-image root/worker checkpoint without a transport or owner injection seam.</summary>
/// <remarks>
/// The reserved supervisor role dispatches this internal native checkpoint candidate. It requires a
/// pre-CLR sanitized, independently terminating OS owner, retained protected inputs and atomic activation.
/// It supports empty and coverage Observations plus compile-selected restricted applications. Runtime
/// application enrollment, Trusted claims and accepted consumer proof remain unsupported.
/// Every original server, worker and account operation is joined; failed preparation preserves paths and
/// reusable identities for quarantine when complete native root custody cannot be established.
/// </remarks>
internal static partial class LinuxEmptyObservationExecution
{
    /// <summary>Runs the protected targeted empty, coverage, or compiled-application Observation and returns data only after strict final cleanup.</summary>
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
        LinuxProducerWorkspace? producerWorkspace = null;
        LinuxApplicationWorkspace? applicationWorkspace = null;
        EvidenceApplicationSelection? applicationSelection = null;
        SupervisionRunOutputBudget? runOutput = null;
        CancellationTokenRegistration outputStop = default;
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
            try
            {
                failures.Capture(phase, error,
                phase is EvidenceNativeObservationPhase.ServerRun or EvidenceNativeObservationPhase.ServerCompletion
                    ? server?.FirstFailure : null,
                phase is EvidenceNativeObservationPhase.Custody or EvidenceNativeObservationPhase.CleanupCustody
                    or EvidenceNativeObservationPhase.FileVerification or EvidenceNativeObservationPhase.AccountsClose
                    ? workspace?.FirstCustodyFailure : null);
            }
            catch (Exception) { } // Diagnostic capture cannot replace the actual execution/cleanup error.
        }
        var failed = false;
        var cleanupFailed = false;
        var qualification = new QualifiedObservation(failures);
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
            var coverage = input.Request.ObservationProducerIds.Count != 0;
            if (coverage)
            {
                if (EvidenceNativeQualification.Current is not (EvidenceNativeQualificationKind.None
                    or EvidenceNativeQualificationKind.C2ArtifactMutation)) throw Rejected();
                _ = input.Request.ApplicationId is null ? EvidenceCoverageObservationPlan.FromInput(input, job.Token) : EvidenceApplicationSelection.ResolvePlan(input, job.Token);
                input.PrepareCoverageInputs(job.Token);
                runOutput = new SupervisionRunOutputBudget(drainOnQuotaExceeded: true);
                // A quota failure closes the original job, causing the actual server/worker and producer
                // ownership paths to stop. The counter remains alive until all original pumps join.
                outputStop = runOutput.StopToken.UnsafeRegister(static state => ((CancellationTokenSource)state!).Cancel(), job);
            }
            else _ = EvidenceEmptyObservationPlan.FromInput(input, job.Token);
            phase = EvidenceNativeObservationPhase.AccountCreate;
            accounts = await LinuxRunAccounts.CreateAsync(owner, job.Token, includeApplication: input.Request.ApplicationId is not null).ConfigureAwait(false);
            phase = EvidenceNativeObservationPhase.WorkspaceCreate;
            workspace = LinuxRunWorkspace.Create(owner, accounts, job.Token);
            if (coverage) producerWorkspace = LinuxProducerWorkspace.Create(input, owner, accounts, job.Token);
            if (input.Request.ApplicationId is not null)
            {
                applicationSelection = EvidenceApplicationSelection.Create(input, owner, accounts, job.Token);
                applicationWorkspace = LinuxApplicationWorkspace.Create(input, owner, accounts,
                    applicationSelection.Definition, applicationSelection.EntryDigest, job.Token);
            }
            await qualification.PrepareSlotAsync(workspace, owner, accounts, job.Token).ConfigureAwait(false);
            phase = EvidenceNativeObservationPhase.ListenerBind;
            listener = LinuxControlListener.Bind(owner, accounts, workspace, job.Token);
            phase = EvidenceNativeObservationPhase.WorkerCreate;
            worker = LinuxWorkerProcess.Create(input, owner, accounts, workspace, listener, job.Token, runOutput, producerWorkspace, applicationSelection?.Descriptor);
            // Complete protected plan/bundle/owner preparation before launching the worker. Its
            // authentication allowance starts at connect and must not pay for this root-only work.
            phase = EvidenceNativeObservationPhase.ServerCreate;
            server = LinuxEmptyObservationControlServer.Create(input, owner, accounts, workspace, listener, worker, job.Token,
                producerWorkspace, runOutput, applicationWorkspace, applicationSelection);
            phase = EvidenceNativeObservationPhase.WorkerStart;
            await worker.StartAsync(job.Token).ConfigureAwait(false);
            qualification.CheckOwnerReferenceGuards(input, owner, accounts, workspace, producerWorkspace,
                applicationWorkspace, worker, server, listener, job.Token);
            phase = EvidenceNativeObservationPhase.ServerLifetime;
            serverLifetime = CancellationTokenSource.CreateLinkedTokenSource(job.Token);
            phase = EvidenceNativeObservationPhase.ServerRun;
            serverTask = server.RunAsync(serverLifetime.Token);
            await serverTask.ConfigureAwait(false);
            phase = EvidenceNativeObservationPhase.ServerCompletion;
            server.RequireSuccessfulCompletion(job.Token);
            phase = EvidenceNativeObservationPhase.WorkerExit;
            await worker.WaitForExitAsync().ConfigureAwait(false);
            await qualification.ObserveLeaderExitAsync(worker, job.Token).ConfigureAwait(false);
            phase = EvidenceNativeObservationPhase.WorkerStop;
            await worker.StopAndJoinAsync().ConfigureAwait(false);
            phase = EvidenceNativeObservationPhase.WorkerCompletion;
            worker.RequireSuccessfulCompletion();
            await qualification.WriteJoinedDescendantAsync(worker, owner).ConfigureAwait(false);
            if (runOutput is not null && !(await runOutput.CompleteAsync().ConfigureAwait(false)).Successful) throw Rejected();
            phase = EvidenceNativeObservationPhase.BeginTeardown;
            owner.BeginRootTeardown();
            cleanupToken = owner.RootTeardownToken;
            if (producerWorkspace is not null)
            {
                phase = EvidenceNativeObservationPhase.ProducerSeal;
                await producerWorkspace.SealForCleanup(owner, accounts, cleanupToken).ConfigureAwait(false);
            }
            if (applicationWorkspace is not null)
                await applicationWorkspace.SealForCleanupAsync(owner, accounts, cleanupToken).ConfigureAwait(false);
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
                SupervisionCleanupAttempt.Run(() => { phase = EvidenceNativeObservationPhase.CleanupBegin; owner.BeginRootTeardown(); cleanupToken = owner.RootTeardownToken; }, error => { Record(error); cleanupFailed = true; });
            }
            // Interrupt actual I/O before joining original tasks. This code runs outside every handler.
            SupervisionCleanupAttempt.Run(() => { phase = EvidenceNativeObservationPhase.ServerCancel; serverLifetime?.Cancel(); }, error => { Record(error); cleanupFailed = true; });
            if (listener is not null)
                await SupervisionCleanupAttempt.RunAsync(async () => { phase = EvidenceNativeObservationPhase.ListenerClose; await listener.DisposeAsync().ConfigureAwait(false); }, error => { Record(error); cleanupFailed = true; }).ConfigureAwait(false);
            if (serverTask is not null)
                await SupervisionCleanupAttempt.RunAsync(async () => { phase = EvidenceNativeObservationPhase.ServerJoin; await serverTask.ConfigureAwait(false); }, error => { Record(error); failed = true; }).ConfigureAwait(false);
            if (worker is not null)
                await SupervisionCleanupAttempt.RunAsync(async () => { phase = EvidenceNativeObservationPhase.WorkerJoin; await worker.StopAndJoinAsync().ConfigureAwait(false); }, error => { Record(error); cleanupFailed = true; }).ConfigureAwait(false);
            if (applicationWorkspace is not null && owner is not null && accounts is not null)
                await SupervisionCleanupAttempt.RunAsync(async () => { await applicationWorkspace.SealForCleanupAsync(owner, accounts, cleanupToken).ConfigureAwait(false); }, error => { Record(error); cleanupFailed = true; }).ConfigureAwait(false);
            if (producerWorkspace is not null && owner is not null && accounts is not null)
                await SupervisionCleanupAttempt.RunAsync(async () => { await producerWorkspace.SealForCleanup(owner, accounts, cleanupToken).ConfigureAwait(false); }, error => { Record(error); cleanupFailed = true; }).ConfigureAwait(false);
            if ((failed || cleanupFailed) && producerWorkspace is not null && input is not null && owner is not null && worker is not null)
                try
                {
                    worker.RetainJoinedFailureOutput(cleanupToken);
                }
                catch (Exception diagnosticError) when (Recoverable(diagnosticError)) { }
            if (runOutput is not null)
                try
                {
                    var outputReceipt = await runOutput.CompleteAsync().ConfigureAwait(false);
                    if (!outputReceipt.Successful) failed = true;
                    // Observation only, after the original tasks join. Missing diagnostic output cannot
                    // upgrade failure, bypass final custody or substitute for physical process settlement.
                    try
                    {
                        var observation = LinuxCoverageOutputObservation.Encode(outputReceipt);
                        using var error = Console.OpenStandardError();
                        await error.WriteAsync(observation, cleanupToken).ConfigureAwait(false);
                        await error.WriteAsync(new byte[] { (byte)'\n' }, cleanupToken).ConfigureAwait(false);
                        await error.FlushAsync(cleanupToken).ConfigureAwait(false);
                    }
                    catch (Exception diagnosticError) when (Recoverable(diagnosticError)) { }

                }
                catch (Exception error) when (Recoverable(error)) { Record(error); cleanupFailed = true; }
            cleanupFailed = await qualification.CaptureFailureAsync(failed, cleanupFailed, input, owner, accounts,
                workspace, worker, server, serverTask, cleanupToken, token, Record).ConfigureAwait(false);
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
                    qualification.MarkAccountsClosedUnderCustody();
                }
                catch (Exception error) when (Recoverable(error)) { Record(error); cleanupFailed = true; }
            else if (accounts is not null)
                await SupervisionCleanupAttempt.RunAsync(async () => { phase = EvidenceNativeObservationPhase.AccountsClose; await accounts.CloseAsync(cleanupToken).ConfigureAwait(false); }, error => { Record(error); cleanupFailed = true; }).ConfigureAwait(false);
            // Transfer already closes original owners. Rejoining their same tasks is harmless and ensures
            // earlier setup failures also attempt every local close, without bypassing account custody.
            if (worker is not null)
                await SupervisionCleanupAttempt.RunAsync(async () => { phase = EvidenceNativeObservationPhase.WorkerClose; await worker.DisposeAsync().ConfigureAwait(false); }, error => { Record(error); cleanupFailed = true; }).ConfigureAwait(false);
            SupervisionCleanupAttempt.Run(() => { phase = EvidenceNativeObservationPhase.CustodyClose; custody?.Dispose(); }, error => { Record(error); cleanupFailed = true; });
            SupervisionCleanupAttempt.Run(() => { applicationWorkspace?.Dispose(); }, error => { Record(error); cleanupFailed = true; });
            SupervisionCleanupAttempt.Run(() => { producerWorkspace?.Dispose(); }, error => { Record(error); cleanupFailed = true; });
            SupervisionCleanupAttempt.Run(() => { outputStop.Dispose(); }, error => { Record(error); cleanupFailed = true; });
            SupervisionCleanupAttempt.Run(() => { runOutput?.Dispose(); }, error => { Record(error); cleanupFailed = true; });
            SupervisionCleanupAttempt.Run(() => { phase = EvidenceNativeObservationPhase.WorkspaceClose; workspace?.Dispose(); }, error => { Record(error); cleanupFailed = true; });
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
            SupervisionCleanupAttempt.Run(() => { phase = EvidenceNativeObservationPhase.ServerLifetimeClose; serverLifetime?.Dispose(); }, error => { Record(error); cleanupFailed = true; });
            SupervisionCleanupAttempt.Run(() => { phase = EvidenceNativeObservationPhase.JobClose; job?.Dispose(); }, error => { Record(error); cleanupFailed = true; });
            SupervisionCleanupAttempt.Run(() => { phase = EvidenceNativeObservationPhase.OwnerClose; owner?.Dispose(); }, error => { Record(error); cleanupFailed = true; });
            SupervisionCleanupAttempt.Run(() => { phase = EvidenceNativeObservationPhase.InputClose; input?.Dispose(); }, error => { Record(error); cleanupFailed = true; });
            SupervisionCleanupAttempt.Run(() => { phase = EvidenceNativeObservationPhase.BackendClose; backend?.Dispose(); }, error => { Record(error); cleanupFailed = true; });
            // Native owners are now closed, so bind the final publication check to the remaining
            // monotonic interval captured immediately before their closes; no new allowance is created.
            // Disposing the owner deliberately cancels borrowers; that expected cancellation is not
            // evidence of expiry. Check only the independent captured interval after those closes.
            if (owner is not null)
                SupervisionCleanupAttempt.Run(() => { phase = EvidenceNativeObservationPhase.FinalDeadline; RequireFinalClose(TimeProvider.System, finalCloseStarted, finalCloseAllowance); }, error => { Record(error); cleanupFailed = true; });
        }
        qualification.ThrowCleanupFailure(cleanupFailed);
        token.ThrowIfCancellationRequested();
        // The optional diagnostic is attached only after every original cleanup and deadline check.
        // No earlier first-fault projection can conceal a failed finalization behind this record.
        qualification.ThrowFinalProjection(failed, manifest, owner);
        if (failed || manifest is null)
        {
            throw failures.Rejected();
        }
        return manifest;
    }

    /// <summary>Encodes fixed private cancellation cleanup data; it creates no native ownership or completion capability.</summary>
    /// <param name="generation">Original generation data; native composition supplies the actual owner generation.</param>
    /// <param name="resultsGid">Original nonroot results group ID, copied through the retained control owner
    /// before account cleanup changes NSS.</param>
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

    private static bool Recoverable(Exception error) => SupervisionExceptionPolicy.IsRecoverable(error);
    private static EvidenceAdmissionException Rejected() =>
        new("ASEVD410", "The protected empty Observation execution or final cleanup could not be established.");
}
