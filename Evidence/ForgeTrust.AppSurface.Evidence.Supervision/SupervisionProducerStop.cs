namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Orders a producer's bounded cooperative stop and forced containment without issuing exit facts.</summary>
/// <remarks>
/// The real holder supplies operations for its retained unit. A stop request only begins TERM processing;
/// this procedure joins a bounded observation and forces the same group when cooperative exit is unconfirmed.
/// The caller selects a grace strictly inside its existing stop reserve and retains the original cleanup token.
/// No allowance is renewed. The caller must still join the original start, process monitor and output pumps,
/// and check the actual unit and empty kernel group twice. Delegates are an internal procedure test seam,
/// never authentication, admission, successful cleanup or native ownership.
/// </remarks>
internal static class SupervisionProducerStop
{
    /// <summary>Joins initial stop and cooperative observation, then KILL containment only when unconfirmed.</summary>
    /// <param name="grace">Positive cooperative slice of the caller's already reserved stopping allowance.</param>
    /// <param name="requestStop">Original selected-unit stop request, joined even when cancellation is ignored.</param>
    /// <param name="observeStopped">Returns only after matching stopped-unit and empty-group observations.</param>
    /// <param name="forceContainment">Fresh selected-unit stop/KILL operation using the original cleanup token.</param>
    /// <param name="token">The original cumulative cleanup token; caller cancellation cannot become success.</param>
    /// <returns>Procedure completion only; actual physical settlement remains the holder's responsibility.</returns>
    internal static async Task RunAsync(TimeSpan grace, Func<CancellationToken, Task> requestStop,
        Func<CancellationToken, Task> observeStopped, Func<CancellationToken, Task> forceContainment,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(requestStop);
        ArgumentNullException.ThrowIfNull(observeStopped);
        ArgumentNullException.ThrowIfNull(forceContainment);
        if (grace <= TimeSpan.Zero || grace > TimeSpan.FromSeconds(15))
            throw new ArgumentOutOfRangeException(nameof(grace));
        token.ThrowIfCancellationRequested();
        var cooperative = false;
        using (var slice = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            slice.CancelAfter(grace);
            try
            {
                await requestStop(slice.Token).ConfigureAwait(false);
                slice.Token.ThrowIfCancellationRequested();
                await observeStopped(slice.Token).ConfigureAwait(false);
                slice.Token.ThrowIfCancellationRequested();
                cooperative = true;
            }
            catch (Exception error) when (!token.IsCancellationRequested
                && SupervisionExceptionPolicy.IsRecoverable(error))
            {
                // A missing/error/canceled observation cannot establish a cooperative stop. The
                // original force operation must join; any force failure remains a failure.
            }
        }
        token.ThrowIfCancellationRequested();
        if (!cooperative) await forceContainment(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
    }
}
