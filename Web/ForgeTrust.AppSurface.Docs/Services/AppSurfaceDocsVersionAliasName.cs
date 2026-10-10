namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Applies the shared URL-safe grammar for documentation version aliases.
/// </summary>
internal static class AppSurfaceDocsVersionAliasName
{
    /// <summary>
    /// Trims and lowercases a candidate alias, accepting 1–64 ASCII characters with alphanumeric edges and
    /// alphanumeric, dot, underscore, or hyphen interior characters.
    /// </summary>
    /// <param name="value">The authored alias name.</param>
    /// <param name="name">The normalized alias name when valid; otherwise an empty string.</param>
    /// <returns><see langword="true"/> when the candidate is safe for the alias route segment.</returns>
    internal static bool TryNormalize(string? value, out string name)
    {
        name = string.Empty;
        if (value is null)
        {
            return false;
        }

        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length is < 1 or > 64 || !IsAsciiAlphaNumeric(normalized[0]) || !IsAsciiAlphaNumeric(normalized[^1]))
        {
            return false;
        }

        for (var index = 1; index < normalized.Length - 1; index++)
        {
            var character = normalized[index];
            if (!IsAsciiAlphaNumeric(character) && character is not ('.' or '_' or '-'))
            {
                return false;
            }
        }

        name = normalized;
        return true;
    }

    private static bool IsAsciiAlphaNumeric(char value)
    {
        return value is >= 'a' and <= 'z' or >= '0' and <= '9';
    }
}
