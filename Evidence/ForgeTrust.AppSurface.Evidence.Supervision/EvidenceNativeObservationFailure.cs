using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Closed checkpoints of the actual root composition, not native completion or admission facts.</summary>
internal enum EvidenceNativeObservationPhase
{
    /// <summary>Unknown or invalid supplied phase data.</summary>
    Unknown,
    /// <summary>Original caller cancellation check.</summary>
    CallerCancellation,
    /// <summary>Retained protected request/deployment acquisition.</summary>
    ProtectedInput,
    /// <summary>Original job token setup.</summary>
    JobDeadline,
    /// <summary>Actual system bus connection.</summary>
    BackendConnect,
    /// <summary>Actual owner activation.</summary>
    OwnerActivation,
    /// <summary>Protected empty plan resolution.</summary>
    Plan,
    /// <summary>Actual account preparation.</summary>
    AccountCreate,
    /// <summary>Actual workspace preparation.</summary>
    WorkspaceCreate,
    /// <summary>Actual listener binding.</summary>
    ListenerBind,
    /// <summary>Actual worker holder creation.</summary>
    WorkerCreate,
    /// <summary>Owned worker startup.</summary>
    WorkerStart,
    /// <summary>Actual server composition.</summary>
    ServerCreate,
    /// <summary>Server cancellation-source setup.</summary>
    ServerLifetime,
    /// <summary>Original server operation and join.</summary>
    ServerRun,
    /// <summary>Server successful-completion check.</summary>
    ServerCompletion,
    /// <summary>Original natural worker exit join.</summary>
    WorkerExit,
    /// <summary>Worker stop and physical join.</summary>
    WorkerStop,
    /// <summary>Worker successful-completion check.</summary>
    WorkerCompletion,
    /// <summary>Original root teardown reservation.</summary>
    BeginTeardown,
    /// <summary>Actual root custody transfer.</summary>
    Custody,
    /// <summary>Actual final-file verification.</summary>
    FileVerification,
    /// <summary>Cleanup teardown initialization.</summary>
    CleanupBegin,
    /// <summary>Actual server I/O interruption.</summary>
    ServerCancel,
    /// <summary>Actual listener disposal.</summary>
    ListenerClose,
    /// <summary>Cleanup join of the original server task.</summary>
    ServerJoin,
    /// <summary>Cleanup join of the owned worker.</summary>
    WorkerJoin,
    /// <summary>Cleanup root custody attempt.</summary>
    CleanupCustody,
    /// <summary>Actual account cleanup.</summary>
    AccountsClose,
    /// <summary>Actual worker disposal.</summary>
    WorkerClose,
    /// <summary>Actual custody holder disposal.</summary>
    CustodyClose,
    /// <summary>Actual workspace disposal.</summary>
    WorkspaceClose,
    /// <summary>Owner identity and final remaining-interval capture.</summary>
    OwnerFinalCheck,
    /// <summary>Server lifetime-source disposal.</summary>
    ServerLifetimeClose,
    /// <summary>Job source disposal.</summary>
    JobClose,
    /// <summary>Actual owner disposal.</summary>
    OwnerClose,
    /// <summary>Protected input disposal.</summary>
    InputClose,
    /// <summary>Actual backend disposal.</summary>
    BackendClose,
    /// <summary>Original captured final-close deadline check.</summary>
    FinalDeadline,
    /// <summary>Missing detached result after original operations.</summary>
    ResultCheck,
}

/// <summary>Finite error families; arbitrary types, messages, paths and inner exceptions are never retained.</summary>
internal enum EvidenceNativeObservationErrorKind
{
    /// <summary>An unrecognized exception or missing result.</summary>
    Unknown,
    /// <summary>A protected admission diagnostic.</summary>
    Admission,
    /// <summary>A fixed account lifecycle diagnostic.</summary>
    Accounts,
    /// <summary>A fixed output-pipe lifecycle diagnostic.</summary>
    OutputPipe,
    /// <summary>A fixed control-line diagnostic.</summary>
    ControlLine,
    /// <summary>Operation cancellation.</summary>
    Cancelled,
    /// <summary>A timeout.</summary>
    Timeout,
    /// <summary>Filesystem or stream I/O failure.</summary>
    Io,
    /// <summary>Access denied.</summary>
    AccessDenied,
    /// <summary>Unsupported platform or operation.</summary>
    Unsupported,
    /// <summary>An already disposed object.</summary>
    Disposed,
    /// <summary>An invalid argument.</summary>
    Argument,
    /// <summary>Invalid JSON or scalar format.</summary>
    InvalidData,
    /// <summary>An invalid operation.</summary>
    InvalidOperation,
}

