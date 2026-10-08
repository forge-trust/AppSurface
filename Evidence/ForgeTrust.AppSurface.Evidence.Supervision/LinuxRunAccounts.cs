using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Closed account preparation failures, without names, NSS contents or utility output.</summary>
internal enum LinuxRunAccountFailure
{
    /// <summary>Generated command or parsed identity data was invalid.</summary>
    InvalidData,
    /// <summary>A name or numeric identity did not resolve consistently through libc NSS.</summary>
    IdentityMismatch,
    /// <summary>NSS failed or exceeded the fixed buffer/group bounds.</summary>
    NssFailed,
    /// <summary>The platform is not root Linux x64.</summary>
    UnsupportedPlatform,
    /// <summary>Creation or identity verification failed after an actual utility attempt.</summary>
    OperationFailed,
    /// <summary>Account cleanup could not establish joined operations and strict name absence.</summary>
    CleanupFailed,
}

/// <summary>Fixed account diagnostic; no supplied data or original exception is retained.</summary>
internal sealed class LinuxRunAccountException : Exception
{
    /// <summary>Creates a closed account failure.</summary>
    internal LinuxRunAccountException(LinuxRunAccountFailure failure, LinuxAccountFailure? firstFailure = null)
        : base("Protected run account preparation was rejected.")
    { Failure = failure; FirstFailure = firstFailure; }

    /// <summary>Gets the closed failure classification.</summary>
    internal LinuxRunAccountFailure Failure { get; }
    /// <summary>Gets detached first-fault data captured before rollback, never the original error or account owner.</summary>
    internal LinuxAccountFailure? FirstFailure { get; }
}

/// <summary>Negative account cancellation carrying closed data while preserving original caller-token semantics.</summary>
internal sealed class LinuxRunAccountCancelledException : OperationCanceledException
{
    /// <summary>Creates cancellation with the original token and no raw error or inner exception.</summary>
    internal LinuxRunAccountCancelledException(CancellationToken token, LinuxAccountFailure? firstFailure) : base(token) =>
        FirstFailure = firstFailure;
    /// <summary>Gets detached pre-rollback data; cancellation supplies no account ownership.</summary>
    internal LinuxAccountFailure? FirstFailure { get; }
}

/// <summary>Generated private account names; these are command data, never identity ownership or admission.</summary>
internal sealed class LinuxRunAccountNames
{
    private LinuxRunAccountNames(string suffix)
    {
        Worker = "evw" + suffix;
        Subject = "evs" + suffix;
        Results = "evr" + suffix;
    }

    /// <summary>Gets the worker user and private primary group name.</summary>
    internal string Worker { get; }
    /// <summary>Gets the subject user and private primary group name.</summary>
    internal string Subject { get; }
    /// <summary>Gets the results supplementary group name.</summary>
    internal string Results { get; }

    /// <summary>Derives three at-most-31-character names from the actual owner generation.</summary>
    /// <remarks>
    /// The 28 hexadecimal suffix characters preserve 112 bits and fit the Linux account name limit.
    /// The utility owner must first establish all three user/group names absent; a collision rejects,
    /// never adopts a previous identity. Generating a name does not create or reserve an OS account.
    /// </remarks>
    internal static LinuxRunAccountNames Create(Guid runId)
    {
        if (runId == Guid.Empty) throw Invalid();
        return new(runId.ToString("N", CultureInfo.InvariantCulture)[..28]);
    }

    /// <summary>Checks only the closed generated grammar, with no shell or option metacharacters.</summary>
    internal static bool IsGenerated(string? name) => name is { Length: 31 }
        && (name.StartsWith("evw", StringComparison.Ordinal) || name.StartsWith("evs", StringComparison.Ordinal)
            || name.StartsWith("evr", StringComparison.Ordinal))
        && name.AsSpan(3).IndexOfAnyExcept("0123456789abcdef") < 0;

    /// <summary>Creates a fixed grammar/data rejection without copying the rejected input.</summary>
    internal static LinuxRunAccountException Invalid() => new(LinuxRunAccountFailure.InvalidData);
}

/// <summary>Closed account utilities. The values never dispatch a process.</summary>
internal enum LinuxRunAccountOperation
{
    /// <summary>Create a worker or subject system user with its private group.</summary>
    CreateUser,
    /// <summary>Create the private results group.</summary>
    CreateResultsGroup,
    /// <summary>Delete a previously reserved user without deleting its home.</summary>
    DeleteUser,
    /// <summary>Delete a previously reserved group.</summary>
    DeleteGroup,
}

/// <summary>Immutable absolute utility and ArgumentList data, not a callable process or cleanup receipt.</summary>
internal sealed class LinuxRunAccountCommand
{
    private LinuxRunAccountCommand(LinuxRunAccountOperation operation, string executable, ImmutableArray<string> arguments)
    {
        Operation = operation;
        Executable = executable;
        Arguments = arguments;
    }

