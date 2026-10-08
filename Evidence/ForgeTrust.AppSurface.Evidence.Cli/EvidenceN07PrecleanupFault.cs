using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Cli;

/// <summary>Copies the first actual Allocate exception into bounded private data before tracked cleanup.</summary>
/// <remarks>
/// This fixed-image latch grants no lease or successful cleanup. The original allocation callback
/// awaits its sole bounded write to the inherited worker stderr, before rethrowing the same fault.
/// Root observes that original pipe separately; serialized data alone cannot construct any owner.
/// The exception is classified immediately and never retained or echoed.
/// </remarks>
internal sealed class EvidenceN07PrecleanupFault
{
    internal const int MaximumJsonBytes = 1024;
    private byte[]? _first;

    /// <summary>Returns copied first-fault data, or null when capture never succeeded.</summary>
    internal byte[]? Bytes => Volatile.Read(ref _first)?.ToArray();

    /// <summary>Best-effort capture before the caller rethrows the same original exception.</summary>
    /// <remarks>No exception from this diagnostic attempt may replace the original allocation fault.</remarks>
    internal void Capture(EvidenceLinuxArtifactAllocationOperation operation, Exception error)
    {
        try
        {
            if (Volatile.Read(ref _first) is not null) return;
            var bytes = Encode(operation, error);
            Interlocked.CompareExchange(ref _first, bytes, null);
        }
        catch (Exception) { } // No I/O, callback, token change, cleanup or outcome publication.
    }

    private int _writeClaimed;

    /// <summary>Writes one complete bounded frame on original inherited stderr under the stage token.</summary>
    /// <remarks>The caller awaits this original operation; no timer, callback or detached writer is created.</remarks>
    internal async Task WriteBeforeRethrowAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _writeClaimed, 1) != 0 || Bytes is not { } bytes)
            throw new InvalidOperationException("N07 precleanup transfer rejected.");
        var frame = new byte[checked(bytes.Length + 1)];
        bytes.CopyTo(frame, 0); frame[^1] = (byte)'\n';
        using var stderr = Console.OpenStandardError();
        await stderr.WriteAsync(frame.AsMemory(), token).ConfigureAwait(false);
        await stderr.FlushAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
    }

    /// <summary>Detached finite encoding for pure controls; equivalent bytes grant no live authority.</summary>
    internal static byte[] Encode(EvidenceLinuxArtifactAllocationOperation operation, Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var detail = EvidenceProtectedCliExecution.CreateAllocationFailureDiagnostic(
            EvidenceAllocationPhase.Allocation, operation, EvidenceWorkerStageOutcome.Failed,
            EvidenceWorkerTerminalCode.None, error);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "issue779-n07-precleanup-allocation-fault-v1",
            phase = "Allocation", operation = detail.Operation.ToString(),
            error_family = detail.ErrorClass.ToString(),
            capture_point = "AllocateCatchBeforeCallbackRethrow", terminal_observed = false,
            observation_only = true, native_authority = false, native_acceptance = false,
        });
        if (bytes.Length is 0 or > MaximumJsonBytes)
            throw new InvalidOperationException("N07 diagnostic data rejected.");
        return bytes;
    }
}
