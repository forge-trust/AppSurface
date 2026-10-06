using System.Runtime.InteropServices;
using ForgeTrust.AppSurface.Evidence.Contracts;
using Tmds.DBus.Protocol;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Sampled activation condition; constructing it does not establish an armed owner.</summary>
internal sealed record LinuxOwnerCondition(string Kind, bool Trigger, bool Negate, string Parameter, int Result);

/// <summary>Closed owner startup/termination data read from systemd, without ownership authority.</summary>
/// <remarks>
/// The read-only projection snapshots compound variants and rejects missing or wrong types. The fixed
/// env bootstrap clears inherited variables before invoking the selected runtime. These sampled facts
/// cannot prove that the trusted OS loader or bootstrap ran safely: before execution, that bootstrap
/// must pin an actual native runtime image, not a script or a file subject to execvp's shell fallback.
/// Managed launch-input checks still bind the executing dotnet process and managed entry separately.
/// </remarks>
internal sealed record LinuxOwnerFacts(LinuxUnitProperties Unit, string Restart, bool SendSigKill,
    int FinalKillSignal, ulong ActiveSinceMicroseconds, bool ConditionResult,
    IReadOnlyList<LinuxOwnerCondition> Conditions, string Executable, IReadOnlyList<string> Arguments,
    bool IgnoreFailure, IReadOnlyList<string> Environment, IReadOnlyList<string> UnsetEnvironment,
    int EnvironmentFileCount, int PassEnvironmentCount)
{
    /// <summary>Gets the only permitted trusted-OS environment-clearing executable.</summary>
    /// <remarks>This fixed pathname is a contract, not proof of the installed executable or its loader.</remarks>
    internal const string BootstrapExecutable = "/usr/bin/env";

    /// <summary>Gets the complete fixed argv count: env, -i, five assignments and six runtime/role arguments.</summary>
    internal const int BootstrapArgumentCount = 13;

    /// <summary>Projects compound-field metadata after the backend has checked native struct-array wire shape.</summary>
    /// <remarks>Portable variant-array fixtures are metadata only. The actual backend rejects them before this projection.</remarks>
    internal static LinuxOwnerFacts Parse(IReadOnlyDictionary<string, VariantValue> unit,
        IReadOnlyDictionary<string, VariantValue> service)
    {
        try
        {
            var rawConditions = unit["Conditions"];
            Array(rawConditions, 128);
            var conditions = new List<LinuxOwnerCondition>();
            for (var i = 0; i < rawConditions.Count; i++)
            {
                var item = rawConditions.GetItem(i);
                Struct(item, 5);
                conditions.Add(new(Text(item.GetItem(0)), item.GetItem(1).GetBool(), item.GetItem(2).GetBool(),
                    Text(item.GetItem(3)), item.GetItem(4).GetInt32()));
            }
            var commands = service["ExecStart"];
            Array(commands, 1);
            if (commands.Count != 1) throw Invalid();
            var command = commands.GetItem(0);
            Struct(command, 10);
            // Check the complete observed command tuple, rather than accepting an arbitrary leading prefix.
            for (var i = 3; i < 7; i++) _ = command.GetItem(i).GetUInt64();
            _ = command.GetItem(7).GetUInt32();
            _ = command.GetItem(8).GetInt32();
            _ = command.GetItem(9).GetInt32();
            var files = service["EnvironmentFiles"];
            Array(files, 0);
            return new(LinuxUnitProperties.Parse(unit, service), Text(service["Restart"]), service["SendSIGKILL"].GetBool(),
                service["FinalKillSignal"].GetInt32(), unit["ActiveEnterTimestampMonotonic"].GetUInt64(),
                unit["ConditionResult"].GetBool(), conditions.AsReadOnly(), Text(command.GetItem(0)),
                Strings(command.GetItem(1), BootstrapArgumentCount), command.GetItem(2).GetBool(), Strings(service["Environment"], 5),
                Strings(service["UnsetEnvironment"], 64), files.Count, Strings(service["PassEnvironment"], 0).Count);
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or ArgumentException
            or IndexOutOfRangeException or EvidenceAdmissionException)
        { throw Invalid(); }
    }

    /// <summary>Checks complete fixed startup policy and that independent runtime plus stop fits the original job.</summary>
    /// <remarks>
    /// Requires the complete environment-clearing wrapper in fixed order. Supplied bootstrap paths may
    /// contain neither environment substitution ($) nor systemd specifier (%) syntax. This pure void
    /// check issues no activation, native guard or admission and cannot establish pre-CLR execution.
    /// </remarks>
    internal void Require(Guid runId, uint processId, string runtimeHost, string entryPath, string requestPath,
        ulong nowMicroseconds, TimeSpan originalRemaining)
    {
        var expectedArguments = BootstrapArguments(runtimeHost, entryPath, requestPath);
        if (runId == Guid.Empty || processId == 0 || Unit.Id != LinuxUnitName.Create(LinuxUnitRole.Owner, runId).Value
            || Unit.LoadState != "loaded" || Unit.ActiveState != "active" || Unit.SubState != "running"
            || Unit.MainPid != processId || Unit.ExecMainPid != processId || Unit.User != "0" || Unit.Group != "0"
            || Unit.Type != "exec" || Unit.KillMode != "control-group" || Unit.RemainAfterExit
            || Restart != "no" || !SendSigKill || FinalKillSignal != 9 || IgnoreFailure
            || Unit.ControlGroup != "/system.slice/" + Unit.Id
            || !ConditionResult || Conditions.Count != 1 || Conditions[0] != new LinuxOwnerCondition(
                "ConditionPathExists", false, false, GuardPath(runId), 1)
            || Executable != BootstrapExecutable || !Arguments.SequenceEqual(expectedArguments, StringComparer.Ordinal)
            || EnvironmentFileCount != 0 || PassEnvironmentCount != 0
            || !Environment.Order(StringComparer.Ordinal).SequenceEqual(FixedEnvironment.Order(StringComparer.Ordinal), StringComparer.Ordinal)
            || UnsetEnvironment.Count != UnsafeEnvironment.Count || !UnsetEnvironment.Order(StringComparer.Ordinal)
                .SequenceEqual(UnsafeEnvironment.Order(StringComparer.Ordinal), StringComparer.Ordinal)) throw Invalid();
        RequireDeadline(nowMicroseconds, originalRemaining);
    }

    /// <summary>Copies the exact bootstrap argv as read-only data, without selecting files or dispatching a process.</summary>
    /// <param name="runtimeHost">Already protected, canonical absolute native runtime image without '=', verified before execution.</param>
    /// <param name="entryPath">Already protected, canonical absolute managed entry.</param>
    /// <param name="requestPath">Already protected, canonical absolute root request.</param>
    /// <remarks>
    /// env -i precedes exactly five fixed assignments, followed by the unchanged runtime/entry and
    /// supervise role arguments. No caller environment, extra env option or expansion syntax is accepted.
    /// The runtime host cannot contain '=' because env would consume it as another assignment before
    /// selecting a command. Canonical entry/request paths may contain '=' after that command boundary.
    /// The actual trusted bootstrap must additionally pin executable format/bytes and OS dependencies;
    /// this data helper performs no filesystem, ELF, identity, guard or authority validation.
    /// </remarks>
    internal static IReadOnlyList<string> BootstrapArguments(string runtimeHost, string entryPath, string requestPath)
        => ManagedArguments(EvidenceProcessRole.Supervisor, runtimeHost, entryPath, requestPath);

    /// <summary>Copies the fixed environment-clearing argv for either reserved managed role.</summary>
    /// <param name="role">Only Supervisor or Worker; no arbitrary command or option is accepted.</param>
    /// <param name="runtimeHost">Pinned native runtime pathname, excluding assignment and expansion syntax.</param>
    /// <param name="entryPath">Pinned same-image managed entry pathname.</param>
    /// <param name="controlPath">Protected request for Supervisor, or authenticated socket for Worker.</param>
    /// <remarks>
    /// The worker retains its existing 4096-byte deployment-path boundary; owner operands are bounded
    /// to 4095 bytes. Socket and deployment containment checks remain in the worker recipe. This method
    /// copies data only and cannot authenticate files, credentials, environment clearing or readiness.
    /// Worker grammar failures keep fixed ASEVD410; owner/unknown-role failures keep fixed ASEVD402.
    /// </remarks>
    internal static IReadOnlyList<string> ManagedArguments(EvidenceProcessRole role, string runtimeHost,
        string entryPath, string controlPath)
    {
        if (role is not (EvidenceProcessRole.Supervisor or EvidenceProcessRole.Worker)) throw Invalid();
        var maximum = role == EvidenceProcessRole.Worker ? 4096 : 4095;
        foreach (var path in new[] { runtimeHost, entryPath, controlPath })
            if (string.IsNullOrEmpty(path) || path[0] != '/' || path.Contains('\\')
                || path.Contains('$') || path.Contains('%') || path.Any(char.IsControl)
                || System.Text.Encoding.UTF8.GetByteCount(path) > maximum
                || path[1..].Split('/').Any(part => part.Length == 0 || part is "." or ".."))
                throw role == EvidenceProcessRole.Worker ? LinuxSystemdBackend.InvalidControl() : Invalid();
        if (runtimeHost.Contains('='))
            throw role == EvidenceProcessRole.Worker ? LinuxSystemdBackend.InvalidControl() : Invalid();
        return System.Array.AsReadOnly(new[]
        {
            BootstrapExecutable, "-i", FixedEnvironment[0], FixedEnvironment[1], FixedEnvironment[2],
            FixedEnvironment[3], FixedEnvironment[4], runtimeHost, entryPath,
            "evidence", role == EvidenceProcessRole.Worker ? "worker" : "supervise",
            role == EvidenceProcessRole.Worker ? "--control" : "--request", controlPath
        });
    }

    /// <summary>Checks the remaining independent owner lifetime against the unchanged original job deadline.</summary>
    internal void RequireDeadline(ulong nowMicroseconds, TimeSpan originalRemaining, bool allowStopping = false)
    {
        if (ActiveSinceMicroseconds == 0 || nowMicroseconds < ActiveSinceMicroseconds
            || originalRemaining <= TimeSpan.Zero || originalRemaining > TimeSpan.FromHours(1)
            || Unit.RuntimeMaxMicroseconds is 0 or > 3_600_000_000UL
            || Unit.TimeoutStopMicroseconds is 0 or > 30_000_000UL) throw Invalid();
        var elapsed = nowMicroseconds - ActiveSinceMicroseconds;
        var lifetime = Unit.RuntimeMaxMicroseconds + Unit.TimeoutStopMicroseconds;
        // The owner must still be in its running allowance, with its stop reserve fitting the original job.
        if (elapsed >= lifetime || (!allowStopping && elapsed >= Unit.RuntimeMaxMicroseconds)
            || lifetime - elapsed > (ulong)(originalRemaining.Ticks / 10)) throw Invalid();
    }

    /// <summary>Gets the fixed per-generation path consumed before any dependent start.</summary>
    internal static string GuardPath(Guid id) => $"/run/appsurface-evidence-owners/{id:N}/armed";

    /// <summary>Gets the five fixed assignments required in both service environment and bootstrap argv.</summary>
    /// <remarks>Service Environment metadata alone does not prove that all inherited variables were cleared.</remarks>
    internal static IReadOnlyList<string> FixedEnvironment { get; } = System.Array.AsReadOnly(new[]
        { "PATH=/usr/bin:/bin", "HOME=/nonexistent", "LANG=C.UTF-8", "DOTNET_CLI_TELEMETRY_OPTOUT=1", "DOTNET_CLI_HOME=/tmp" });

    /// <summary>Gets the fixed runtime-injection variables removed before the managed image starts.</summary>
    internal static IReadOnlyList<string> UnsafeEnvironment { get; } = System.Array.AsReadOnly(new[]
    {
        "DOTNET_STARTUP_HOOKS", "DOTNET_ADDITIONAL_DEPS", "DOTNET_SHARED_STORE", "DOTNET_ROOT",
        "DOTNET_ROOT_X64", "DOTNET_HOST_PATH", "DOTNET_ROLL_FORWARD", "DOTNET_ROLL_FORWARD_TO_PRERELEASE",
        "DOTNET_MULTILEVEL_LOOKUP", "CORECLR_ENABLE_PROFILING", "CORECLR_PROFILER", "CORECLR_PROFILER_PATH",
        "CORECLR_PROFILER_PATH_64", "COR_ENABLE_PROFILING", "COR_PROFILER", "COR_PROFILER_PATH",
        "COMPlus_ReadyToRun", "COMPlus_ZapDisable"
    });

    private static IReadOnlyList<string> Strings(VariantValue value, int maximum)
    {
        Array(value, maximum);
        if (value.ItemType != VariantValueType.String) throw Invalid();
        return System.Array.AsReadOnly(Enumerable.Range(0, value.Count).Select(i => Text(value.GetItem(i))).ToArray());
    }
    private static void Array(VariantValue value, int maximum)
    { if (value.Type != VariantValueType.Array || value.Count < 0 || value.Count > maximum) throw Invalid(); }
    private static void Struct(VariantValue value, int count)
    { if (value.Type != VariantValueType.Struct || value.Count != count) throw Invalid(); }
    private static string Text(VariantValue value)
    {
        var text = value.GetString();
        if (text.Length > 4096 || text.Any(char.IsControl)) throw Invalid();
        return text;
    }
    internal static EvidenceAdmissionException Invalid() => new("ASEVD402", "The protected owner activation is invalid or expired.");
}

