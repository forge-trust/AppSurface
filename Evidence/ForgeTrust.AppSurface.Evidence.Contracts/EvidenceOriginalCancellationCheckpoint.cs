using System.Text;
using System.Text.Json;

namespace ForgeTrust.AppSurface.Evidence.Contracts;

/// <summary>Private compile-fixed cancellation location; no request or environment can select it.</summary>
internal enum EvidenceOriginalCancellationPhase { BeforeAllocation, BeforeActivation }

/// <summary>Detached cancellation facts, never an admission, lease, FD or signal receipt.</summary>
internal sealed record EvidenceOriginalCancellationObservation(string Case, string Phase,
    bool CallerTokenCancelled, bool LifecycleCallerCancelled, bool OwnWorkStopped)
{
    /// <summary>Gets the closed private diagnostic schema.</summary>
    public string Schema => "issue779-original-cancellation-v1";
    /// <summary>Gets the permanently false authority declaration.</summary>
    public bool NativeAuthority => false;
}

/// <summary>One fixed-image stage wait inside the original lifecycle callback.</summary>
/// <remarks>
/// Phase transfer uses the original stage token and remains owned by that callback. A cancellation
/// registration completes data only. Failed transfer cannot replace cancellation or create authority.
/// A missing transfer prevents root signalling and eventually follows original deadline containment.
/// Main derives N09 by changing only SelectedPhase in a separate complete source image.
/// </remarks>
internal sealed class EvidenceOriginalCancellationCheckpoint
{
    /// <summary>The sole source-input selection; there is no runtime selector.</summary>
    internal const EvidenceOriginalCancellationPhase SelectedPhase = EvidenceOriginalCancellationPhase.BeforeAllocation;
    /// <summary>Gets the exact fixed case label.</summary>
    internal static string Case => SelectedPhase == EvidenceOriginalCancellationPhase.BeforeAllocation ? "N08" : "N09";
    /// <summary>Gets the fixed first stderr frame, including LF, with no caller data.</summary>
    internal static byte[] Frame => Encoding.UTF8.GetBytes(
        $"{{\"schema\":\"issue779-cancellation-phase-v1\",\"case\":\"{Case}\",\"phase\":\"{SelectedPhase}\",\"native_authority\":false}}\n");
    private int _claimed;
    private bool _callerCancelled;
    private bool _transferSucceeded;

    /// <summary>Waits only at the compile-selected location; neither token is manufactured or replaced.</summary>
    /// <param name="phase">Actual original callback location.</param>
    /// <param name="error">Borrowed actual worker stderr; this object does not close it.</param>
    /// <param name="stageToken">Original linked stage token, also releasing deadline/stop cancellation.</param>
    /// <param name="callerToken">Original CliFx registration token, independently sampled after release.</param>
    internal async ValueTask WaitAtAsync(EvidenceOriginalCancellationPhase phase, Stream error,
        CancellationToken stageToken, CancellationToken callerToken)
    {
        if (phase != SelectedPhase) return;
        if (Interlocked.Exchange(ref _claimed, 1) != 0) throw Rejected();
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = stageToken.UnsafeRegister(static state =>
            ((TaskCompletionSource)state!).TrySetResult(), released);
        try
        {
            await error.WriteAsync(Frame, stageToken).ConfigureAwait(false);
            _transferSucceeded = true; // Write completed; root may observe its LF before cancellation reaches Flush.
            await error.FlushAsync(stageToken).ConfigureAwait(false);
        }
        catch (Exception errorValue) when (errorValue is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        { /* Data transfer cannot replace the original token check or stage outcome. */ }
        await released.Task.ConfigureAwait(false); // Same callback, no timer or detached task.
        _callerCancelled = callerToken.IsCancellationRequested;
    }

    /// <summary>Copies actual lifecycle state only after its original callback/STOP/WAIT settlement.</summary>
    /// <remarks>A caller token alone is not lifecycle classification; a prior first failure remains first.</remarks>
    internal EvidenceOriginalCancellationObservation ObserveJoined(EvidenceWorkerExecution execution)
    {
        if (!execution.OwnWorkStopped || Volatile.Read(ref _claimed) != 1 || !_transferSucceeded) throw Rejected();
        return new(Case, SelectedPhase.ToString(), _callerCancelled,
            execution.TerminalCode == EvidenceWorkerTerminalCode.CallerCancelled, execution.OwnWorkStopped);
    }

    /// <summary>Best-effort closed projection through the existing private diagnostic channel after joins.</summary>
    internal void ReportJoined(EvidenceWorkerExecution execution, Action<EvidenceOriginalCancellationObservation>? sink)
    {
        if (sink is null) return;
        try { sink(ObserveJoined(execution)); }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        { /* Data cannot alter the sticky original failure or cleanup. */ }
    }

    /// <summary>Encodes detached data only, with a fixed bound and no arbitrary input text.</summary>
    internal static string Encode(EvidenceOriginalCancellationObservation data)
    {
        if (data.Case != Case || data.Phase != SelectedPhase.ToString() || !data.OwnWorkStopped) throw Rejected();
        var line = JsonSerializer.Serialize(data, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (Encoding.UTF8.GetByteCount(line) + 1 > 1024) throw Rejected();
        return line;
    }

    private static InvalidOperationException Rejected() => new("cancellation-checkpoint-rejected");
}
