using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Bounded first-line observation fed only by the original stderr pump, not another reader.</summary>
/// <remarks>Bytes are data only. Original owner, process identity and READY commit gate the actual signal.</remarks>
internal sealed class SupervisionCancellationPhaseObservation
{
    private readonly byte[] _buffer = new byte[1024];
    private readonly TaskCompletionSource _phase = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _length;
    private bool _ended;
    /// <summary>Gets the observed first-frame task; joining pumps remains independent.</summary>
    internal Task Observed => _phase.Task;
    /// <summary>Gets fixed expected bytes for intentionally detached parser controls.</summary>
    internal static byte[] ExpectedFrame => EvidenceOriginalCancellationCheckpoint.Frame;

    /// <summary>Accepts each already charged original stderr chunk; never reads the pipe itself.</summary>
    internal void Feed(ReadOnlySpan<byte> bytes)
    {
        if (_ended) return;
        foreach (var value in bytes)
        {
            if (_length == _buffer.Length) { Reject(); return; }
            _buffer[_length++] = value;
            if (value != (byte)'\n') continue;
            _ended = true;
            if (_buffer.AsSpan(0, _length).SequenceEqual(ExpectedFrame)) _phase.TrySetResult();
            else Reject();
            return;
        }
    }

    /// <summary>EOF/read failure before a full exact frame cannot synthesize phase observation.</summary>
    internal void Complete() { if (!_ended) Reject(); }
    private void Reject()
    {
        _ended = true;
        _phase.TrySetException(new InvalidOperationException("cancellation-phase-rejected"));
        _ = _phase.Task.Exception; // Observe failed data even when root startup never reaches READY.
    }
}
