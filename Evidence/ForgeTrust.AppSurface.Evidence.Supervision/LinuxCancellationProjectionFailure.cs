using System.Text.Json;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Closed checkpoints of the original private cancellation projection; none grants authority.</summary>
internal enum LinuxCancellationProjectionStage
{
    /// <summary>No original projection operation has selected a checkpoint.</summary>
    Unknown,
    /// <summary>The original native custody prerequisites and protected input recheck.</summary>
    OriginalCustody,
    /// <summary>The original successful monitor and retained terminal/output references.</summary>
    OriginalMonitor,
    /// <summary>The original committed READY descriptor and joined server owners.</summary>
    ReadyDescriptor,
    /// <summary>Encoding the copied kernel observation after native prerequisites.</summary>
    KernelEncoding,
    /// <summary>Encoding the complete charged output from the original joined collector.</summary>
    JoinedStreamEncoding,
    /// <summary>The existing final owner identity check after joined-stream encoding.</summary>
    HolderIdentity,
    /// <summary>The original signal task, one-attempt syscall claim and observed cancellation phase.</summary>
    SignalProvenance,
    /// <summary>Writing the original signal record to private stderr.</summary>
    SignalWrite,
    /// <summary>Writing the original kernel record to private stderr.</summary>
    KernelWrite,
    /// <summary>Checking the original token and writing the original joined-stream record.</summary>
    JoinedStreamWrite,
    /// <summary>The final original-token check before marking the projection written.</summary>
    FinalBound,
}

/// <summary>Encodes a separate later projection failure without replacing the first execution fault.</summary>
/// <remarks>
/// The actual root composition calls this only after a private N08/N09 projection throws. Checkpoints
/// are assigned before the existing operations; no guard, native read, deadline or ownership predicate
/// changes. Detached calls establish encoding only. The output contains no message, stack, path, PID,
/// raw stream or inner exception and cannot establish cancellation, cleanup, custody or acceptance.
/// The original large-output attempt flag still prevents a second large projection after partial I/O.
/// </remarks>
internal static class LinuxCancellationProjectionFailure
{
    /// <summary>Maximum JSON bytes excluding one terminal LF; separate from the large output projection.</summary>
    internal const int MaximumJsonBytes = 1024;

    /// <summary>Copies closed stage, error family and filtered diagnostic code into bounded detached data.</summary>
    /// <param name="generation">Original run generation in native use; metadata alone creates no owner.</param>
    /// <param name="stage">The checkpoint assigned before the original failing operation.</param>
    /// <param name="error">The caught original error; its text and inner exception are never serialized.</param>
    /// <returns>A copied UTF-8 JSON object with no native authority or acceptance.</returns>
    /// <exception cref="InvalidOperationException">Empty generation, unknown stage or missing error.</exception>
    internal static byte[] EncodeDetached(Guid generation, LinuxCancellationProjectionStage stage, Exception error)
    {
        if (generation == Guid.Empty || !Enum.IsDefined(stage) || error is null) throw Rejected();
        var failure = EvidenceNativeObservationFailure.Capture(EvidenceNativeObservationPhase.Unknown, error);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "issue779-cancellation-projection-failure-v1",
            generation = generation.ToString("N"),
            stage = stage.ToString(),
            error_kind = failure.ErrorKind.ToString(),
            diagnostic_code = failure.DiagnosticCode,
            observation_only = true,
            native_authority = false,
            native_acceptance = false,
        });
        if (bytes.Length is 0 or > MaximumJsonBytes) throw Rejected();
        return bytes;
    }

    private static InvalidOperationException Rejected() =>
        new("ASEVD410: Cancellation projection diagnostic data rejected.");
}
