using System.Collections.Immutable;
using ForgeTrust.AppSurface.Durable.Provider;

namespace ForgeTrust.AppSurface.Durable.Testing;

/// <summary>In-memory drain control that records each state transition request.</summary>
public sealed class FakeDurableRuntimeDrainControl : IDurableRuntimeDrainControl
{
    private readonly object _gate = new();
    private ImmutableArray<DurableRuntimeDrainTransition> _history = [];
    private bool _isDraining;
    private long _sequence;

    /// <summary>Gets the current drain state.</summary>
    public bool IsDraining { get { lock (_gate) return _isDraining; } }

    /// <summary>Gets an atomic immutable transition snapshot.</summary>
    public ImmutableArray<DurableRuntimeDrainTransition> History { get { lock (_gate) return _history; } }

    /// <inheritdoc />
    public ValueTask BeginDrainAsync(CancellationToken cancellationToken = default) => SetAsync(true, cancellationToken);

    /// <inheritdoc />
    public ValueTask ResumeAsync(CancellationToken cancellationToken = default) => SetAsync(false, cancellationToken);

    private ValueTask SetAsync(bool draining, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_gate) { _isDraining = draining; _history = _history.Add(new(++_sequence, draining)); }
        return ValueTask.CompletedTask;
    }
}

/// <summary>A recorded drain-state request in invocation order.</summary>
public sealed record DurableRuntimeDrainTransition(long Sequence, bool IsDraining);
