namespace ForgeTrust.AppSurface.Core.Extensions;

/// <summary>
/// Provides extension methods for strings.
/// </summary>
public static class StringExtensions
{
    /// <summary>
    /// Splits a string on any whitespace character, removing empty entries.
    /// </summary>
    /// <param name="input">The string to split.</param>
    /// <returns>An array of substrings delimited by whitespace.</returns>
    public static string[] SplitOnWhiteSpace(this string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return Array.Empty<string>();
        }

        // This will split on any whitespace character, including tabs and newlines.
        return input.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    }
}
