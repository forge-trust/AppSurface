using System.Collections.Immutable;

namespace ForgeTrust.AppSurface.Web;

/// <summary>
/// Represents the immutable result of one named canary evaluation.
/// </summary>
public sealed class AppSurfaceCanaryResult
{
    /// <summary>
    /// Initializes a new result with no optional evidence and an empty immutable detail collection.
    /// </summary>
    /// <param name="status">A defined named-canary status.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="status"/> is undefined.</exception>
    public AppSurfaceCanaryResult(AppSurfaceCanaryStatus status)
        : this(status, static _ => { })
    {
    }

    /// <summary>Initializes a result with bounded operator evidence.</summary>
    /// <param name="status">A defined named-canary status.</param>
    /// <param name="configure">The callback that supplies optional evidence before it is snapshotted.</param>
    /// <remarks>
    /// AppSurface validates and bounds evidence but does not determine whether app-authored text is sensitive. The
    /// callback-local options cannot mutate the result after construction.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="status"/> is undefined.</exception>
    /// <exception cref="ArgumentException">A configured scalar value violates its documented contract.</exception>
    /// <exception cref="InvalidOperationException">The callback adds a duplicate or seventeenth detail.</exception>
    public AppSurfaceCanaryResult(
        AppSurfaceCanaryStatus status,
        Action<AppSurfaceCanaryResultOptions> configure)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "The AppSurface canary status must be defined.");
        }

        ArgumentNullException.ThrowIfNull(configure);
        var options = new AppSurfaceCanaryResultOptions();
        configure(options);
        AppSurfaceCanaryResultValidation.ValidateOptions(options);

        Status = status;
        ObservedAt = options.ObservedAt?.ToUniversalTime();
        MatchedCount = options.MatchedCount;
        ReasonCode = options.ReasonCode;
        Summary = options.Summary;
        CorrelationId = options.CorrelationId;
        Details = options.DetailValues.ToImmutableSortedDictionary(StringComparer.Ordinal);
    }

    /// <summary>Gets the current canary status.</summary>
    public AppSurfaceCanaryStatus Status { get; }

    /// <summary>Gets the optional proof-observation time normalized to UTC.</summary>
    public DateTimeOffset? ObservedAt { get; }

    /// <summary>Gets the optional non-negative count of matching proofs.</summary>
    public int? MatchedCount { get; }

    /// <summary>Gets the optional stable machine-readable response reason.</summary>
    public string? ReasonCode { get; }

    /// <summary>Gets optional response-only operator-safe explanatory text.</summary>
    public string? Summary { get; }

    /// <summary>Gets the optional response-only non-secret correlation identifier.</summary>
    public string? CorrelationId { get; }

    /// <summary>Gets the immutable ordinal-sorted response-only details.</summary>
    public IReadOnlyDictionary<string, string> Details { get; }
}
