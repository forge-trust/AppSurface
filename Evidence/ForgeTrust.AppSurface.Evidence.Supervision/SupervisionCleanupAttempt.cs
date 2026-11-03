namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Applies the root owner's existing recoverable-exception policy to one cleanup operation.</summary>
/// <remarks>
/// Callers retain the original operation, its exact invocation order, deadline, captured variables and
/// failure latch. Returning means that operation returned or its original recoverable failure was recorded;
/// it does not establish physical exit, custody, account deletion, admission or successful cleanup. This
/// helper starts no background work, retries nothing and awaits the original task without a cancelable wait.
/// Fatal runtime exceptions propagate, and an exception from the failure recorder is never intercepted.
/// </remarks>
internal static class SupervisionCleanupAttempt
{
    /// <summary>Invokes one synchronous close/check, recording only its original recoverable failure.</summary>
    /// <param name="operation">The caller's original operation against already retained resources.</param>
    /// <param name="recordFailure">The caller's original nonthrowing error recorder and sticky failure latch.</param>
    internal static void Run(Action operation, Action<Exception> recordFailure)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(recordFailure);
        try { operation(); }
        catch (Exception error) when (SupervisionExceptionPolicy.IsRecoverable(error)) { recordFailure(error); }
    }

    /// <summary>Invokes and joins the original asynchronous cleanup, preserving its exception instance.</summary>
    /// <param name="operation">The caller's original task-producing operation; no token or timer is substituted.</param>
    /// <param name="recordFailure">The caller's original error recorder and sticky failure latch.</param>
    internal static async Task RunAsync(Func<Task> operation, Action<Exception> recordFailure)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(recordFailure);
        try { await operation().ConfigureAwait(false); }
        catch (Exception error) when (SupervisionExceptionPolicy.IsRecoverable(error)) { recordFailure(error); }
    }
}
