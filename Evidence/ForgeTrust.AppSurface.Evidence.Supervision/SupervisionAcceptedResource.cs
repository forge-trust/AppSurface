namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Joins an original accept before its synchronous validation and ownership transfer.</summary>
/// <remarks>
/// Network cancellation belongs to the supplied accept task. After that task returns a resource,
/// validation always receives CancellationToken.None: the actual owner supplies its original
/// identity and deadline guards independently. No cancellation sample can skip those checks.
/// This procedure seam creates no listener, peer, admission, native identity or completion claim.
/// </remarks>
internal static class SupervisionAcceptedResource
{
    /// <summary>Checks and transfers an actually returned resource, or closes it on rejection.</summary>
    /// <param name="originalAccept">Original network task; it is joined directly without a proxy wait.</param>
    /// <param name="checkAndTransfer">Synchronous validation and transfer under the retained owner's guards.</param>
    /// <param name="closeRejected">Closes an untransferred resource; its failure propagates too.</param>
    /// <remarks>
    /// The caller keeps original pending-task ownership throughout this procedure. Successful transfer
    /// gives ownership to the caller; validation failure closes the resource before propagating.
    /// For a Socket, Dispose is safe when the native connection factory already closed it on rejection.
    /// The callback must not replace the original owner deadline with a new allowance.
    /// </remarks>
    internal static async Task<TResult> CompleteAsync<TResource, TResult>(Task<TResource> originalAccept,
        Func<TResource, CancellationToken, TResult> checkAndTransfer, Action<TResource> closeRejected)
        where TResource : class
    {
        ArgumentNullException.ThrowIfNull(originalAccept);
        ArgumentNullException.ThrowIfNull(checkAndTransfer);
        ArgumentNullException.ThrowIfNull(closeRejected);
        var accepted = await originalAccept.ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(accepted);
        var transferred = false;
        try
        {
            var result = checkAndTransfer(accepted, CancellationToken.None);
            transferred = true;
            return result;
        }
        finally
        {
            if (!transferred) closeRejected(accepted);
        }
    }
}
