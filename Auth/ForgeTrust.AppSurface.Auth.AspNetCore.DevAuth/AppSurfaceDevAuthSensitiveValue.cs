namespace ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth;

/// <summary>
/// Detects sensitive-looking local persona ids and preview values without rejecting innocent words such as
/// <c>monkey</c> or <c>mailbox</c>.
/// </summary>
/// <remarks>
/// DevAuth diagnostics and control pages must not render obvious secrets, tokens, passwords, keys, credentials, or
/// email-shaped values. This helper treats separators, PascalCase, and camelCase as token boundaries so names such as
/// <c>apiKey</c>, <c>passwordHash</c>, <c>access_token</c>, and <c>secret-token</c> are hidden.
/// </remarks>
internal static class AppSurfaceDevAuthSensitiveValue
{
    private static readonly string[] SensitiveWords =
    [
        "token",
        "secret",
        "password",
        "credential",
        "email",
        "mail",
        "key",
    ];

    /// <summary>
    /// Determines whether a value looks too sensitive to expose in DevAuth diagnostics or route-visible persona ids.
    /// </summary>
    /// <param name="value">Candidate local value, claim type, claim value, display name, subject, or persona id.</param>
    /// <returns><see langword="true"/> when the value contains an email marker or sensitive word boundary.</returns>
    public static bool ContainsSensitiveToken(string value)
    {
        if (value.Contains('@', StringComparison.Ordinal))
        {
            return true;
        }

        return SensitiveWords.Any(word => ContainsSensitiveWord(value, word));
    }

    private static bool ContainsSensitiveWord(string value, string word)
    {
        var index = value.IndexOf(word, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            if (IsSensitiveWordBoundary(value, index, word.Length, word))
            {
                return true;
            }

            index = value.IndexOf(word, index + 1, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool IsSensitiveWordBoundary(string value, int index, int length, string word)
    {
        var startBoundary = IsStartBoundary(value, index);
        var endBoundary = IsEndBoundary(value, index + length);

        return word is "key" or "mail"
            ? startBoundary && endBoundary
            : (startBoundary && (endBoundary || index == 0));
    }

    private static bool IsStartBoundary(string value, int index)
    {
        if (index == 0)
        {
            return true;
        }

        var previous = value[index - 1];
        var current = value[index];
        return !char.IsLetterOrDigit(previous) || (char.IsLower(previous) && char.IsUpper(current));
    }

    private static bool IsEndBoundary(string value, int index)
    {
        if (index >= value.Length)
        {
            return true;
        }

        var previous = value[index - 1];
        var current = value[index];
        return !char.IsLetterOrDigit(current) || (char.IsLower(previous) && char.IsUpper(current));
    }
}