    /// <summary>Gets the closed operation; it selects no arbitrary executable or shell.</summary>
    internal LinuxRunAccountOperation Operation { get; }

    /// <summary>Gets one of the four fixed /usr/sbin executable paths.</summary>
    internal string Executable { get; }
    /// <summary>Gets arguments supplied individually, never through a shell.</summary>
    internal ImmutableArray<string> Arguments { get; }
    /// <summary>Gets the complete explicit utility environment; inherited variables must be cleared.</summary>
    internal static ImmutableArray<string> Environment { get; } =
        ["PATH=/usr/sbin:/usr/bin:/sbin:/bin", "HOME=/nonexistent", "LANG=C.UTF-8"];

    /// <summary>Creates only the existing system/no-login/no-home account policy.</summary>
    /// <remarks>
    /// No numeric UID/GID or supplementary membership can be selected. Fixture numeric identities are
    /// not production reservations: both forward and reverse NSS name checks remain required.
    /// Utilities, account-db custody and owned exit must be authenticated by the live executor.
    /// </remarks>
    internal static LinuxRunAccountCommand Create(LinuxRunAccountOperation operation, string name)
    {
        if (!LinuxRunAccountNames.IsGenerated(name)) throw LinuxRunAccountNames.Invalid();
        var user = name.StartsWith("evw", StringComparison.Ordinal) || name.StartsWith("evs", StringComparison.Ordinal);
        return operation switch
        {
            LinuxRunAccountOperation.CreateUser when user => new(operation, "/usr/sbin/useradd",
                ["--system", "--user-group", "--no-create-home", "--shell", "/usr/sbin/nologin", name]),
            LinuxRunAccountOperation.CreateResultsGroup when !user => new(operation, "/usr/sbin/groupadd", ["--system", name]),
            LinuxRunAccountOperation.DeleteUser when user => new(operation, "/usr/sbin/userdel", [name]),
            LinuxRunAccountOperation.DeleteGroup => new(operation, "/usr/sbin/groupdel", [name]),
            _ => throw LinuxRunAccountNames.Invalid(),
        };
    }
}

/// <summary>Bounded pending-name ledger data, including potentially accepted failed launches.</summary>
/// <remarks>
/// Record immediately before dispatch, after an actual all-names-absent check. There is deliberately no
/// acknowledge/success shortcut that drops reservations. Reverse cleanup remains an OS operation followed
/// by live NSS absence checks. This class cannot authorize deletion of an account and is not thread safe;
/// the account owner must serialize create/close before dispatch under its existing work ledger.
/// </remarks>
internal sealed class LinuxRunAccountReservations
{
    private readonly LinuxRunAccountNames _names;
    private readonly List<LinuxRunAccountCommand> _cleanup = [];
    private int _next;
    private bool _failed;

    /// <summary>Creates an empty data ledger for one generated name set.</summary>
    internal LinuxRunAccountReservations(LinuxRunAccountNames names) =>
        _names = names ?? throw new ArgumentNullException(nameof(names));

    /// <summary>Gets the sticky failure state; successful later data operations cannot clear it.</summary>
    internal bool Failed => _failed;

    /// <summary>Reserves user and implicit private group before either useradd can run, then results group.</summary>
    /// <returns>The next fixed create command; reservations are retained even if dispatch fails.</returns>
    internal LinuxRunAccountCommand ReserveNext()
    {
        if (_failed || _next >= 3) throw LinuxRunAccountNames.Invalid();
        var name = _next switch { 0 => _names.Worker, 1 => _names.Subject, _ => _names.Results };
        var operation = _next < 2 ? LinuxRunAccountOperation.CreateUser : LinuxRunAccountOperation.CreateResultsGroup;
        // Register implicit group first so reverse cleanup deletes its user before the group.
        _cleanup.Add(LinuxRunAccountCommand.Create(LinuxRunAccountOperation.DeleteGroup, name));
        if (_next < 2) _cleanup.Add(LinuxRunAccountCommand.Create(LinuxRunAccountOperation.DeleteUser, name));
        _next++;
        return LinuxRunAccountCommand.Create(operation, name);
    }

    /// <summary>Latches any launch, identity, deadline or cleanup failure.</summary>
    internal void MarkFailed() => _failed = true;

    /// <summary>Returns a copied strict reverse obligation list, without claiming deletion or absence.</summary>
    internal ImmutableArray<LinuxRunAccountCommand> ReverseCleanup() => _cleanup.AsEnumerable().Reverse().ToImmutableArray();
}