/// <summary>Live root service with a consumed single-use activation guard, before account or worker creation.</summary>
/// <remarks>
/// A private native factory binds actual systemd/PID/UID/GID/cgroup, retained deployment and guard bytes.
/// This is owner authority only, never worker admission or accepted consumer proof. Dispose never recreates
/// the armed file. The protected bootstrap retains the generation until all pending starts and units settle.
/// </remarks>
internal sealed partial class LinuxOwnerActivation : IDisposable
{
    private readonly EvidenceProtectedLaunchInput _input;
    private readonly LinuxOwnerFacts _facts;
    private readonly LinuxProcessIdentity _identity;
    private readonly LinuxOwnerGuard _guard;
    private readonly SupervisionSingleAttempt _accountCreation = new();
    private readonly SupervisionSingleAttempt _workerCreation = new();
    private readonly SupervisionTeardownDeadline _teardown = new(TimeProvider.System);
    private int _workerReserved;
    private int _closed;
    private int _failed;
    private int _cleanupRejected;

    private LinuxOwnerActivation(EvidenceProtectedLaunchInput input, LinuxOwnerFacts facts,
        LinuxProcessIdentity identity, LinuxOwnerGuard guard, Guid runId)
    { _input = input; _facts = facts; _identity = identity; _guard = guard; RunId = runId; }