/// <summary>Closed account preparation and deletion checkpoints; values grant no account ownership.</summary>
internal enum LinuxAccountPreparationStage
{
    /// <summary>Unrecognized stage data.</summary>
    Unknown,
    /// <summary>Initial live NSS absence check.</summary>
    NamesAbsent,
    /// <summary>Owner check and pending-name reservation.</summary>
    ReserveUtility,
    /// <summary>Actual utility holder acquisition.</summary>
    UtilityCreate,
    /// <summary>Owned forward utility execution.</summary>
    UtilityExecute,
    /// <summary>Final forward/reverse NSS identity read.</summary>
    IdentityRead,
    /// <summary>Live created-holder verification.</summary>
    OwnershipVerify,
    /// <summary>Cleanup owner, custody and pending-utility checks.</summary>
    CleanupCheck,
    /// <summary>Live deletion identity/absence check.</summary>
    CleanupNameCheck,
    /// <summary>Owned reverse utility execution.</summary>
    CleanupUtility,
    /// <summary>Final all-names-absent check.</summary>
    FinalNamesAbsent,
    /// <summary>Final cleanup custody/owner verification.</summary>
    FinalOwnershipCheck,
}

/// <summary>Closed phases of one actual account utility; values are diagnostic data only.</summary>
internal enum LinuxAccountUtilityStage
{
    /// <summary>No utility phase or unrecognized phase data.</summary>
    Unknown,
    /// <summary>Original owner and remaining-budget checks.</summary>
    OwnerCheck,
    /// <summary>Output pipe creation and pump registration.</summary>
    Pipes,
    /// <summary>Actual system-bus connection.</summary>
    BackendConnect,
    /// <summary>Fixed unit recipe construction.</summary>
    Recipe,
    /// <summary>Original pending start and FD transfer.</summary>
    Start,
    /// <summary>Closure of local write copies.</summary>
    CloseWrites,
    /// <summary>Actual typed unit read.</summary>
    UnitRead,
    /// <summary>Existing terminal policy predicate.</summary>
    TerminalCheck,
    /// <summary>Existing bounded observation delay.</summary>
    ObservationDelay,
    /// <summary>Whole-run teardown reservation after failure.</summary>
    BeginTeardown,
    /// <summary>Independent stop, original start join and second stop.</summary>
    Stop,
    /// <summary>Actual kernel group inspection.</summary>
    GroupRead,
    /// <summary>Original output pump join and receipt checks.</summary>
    OutputJoin,
    /// <summary>Actual pipe disposal.</summary>
    PipeDispose,
    /// <summary>Actual starting-backend disposal.</summary>
    BackendDispose,
    /// <summary>Final physical-settlement owner check.</summary>
    PhysicalSettlement,
    /// <summary>Final original caller/owner check.</summary>
    FinalOwnerCheck,
}

/// <summary>Immutable bounded first account fault; no names, paths, outputs or exception objects survive.</summary>
internal sealed class LinuxAccountFailure
{
    private LinuxAccountFailure(LinuxAccountPreparationStage preparation, LinuxAccountUtilityStage utility,
        LinuxRunAccountOperation? operation, EvidenceNativeObservationErrorKind kind, string? code,
        LinuxRunAccountFailure? accountCode, int? execCode, int? execStatus, LinuxSystemdStartError? dbus)
    {
        PreparationStage = preparation; UtilityStage = utility; Operation = operation; ErrorKind = kind;
        DiagnosticCode = code; AccountCode = accountCode; ExecMainCode = execCode; ExecMainStatus = execStatus;
        DBusCategory = dbus;
    }

