namespace ForgeTrust.AppSurface.Caching;

/// <summary>
/// Describes the expiration behavior for a cached entry.
/// Use the static factory methods to create common configurations.
/// </summary>
public sealed record CachePolicy
{
    /// <summary>
    /// Prevents external parameterless construction.
    /// Use the static factory methods (<see cref="Absolute"/>, <see cref="Sliding"/>,
    /// <see cref="SlidingWithAbsolute"/>) to create instances.
    /// </summary>
    private CachePolicy()
    {
    }

    /// <summary>
    /// Gets the absolute expiration duration. The entry is evicted after this duration
    /// regardless of access patterns.
    /// </summary>
    public TimeSpan? AbsoluteExpiration { get; internal init; }

    /// <summary>
    /// Gets the sliding expiration window. The entry is evicted if not accessed within
    /// this duration. Each access resets the timer.
    /// </summary>
    public TimeSpan? SlidingExpiration { get; internal init; }

    /// <summary>
    /// Gets the bounded stale window used after <see cref="AbsoluteExpiration"/> elapses.
    /// During this window <see cref="IMemo"/> returns the stale value immediately and refreshes it in the background.
    /// </summary>
    public TimeSpan? StaleWhileRevalidate { get; internal init; }

    /// <summary>
    /// Creates a policy that evicts the entry after a fixed duration from creation.
    /// </summary>
    /// <param name="duration">The time after which the entry expires.</param>
    /// <returns>A <see cref="CachePolicy"/> with absolute expiration.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="duration"/> is not positive.</exception>
    public static CachePolicy Absolute(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be positive.");
        }

        return new CachePolicy { AbsoluteExpiration = duration };
    }

    /// <summary>
    /// Creates an absolute-expiration policy that can serve stale values while one background refresh runs.
    /// </summary>
    /// <param name="freshDuration">The duration for which the cached value is considered fresh.</param>
    /// <param name="staleDuration">The additional duration for which the stale value can be served while revalidating.</param>
    /// <returns>A <see cref="CachePolicy"/> with absolute freshness and stale-while-revalidate behavior.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="freshDuration"/> or <paramref name="staleDuration"/> is not positive.
    /// </exception>
    public static CachePolicy AbsoluteWithStaleWhileRevalidate(TimeSpan freshDuration, TimeSpan staleDuration)
    {
        if (freshDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(freshDuration), "Fresh duration must be positive.");
        }

        if (staleDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(staleDuration), "Stale duration must be positive.");
        }

        return new CachePolicy
        {
            AbsoluteExpiration = freshDuration,
            StaleWhileRevalidate = staleDuration
        };
    }

    /// <summary>
    /// Adds a bounded stale-while-revalidate window to an absolute-expiration policy.
    /// </summary>
    /// <remarks>
    /// Stale-while-revalidate is supported only for absolute-expiration policies because sliding freshness changes on
    /// access and cannot define a stable revalidation moment.
    /// </remarks>
    /// <param name="duration">The additional duration for which the stale value can be served while revalidating.</param>
    /// <returns>A new policy with the same absolute freshness and the requested stale window.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="duration"/> is not positive.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the current policy is not absolute-only.</exception>
    public CachePolicy WithStaleWhileRevalidate(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Stale duration must be positive.");
        }

        if (AbsoluteExpiration is null || SlidingExpiration is not null)
        {
            throw new InvalidOperationException(
                "Stale-while-revalidate can only be added to an absolute-expiration cache policy.");
        }

        return this with { StaleWhileRevalidate = duration };
    }

    /// <summary>
    /// Creates a policy that evicts the entry if it is not accessed within the given window.
    /// Each access resets the sliding timer.
    /// </summary>
    /// <param name="window">The idle duration after which the entry expires.</param>
    /// <returns>A <see cref="CachePolicy"/> with sliding expiration.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="window"/> is not positive.</exception>
    public static CachePolicy Sliding(TimeSpan window)
    {
        if (window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(window), "Window must be positive.");
        }

        return new CachePolicy { SlidingExpiration = window };
    }

    /// <summary>
    /// Creates a policy that slides the entry on each access but enforces an absolute ceiling.
    /// The entry is evicted when it has been idle for <paramref name="window"/>,
    /// or unconditionally after <paramref name="max"/> from creation, whichever comes first.
    /// </summary>
    /// <param name="window">The sliding idle window.</param>
    /// <param name="max">The absolute maximum lifetime.</param>
    /// <returns>A <see cref="CachePolicy"/> with both sliding and absolute expiration.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="window"/> or <paramref name="max"/> is not positive.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="window"/> is greater than or equal to <paramref name="max"/>.</exception>
    public static CachePolicy SlidingWithAbsolute(TimeSpan window, TimeSpan max)
    {
        if (window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(window), "Window must be positive.");
        }

        if (max <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(max), "Max must be positive.");
        }

        if (window >= max)
        {
            throw new ArgumentException("Sliding window must be less than the absolute maximum.", nameof(window));
        }

        return new CachePolicy
        {
            SlidingExpiration = window,
            AbsoluteExpiration = max
        };
    }
}