    /// <summary>Gets the generation derived from the actual current systemd unit, not from request JSON.</summary>
    internal Guid RunId { get; }
    /// <summary>Gets the actual selected owner name.</summary>
    internal LinuxUnitName Unit => LinuxUnitName.Create(LinuxUnitRole.Owner, RunId);
    /// <summary>Gets the original job remainder after rechecking live owner/guard and independent lifetime.</summary>
    internal TimeSpan Remaining { get { RequireActive(default); return _input.Remaining; } }
    /// <summary>Gets the original remainder, lowered by the first shared teardown expiry once explicitly begun.</summary>
    /// <remarks>Reading this property does not begin teardown or close admission during successful utility creation.</remarks>
    internal TimeSpan CleanupRemaining
    {
        get { RequireCleanup(default); return _teardown.IsStarted ? _teardown.Remaining : _input.Remaining; }
    }
    /// <summary>Gets the protected stop reserve, lowered for fixed short account utilities.</summary>
    internal TimeSpan UtilityStopping => TimeSpan.FromSeconds(Math.Min(_input.Request.StoppingSeconds, 5));
    /// <summary>Gets a cleanup allowance capped by both the original job and the protected cleanup reserve.</summary>
    internal TimeSpan CleanupAllowance => TimeSpan.FromTicks(Math.Min(CleanupRemaining.Ticks,
        TimeSpan.FromSeconds(_input.Request.CleanupSeconds).Ticks));

