using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ForgeTrust.RazorWire;

/// <summary>
/// Provides utility methods for string manipulation, specifically for generating safe identifiers.
/// </summary>
public static class StringUtils
{
    private static readonly Regex IdentifierRegex = new(@"[^a-zA-Z0-9-_]", RegexOptions.Compiled);
    private static readonly Regex MultiHyphenRegex = new(@"-+", RegexOptions.Compiled);

    /// <summary>
    /// Produces a safe identifier by replacing disallowed characters with hyphens, collapsing consecutive hyphens, trimming edge hyphens, and defaulting to "id" for null, whitespace, or empty results.
    /// Optionally appends a short deterministic 4-character lowercase hex hash (prefixed with a hyphen) derived from the original input to ensure uniqueness.
    /// </summary>
    /// <param name="input">The source string to convert into a safe identifier.</param>
    /// <param name="appendHash">If true, appends a short deterministic 4-character lowercase hex hash (prefixed with a hyphen).</param>
    /// <returns>The sanitized identifier; if <paramref name="appendHash"/> is true the value is suffixed with "-" and a 4-character lowercase hex hash.</returns>
    public static string ToSafeId(string? input, bool appendHash = false)
    {
        string sanitized;
        if (string.IsNullOrWhiteSpace(input))
        {
            sanitized = "id";
        }
        else
        {
            // 1. Sanitize: Replace non-alphanumeric (except - and _) with -
            sanitized = IdentifierRegex.Replace(input, "-");

            // 2. Clean up: Trim and normalize hyphens using Regex
            sanitized = MultiHyphenRegex.Replace(sanitized, "-").Trim('-');

            if (string.IsNullOrEmpty(sanitized))
            {
                sanitized = "id";
            }
        }

        if (!appendHash)
        {
            return sanitized;
        }

        // 3. Optional: Append a short deterministic hash of the original input
        var hash = GetDeterministicHash(input ?? string.Empty);

        return $"{sanitized}-{hash}";
    }

    /// <summary>
    /// Produces a short deterministic 4-character lowercase hexadecimal string derived from the SHA-256 hash of the input.
    /// </summary>
    /// <remarks>
    /// Returns only the first 4 hex characters (16 bits) of the SHA-256 digest; collisions are possible.
    /// </remarks>
    /// <param name="input">The string to hash.</param>
    /// <returns>A 4-character lowercase hexadecimal string.</returns>
    private static string GetDeterministicHash(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hashBytes = SHA256.HashData(bytes);

        return Convert.ToHexString(hashBytes)[..4].ToLowerInvariant();
    }
}
