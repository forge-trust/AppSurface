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

/// <summary>Detached four-field diagnostic data; construction issues no execution, admission or cleanup authority.</summary>
internal sealed class EvidenceNativeObservationFailure
{
    private EvidenceNativeObservationFailure(EvidenceNativeObservationPhase phase,
        EvidenceNativeObservationErrorKind kind, string? code)
    { Phase = phase; ErrorKind = kind; DiagnosticCode = code; }

    /// <summary>Gets the validated closed checkpoint.</summary>
    internal EvidenceNativeObservationPhase Phase { get; }
    /// <summary>Gets the closed error family.</summary>
    internal EvidenceNativeObservationErrorKind ErrorKind { get; }
    /// <summary>Gets an allowlisted admission code, or null. Messages are never searched for codes.</summary>
    internal string? DiagnosticCode { get; }

    /// <summary>Projects only an actual exception's family and allowlisted admission code, without retaining it.</summary>
    internal static EvidenceNativeObservationFailure Capture(EvidenceNativeObservationPhase phase, Exception? error)
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
        return new(phase, kind, FilterCode(code));
    }

    /// <summary>Filters detached code data; this never creates an admission exception or execution authority.</summary>
    internal static string? FilterCode(string? code) => code is "ASEVD402" or "ASEVD404" or "ASEVD407"
        or "ASEVD409" or "ASEVD410" or "ASEVD420" or "ASEVD421" ? code : null;

    /// <summary>Returns exactly four JSON fields, without LF; all values are closed and the packet is below 1 KiB.</summary>
    /// <remarks>This data serialization never reads native state, inspects inner errors or changes a failure outcome.</remarks>
    internal string ToJson() => "{\"schema\":\"evidence-native-observation-failure-v1\",\"phase\":\"" + Phase
        + "\",\"error_kind\":\"" + ErrorKind + "\",\"diagnostic_code\":"
        + (DiagnosticCode is null ? "null" : "\"" + DiagnosticCode + "\"") + "}";
}

/// <summary>Sticky first-failure data latch. Later cleanup cannot replace an earlier execution fault.</summary>
internal sealed class EvidenceNativeObservationFailureLatch
{
    private EvidenceNativeObservationFailure? _first;
    /// <summary>Gets the retained data, or null before any fault. Null does not prove successful native execution.</summary>
    internal EvidenceNativeObservationFailure? First => Volatile.Read(ref _first);
    /// <summary>Retains the first closed projection; does not store the original exception or grant authority.</summary>
    internal void Capture(EvidenceNativeObservationPhase phase, Exception error) =>
        Interlocked.CompareExchange(ref _first, EvidenceNativeObservationFailure.Capture(phase, error), null);
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