    /// <summary>Gets the closed account checkpoint.</summary>
    internal LinuxAccountPreparationStage PreparationStage { get; }
    /// <summary>Gets the closed utility checkpoint, or Unknown when no utility was executing.</summary>
    internal LinuxAccountUtilityStage UtilityStage { get; }
    /// <summary>Gets the actual command operation, or null for nonutility/invalid operation data.</summary>
    internal LinuxRunAccountOperation? Operation { get; }
    /// <summary>Gets the original error family without retaining the exception.</summary>
    internal EvidenceNativeObservationErrorKind ErrorKind { get; }
    /// <summary>Gets only an existing allowlisted admission code.</summary>
    internal string? DiagnosticCode { get; }
    /// <summary>Gets an actual closed account error code, or null.</summary>
    internal LinuxRunAccountFailure? AccountCode { get; }
    /// <summary>Gets a bounded actual typed unit code; null means no sample or invalid numeric data.</summary>
    internal int? ExecMainCode { get; }
    /// <summary>Gets a bounded actual typed unit status; null means no sample or invalid numeric data.</summary>
    internal int? ExecMainStatus { get; }
    /// <summary>Gets the same backend's actual closed D-Bus start-error category, never its raw name/message.</summary>
    internal LinuxSystemdStartError? DBusCategory { get; }

    /// <summary>Projects detached data only; supplied samples and enums cannot issue native ownership.</summary>
    /// <remarks>Native callers supply numbers only after ReadUnitAsync returned. Inner errors/messages are ignored.</remarks>
    internal static LinuxAccountFailure Capture(LinuxAccountPreparationStage preparation,
        LinuxAccountUtilityStage utility, LinuxRunAccountOperation? operation, Exception? error,
        int? execCode = null, int? execStatus = null, LinuxSystemdStartError? dbus = null)
    {
        var projected = EvidenceNativeObservationFailure.Capture(EvidenceNativeObservationPhase.AccountCreate, error);
        return new(Enum.IsDefined(preparation) ? preparation : LinuxAccountPreparationStage.Unknown,
            Enum.IsDefined(utility) ? utility : LinuxAccountUtilityStage.Unknown,
            operation is { } op && Enum.IsDefined(op) ? op : null, projected.ErrorKind, projected.DiagnosticCode,
            error is LinuxRunAccountException account && Enum.IsDefined(account.Failure) ? account.Failure : null,
            execCode is >= 1 and <= 6 && execStatus is >= 0 and <= 255 ? execCode : null,
            execCode is >= 1 and <= 6 && execStatus is >= 0 and <= 255 ? execStatus : null,
            dbus is { } category && Enum.IsDefined(category) ? category : null);
    }

    /// <summary>Serializes exactly nine closed fields without native reads, raw error text or completion claims.</summary>
    internal string ToJson() => "{\"preparation_stage\":\"" + PreparationStage + "\",\"utility_stage\":\"" + UtilityStage
        + "\",\"operation\":" + Text(Operation) + ",\"error_kind\":\"" + ErrorKind
        + "\",\"diagnostic_code\":" + Text(DiagnosticCode) + ",\"account_code\":" + Text(AccountCode)
        + ",\"exec_main_code\":" + Number(ExecMainCode) + ",\"exec_main_status\":" + Number(ExecMainStatus)
        + ",\"dbus_category\":" + Text(DBusCategory) + "}";

    private static string Text(object? value) => value is null ? "null" : "\"" + value + "\"";
    private static string Number(int? value) => value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null";
}

/// <summary>Best-effort first account-fault latch; later rollback faults never replace an earlier projection.</summary>
internal sealed class LinuxAccountFailureLatch
{
    private LinuxAccountFailure? _first;
    /// <summary>Gets detached first-fault data; absence establishes no success.</summary>
    internal LinuxAccountFailure? First => Volatile.Read(ref _first);
    /// <summary>Retains only a closed projection. Diagnostic allocation/capture failure cannot change lifecycle outcomes.</summary>
    internal void Capture(LinuxAccountPreparationStage preparation, LinuxAccountUtilityStage utility,
        LinuxRunAccountOperation? operation, Exception? error, int? code = null, int? status = null,
        LinuxSystemdStartError? dbus = null, LinuxAccountFailure? first = null)
    {
        try
        {
            if (First is null) Interlocked.CompareExchange(ref _first,
                first ?? LinuxAccountFailure.Capture(preparation, utility, operation, error, code, status, dbus), null);
        }
        catch (Exception) { /* Diagnostic capture must never replace the original operation. */ }
    }
}