/// <summary>Copied passwd identity fields only; password/GECOS values are never retained.</summary>
internal sealed record LinuxRunAccountUser(string Name, uint Uid, uint Gid, string Home, string Shell)
{
    /// <summary>Validates native or portable data without creating an owned account.</summary>
    internal static LinuxRunAccountUser Parse(string name, uint uid, uint gid, string home, string shell)
    {
        if (!LinuxRunAccountNames.IsGenerated(name) || uid is 0 or uint.MaxValue || gid is 0 or uint.MaxValue
            || home.Length is 0 or > 4096 || !home.StartsWith('/') || home.Any(char.IsControl)
            || shell != "/usr/sbin/nologin") throw LinuxRunAccountNames.Invalid();
        return new(name, uid, gid, home, shell);
    }
}

/// <summary>Copied group identity fields, never a grant or a proof of supplementary process credentials.</summary>
internal sealed record LinuxRunAccountGroup(string Name, uint Gid, ImmutableArray<string> Members)
{
    /// <summary>Requires the private groups to have no registered supplementary members.</summary>
    internal static LinuxRunAccountGroup Parse(string name, uint gid, ImmutableArray<string> members)
    {
        if (!LinuxRunAccountNames.IsGenerated(name) || gid is 0 or uint.MaxValue
            || members.IsDefault || !members.IsEmpty) throw LinuxRunAccountNames.Invalid();
        return new(name, gid, members);
    }
}

/// <summary>Consistent name/number identity data; constructing it is never worker admission or account ownership.</summary>
internal sealed record LinuxRunAccountSnapshot(uint WorkerUid, uint WorkerGid, uint SubjectUid,
    uint SubjectGid, uint ResultsGid)
{
    /// <summary>Checks the complete five-record private identity map and distinct UID/GID namespaces.</summary>
    internal static LinuxRunAccountSnapshot Parse(LinuxRunAccountNames names, LinuxRunAccountUser worker,
        LinuxRunAccountUser subject, LinuxRunAccountGroup workerGroup, LinuxRunAccountGroup subjectGroup,
        LinuxRunAccountGroup resultsGroup)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(workerGroup);
        ArgumentNullException.ThrowIfNull(subjectGroup);
        ArgumentNullException.ThrowIfNull(resultsGroup);
        _ = LinuxRunAccountUser.Parse(worker.Name, worker.Uid, worker.Gid, worker.Home, worker.Shell);
        _ = LinuxRunAccountUser.Parse(subject.Name, subject.Uid, subject.Gid, subject.Home, subject.Shell);
        _ = LinuxRunAccountGroup.Parse(workerGroup.Name, workerGroup.Gid, workerGroup.Members);
        _ = LinuxRunAccountGroup.Parse(subjectGroup.Name, subjectGroup.Gid, subjectGroup.Members);
        _ = LinuxRunAccountGroup.Parse(resultsGroup.Name, resultsGroup.Gid, resultsGroup.Members);
        if (worker.Name != names.Worker || subject.Name != names.Subject || workerGroup.Name != names.Worker
            || subjectGroup.Name != names.Subject || resultsGroup.Name != names.Results
            || worker.Uid == subject.Uid || worker.Gid != workerGroup.Gid || subject.Gid != subjectGroup.Gid
            || workerGroup.Gid == subjectGroup.Gid || workerGroup.Gid == resultsGroup.Gid
            || subjectGroup.Gid == resultsGroup.Gid) throw new LinuxRunAccountException(LinuxRunAccountFailure.IdentityMismatch);
        return new(worker.Uid, worker.Gid, subject.Uid, subject.Gid, resultsGroup.Gid);
    }
}

/// <summary>Private owner of actual generated accounts and every creation/deletion utility attempt.</summary>
/// <remarks>
/// The private factory first observes all names absent, retains reverse obligations before dispatch,
/// joins the fixed systemd utilities, and verifies actual forward/reverse NSS mappings. Metadata cannot
/// construct this object. Failed creation rolls back only after all accepted/ambiguous utilities physically
/// settle; uncertain exit quarantines identities. Closing is one shared irreversible operation, with no
/// retry, and requires all consumer identities/pending starts to have already joined in the run owner.
/// Every deletion joins its unit/pumps; final live NSS absence is mandatory. Original failure never becomes
/// successful admission. Workspace/artifact custody must be secured before deleting IDs and permitting reuse.
/// </remarks>
internal sealed class LinuxRunAccounts
{
    private readonly object _gate = new();
    private readonly LinuxOwnerActivation _owner;
    private readonly LinuxRunAccountNames _names;
    private readonly LinuxRunAccountReservations _reservations;
    private readonly List<LinuxAccountUtility> _utilities = [];
    private readonly LinuxAccountFailureLatch _failures = new();
    private LinuxWorkerProcess? _workerCustodyPending;
    private LinuxRunWorkspace? _workspaceCustodyPending;
    private LinuxRunAccountSnapshot? _identities;
    private Task? _closeTask;
    private bool _quarantined;

    private LinuxRunAccounts(LinuxOwnerActivation owner)
    {
        _owner = owner;
        _names = LinuxRunAccountNames.Create(owner.RunId);
        _reservations = new(_names);
    }

