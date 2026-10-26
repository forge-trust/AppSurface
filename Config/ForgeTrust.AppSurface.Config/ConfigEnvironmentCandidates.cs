using System.Collections.Immutable;

namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// Provides the environment-variable candidate names used by configuration lookup and patching.
/// </summary>
internal static class ConfigEnvironmentCandidates
{
    /// <summary>
    /// Gets the ordered direct candidates for a logical configuration key.
    /// </summary>
    /// <param name="environment">The environment name used for the scoped candidates.</param>
    /// <param name="key">The logical configuration key.</param>
    /// <returns>
    /// The distinct candidates in scoped flat, unscoped flat, scoped hierarchical, and
    /// unscoped hierarchical order.
    /// </returns>
    internal static IReadOnlyList<string> GetDirectCandidates(string environment, string key)
        => GetDirectMetadataCandidates(environment, key).Select(candidate => candidate.Name).ToArray();

    /// <summary>Materializes direct legacy producers and their scope/shape before ordinal de-duplication.</summary>
    internal static ImmutableArray<EnvironmentCompositionPathCandidate> GetDirectMetadataCandidates(string environment, string key)
    {
        var envPrefix = NormalizeSegment(environment);
        var legacyKey = NormalizeSegment(key);
        var hierarchicalKey = NormalizeHierarchicalKey(key);
        return BuildMetadataCandidates(envPrefix, legacyKey, hierarchicalKey);
    }

    /// <summary>
    /// Gets the ordered direct candidates for canonical path segments.
    /// </summary>
    /// <param name="environment">The environment name used for the scoped candidates.</param>
    /// <param name="segments">The canonical logical path segments.</param>
    /// <returns>
    /// The distinct candidates in the same order as <see cref="GetDirectCandidates"/>,
    /// with the segments projected through the existing dotted-key spelling rules.
    /// </returns>
    internal static IReadOnlyList<string> GetPathCandidates(
        string environment,
        IReadOnlyList<string> segments)
        => GetPathMetadataCandidates(environment, segments).Select(candidate => candidate.Name).ToArray();

    /// <summary>Projects literal path segments through the existing dotted composition spelling.</summary>
    /// <remarks>This projection is intentionally not an injective logical-key encoding.</remarks>
    internal static ImmutableArray<EnvironmentCompositionPathCandidate> GetPathMetadataCandidates(
        string environment, IReadOnlyList<string> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        return GetDirectMetadataCandidates(environment, string.Join('.', segments));
    }

    /// <summary>
    /// Normalizes one environment or legacy key segment to uppercase flat spelling.
    /// </summary>
    /// <param name="value">The segment to normalize.</param>
    /// <returns>The uppercase segment with periods and hyphens replaced by underscores.</returns>
    internal static string NormalizeSegment(string value) =>
        value.ToUpperInvariant()
            .Replace('.', '_')
            .Replace('-', '_');

    /// <summary>
    /// Normalizes a logical key to uppercase hierarchical environment-variable spelling.
    /// </summary>
    /// <param name="value">The logical key to normalize.</param>
    /// <returns>
    /// The key split on periods and hyphens, with empty segments removed and segments joined
    /// by double underscores.
    /// </returns>
    internal static string NormalizeHierarchicalKey(string value)
    {
        var segments = value.Split(['.', '-'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join("__", segments.Select(s => s.ToUpperInvariant()));
    }

    /// <summary>
    /// Builds and ordinal-deduplicates the legacy direct candidate sequence.
    /// </summary>
    /// <param name="envPrefix">The normalized environment prefix.</param>
    /// <param name="legacyKey">The normalized flat key.</param>
    /// <param name="hierarchicalKey">The normalized hierarchical key.</param>
    /// <returns>The candidates in legacy precedence order.</returns>
    internal static IReadOnlyList<string> BuildDirectCandidates(
        string envPrefix,
        string legacyKey,
        string hierarchicalKey)
        => BuildMetadataCandidates(envPrefix, legacyKey, hierarchicalKey).Select(candidate => candidate.Name).ToArray();

    /// <summary>Attaches producer metadata and retains the first ordinal name in legacy precedence order.</summary>
    internal static ImmutableArray<EnvironmentCompositionPathCandidate> BuildMetadataCandidates(
        string envPrefix, string legacyKey, string hierarchicalKey)
    {
        EnvironmentCompositionPathCandidate[] ordered =
        {
            new($"{envPrefix}_{legacyKey}", EnvironmentCandidateScope.Scoped, false),
            new(legacyKey, EnvironmentCandidateScope.Unscoped, false),
            new($"{envPrefix}__{hierarchicalKey}", EnvironmentCandidateScope.Scoped, true),
            new(hierarchicalKey, EnvironmentCandidateScope.Unscoped, true)
        };

        var distinct = ImmutableArray.CreateBuilder<EnvironmentCompositionPathCandidate>(ordered.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in ordered)
        {
            if (seen.Add(candidate.Name))
            {
                distinct.Add(candidate);
            }
        }

        return distinct.ToImmutable();
    }
}