/// <summary>Closed server checkpoints; names are diagnostic data, never a protocol or physical-exit assertion.</summary>
internal enum LinuxControlFailureStage
{
    /// <summary>Closed Unknown checkpoint.</summary>
    Unknown,
    /// <summary>Closed PeerCheck checkpoint.</summary>
    PeerCheck,
    /// <summary>Closed WorkerExitTask checkpoint.</summary>
    WorkerExitTask,
    /// <summary>Closed RequestLifetime checkpoint.</summary>
    RequestLifetime,
    /// <summary>Closed AcceptLoop checkpoint.</summary>
    AcceptLoop,
    /// <summary>Closed HandlerJoin checkpoint.</summary>
    HandlerJoin,
    /// <summary>Closed CapacityWait checkpoint.</summary>
    CapacityWait,
    /// <summary>Closed Accept checkpoint.</summary>
    Accept,
    /// <summary>Closed AcceptJoin checkpoint.</summary>
    AcceptJoin,
    /// <summary>Closed ControlRegistration checkpoint.</summary>
    ControlRegistration,
    /// <summary>Closed HandlerDispatch checkpoint.</summary>
    HandlerDispatch,
    /// <summary>Closed RequestRead checkpoint.</summary>
    RequestRead,
    /// <summary>Closed RequestClassify checkpoint.</summary>
    RequestClassify,
    /// <summary>Closed CleanupRegistration checkpoint.</summary>
    CleanupRegistration,
    /// <summary>Closed Stop checkpoint.</summary>
    Stop,
    /// <summary>Closed WaitJoin checkpoint.</summary>
    WaitJoin,
    /// <summary>Closed ReplyGate checkpoint.</summary>
    ReplyGate,
    /// <summary>Closed ReadyAuthorization checkpoint.</summary>
    ReadyAuthorization,
    /// <summary>Closed ReadyClaim checkpoint.</summary>
    ReadyClaim,
    /// <summary>Closed ReadyData checkpoint.</summary>
    ReadyData,
    /// <summary>Closed ResponseData checkpoint.</summary>
    ResponseData,
    /// <summary>Closed WaitClaim checkpoint.</summary>
    WaitClaim,
    /// <summary>Closed ExitClaim checkpoint.</summary>
    ExitClaim,
    /// <summary>Closed ResponseWrite checkpoint.</summary>
    ResponseWrite,
    /// <summary>Closed ConnectionRelease checkpoint.</summary>
    ConnectionRelease,
    /// <summary>Closed PostWriteCheck checkpoint.</summary>
    PostWriteCheck,
    /// <summary>Closed ReplyCommit checkpoint.</summary>
    ReplyCommit,
    /// <summary>Closed HandlerFailureCommit checkpoint.</summary>
    HandlerFailureCommit,
    /// <summary>Closed ReplyGateRelease checkpoint.</summary>
    ReplyGateRelease,
    /// <summary>Closed ControlRelease checkpoint.</summary>
    ControlRelease,
    /// <summary>Closed CleanupRegistrationClose checkpoint.</summary>
    CleanupRegistrationClose,
    /// <summary>Closed ExitCommit checkpoint.</summary>
    ExitCommit,
    /// <summary>Closed AcceptCancel checkpoint.</summary>
    AcceptCancel,
    /// <summary>Closed ListenerClose checkpoint.</summary>
    ListenerClose,
    /// <summary>Closed PendingAcceptJoin checkpoint.</summary>
    PendingAcceptJoin,
    /// <summary>Closed HandlersJoin checkpoint.</summary>
    HandlersJoin,
    /// <summary>Closed DescendantsStop checkpoint.</summary>
    DescendantsStop,
    /// <summary>Closed ControlsJoin checkpoint.</summary>
    ControlsJoin,
    /// <summary>Closed FinalCancellation checkpoint.</summary>
    FinalCancellation,
    /// <summary>Closed CleanupBound checkpoint.</summary>
    CleanupBound,
    /// <summary>Closed OwnerCheck checkpoint.</summary>
    OwnerCheck,
    /// <summary>Closed ProtocolIncomplete checkpoint.</summary>
    ProtocolIncomplete,
    /// <summary>Closed WorkerTerminalTaskCompleted checkpoint.</summary>
    WorkerTerminalTaskCompleted,
    /// <summary>Closed ReplyGateClose checkpoint.</summary>
    ReplyGateClose,
}