    private LinuxRunAccountSnapshot Identities
    {
        get { RequireOwnedBy(_owner, default); return _identities!; }
    }

    /// <summary>Gets the actual created worker UID while account ownership remains active.</summary>
    internal uint WorkerUid => Identities.WorkerUid;
    /// <summary>Gets the actual created worker private GID.</summary>
    internal uint WorkerGid => Identities.WorkerGid;
    /// <summary>Gets the actual created subject UID.</summary>
    internal uint SubjectUid => Identities.SubjectUid;
    /// <summary>Gets the actual created subject private GID.</summary>
    internal uint SubjectGid => Identities.SubjectGid;
    /// <summary>Gets the actual created results GID; this adds no membership or writable paths.</summary>
    internal uint ResultsGid => Identities.ResultsGid;
    /// <summary>Gets whether unsafe/incomplete cleanup retained identities for root quarantine.</summary>
    internal bool IsQuarantined { get { lock (_gate) return _quarantined; } }

    /// <summary>Requires reference-equal actual owner and successful live-created, not closed account ownership.</summary>
    internal void RequireOwnedBy(LinuxOwnerActivation owner, CancellationToken token)
    {
        if (!ReferenceEquals(_owner, owner)) throw Failed();
        lock (_gate)
            if (_identities is null || _closeTask is not null || _quarantined || _reservations.Failed) throw Failed();
        owner.RequireActive(token);
        lock (_gate)
            if (_closeTask is not null || _quarantined) throw Failed();
    }

