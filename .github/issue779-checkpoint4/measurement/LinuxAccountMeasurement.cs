#if !EVIDENCE_PRIVATE_C4_ACCOUNTS
#error Account controls must be linked only into their closed private build variants.
#endif
#if EVIDENCE_PRIVATE_C3 || EVIDENCE_PRIVATE_C3_SDK || EVIDENCE_PRIVATE_C2_ARTIFACT_MUTATION || EVIDENCE_PRIVATE_ACCEPTED_WORK || EVIDENCE_PRIVATE_N04 || EVIDENCE_PRIVATE_N05 || EVIDENCE_PRIVATE_N06 || EVIDENCE_PRIVATE_N07 || EVIDENCE_PRIVATE_N08 || EVIDENCE_PRIVATE_N09 || EVIDENCE_PRIVATE_N10 || EVIDENCE_PRIVATE_N11 || EVIDENCE_PRIVATE_N12 || EVIDENCE_PRIVATE_N13 || EVIDENCE_PRIVATE_N14 || EVIDENCE_PRIVATE_N15 || EVIDENCE_PRIVATE_N16
#error Account controls cannot share an image with another private qualification variant.
#endif

using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Runs one source-selected account mechanism control under the authentic protected supervisor.</summary>
/// <remarks>
/// This linked implementation is absent from ordinary images. It creates no worker, manifest or admission.
/// Its single-process witness receives detached observations only; it cannot supply native callbacks or owners.
/// A receipt is returned only after original account tasks, native owners and final deadline checks complete.
/// </remarks>
internal static class LinuxAccountMeasurement
{
    private static int attempted;
    private static Witness? witness;