/// <summary>Detached four-field first caught control fault; no bytes, identities, paths or exception objects survive.</summary>
internal sealed class LinuxControlFailure
{
    private LinuxControlFailure(LinuxControlFailureStage stage, EvidenceControlOperation? operation,
        EvidenceNativeObservationErrorKind kind, string? code)
    { Stage = stage; Operation = operation; ErrorKind = kind; DiagnosticCode = code; }
    /// <summary>Gets the clamped fixed server checkpoint.</summary>
    internal LinuxControlFailureStage Stage { get; }
    /// <summary>Gets the closed parsed operation; null means none was parsed or invalid data.</summary>
    internal EvidenceControlOperation? Operation { get; }
    /// <summary>Gets the existing closed exception family.</summary>
    internal EvidenceNativeObservationErrorKind ErrorKind { get; }
    /// <summary>Gets an existing allowlisted admission code only from its actual exception.</summary>
    internal string? DiagnosticCode { get; }
    /// <summary>Projects data only, ignoring messages, inner errors and arbitrary type names.</summary>
    internal static LinuxControlFailure Capture(LinuxControlFailureStage stage,
        EvidenceControlOperation? operation, Exception? error)
    {
        var projected = EvidenceNativeObservationFailure.Capture(EvidenceNativeObservationPhase.ServerRun, error);
        return new(Enum.IsDefined(stage) ? stage : LinuxControlFailureStage.Unknown,
            operation is { } value && Enum.IsDefined(value) ? value : null,
            projected.ErrorKind, projected.DiagnosticCode);
    }
    /// <summary>Serializes exactly four finite fields; this issues no authority or completion receipt.</summary>
    internal string ToJson() => "{\"stage\":\"" + Stage + "\",\"operation\":"
        + (Operation is { } operation ? "\"" + operation + "\"" : "null")
        + ",\"error_kind\":\"" + ErrorKind + "\",\"diagnostic_code\":"
        + (DiagnosticCode is null ? "null" : "\"" + DiagnosticCode + "\"") + "}";
}

/// <summary>Best-effort sticky control-fault data; capture failures never replace actual lifecycle errors.</summary>
internal sealed class LinuxControlFailureLatch
{
    private LinuxControlFailure? _first;
    /// <summary>Gets the first retained projection; null is not evidence of success.</summary>
    internal LinuxControlFailure? First => Volatile.Read(ref _first);
    /// <summary>Retains one closed projection atomically; later handlers and cleanup cannot replace it.</summary>
    internal void Capture(LinuxControlFailureStage stage, EvidenceControlOperation? operation, Exception? error)
    {
        try
        {
            if (First is null) Interlocked.CompareExchange(ref _first, LinuxControlFailure.Capture(stage, operation, error), null);
        }
        catch (Exception) { /* Diagnostic capture cannot alter an original error or success. */ }
    }
}

/// <summary>Detached six-field diagnostic data; construction issues no execution, admission or cleanup authority.</summary>
internal sealed class EvidenceNativeObservationFailure
{
    private EvidenceNativeObservationFailure(EvidenceNativeObservationPhase phase,
        EvidenceNativeObservationErrorKind kind, string? code, LinuxAccountFailure? account, LinuxControlFailure? control)
    { Phase = phase; ErrorKind = kind; DiagnosticCode = code; AccountFailure = account; ControlFailure = control; }

    /// <summary>Gets the validated closed checkpoint.</summary>
    internal EvidenceNativeObservationPhase Phase { get; }
    /// <summary>Gets the closed error family.</summary>
    internal EvidenceNativeObservationErrorKind ErrorKind { get; }
    /// <summary>Gets an allowlisted admission code, or null. Messages are never searched for codes.</summary>
    internal string? DiagnosticCode { get; }
    /// <summary>Gets closed first account-fault data only from an actual account lifecycle exception.</summary>
    internal LinuxAccountFailure? AccountFailure { get; }
    /// <summary>Gets first server-fault data only for ServerRun or ServerCompletion failure projection.</summary>
    internal LinuxControlFailure? ControlFailure { get; }