    /// <summary>Closes work admission and retains one cumulative root teardown expiry.</summary>
    /// <remarks>
    /// After an actual worker claim the allowance includes the existing separate collection and cleanup
    /// reserves. Before any worker claim, failed account rollback has only the cleanup reserve. Both are
    /// capped by the original job and never renewed; per-unit stopping remains a separate smaller bound.
    /// Ordinary successful account-utility settlement must not invoke this transition.
    /// </remarks>
    internal void BeginRootTeardown()
    {
        CloseWorkAdmission();
        RequireCleanup(default);
        var seconds = _input.Request.CleanupSeconds
            + (Volatile.Read(ref _workerReserved) != 0 ? _input.Request.CollectionSeconds : 0);
        _teardown.Begin(_input.Remaining, TimeSpan.FromSeconds(seconds));
        RequireCleanup(default);
    }

    /// <summary>Gets the borrowed owner-owned teardown token after the explicit irreversible transition.</summary>
    /// <remarks>No consumer may cancel, dispose or replace its source; local request tokens remain separate.</remarks>
    internal CancellationToken RootTeardownToken { get { RequireCleanup(default); return _teardown.Token; } }

    /// <summary>Borrows cancellation before teardown begins so already-owned I/O receives the first expiry.</summary>
    /// <remarks>
    /// This does not start the clock or admit work. Actual root continuity remains mandatory. Existing
    /// observers link this token at acquisition and still join their original operations after cancellation.
    /// </remarks>
    internal CancellationToken TeardownCancellation { get { RequireControlIdentity(default); return _teardown.Cancellation; } }