    /// <summary>Runs the one compiled control and returns at most 4096 bytes of closed mechanism data.</summary>
    /// <param name="requestPath">The original protected request used by the unchanged supervisor argv.</param>
    /// <param name="originalToken">The original CLI cancellation token, retained through finalization.</param>
    /// <returns>Detached receipt bytes, never an observation manifest or native completion capability.</returns>
    internal static async Task<byte[]> RunAsync(string requestPath, CancellationToken originalToken)
    {
        if (Interlocked.Exchange(ref attempted, 1) != 0) throw Rejected();
        EvidenceProtectedLaunchInput? input = null;
        LinuxSystemdBackend? backend = null;
        LinuxOwnerActivation? owner = null;
        LinuxRunAccounts? accounts = null;
        CancellationTokenSource? job = null;
        LinuxRunAccountSnapshot? snapshot = null;
        LinuxAccountMeasurementState? terminal = null;
        LinuxAccountFailure? operationFailure = null;
        var generation = Guid.Empty;
        string? entrySha256 = null;
        var failed = false;
        var cleanupFailed = false;
        var namesAbsent = false;
        var numbersAbsent = false;
        bool? sharedClose = null;
        bool? closedHolderRejected = null;
        long closeStarted = 0;
        var closeAllowance = TimeSpan.Zero;
        var cleanupToken = CancellationToken.None;
        bool? cleanupIdentityGuardsChecked = null;
        try
        {
            originalToken.ThrowIfCancellationRequested();
            input = EvidenceProtectedLaunchInput.Open(requestPath, originalToken);
            entrySha256 = input.EntrySha256;
            job = CancellationTokenSource.CreateLinkedTokenSource(originalToken);
            job.CancelAfter(input.Remaining);
            backend = await LinuxSystemdBackend.ConnectAsync(job.Token).ConfigureAwait(false);
            owner = await LinuxOwnerActivation.OpenAsync(input, backend, job.Token).ConfigureAwait(false);
            generation = owner.RunId;
            if (input.Request.ApplicationId is not null || input.Request.ObservationProducerIds.Count != 0)
                throw Rejected();
            _ = EvidenceEmptyObservationPlan.FromInput(input, job.Token);
            var observation = new Witness(owner);
            witness = observation;
#if EVIDENCE_PRIVATE_C4_ACCOUNT_ROLLBACK
            try
            {
                accounts = await LinuxRunAccounts.CreateAsync(owner, job.Token, includeApplication: false)
                    .ConfigureAwait(false);
            }
            catch (LinuxRunAccountException error) when (error.Failure == LinuxRunAccountFailure.OperationFailed)
            {
                operationFailure = error.FirstFailure;
            }
            if (accounts is not null || operationFailure is null || observation.Failed
                || observation.Snapshot is not { } observedSnapshot || observation.Terminal is not { } rollback)
                throw Rejected();
            snapshot = observedSnapshot;
            terminal = rollback;
            if (!ReferenceEquals(operationFailure, rollback.FirstFailure)
                || !rollback.CloseCompleted || rollback.Quarantined || !rollback.ReservationFailed
                || rollback.IdentitiesPublished || rollback.FailedUtilities != 1
                || rollback.Utilities is < 7 or > 9 || rollback.SettledUtilities != rollback.Utilities
                || operationFailure.PreparationStage != LinuxAccountPreparationStage.UtilityExecute
                || operationFailure.UtilityStage != LinuxAccountUtilityStage.TerminalCheck
                || operationFailure.Operation != LinuxRunAccountOperation.CreateResultsGroup
                || operationFailure.ExecMainCode != 1 || operationFailure.ExecMainStatus is not (>= 1 and <= 255))
                throw Rejected();
#else
            accounts = await LinuxRunAccounts.CreateAsync(owner, job.Token, includeApplication: false)
                .ConfigureAwait(false);
            accounts.RequireOwnedBy(owner, job.Token);
            snapshot = accounts.RequireControlOwnedBy(owner, job.Token);
            var independent = await LinuxRunAccountNss.ReadAsync(owner, job.Token, includeApplication: false)
                .ConfigureAwait(false);
            if (snapshot != independent) throw Rejected();
            var created = accounts.CaptureAccountMeasurementState(owner);
            if (created.Utilities != 3 || created.SettledUtilities != 3 || created.FailedUtilities != 0
                || created.CloseCompleted || created.Quarantined || created.ReservationFailed
                || !created.IdentitiesPublished || created.FirstFailure is not null)
                throw Rejected();
            owner.RequireActive(job.Token); // Successful local utility settlement has not closed admission.
            var names = LinuxRunAccountNames.Create(owner.RunId);
            await RequireCleanupIdentityMismatchAsync(owner,
                LinuxRunAccountCommand.Create(LinuxRunAccountOperation.DeleteUser, names.Worker),
                snapshot with { WorkerUid = snapshot.SubjectUid }, snapshot, job.Token).ConfigureAwait(false);
            await RequireCleanupIdentityMismatchAsync(owner,
                LinuxRunAccountCommand.Create(LinuxRunAccountOperation.DeleteUser, names.Worker),
                snapshot with { WorkerGid = snapshot.SubjectGid }, snapshot, job.Token).ConfigureAwait(false);
            await RequireCleanupIdentityMismatchAsync(owner,
                LinuxRunAccountCommand.Create(LinuxRunAccountOperation.DeleteGroup, names.Results),
                snapshot with { ResultsGid = snapshot.WorkerGid }, snapshot, job.Token).ConfigureAwait(false);
            var verifiedAfterIdentityGuards = await LinuxRunAccountNss.ReadAsync(owner, job.Token, includeApplication: false)
                .ConfigureAwait(false);
            if (accounts.CaptureAccountMeasurementState(owner) != created || verifiedAfterIdentityGuards != snapshot)
                throw Rejected();
            cleanupIdentityGuardsChecked = true;
            owner.RequireActive(job.Token); // Read-only identity mismatches leave the owner active for original cleanup.
            owner.BeginRootTeardown();
            var first = accounts.CloseAsync(owner.RootTeardownToken);
            var second = accounts.CloseAsync(owner.RootTeardownToken);
            sharedClose = ReferenceEquals(first, second);
            await first.ConfigureAwait(false); // Always join the original task before interpreting observations.
            if (sharedClose != true) throw Rejected();
            terminal = accounts.CaptureAccountMeasurementState(owner);
            if (!terminal.Value.CloseCompleted || terminal.Value.Quarantined || terminal.Value.ReservationFailed
                || terminal.Value.FirstFailure is not null || terminal.Value.FailedUtilities != 0
                || terminal.Value.Utilities is < 6 or > 8
                || terminal.Value.SettledUtilities != terminal.Value.Utilities)
                throw Rejected();
            var ownedRejected = false;
            try { accounts.RequireOwnedBy(owner, default); }
            catch (LinuxRunAccountException error) when (error.Failure == LinuxRunAccountFailure.OperationFailed)
            { ownedRejected = true; }
            var controlRejected = false;
            try { _ = accounts.RequireControlOwnedBy(owner, default); }
            catch (LinuxRunAccountException error) when (error.Failure == LinuxRunAccountFailure.OperationFailed)
            { controlRejected = true; }
            closedHolderRejected = ownedRejected && controlRejected;
            if (closedHolderRejected != true) throw Rejected();
#endif
            if (snapshot is null) throw Rejected();
            await LinuxRunAccountNss.RequireNamesAbsentAfterCleanupAsync(owner, owner.RootTeardownToken,
                includeApplication: false).ConfigureAwait(false);
            namesAbsent = true;
            await LinuxRunAccountNss.RequireAccountMeasurementNumbersAbsentAsync(owner, snapshot,
                owner.RootTeardownToken).ConfigureAwait(false);
            numbersAbsent = true;
        }
        catch (Exception error) when (Recoverable(error)) { failed = true; }
        finally
        {
            // Cleanup has its original reserve even when the caller canceled forward preparation.
            if (owner is not null)
            {
                try { owner.BeginRootTeardown(); cleanupToken = owner.RootTeardownToken; }
                catch (Exception error) when (Recoverable(error)) { cleanupFailed = true; }
                if (accounts is not null)
                    try { await accounts.CloseAsync(cleanupToken).ConfigureAwait(false); }
                    catch (Exception error) when (Recoverable(error)) { cleanupFailed = true; }
                try
                {
                    owner.RequireControlIdentity(cleanupToken);
                    closeStarted = TimeProvider.System.GetTimestamp();
                    closeAllowance = owner.CleanupRemaining;
                }
                catch (Exception error) when (Recoverable(error)) { cleanupFailed = true; }
            }
            witness = null; // All creation/rollback work has joined; no native authority is exported.
            try { job?.Dispose(); }
            catch (Exception error) when (Recoverable(error)) { cleanupFailed = true; }
            try { owner?.Dispose(); }
            catch (Exception error) when (Recoverable(error)) { cleanupFailed = true; }
            try { input?.Dispose(); }
            catch (Exception error) when (Recoverable(error)) { cleanupFailed = true; }
            try { backend?.Dispose(); }
            catch (Exception error) when (Recoverable(error)) { cleanupFailed = true; }
            if (owner is not null)
                try { LinuxEmptyObservationExecution.RequireFinalClose(TimeProvider.System, closeStarted, closeAllowance); }
                catch (Exception error) when (Recoverable(error)) { cleanupFailed = true; }
        }
        originalToken.ThrowIfCancellationRequested();
        if (failed || cleanupFailed || snapshot is null || terminal is null || generation == Guid.Empty
            || entrySha256 is null || !namesAbsent || !numbersAbsent)
            throw Rejected();
        var state = terminal.Value;
        var receipt = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "issue779-c4-accounts-v1",
#if EVIDENCE_PRIVATE_C4_ACCOUNT_ROLLBACK
            variant = "C4AccountRollback",
            creation_succeeded = false,
#else
            variant = "C4Accounts",
            creation_succeeded = true,
#endif
            generation = generation.ToString("N"),
            managed_entry_sha256 = entrySha256,
            native_acceptance = false,
            consumer_started = false,
            cleanup_identity_guards_checked = cleanupIdentityGuardsChecked,
            shared_close_task = sharedClose,
            closed_holder_rejected = closedHolderRejected,
            names_absent = namesAbsent,
            numeric_ids_absent = numbersAbsent,
            utilities = state.Utilities,
            settled_utilities = state.SettledUtilities,
            failed_utilities = state.FailedUtilities,
            close_completed = state.CloseCompleted,
            quarantined = state.Quarantined,
            reservation_failed = state.ReservationFailed,
            identities_published = state.IdentitiesPublished,
            original_handles_closed = true,
            ids = new
            {
                worker_uid = snapshot.WorkerUid, worker_gid = snapshot.WorkerGid,
                subject_uid = snapshot.SubjectUid, subject_gid = snapshot.SubjectGid,
                results_gid = snapshot.ResultsGid
            },
            operation_failure = operationFailure is null ? null : new
            {
                account_failure = nameof(LinuxRunAccountFailure.OperationFailed),
                preparation_stage = operationFailure.PreparationStage.ToString(),
                utility_stage = operationFailure.UtilityStage.ToString(),
                operation = operationFailure.Operation?.ToString(),
                error_kind = operationFailure.ErrorKind.ToString(),
                diagnostic_code = operationFailure.DiagnosticCode,
                exec_main_code = operationFailure.ExecMainCode,
                exec_main_status = operationFailure.ExecMainStatus,
                dbus_category = operationFailure.DBusCategory?.ToString()
            }
        });
        if (receipt.Length is 0 or > 4096) throw Rejected();
        LinuxEmptyObservationExecution.RequireFinalClose(TimeProvider.System, closeStarted, closeAllowance, originalToken);
        return receipt;
    }

    /// <summary>Requires a substituted NSS identity to reject while the correct live identity remains present.</summary>
    /// <remarks>No account utility is dispatched; both lookups use the authentic owner's existing time budget.</remarks>
    private static async Task RequireCleanupIdentityMismatchAsync(LinuxOwnerActivation owner,
        LinuxRunAccountCommand command, LinuxRunAccountSnapshot incorrect, LinuxRunAccountSnapshot expected,
        CancellationToken token)
    {
        var rejected = false;
        try
        {
            _ = await LinuxRunAccountNss.IsCleanupNameAbsentAsync(owner, command, incorrect, token).ConfigureAwait(false);
        }
        catch (LinuxRunAccountException error) when (error.Failure == LinuxRunAccountFailure.IdentityMismatch)
        {
            rejected = true;
        }
        if (!rejected) throw Rejected();
        if (await LinuxRunAccountNss.IsCleanupNameAbsentAsync(owner, command, expected, token).ConfigureAwait(false))
            throw Rejected();
    }

    /// <summary>Copies a single actual holder's rollback observation without changing its native outcome.</summary>
    internal static void RecordRollback(LinuxOwnerActivation owner, LinuxRunAccountSnapshot snapshot,
        LinuxAccountMeasurementState state)
    {
        var current = witness;
        if (current is null || !ReferenceEquals(current.Owner, owner) || current.Snapshot is not null)
            throw Rejected();
        current.Snapshot = snapshot;
        current.Terminal = state;
    }

    /// <summary>Latches unavailable private observation; it cannot replace the original account failure.</summary>
    internal static void RejectWitness()
    {
        if (witness is { } current) current.Failed = true;
    }

    /// <summary>Creates one fixed rejection with no original exception, account name or path.</summary>
    internal static EvidenceAdmissionException Rejected() =>
        new("ASEVD410", "The private account mechanism control or its final cleanup could not be established.");

    private static bool Recoverable(Exception error) =>
        error is not (OutOfMemoryException or StackOverflowException or AccessViolationException);

    private sealed class Witness(LinuxOwnerActivation owner)
    {
        internal LinuxOwnerActivation Owner { get; } = owner;
        internal LinuxRunAccountSnapshot? Snapshot { get; set; }
        internal LinuxAccountMeasurementState? Terminal { get; set; }
        internal bool Failed { get; set; }
    }
}

