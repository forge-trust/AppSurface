using System.Collections.Immutable;
using ForgeTrust.AppSurface.Durable.Provider;

namespace ForgeTrust.AppSurface.Durable.Testing;

/// <summary>Records calls to both durable pump contracts and optionally delegates execution.</summary>
/// <remarks>
/// History is retained for this instance until <see cref="ClearHistory"/>. Snapshots freeze order and scalar metadata,
/// while requests remain shallow references; callers should not mutate their payload-bearing inputs during assertions.
/// Payload values are never formatted or serialized. Calls canceled before delegate invocation are recorded as canceled.
/// Once a delegate starts, its actual result or exception is recorded even if it ignores a subsequently canceled token.
/// </remarks>
public sealed class RecordingDurableRuntimePump : IDurableRuntimePumpAdmission, IDurableRuntimePump
{
    private readonly object _gate = new();
    private ImmutableArray<DurableRuntimePumpCall> _history = [];
    private long _sequence;
    private long _generation;

    /// <summary>Delegate for admission-aware attempts; defaults to a completed empty pass.</summary>
    public Func<DurableRuntimePumpRequest, CancellationToken, ValueTask<DurableRuntimePumpAttempt>>? Admission { get; set; }

    /// <summary>Delegate for legacy pump calls; defaults to a completed empty pass.</summary>
    public Func<DurableRuntimePumpRequest, CancellationToken, ValueTask<DurableRuntimePumpResult>>? Pump { get; set; }

    /// <summary>Gets an atomic immutable snapshot ordered by invocation start.</summary>
    public ImmutableArray<DurableRuntimePumpCall> History { get { lock (_gate) return _history; } }

    /// <summary>Atomically removes current history; already-running calls from earlier generations stay removed.</summary>
    public void ClearHistory() { lock (_gate) { _generation++; _history = []; } }

    /// <inheritdoc />
    public ValueTask<DurableRuntimePumpAttempt> TryRunOnceAsync(DurableRuntimePumpRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Invoke(request, cancellationToken, DurableRuntimePumpCallKind.Admission, Admission);
    }

    /// <inheritdoc />
    public ValueTask<DurableRuntimePumpResult> RunOnceAsync(DurableRuntimePumpRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Invoke(request, cancellationToken, DurableRuntimePumpCallKind.Pump, Pump);
    }

    private async ValueTask<T> Invoke<T>(DurableRuntimePumpRequest request, CancellationToken token, DurableRuntimePumpCallKind kind, Func<DurableRuntimePumpRequest, CancellationToken, ValueTask<T>>? handler)
    {
        DurableRuntimePumpCall call;
        lock (_gate)
        {
            call = new(++_sequence, _generation, kind, request, request.MaximumItems, request.TimeBudget, request.Surfaces);
            _history = _history.Add(call);
        }
        var indexGeneration = call.Generation;
        try
        {
            token.ThrowIfCancellationRequested();
            T result;
            if (handler is null)
            {
                result = kind == DurableRuntimePumpCallKind.Admission
                    ? (T)(object)new DurableRuntimePumpAttempt(DurableRuntimePumpAttemptKind.Completed, EmptyResult(), null)
                    : (T)(object)EmptyResult();
            }
            else result = await handler(request, token).ConfigureAwait(false);
            Finish(call.Sequence, indexGeneration, result, null, token.IsCancellationRequested);
            return result;
        }
        catch (Exception exception)
        {
            Finish(call.Sequence, indexGeneration, default, exception, token.IsCancellationRequested);
            throw;
        }
    }

    private void Finish(long sequence, long generation, object? result, Exception? exception, bool canceled)
    {
        lock (_gate)
        {
            if (_generation != generation) return;
            var index = -1;
            for (var i = 0; i < _history.Length; i++)
            {
                if (_history[i].Sequence == sequence) { index = i; break; }
            }
            if (index >= 0) _history = _history.SetItem(index, _history[index] with { Result = result, Exception = exception, CancellationRequestedAtCompletion = canceled, IsCompleted = true });
        }
    }

    private static DurableRuntimePumpResult EmptyResult() => new(0, 0, 0, 0, 0, false, null, TimeSpan.Zero);
}

/// <summary>Identifies which production pump contract was invoked.</summary>
public enum DurableRuntimePumpCallKind
{
    /// <summary>Admission-aware pump invocation.</summary>
    Admission,
    /// <summary>Legacy bounded pump invocation.</summary>
    Pump,
}

/// <summary>An immutable invocation record; request is the exact shallow caller reference.</summary>
public sealed record DurableRuntimePumpCall(long Sequence, long Generation, DurableRuntimePumpCallKind Kind,
    DurableRuntimePumpRequest Request, int MaximumItems, TimeSpan TimeBudget, DurableRuntimeSurface Surfaces,
    object? Result = null, Exception? Exception = null, bool CancellationRequestedAtCompletion = false, bool IsCompleted = false)
{
    /// <summary>Formats only safe scalar call metadata, never retained request or delegate objects.</summary>
    public override string ToString() =>
        $"DurableRuntimePumpCall {{ Sequence = {Sequence}, Kind = {Kind}, MaximumItems = {MaximumItems}, Surfaces = {Surfaces}, IsCompleted = {IsCompleted} }}";
}