    /// <summary>Checks the same live account holder for control cleanup without reopening work admission.</summary>
    /// <remarks>Returns copied identity data only; worker custody remains retained and no account may be deleted.</remarks>
    internal LinuxRunAccountSnapshot RequireControlOwnedBy(LinuxOwnerActivation owner, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(_owner, owner)) throw Failed();
        lock (_gate)
            if (_identities is null || _closeTask is not null || _quarantined || _reservations.Failed) throw Failed();
        owner.RequireControlIdentity(token);
        lock (_gate)
        {
            if (_identities is null || _closeTask is not null || _quarantined || _reservations.Failed) throw Failed();
            token.ThrowIfCancellationRequested();
            return _identities;
        }
    }

    /// <summary>Retains the actual workspace before any account-associated directory can be created.</summary>
    /// <remarks>
    /// Failed or partial directory creation preserves this reservation even before worker construction.
    /// Closing descriptors, cancellation or lack of a worker cannot permit reusable UID/GID deletion.
    /// Only complete native root custody can subsequently release the accounts.
    /// </remarks>
    internal void RetainWorkspaceForCustody(LinuxRunWorkspace workspace, LinuxOwnerActivation owner)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        workspace.RequireAccountOwner(this, owner);
        lock (_gate)
        {
            if (!ReferenceEquals(owner, _owner) || _workspaceCustodyPending is not null
                || _identities is null || _closeTask is not null || _quarantined || _reservations.Failed) throw Failed();
            _workspaceCustodyPending = workspace;
        }
    }

    /// <summary>Requires the original private workspace reservation, without granting account release.</summary>
    internal void RequireWorkspaceCustody(LinuxRunWorkspace workspace)
    {
        lock (_gate)
            if (!ReferenceEquals(workspace, _workspaceCustodyPending)) throw Failed();
    }

    /// <summary>Retains the actual worker holder before its startup can dispatch or accounts can close.</summary>
    /// <remarks>
    /// This reservation is not released by process exit or worker disposal. Root filesystem custody must
    /// first remove reusable account ownership from retained paths. Only the private native root holder
    /// can then own strict account closure; a caller Boolean or completion-data receipt never releases
    /// this reservation. The closed supervisor entry remains unavailable during this checkpoint.
    /// </remarks>
    internal void RetainWorkerForCustody(LinuxWorkerProcess worker, LinuxOwnerActivation owner)
    {
        ArgumentNullException.ThrowIfNull(worker);
        worker.RequireAccountOwner(this, owner);
        lock (_gate)
        {
            if (!ReferenceEquals(owner, _owner) || _workerCustodyPending is not null || _identities is null
                || _closeTask is not null || _quarantined || _reservations.Failed) throw Failed();
            _workerCustodyPending = worker;
        }
    }

    /// <summary>Authenticates the actual owner before constructing a private pending account owner.</summary>
    internal static Task<LinuxRunAccounts> CreateAsync(LinuxOwnerActivation owner, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(owner);
        owner.ClaimAccountCreation(token);
        return new LinuxRunAccounts(owner).CreateCoreAsync(token);
    }

    private async Task<LinuxRunAccounts> CreateCoreAsync(CancellationToken token)
    {
        var stage = LinuxAccountPreparationStage.NamesAbsent;
        try
        {
            await LinuxRunAccountNss.RequireNamesAbsentAsync(_owner, token).ConfigureAwait(false);
            for (var i = 0; i < 3; i++)
            {
                stage = LinuxAccountPreparationStage.ReserveUtility;
                _owner.RequireActive(token);
                var command = _reservations.ReserveNext(); // Including implicit group, before any start I/O.
                stage = LinuxAccountPreparationStage.UtilityExecute;
                await RunUtilityAsync(command, cleanup: false, token).ConfigureAwait(false);
            }
            stage = LinuxAccountPreparationStage.IdentityRead;
            _identities = await LinuxRunAccountNss.ReadAsync(_owner, token).ConfigureAwait(false);
            stage = LinuxAccountPreparationStage.OwnershipVerify;
            RequireOwnedBy(_owner, token);
            return this;
        }
        catch (Exception error)
        {
            _failures.Capture(stage, LinuxAccountUtilityStage.Unknown, null, error);
            _reservations.MarkFailed();
            try { await CloseAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception) { lock (_gate) _quarantined = true; }
            if (IsQuarantined) throw new LinuxRunAccountException(LinuxRunAccountFailure.CleanupFailed, _failures.First);
            if (token.IsCancellationRequested) throw new LinuxRunAccountCancelledException(token, _failures.First);
            throw new LinuxRunAccountException(LinuxRunAccountFailure.OperationFailed, _failures.First);
        }
    }

    /// <summary>Closes identity admission before one strict reverse cleanup on the original cleanup allowance.</summary>
    /// <remarks>
    /// Run callers must first close consumer admission, join every pending unit and consumer, and secure
    /// retained output custody. All callers receive the same actual task, including failure. Cancellation
    /// cannot detach a utility or renew the job; uncertain physical exit prevents deletion and reuse.
    /// </remarks>
    internal Task CloseAsync(CancellationToken token) => StartClose(token, custody: null);

    /// <summary>Starts strict account closure only under the exact privately issued root filesystem holder.</summary>
    /// <remarks>
    /// The holder retains its original task and descriptors through every deletion and final NSS check.
    /// No native callback runs under the account bookkeeping lock. This method cannot clear worker
    /// retention or adopt a different holder; callers must use that holder's shared CloseAccountsAsync.
    /// </remarks>
    internal Task CloseUnderCustodyAsync(LinuxRunWorkspace.RootCustody custody, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(custody);
        custody.RequireAccountCleanupDispatch(this, token);
        LinuxWorkerProcess worker;
        lock (_gate) worker = _workerCustodyPending ?? throw Failed();
        custody.RequireAccountRelease(this, _owner, worker, token);
        return StartClose(token, custody);
    }

    private Task StartClose(CancellationToken token, LinuxRunWorkspace.RootCustody? custody)
    {
        var dispatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task execution;
        lock (_gate)
        {
            if (_closeTask is not null) return _closeTask;
            execution = CloseCoreAsync(dispatch.Task, token, custody);
            _closeTask = execution;
        }
        dispatch.SetResult();
        return execution;
    }

    private async Task CloseCoreAsync(Task dispatch, CancellationToken token, LinuxRunWorkspace.RootCustody? custody)
    {
        await dispatch.ConfigureAwait(false);
        var failed = false;
        var stage = LinuxAccountPreparationStage.CleanupCheck;
        try
        {
            _owner.BeginRootTeardown();
            _owner.RequireCleanup(token);
            RequireCustody(custody, token);
            if (_utilities.Any(static utility => !utility.PhysicallySettled)) throw Failed();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, _owner.RootTeardownToken);
            foreach (var command in _reservations.ReverseCleanup())
            {
                try
                {
                    stage = LinuxAccountPreparationStage.CleanupNameCheck;
                    _owner.RequireCleanup(deadline.Token);
                    RequireCustody(custody, deadline.Token);
                    // userdel may already remove its private group. Only actual absence allows skipping,
                    // and an existing known identity must still match its created forward/reverse mapping.
                    if (!await LinuxRunAccountNss.IsCleanupNameAbsentAsync(_owner, command, _identities,
                        deadline.Token).ConfigureAwait(false))
                        await RunUtilityAsync(command, cleanup: true, deadline.Token).ConfigureAwait(false);
                    RequireCustody(custody, deadline.Token);
                }
                catch (Exception error)
                { _failures.Capture(stage, LinuxAccountUtilityStage.Unknown, command.Operation, error); failed = true; }
                if (_utilities.Any(static utility => !utility.PhysicallySettled)) throw Failed();
            }
            stage = LinuxAccountPreparationStage.FinalNamesAbsent;
            await LinuxRunAccountNss.RequireNamesAbsentAfterCleanupAsync(_owner, deadline.Token).ConfigureAwait(false);
            stage = LinuxAccountPreparationStage.FinalOwnershipCheck;
            RequireCustody(custody, deadline.Token);
            _owner.RequireCleanup(deadline.Token);
        }
        catch (Exception error)
        { _failures.Capture(stage, LinuxAccountUtilityStage.Unknown, null, error); failed = true; }
        if (failed)
        {
            lock (_gate) _quarantined = true;
            throw new LinuxRunAccountException(LinuxRunAccountFailure.CleanupFailed, _failures.First);
        }
    }

    private void RequireCustody(LinuxRunWorkspace.RootCustody? custody, CancellationToken token)
    {
        LinuxWorkerProcess? worker;
        LinuxRunWorkspace? workspace;
        lock (_gate) { worker = _workerCustodyPending; workspace = _workspaceCustodyPending; }
        if (worker is null)
        {
            if (custody is not null || workspace is not null) throw Failed();
            return; // Account preparation alone created no consumer account-associated tree.
        }
        if (custody is null) throw Failed();
        custody.RequireAccountRelease(this, _owner, worker, token);
    }

    private async Task RunUtilityAsync(LinuxRunAccountCommand command, bool cleanup, CancellationToken token)
    {
        LinuxAccountUtility? utility = null;
        try
        {
            utility = LinuxAccountUtility.Create(_owner, command, cleanup);
            _utilities.Add(utility); // Retain before the execution attempt can dispatch or be accepted.
            await utility.ExecuteAsync(token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _failures.Capture(utility is null ? LinuxAccountPreparationStage.UtilityCreate
                : cleanup ? LinuxAccountPreparationStage.CleanupUtility : LinuxAccountPreparationStage.UtilityExecute,
                LinuxAccountUtilityStage.Unknown, command.Operation, error, first: utility?.FirstFailure);
            throw;
        }
    }

    private static LinuxRunAccountException Failed() => new(LinuxRunAccountFailure.OperationFailed);
}

