using ForgeTrust.AppSurface.Durable.Provider;

namespace ForgeTrust.AppSurface.Durable.Testing;

/// <summary>A configurable in-memory health reader for deterministic tests.</summary>
/// <remarks>The configured snapshot is returned unchanged. No payloads are logged or serialized.</remarks>
public sealed class FakeDurableRuntimeHealth : IDurableRuntimeHealth
{
    /// <summary>Initializes the fake with the supplied snapshot.</summary>
    public FakeDurableRuntimeHealth(DurableRuntimeHealthSnapshot snapshot)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
    }

    /// <summary>Gets or sets the snapshot returned to subsequent callers.</summary>
    public DurableRuntimeHealthSnapshot Snapshot { get; set; }

    /// <summary>Reads the configured snapshot, honoring cancellation before returning.</summary>
    public ValueTask<DurableRuntimeHealthSnapshot> GetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Snapshot);
    }
}
