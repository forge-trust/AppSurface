using System.Collections.Immutable;

namespace ForgeTrust.AppSurface.Web;

/// <summary>
/// Represents the immutable result of one named canary evaluation.
/// </summary>
public sealed class AppSurfaceCanaryResult
{
    private static readonly IReadOnlyDictionary<string, string> EmptyDetails =
        ImmutableSortedDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new result with no optional evidence and an empty immutable detail collection.
    /// </summary>
    /// <param name="status">A defined named-canary status.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="status"/> is undefined.</exception>
    public AppSurfaceCanaryResult(AppSurfaceCanaryStatus status)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "The AppSurface canary status must be defined.");
        }

        Status = status;
        Details = EmptyDetails;
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

    /// <summary>
    /// Gets the optional stable machine-readable response reason: 1-64 lowercase ASCII letters or digits separated by
    /// internal hyphens, with an alphanumeric first and last character.
    /// </summary>
    public string? ReasonCode { get; }

    /// <summary>
    /// Gets optional response-only operator-safe explanatory text that is nonblank, at most 256 UTF-8 bytes,
    /// well-formed Unicode, and free of Unicode control (<c>Cc</c>) scalars.
    /// </summary>
    public string? Summary { get; }

    /// <summary>
    /// Gets the optional response-only non-secret correlation identifier: 1-128 ASCII letters, digits, periods,
    /// underscores, colons, or hyphens, with an alphanumeric first and last character.
    /// </summary>
    public string? CorrelationId { get; }

    /// <summary>
    /// Gets up to 16 immutable, ordinal-sorted response-only details. Keys use 1-64 character lowercase dot-separated
    /// segments; values are nonblank, at most 128 UTF-8 bytes, well-formed Unicode, and free of Unicode control
    /// (<c>Cc</c>) scalars.
    /// </summary>
    public IReadOnlyDictionary<string, string> Details { get; }
}