/// <summary>Actual Linux x64 libc NSS inspection, bounded in copied bytes and group count.</summary>
/// <remarks>
/// This reader authenticates no lease and performs no account mutation. Calls require an actual creation
/// or retained cleanup owner; before/after checks consume its original remainder. NSS is synchronous and may use configured
/// providers: cancellation joins the actual call instead of detaching it. An independently terminating root
/// owner must contain a stalled NSS provider. There is no per-call fresh job allowance or fake cancellation.
/// Protected pre-CLR NSS configuration and trusted provider/library selection remain bootstrap prerequisites;
/// a successful numeric lookup alone cannot establish that custody or reserve a reusable identity.
/// </remarks>
internal static partial class LinuxRunAccountNss
{
    private const int MaximumBufferBytes = 65_536;
    private const int MaximumSupplementaryGroups = 64;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Reads five generated accounts, their reverse numeric mappings and both group lists.</summary>
    /// <returns>Copied consistent data only; no created account owner or admission.</returns>
    internal static async Task<LinuxRunAccountSnapshot> ReadAsync(LinuxOwnerActivation owner, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(owner);
        owner.RequireActive(token);
        RequirePlatform();
        var names = LinuxRunAccountNames.Create(owner.RunId);
        // Await the original task directly, including when a synchronous NSS provider ignores cancellation.
        var data = await Task.Run(() => Read(owner, names, token), CancellationToken.None).ConfigureAwait(false);
        owner.RequireActive(token);
        return data;
    }

    /// <summary>Checks all generated names absent before future reservation/dispatch; unknown NSS fails.</summary>
    /// <remarks>Absence is an observation, not an exclusive reservation against other privileged writers.</remarks>
    internal static Task RequireNamesAbsentAsync(LinuxOwnerActivation owner, CancellationToken token) =>
        ObserveAbsenceAsync(owner, token, cleanup: false);

    /// <summary>Checks all reserved names absent after strict cleanup, even when new work was cancelled.</summary>
    /// <remarks>
    /// Uses the actual consumed owner and its original cleanup remainder. Unknown/error NSS is a failure;
    /// no successful deletion exit or expired creation gate can stand in for this observation. All pending
    /// utilities and consumer identities must already have settled before absence can permit release.
    /// </remarks>
    internal static Task RequireNamesAbsentAfterCleanupAsync(LinuxOwnerActivation owner, CancellationToken token) =>
        ObserveAbsenceAsync(owner, token, cleanup: true);