    /// <summary>Projects only an actual exception's family and allowlisted admission code, without retaining it.</summary>
    internal static EvidenceNativeObservationFailure Capture(EvidenceNativeObservationPhase phase, Exception? error,
        LinuxControlFailure? control = null)
    {
        if (!Enum.IsDefined(phase)) phase = EvidenceNativeObservationPhase.Unknown;
        var kind = error switch
        {
            EvidenceAdmissionException => EvidenceNativeObservationErrorKind.Admission,
            LinuxRunAccountException => EvidenceNativeObservationErrorKind.Accounts,
            SupervisionOutputPipeException => EvidenceNativeObservationErrorKind.OutputPipe,
            ControlLineException => EvidenceNativeObservationErrorKind.ControlLine,
            OperationCanceledException => EvidenceNativeObservationErrorKind.Cancelled,
            TimeoutException => EvidenceNativeObservationErrorKind.Timeout,
            UnauthorizedAccessException => EvidenceNativeObservationErrorKind.AccessDenied,
            IOException => EvidenceNativeObservationErrorKind.Io,
            NotSupportedException => EvidenceNativeObservationErrorKind.Unsupported,
            ObjectDisposedException => EvidenceNativeObservationErrorKind.Disposed,
            ArgumentException => EvidenceNativeObservationErrorKind.Argument,
            System.Text.Json.JsonException or FormatException => EvidenceNativeObservationErrorKind.InvalidData,
            InvalidOperationException => EvidenceNativeObservationErrorKind.InvalidOperation,
            _ => EvidenceNativeObservationErrorKind.Unknown,
        };
        string? code = error is EvidenceAdmissionException admission ? admission.Code : null;
        var accountFailure = error switch
        {
            LinuxRunAccountException account => account.FirstFailure,
            LinuxRunAccountCancelledException cancelled => cancelled.FirstFailure,
            _ => null,
        };
        return new(phase, kind, FilterCode(code), accountFailure,
            phase is EvidenceNativeObservationPhase.ServerRun or EvidenceNativeObservationPhase.ServerCompletion ? control : null);
    }

    /// <summary>Filters detached code data; this never creates an admission exception or execution authority.</summary>
    internal static string? FilterCode(string? code) => code is "ASEVD402" or "ASEVD404" or "ASEVD407"
        or "ASEVD409" or "ASEVD410" or "ASEVD420" or "ASEVD421" ? code : null;

    /// <summary>Returns exactly six JSON fields, without LF; all values are closed and the packet is below 1 KiB.</summary>
    /// <remarks>This data serialization never reads native state, inspects inner errors or changes a failure outcome.</remarks>
    internal string ToJson() => "{\"schema\":\"evidence-native-observation-failure-v3\",\"phase\":\"" + Phase
        + "\",\"error_kind\":\"" + ErrorKind + "\",\"diagnostic_code\":"
        + (DiagnosticCode is null ? "null" : "\"" + DiagnosticCode + "\"")
        + ",\"account_failure\":" + (AccountFailure?.ToJson() ?? "null")
        + ",\"control_failure\":" + (ControlFailure?.ToJson() ?? "null") + "}";
}

/// <summary>Sticky first-failure data latch. Later cleanup cannot replace an earlier execution fault.</summary>
internal sealed class EvidenceNativeObservationFailureLatch
{
    private EvidenceNativeObservationFailure? _first;
    /// <summary>Gets the retained data, or null before any fault. Null does not prove successful native execution.</summary>
    internal EvidenceNativeObservationFailure? First => Volatile.Read(ref _first);
    /// <summary>Retains the first closed projection; does not store the original exception or grant authority.</summary>
    internal void Capture(EvidenceNativeObservationPhase phase, Exception error, LinuxControlFailure? control = null) =>
        Interlocked.CompareExchange(ref _first, EvidenceNativeObservationFailure.Capture(phase, error, control), null);
    /// <summary>Builds a negative-only exception, with a closed missing-result fallback if no exception was caught.</summary>
    internal EvidenceNativeObservationException Rejected() => new(First
        ?? EvidenceNativeObservationFailure.Capture(EvidenceNativeObservationPhase.ResultCheck, null));
}

/// <summary>Internal negative-only wrapper carrying closed diagnostic data and the original fixed ASEVD410 text.</summary>
/// <remarks>It retains no raw error or inner exception and cannot construct an admission or native receipt.</remarks>
internal sealed class EvidenceNativeObservationException : Exception
{
    /// <summary>Creates the reserved supervisor's negative diagnostic; it has no inner exception.</summary>
    internal EvidenceNativeObservationException(EvidenceNativeObservationFailure failure)
        : base(new EvidenceAdmissionException("ASEVD410",
            "The protected empty Observation execution or final cleanup could not be established.").Message)
    { ArgumentNullException.ThrowIfNull(failure); Failure = failure; }
    /// <summary>Gets detached diagnostic data, never a successful result or native owner.</summary>
    internal EvidenceNativeObservationFailure Failure { get; }
}
