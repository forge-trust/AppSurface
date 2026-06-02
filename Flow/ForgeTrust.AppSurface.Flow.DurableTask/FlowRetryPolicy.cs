namespace ForgeTrust.AppSurface.Flow.DurableTask;

/// <summary>
/// Describes retry settings that a Durable Task host can apply when scheduling flow node work.
/// </summary>
/// <remarks>
/// The adapter only carries retry intent. Durable Task worker/client code remains responsible for translating this value
/// into the provider-specific retry options used by the host.
/// </remarks>
public sealed record FlowRetryPolicy
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FlowRetryPolicy"/> class.
    /// </summary>
    /// <param name="maxAttempts">Maximum number of attempts, including the first attempt.</param>
    /// <param name="firstRetryInterval">Delay before the first retry.</param>
    /// <param name="backoffCoefficient">Backoff coefficient applied by the durable host.</param>
    public FlowRetryPolicy(int maxAttempts, TimeSpan firstRetryInterval, double backoffCoefficient = 1)
    {
        if (maxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), "Max attempts must be at least 1.");
        }

        if (firstRetryInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(firstRetryInterval), "First retry interval must be positive.");
        }

        if (backoffCoefficient < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(backoffCoefficient), "Backoff coefficient must be at least 1.");
        }

        MaxAttempts = maxAttempts;
        FirstRetryInterval = firstRetryInterval;
        BackoffCoefficient = backoffCoefficient;
    }

    /// <summary>
    /// Gets the maximum number of attempts, including the first attempt.
    /// </summary>
    public int MaxAttempts { get; }

    /// <summary>
    /// Gets the delay before the first retry.
    /// </summary>
    public TimeSpan FirstRetryInterval { get; }

    /// <summary>
    /// Gets the backoff coefficient applied by the durable host.
    /// </summary>
    public double BackoffCoefficient { get; }
}