    /// <summary>Observes a reserved deletion target absent, or verifies its known created identity before deletion.</summary>
    /// <remarks>
    /// This is not account deletion authority: only the private account owner selects its retained obligations.
    /// A user/group removed automatically is skipped only after actual NSS absence. Unknown/error/substituted
    /// records reject. The actual NSS task is joined even if cancellation is observed while it runs.
    /// </remarks>
    internal static async Task<bool> IsCleanupNameAbsentAsync(LinuxOwnerActivation owner,
        LinuxRunAccountCommand command, LinuxRunAccountSnapshot? expected, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(command);
        Check(owner, token, cleanup: true);
        RequirePlatform();
        var names = LinuxRunAccountNames.Create(owner.RunId);
        var name = command.Arguments[^1];
        if (command.Operation is not (LinuxRunAccountOperation.DeleteUser or LinuxRunAccountOperation.DeleteGroup)
            || name != names.Worker && name != names.Subject && name != names.Results) throw Mismatch();
        var absent = await Task.Run(() =>
        {
            Check(owner, token, cleanup: true);
            if (command.Operation == LinuxRunAccountOperation.DeleteUser)
            {
                var user = User(name, null);
                if (user is null) return true;
                if (expected is not null)
                {
                    var uid = name == names.Worker ? expected.WorkerUid : expected.SubjectUid;
                    var gid = name == names.Worker ? expected.WorkerGid : expected.SubjectGid;
                    if (user.Uid != uid || user.Gid != gid || User(name, uid) != user) throw Mismatch();
                }
                RequirePrimaryOnly(user);
            }
            else
            {
                var group = Group(name, null);
                if (group is null) return true;
                if (expected is not null)
                {
                    var gid = name == names.Worker ? expected.WorkerGid
                        : name == names.Subject ? expected.SubjectGid : expected.ResultsGid;
                    var reverse = Group(name, gid);
                    if (group.Gid != gid || reverse is null || reverse.Name != name || reverse.Gid != gid) throw Mismatch();
                }
            }
            Check(owner, token, cleanup: true);
            return false;
        }, CancellationToken.None).ConfigureAwait(false);
        Check(owner, token, cleanup: true);
        return absent;
    }

    private static async Task ObserveAbsenceAsync(LinuxOwnerActivation owner, CancellationToken token, bool cleanup)
    {
        ArgumentNullException.ThrowIfNull(owner);
        Check(owner, token, cleanup);
        RequirePlatform();
        var names = LinuxRunAccountNames.Create(owner.RunId);
        await Task.Run(() =>
        {
            foreach (var name in new[] { names.Worker, names.Subject, names.Results })
            {
                Check(owner, token, cleanup);
                if (User(name, null) is not null) throw Mismatch();
                Check(owner, token, cleanup);
                if (Group(name, null) is not null) throw Mismatch();
                Check(owner, token, cleanup);
            }
        }, CancellationToken.None).ConfigureAwait(false);
        Check(owner, token, cleanup);
    }

    private static LinuxRunAccountSnapshot Read(LinuxOwnerActivation owner, LinuxRunAccountNames names, CancellationToken token)
    {
        Check(owner, token);
        var worker = User(names.Worker, null) ?? throw Mismatch();
        Check(owner, token);
        var subject = User(names.Subject, null) ?? throw Mismatch();
        Check(owner, token);
        var workerGroup = Group(names.Worker, null) ?? throw Mismatch();
        Check(owner, token);
        var subjectGroup = Group(names.Subject, null) ?? throw Mismatch();
        Check(owner, token);
        var results = Group(names.Results, null) ?? throw Mismatch();
        var snapshot = LinuxRunAccountSnapshot.Parse(names, worker, subject, workerGroup, subjectGroup, results);
        foreach (var user in new[] { worker, subject })
        {
            Check(owner, token);
            if (User(user.Name, user.Uid) != user) throw Mismatch();
            Check(owner, token);
            RequirePrimaryOnly(user);
        }
        foreach (var group in new[] { workerGroup, subjectGroup, results })
        {
            Check(owner, token);
            var reverse = Group(group.Name, group.Gid);
            if (reverse is null || reverse.Name != group.Name || reverse.Gid != group.Gid || !reverse.Members.IsEmpty)
                throw Mismatch();
        }
        Check(owner, token);
        return snapshot;
    }

    private static void Check(LinuxOwnerActivation owner, CancellationToken token, bool cleanup = false)
    {
        if (cleanup)
        {
            owner.RequireCleanup(token);
            _ = owner.CleanupRemaining;
        }
        else
        {
            owner.RequireActive(token);
            _ = owner.Remaining;
        }
    }

    private static unsafe LinuxRunAccountUser? User(string name, uint? number)
    {
        var encoded = NameBytes(name);
        for (var size = 1024; size <= MaximumBufferBytes; size *= 2)
        {
            var buffer = new byte[size];
            fixed (byte* namePointer = encoded)
            fixed (byte* bufferPointer = buffer)
            {
                NativePasswd entry;
                nint result;
                var code = number.HasValue
                    ? GetPwUid(number.Value, out entry, bufferPointer, (nuint)size, out result)
                    : GetPwName(namePointer, out entry, bufferPointer, (nuint)size, out result);
                if (code == 34) continue; // Linux ERANGE; do not invent an absent record on failure.
                if (code != 0) throw NssFailed();
                if (result == 0) return null;
                return LinuxRunAccountUser.Parse(Text(entry.Name, bufferPointer, size, 32), entry.Uid, entry.Gid,
                    Text(entry.Directory, bufferPointer, size, 4096), Text(entry.Shell, bufferPointer, size, 128));
            }
        }
        throw NssFailed();
    }