/// <summary>Detached counts and closed state copied from the actual account holder, never deletion authority.</summary>
/// <param name="Utilities">Original retained utility executor count.</param>
/// <param name="SettledUtilities">Executors that actually published physical settlement.</param>
/// <param name="FailedUtilities">Executors with an actual retained first failure.</param>
/// <param name="CloseCompleted">Whether the original shared close task completed successfully.</param>
/// <param name="Quarantined">Actual sticky quarantine state.</param>
/// <param name="ReservationFailed">Actual reservation failure latch.</param>
/// <param name="IdentitiesPublished">Whether normal creation published its production identity snapshot.</param>
/// <param name="FirstFailure">The actual immutable first-fault projection.</param>
internal readonly record struct LinuxAccountMeasurementState(int Utilities, int SettledUtilities,
    int FailedUtilities, bool CloseCompleted, bool Quarantined, bool ReservationFailed,
    bool IdentitiesPublished, LinuxAccountFailure? FirstFailure);

internal sealed partial class LinuxRunAccounts
{
    /// <summary>Copies actual executor and closure data without reopening account admission.</summary>
    internal LinuxAccountMeasurementState CaptureAccountMeasurementState(LinuxOwnerActivation owner)
    {
        if (!ReferenceEquals(owner, _owner)) throw LinuxAccountMeasurement.Rejected();
        lock (_gate)
            return new(_utilities.Count, _utilities.Count(static value => value.PhysicallySettled),
                _utilities.Count(static value => value.FirstFailure is not null),
                _closeTask is { IsCompletedSuccessfully: true }, _quarantined, _reservations.Failed,
                _identities is not null, _failures.First);
    }

#if EVIDENCE_PRIVATE_C4_ACCOUNT_ROLLBACK
    private LinuxRunAccountSnapshot? accountMeasurementSnapshot;

