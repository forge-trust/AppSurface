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
    /// or ASEVD410 when execution or cleanup cannot be established.</exception>
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
        var failed = false;
        var cleanupFailed = false;
        var cleanupToken = CancellationToken.None;
        long finalCloseStarted = 0;
        TimeSpan finalCloseAllowance = TimeSpan.Zero;
        try
        {
            token.ThrowIfCancellationRequested();
            input = EvidenceProtectedLaunchInput.Open(requestPath, token);
            job = CancellationTokenSource.CreateLinkedTokenSource(token);
            job.CancelAfter(input.Remaining);
            backend = await LinuxSystemdBackend.ConnectAsync(job.Token).ConfigureAwait(false);
            owner = await LinuxOwnerActivation.OpenAsync(input, backend, job.Token).ConfigureAwait(false);
            _ = EvidenceEmptyObservationPlan.FromInput(input, job.Token);
            accounts = await LinuxRunAccounts.CreateAsync(owner, job.Token).ConfigureAwait(false);
            workspace = LinuxRunWorkspace.Create(owner, accounts, job.Token);
            listener = LinuxControlListener.Bind(owner, accounts, workspace, job.Token);
            worker = LinuxWorkerProcess.Create(input, owner, accounts, workspace, listener, job.Token);
            await worker.StartAsync(job.Token).ConfigureAwait(false);
            server = LinuxEmptyObservationControlServer.Create(input, owner, accounts, workspace, listener, worker, job.Token);
            serverLifetime = CancellationTokenSource.CreateLinkedTokenSource(job.Token);
            serverTask = server.RunAsync(serverLifetime.Token);
            await serverTask.ConfigureAwait(false);
            server.RequireSuccessfulCompletion(job.Token);
            await worker.WaitForExitAsync().ConfigureAwait(false);
            await worker.StopAndJoinAsync().ConfigureAwait(false);
            worker.RequireSuccessfulCompletion();
            owner.BeginRootTeardown();
            cleanupToken = owner.RootTeardownToken;
            custody = await workspace.TakeRootCustodyAsync(input, owner, accounts, worker, server, cleanupToken).ConfigureAwait(false);
            manifest = custody.VerifyObservationFiles(cleanupToken);
        }
        catch (Exception error) when (Recoverable(error)) { failed = true; }
        finally
        {
            if (owner is not null)
            {
                try { owner.BeginRootTeardown(); cleanupToken = owner.RootTeardownToken; }
                catch (Exception error) when (Recoverable(error)) { cleanupFailed = true; }
            }
            // Interrupt actual I/O before joining original tasks. This code runs outside every handler.
            try { serverLifetime?.Cancel(); }
            catch (Exception error) when (Recoverable(error)) { cleanupFailed = true; }
            if (listener is not null)
                try { await listener.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) when (Recoverable(error)) { cleanupFailed = true; }
            if (serverTask is not null)
                try { await serverTask.ConfigureAwait(false); }
                catch (Exception error) when (Recoverable(error)) { failed = true; }
            if (worker is not null)
                try { await worker.StopAndJoinAsync().ConfigureAwait(false); }
                catch (Exception error) when (Recoverable(error)) { cleanupFailed = true; }
            if (custody is null && input is not null && owner is not null && accounts is not null
                && workspace is not null && worker is not null && server is not null)
                try
                {
                    custody = await workspace.TakeRootCustodyAsync(input, owner, accounts, worker, server, cleanupToken)
                        .ConfigureAwait(false);
                }
                catch (Exception error) when (Recoverable(error)) { cleanupFailed = true; }
            if (custody is not null)
                try { await custody.CloseAccountsAsync(cleanupToken).ConfigureAwait(false); }
                catch (Exception error) when (Recoverable(error)) { cleanupFailed = true; }
            else if (accounts is not null)
                try { await accounts.CloseAsync(cleanupToken).ConfigureAwait(false); }
                catch (Exception error) when (Recoverable(error)) { cleanupFailed = true; }
            // Transfer already closes original owners. Rejoining their same tasks is harmless and ensures
            // earlier setup failures also attempt every local close, without bypassing account custody.
            if (worker is not null)
                try { await worker.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) when (Recoverable(error)) { cleanupFailed = true; }
            try { custody?.Dispose(); }
            catch (Exception error) when (Recoverable(error)) { cleanupFailed = true; }
            try { workspace?.Dispose(); }
            catch (Exception error) when (Recoverable(error)) { cleanupFailed = true; }
            if (owner is not null)
                try
                {
                    cleanupToken.ThrowIfCancellationRequested();
                    owner.RequireControlIdentity(cleanupToken);
                    finalCloseStarted = TimeProvider.System.GetTimestamp();
                    finalCloseAllowance = owner.CleanupRemaining;
                }
                catch (Exception error) when (Recoverable(error)) { cleanupFailed = true; }
            try { serverLifetime?.Dispose(); }
            catch (Exception error) when (Recoverable(error)) { cleanupFailed = true; }
            try { job?.Dispose(); }
            catch (Exception error) when (Recoverable(error)) { cleanupFailed = true; }
            try { owner?.Dispose(); }
            catch (Exception error) when (Recoverable(error)) { cleanupFailed = true; }
            try { input?.Dispose(); }
            catch (Exception error) when (Recoverable(error)) { cleanupFailed = true; }
            try { backend?.Dispose(); }
            catch (Exception error) when (Recoverable(error)) { cleanupFailed = true; }
            // Native owners are now closed, so bind the final publication check to the remaining
            // monotonic interval captured immediately before their closes; no new allowance is created.
            // Disposing the owner deliberately cancels borrowers; that expected cancellation is not
            // evidence of expiry. Check only the independent captured interval after those closes.
            if (owner is not null)
                try { RequireFinalClose(TimeProvider.System, finalCloseStarted, finalCloseAllowance); }
                catch (Exception error) when (Recoverable(error)) { cleanupFailed = true; }
        }
        if (cleanupFailed) throw Rejected();
        token.ThrowIfCancellationRequested();
        if (failed || manifest is null) throw Rejected();
        return manifest;
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