    private static unsafe LinuxRunAccountGroup? Group(string name, uint? number)
    {
        var encoded = NameBytes(name);
        for (var size = 1024; size <= MaximumBufferBytes; size *= 2)
        {
            var buffer = new byte[size];
            fixed (byte* namePointer = encoded)
            fixed (byte* bufferPointer = buffer)
            {
                NativeGroup entry;
                nint result;
                var code = number.HasValue
                    ? GetGrGid(number.Value, out entry, bufferPointer, (nuint)size, out result)
                    : GetGrName(namePointer, out entry, bufferPointer, (nuint)size, out result);
                if (code == 34) continue;
                if (code != 0) throw NssFailed();
                if (result == 0) return null;
                // These are new private groups; even a single registered supplementary member rejects.
                if (entry.Members == 0 || !Contains(entry.Members, sizeof(nint), bufferPointer, size)
                    || *(nint*)entry.Members != 0) throw Mismatch();
                return LinuxRunAccountGroup.Parse(Text(entry.Name, bufferPointer, size, 32), entry.Gid, []);
            }
        }
        throw NssFailed();
    }

    private static unsafe void RequirePrimaryOnly(LinuxRunAccountUser user)
    {
        var encoded = NameBytes(user.Name);
        var groups = new uint[MaximumSupplementaryGroups];
        var count = groups.Length;
        fixed (byte* name = encoded)
        fixed (uint* values = groups)
        {
            var result = GetGroupList(name, user.Gid, values, ref count);
            if (result < 0 || count != 1 || result != 1 || groups[0] != user.Gid) throw Mismatch();
        }
    }

    private static byte[] NameBytes(string name)
    {
        if (!LinuxRunAccountNames.IsGenerated(name)) throw LinuxRunAccountNames.Invalid();
        return Encoding.ASCII.GetBytes(name + "\0");
    }

    private static unsafe string Text(nint address, byte* buffer, int size, int maximum)
    {
        if (!Contains(address, 1, buffer, size)) throw NssFailed();
        var available = (int)((nuint)buffer + (nuint)size - (nuint)address);
        var bytes = new ReadOnlySpan<byte>((void*)address, Math.Min(available, maximum + 1));
        var length = bytes.IndexOf((byte)0);
        if (length < 0 || length > maximum) throw NssFailed();
        try { return StrictUtf8.GetString(bytes[..length]); }
        catch (DecoderFallbackException) { throw NssFailed(); }
    }

    private static unsafe bool Contains(nint address, int length, byte* buffer, int size) =>
        (nuint)address >= (nuint)buffer && (nuint)address <= (nuint)buffer + (nuint)size - (nuint)length;

    private static void RequirePlatform()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64
            || RuntimeInformation.OSArchitecture != Architecture.X64 || GetUid() != 0 || GetEffectiveUid() != 0)
            throw new LinuxRunAccountException(LinuxRunAccountFailure.UnsupportedPlatform);
    }

    private static LinuxRunAccountException NssFailed() => new(LinuxRunAccountFailure.NssFailed);
    private static LinuxRunAccountException Mismatch() => new(LinuxRunAccountFailure.IdentityMismatch);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePasswd
    {
        internal nint Name;
        internal nint Password;
        internal uint Uid;
        internal uint Gid;
        internal nint Gecos;
        internal nint Directory;
        internal nint Shell;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeGroup
    {
        internal nint Name;
        internal nint Password;
        internal uint Gid;
        internal nint Members;
    }

    [LibraryImport("libc", EntryPoint = "getuid")]
    private static partial uint GetUid();
    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEffectiveUid();
    [LibraryImport("libc", EntryPoint = "getpwnam_r")]
    private static unsafe partial int GetPwName(byte* name, out NativePasswd entry, byte* buffer, nuint length, out nint result);
    [LibraryImport("libc", EntryPoint = "getpwuid_r")]
    private static unsafe partial int GetPwUid(uint uid, out NativePasswd entry, byte* buffer, nuint length, out nint result);
    [LibraryImport("libc", EntryPoint = "getgrnam_r")]
    private static unsafe partial int GetGrName(byte* name, out NativeGroup entry, byte* buffer, nuint length, out nint result);
    [LibraryImport("libc", EntryPoint = "getgrgid_r")]
    private static unsafe partial int GetGrGid(uint gid, out NativeGroup entry, byte* buffer, nuint length, out nint result);
    [LibraryImport("libc", EntryPoint = "getgrouplist")]
    private static unsafe partial int GetGroupList(byte* name, uint gid, uint* groups, ref int count);
}