    // Only this generated results-group obligation is seeded, after reservation and before normal dispatch.
    private async Task PrepareAccountMeasurementCollisionAsync(LinuxRunAccountCommand command, CancellationToken token)
    {
        if (_includeApplication || command.Operation != LinuxRunAccountOperation.CreateResultsGroup
            || command.Arguments[^1] != _names.Results || accountMeasurementSnapshot is not null
            || _utilities.Count != 2 || _identities is not null)
            throw LinuxAccountMeasurement.Rejected();
        await RunUtilityAsync(command, cleanup: false, token).ConfigureAwait(false);
        if (_utilities.Count != 3 || _utilities.Any(static value => !value.PhysicallySettled))
            throw LinuxAccountMeasurement.Rejected();
        accountMeasurementSnapshot = await LinuxRunAccountNss.ReadAsync(_owner, token, includeApplication: false)
            .ConfigureAwait(false);
    }

    private void RecordAccountMeasurementRollback()
    {
        try
        {
            LinuxAccountMeasurement.RecordRollback(_owner,
                accountMeasurementSnapshot ?? throw LinuxAccountMeasurement.Rejected(),
                CaptureAccountMeasurementState(_owner));
        }
        catch (Exception) { LinuxAccountMeasurement.RejectWitness(); }
    }
#endif
}

