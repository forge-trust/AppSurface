using System.Net;

namespace ForgeTrust.AppSurface.Evidence.Readiness;

/// <summary>Configures one HTTP readiness operation; values are snapshotted before work begins.</summary>
public sealed class HttpReadinessOptions
{
    /// <summary>Gets the finite positive per-request maximum; defaults to three seconds.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Gets the finite positive delay between failed attempts; defaults to 250 milliseconds.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Gets the synchronous status-only predicate; defaults to exact HTTP 200. Exceptions terminate the operation.</summary>
    public Func<HttpStatusCode, bool> IsSuccess { get; init; } = status => status == HttpStatusCode.OK;

    /// <summary>Gets the optional positive request cap; null means the deadline alone bounds attempts.</summary>
    public int? MaxAttempts { get; init; }
}

/// <summary>Immutable validated settings shared by the public probe and hosting adapters.</summary>
/// <param name="RequestTimeout">Finite positive maximum for one request, shortened by remaining time.</param>
/// <param name="RetryDelay">Finite positive fixed delay, shortened by remaining time.</param>
/// <param name="IsSuccess">Snapshotted synchronous status-only predicate; exceptions are terminal.</param>
/// <param name="MaxAttempts">Positive request cap, or null for deadline-only behavior.</param>
internal sealed record HttpReadinessSettings(
    TimeSpan RequestTimeout,
    TimeSpan RetryDelay,
    Func<HttpStatusCode, bool> IsSuccess,
    int? MaxAttempts)
{
    /// <summary>Validates and snapshots options without acquiring resources or invoking callbacks.</summary>
    internal static HttpReadinessSettings Snapshot(HttpReadinessOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ReadinessDeadline.ValidateDuration(options.RequestTimeout, nameof(options.RequestTimeout));
        ReadinessDeadline.ValidateDuration(options.RetryDelay, nameof(options.RetryDelay));
        ArgumentNullException.ThrowIfNull(options.IsSuccess);
        if (options.MaxAttempts is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaxAttempts));
        }

        return new(options.RequestTimeout, options.RetryDelay, options.IsSuccess, options.MaxAttempts);
    }
}
