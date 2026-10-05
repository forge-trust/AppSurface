namespace ForgeTrust.AppSurface.Evidence.Aspire;

/// <summary>
/// Declares an owned stop-and-join boundary for a producer, resource, or envelope verifier.
/// </summary>
/// <remarks>
/// The host may call this capability while execution is active. Implementations must be safe under
/// that overlap, observe the cleanup token, and complete only after all owned work and external
/// descendants have stopped. Stopping an await, cancelling a token, or observing only the leader's
/// exit does not prove quiescence. Use an independently supervised process boundary for work that
/// cannot cooperate. Ordinary disposal begins only after stop and tracked callbacks have settled.
/// </remarks>
public interface IEvidenceExecutionLifetime
{
    /// <summary>Stops and joins all work owned by this registration, including external descendants.</summary>
    /// <param name="cancellationToken">The registration's share of the host cleanup allowance.</param>
    /// <returns>A completion that proves owned work stopped; otherwise throw or observe cancellation.</returns>
    ValueTask StopAsync(CancellationToken cancellationToken);
}
