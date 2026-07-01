namespace ForgeTrust.AppSurface.Workers;

/// <summary>
/// Describes whether a durable worker outcome should be retried automatically.
/// </summary>
/// <remarks>
/// These values are intended for workflow decisions and diagnostics. They do not execute retries directly; host
/// adapters translate the value into the chosen runtime's retry, timer, or operator-alert behavior.
/// </remarks>
public enum DurableWorkerRetryability
{
    /// <summary>
    /// The outcome may be retried by the durable runtime.
    /// </summary>
    Retryable = 0,

    /// <summary>
    /// The outcome is final and should not be retried automatically.
    /// </summary>
    Terminal = 1,

    /// <summary>
    /// The outcome requires an operator, administrator, or application-specific repair process.
    /// </summary>
    OperatorRequired = 2,
}