    /// <summary>Requires the actual root continuity and original retained input while observing final worker exit.</summary>
    /// <remarks>This permits no new work or READY admission after teardown starts.</remarks>
    internal void RequireCleanupLaunchInput(EvidenceProtectedLaunchInput input, CancellationToken token)
    {
        if (!ReferenceEquals(input, _input)) throw LinuxOwnerFacts.Invalid();
        RequireControlIdentity(token);
        input.Recheck(token);
        RequireControlIdentity(token);
    }

    /// <summary>Claims account creation once per actual owner, before NSS work or any utility reservation.</summary>
    /// <remarks>Failure or cancellation never releases this claim; another holder cannot delete the first holder's accounts.</remarks>
    internal void ClaimAccountCreation(CancellationToken token)
    {
        RequireActive(token);
        _accountCreation.Claim();
        RequireActive(token);
    }

    /// <summary>Binds the actual retained launch input and claims one worker holder before native acquisition.</summary>
    /// <remarks>
    /// Reference identity is required, not equal decoded request fields. Failure never releases the claim;
    /// a second holder cannot stop or adopt the first holder's generated worker unit.
    /// </remarks>
    internal void ClaimWorkerCreation(EvidenceProtectedLaunchInput input, CancellationToken token)
    {
        RequireLaunchInput(input, token);
        _workerCreation.Claim();
        Interlocked.Exchange(ref _workerReserved, 1);
        RequireLaunchInput(input, token);
    }

    /// <summary>Rechecks the same native launch input authenticated by this owner; data cannot replace it.</summary>
    internal void RequireLaunchInput(EvidenceProtectedLaunchInput input, CancellationToken token)
    {
        if (!ReferenceEquals(input, _input)) throw LinuxOwnerFacts.Invalid();
        RequireActive(token);
        input.Recheck(token);
        RequireActive(token);
    }

    /// <summary>Authenticates the existing protected service and atomically consumes its armed marker.</summary>
    internal static async Task<LinuxOwnerActivation> OpenAsync(EvidenceProtectedLaunchInput input,
        LinuxSystemdBackend backend, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(backend);
        LinuxProtectedDeployment.RequirePlatform();
        input.Recheck(token);
        var facts = await backend.ReadOwnerAsync(token).ConfigureAwait(false);
        const string prefix = "appsurface-evidence-owner-";
        if (!facts.Unit.Id.StartsWith(prefix, StringComparison.Ordinal) || !facts.Unit.Id.EndsWith(".service", StringComparison.Ordinal)
            || !Guid.TryParseExact(facts.Unit.Id[prefix.Length..^8], "N", out var id)) throw LinuxOwnerFacts.Invalid();
        var request = input.Request;
        facts.Require(id, (uint)System.Environment.ProcessId, request.RuntimeHost, request.EntryPath, input.RequestPath,
            MonotonicMicroseconds(), input.Remaining);
        LinuxProcessIdentity? identity = null;
        LinuxOwnerGuard? guard = null;
        try
        {
            identity = LinuxProcessIdentity.CaptureOwner((uint)System.Environment.ProcessId,
                LinuxUnitName.Create(LinuxUnitRole.Owner, id), token);
            input.Recheck(token);
            guard = LinuxOwnerGuard.Consume(id, token);
            var owner = new LinuxOwnerActivation(input, facts, identity, guard, id);
            owner.RequireActive(token);
            return owner;
        }
        catch { guard?.Dispose(); identity?.Dispose(); throw; }
    }

