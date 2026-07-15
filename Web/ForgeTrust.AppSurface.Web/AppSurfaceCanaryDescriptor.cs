using System.Collections.Immutable;

namespace ForgeTrust.AppSurface.Web;

/// <summary>
/// Stores an immutable named-canary registration without resolving its evaluator.
/// </summary>
internal sealed class AppSurfaceCanaryDescriptor
{
    /// <summary>
    /// Initializes a validated immutable registration snapshot.
    /// </summary>
    /// <param name="name">The non-null validated lowercase, dot-separated registration name.</param>
    /// <param name="displayName">The non-null, nonblank adopter-facing display name.</param>
    /// <param name="description">The optional description, or <see langword="null"/>.</param>
    /// <param name="tags">Validated tags to snapshot into an ordinal-sorted immutable set with duplicates removed.</param>
    /// <param name="markerRequired">Whether the HTTP adapter must receive a nonblank marker.</param>
    /// <param name="freshSinceRequired">Whether the HTTP adapter must receive a valid freshness boundary.</param>
    /// <param name="evaluatorType">
    /// The registered concrete service type expected to implement <see cref="IAppSurfaceCanaryEvaluator"/>.
    /// </param>
    /// <remarks>
    /// Callers must validate registration inputs before construction. The constructor snapshots tags and never resolves
    /// or activates <paramref name="evaluatorType"/>.
    /// </remarks>
    internal AppSurfaceCanaryDescriptor(
        string name,
        string displayName,
        string? description,
        IEnumerable<string> tags,
        bool markerRequired,
        bool freshSinceRequired,
        Type evaluatorType)
    {
        Name = name;
        DisplayName = displayName;
        Description = description;
        Tags = tags.ToImmutableSortedSet(StringComparer.Ordinal);
        MarkerRequired = markerRequired;
        FreshSinceRequired = freshSinceRequired;
        EvaluatorType = evaluatorType;
    }

    /// <summary>Gets the validated exact registration name.</summary>
    internal string Name { get; }

    /// <summary>Gets the nonblank adopter-facing display name.</summary>
    internal string DisplayName { get; }

    /// <summary>Gets the optional description.</summary>
    internal string? Description { get; }

    /// <summary>Gets the immutable ordinal-sorted tag snapshot.</summary>
    internal IReadOnlySet<string> Tags { get; }

    /// <summary>Gets a value indicating whether the marker input is required.</summary>
    internal bool MarkerRequired { get; }

    /// <summary>Gets a value indicating whether the freshness boundary is required.</summary>
    internal bool FreshSinceRequired { get; }

    /// <summary>Gets the concrete evaluator service type resolved for each evaluation.</summary>
    internal Type EvaluatorType { get; }
}
