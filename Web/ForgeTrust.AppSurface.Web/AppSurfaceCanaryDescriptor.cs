using System.Collections.Immutable;

namespace ForgeTrust.AppSurface.Web;

/// <summary>
/// Stores an immutable named-canary registration without resolving its evaluator.
/// </summary>
internal sealed class AppSurfaceCanaryDescriptor
{
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

    internal string Name { get; }

    internal string DisplayName { get; }

    internal string? Description { get; }

    internal IReadOnlySet<string> Tags { get; }

    internal bool MarkerRequired { get; }

    internal bool FreshSinceRequired { get; }

    internal Type EvaluatorType { get; }
}