    /// <summary>Rechecks the actual live owner, consumed marker and original deadline; any failure stays latched.</summary>
    internal void RequireActive(CancellationToken token)
    {
        if (Volatile.Read(ref _closed) != 0 || Volatile.Read(ref _failed) != 0) throw LinuxOwnerFacts.Invalid();
        try
        {
            token.ThrowIfCancellationRequested();
            _facts.RequireDeadline(MonotonicMicroseconds(), _input.Remaining);
            _identity.Recheck(token);
            _guard.Recheck(token);
            _facts.RequireDeadline(MonotonicMicroseconds(), _input.Remaining);
            token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { Interlocked.Exchange(ref _failed, 1); throw; }
        catch
        {
            Interlocked.Exchange(ref _failed, 1);
            Interlocked.Exchange(ref _cleanupRejected, 1);
            throw;
        }
    }

    /// <summary>Allows strict cleanup after admission closes, within the original external stop/job bounds.</summary>
    /// <remarks>A failed or canceled start cannot reopen new work; cleanup still needs the original live root and guard.</remarks>
    internal void RequireCleanup(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _closed) != 0 || Volatile.Read(ref _cleanupRejected) != 0)
            throw LinuxOwnerFacts.Invalid();
        try
        {
            _facts.RequireDeadline(MonotonicMicroseconds(), _input.Remaining, allowStopping: true);
            if (_teardown.IsStarted) { _teardown.Token.ThrowIfCancellationRequested(); _ = _teardown.Remaining; }
            _identity.Recheck(token);
            _guard.Recheck(token);
            _facts.RequireDeadline(MonotonicMicroseconds(), _input.Remaining, allowStopping: true);
            if (_teardown.IsStarted) { _teardown.Token.ThrowIfCancellationRequested(); _ = _teardown.Remaining; }
            token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch
        {
            Interlocked.Exchange(ref _failed, 1);
            Interlocked.Exchange(ref _cleanupRejected, 1);
            throw;
        }
    }

    /// <summary>Checks live control transport continuity without granting active work admission.</summary>
    /// <remarks>
    /// Local I/O cancellation is checked outside the native inspection. The original deadline,
    /// retained root identity and consumed marker remain mandatory; no new allowance is created.
    /// Ready and execution handlers must separately require active admission.
    /// </remarks>
    internal void RequireControlIdentity(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        RequireCleanup(default);
        token.ThrowIfCancellationRequested();
    }

    /// <summary>Irreversibly closes work admission without asserting a native identity failure.</summary>
    /// <remarks>Used for interrupted read-only inspection. This never clears either failure latch or grants cleanup.</remarks>
    internal void CloseWorkAdmission() => Interlocked.Exchange(ref _failed, 1);

    /// <summary>Closes retained guard handles after users and pending starts join; never rearms a generation.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            try { _teardown.Dispose(); }
            finally
            {
                try { _guard.Dispose(); }
                finally { _identity.Dispose(); }
            }
        }
    }

    private static ulong MonotonicMicroseconds()
    {
        if (ClockGetTime(1, out var time) != 0 || time.Seconds < 0 || time.Nanoseconds is < 0 or >= 1_000_000_000)
            throw LinuxOwnerFacts.Invalid();
        return checked((ulong)time.Seconds * 1_000_000 + (ulong)time.Nanoseconds / 1000);
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeTime { internal long Seconds; internal long Nanoseconds; }
    [LibraryImport("libc", EntryPoint = "clock_gettime", SetLastError = true)]
    private static partial int ClockGetTime(int clock, out NativeTime time);
}
