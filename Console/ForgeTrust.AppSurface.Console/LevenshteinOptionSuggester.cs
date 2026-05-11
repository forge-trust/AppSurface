

namespace ForgeTrust.AppSurface.Console;

/// <summary>
///     Suggests options using the Levenshtein distance algorithm.
/// </summary>
public class LevenshteinOptionSuggester : IOptionSuggester
{
    private const int MaxDistance = 3;

    /// <summary>
    /// Gets a list of suggested options based on their Levenshtein distance to the unknown option.
    /// </summary>
    /// <param name="unknownOption">The unknown command line option.</param>
    /// <param name="validOptions">The list of valid command line options.</param>
    /// <returns>A collection of suggested options that closely match the unknown option.</returns>
    public IReadOnlyList<string> GetSuggestions(string? unknownOption, IEnumerable<string>? validOptions)
    {
        if (string.IsNullOrEmpty(unknownOption) || validOptions == null)
        {
            return Array.Empty<string>();
        }

        // Normalize case so that distance calculation is consistent with case-insensitive option matching.
        var unknownNormalized = unknownOption.ToUpperInvariant();

        return validOptions
            .Where(option => !string.IsNullOrEmpty(option))
            .Select(option => new
            {
                Option = option,
                Distance = ComputeLevenshteinDistance(unknownNormalized, option.ToUpperInvariant())
            })
            .Where(x => x.Distance <= MaxDistance)
            .OrderBy(x => x.Distance)
            .Select(x => x.Option)
            .ToList();
    }

    /// <summary>
    /// Computes the Levenshtein distance between two strings using a dynamic programming approach.
    /// </summary>
    /// <param name="s">The first string to compare.</param>
    /// <param name="t">The second string to compare.</param>
    /// <returns>The number of single-character edits (insertions, deletions, or substitutions) required to change one string into the other.</returns>
    internal static int ComputeLevenshteinDistance(string s, string t)
    {
        if (string.IsNullOrEmpty(s)) return string.IsNullOrEmpty(t) ? 0 : t.Length;
        if (string.IsNullOrEmpty(t)) return s.Length;

        var d = new int[s.Length + 1, t.Length + 1];

        for (var i = 0; i <= s.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= t.Length; j++) d[0, j] = j;

        for (var j = 1; j <= t.Length; j++)
        {
            for (var i = 1; i <= s.Length; i++)
            {
                var cost = s[i - 1] == t[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        }

        return d[s.Length, t.Length];
    }
}
