using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.EvidenceGate;

/// <summary>
/// Closed production placeholder until the host receives independently validated protected release-event identity.
/// </summary>
internal sealed class UnavailableProtectedReleaseInvocationProvider : IProtectedReleaseInvocationProvider
{
    /// <inheritdoc />
    public ValueTask<ProtectedReleaseInvocation?> ReadAsync(
        EvidenceProducerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<ProtectedReleaseInvocation?>(null);
    }
}
