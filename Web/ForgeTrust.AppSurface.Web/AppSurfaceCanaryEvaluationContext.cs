namespace ForgeTrust.AppSurface.Web;

/// <summary>
/// Contains the validated inputs for one named canary evaluation.
/// </summary>
public sealed class AppSurfaceCanaryEvaluationContext
{
    /// <summary>
    /// Initializes a new canary evaluation context.
    /// </summary>
    /// <param name="name">The registered lowercase, dot-separated canary name.</param>
    /// <param name="marker">The optional opaque deploy marker.</param>
    /// <param name="freshSince">The optional typed freshness boundary. The HTTP adapter normalizes parsed values to UTC.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> does not use the named-canary grammar.</exception>
    public AppSurfaceCanaryEvaluationContext(
        string name,
        string? marker,
        DateTimeOffset? freshSince)
    {
        AppSurfaceCanaryValidation.ValidateName(name);
        Name = name;
        Marker = marker;
        FreshSince = freshSince;
    }

    /// <summary>Gets the exact registered canary name.</summary>
    public string Name { get; }

    /// <summary>
    /// Gets the optional opaque deploy marker exactly as supplied to this context. The HTTP adapter preserves the value
    /// delivered by ASP.NET Core; HTTP servers and clients may normalize leading or trailing field-value whitespace.
    /// </summary>
    public string? Marker { get; }

    /// <summary>Gets the optional freshness boundary normalized by the HTTP adapter to UTC.</summary>
    public DateTimeOffset? FreshSince { get; }
}