internal static partial class LinuxRunAccountNss
{
    /// <summary>Requires real NSS absence of the five numeric identities captured from this run's default profile.</summary>
    /// <remarks>Original synchronous NSS tasks are joined; errors or ID reuse never count as absence.</remarks>
    internal static async Task RequireAccountMeasurementNumbersAbsentAsync(LinuxOwnerActivation owner,
        LinuxRunAccountSnapshot snapshot, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(snapshot);
        Check(owner, token, cleanup: true);
        RequirePlatform();
        var users = new[] { snapshot.WorkerUid, snapshot.SubjectUid };
        var groups = new[] { snapshot.WorkerGid, snapshot.SubjectGid, snapshot.ResultsGid };
        if (users.Any(static value => value is 0 or uint.MaxValue) || users.Distinct().Count() != 2
            || groups.Any(static value => value is 0 or uint.MaxValue) || groups.Distinct().Count() != 3
            || snapshot.ApplicationUid != 0 || snapshot.ApplicationGid != 0 || snapshot.ResourceAccessGid != 0)
            throw Mismatch();
        var names = LinuxRunAccountNames.Create(owner.RunId);
        await Task.Run(() =>
        {
            Check(owner, token, cleanup: true);
            if (User(names.Worker, snapshot.WorkerUid) is not null) throw Mismatch();
            Check(owner, token, cleanup: true);
            if (User(names.Subject, snapshot.SubjectUid) is not null) throw Mismatch();
            Check(owner, token, cleanup: true);
            if (Group(names.Worker, snapshot.WorkerGid) is not null) throw Mismatch();
            Check(owner, token, cleanup: true);
            if (Group(names.Subject, snapshot.SubjectGid) is not null) throw Mismatch();
            Check(owner, token, cleanup: true);
            if (Group(names.Results, snapshot.ResultsGid) is not null) throw Mismatch();
            Check(owner, token, cleanup: true);
        }, CancellationToken.None).ConfigureAwait(false);
        Check(owner, token, cleanup: true);
    }
}
