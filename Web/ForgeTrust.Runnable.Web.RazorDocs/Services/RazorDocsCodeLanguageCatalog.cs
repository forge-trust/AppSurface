using System.Text;

namespace ForgeTrust.Runnable.Web.RazorDocs.Services;

/// <summary>
/// Owns Markdown code-fence language normalization, safe CSS class suffixes, and TextMateSharp lookup ids.
/// </summary>
internal sealed class RazorDocsCodeLanguageCatalog
{
    internal static readonly RazorDocsCodeLanguageCatalog Shared = new();

    private static readonly Dictionary<string, RazorDocsCodeLanguage> KnownLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cs"] = Known("csharp", "csharp", "C#", "csharp"),
        ["c#"] = Known("csharp", "csharp", "C#", "csharp"),
        ["csharp"] = Known("csharp", "csharp", "C#", "csharp"),
        ["razor"] = Known("razor", "razor", "Razor", "razor"),
        ["cshtml"] = Known("razor", "razor", "Razor", "razor"),
        ["xml"] = Known("xml", "xml", "XML", "xml"),
        ["json"] = Known("json", "json", "JSON", "json"),
        ["bash"] = Known("bash", "bash", "Bash", "shellscript"),
        ["sh"] = Known("bash", "bash", "Bash", "shellscript"),
        ["shell"] = Known("bash", "bash", "Bash", "shellscript"),
        ["html"] = Known("html", "html", "HTML", "html"),
        ["css"] = Known("css", "css", "CSS", "css"),
        ["js"] = Known("javascript", "javascript", "JavaScript", "javascript"),
        ["javascript"] = Known("javascript", "javascript", "JavaScript", "javascript"),
        ["md"] = Known("markdown", "markdown", "Markdown", "markdown"),
        ["markdown"] = Known("markdown", "markdown", "Markdown", "markdown"),
        ["diff"] = Known("diff", "diff", "Diff", "diff"),
        ["txt"] = Plain("plaintext", "plaintext", "Plain text", isKnown: true),
        ["text"] = Plain("plaintext", "plaintext", "Plain text", isKnown: true),
        ["plaintext"] = Plain("plaintext", "plaintext", "Plain text", isKnown: true),
    };

    /// <summary>
    /// Normalizes an authored language token into RazorDocs' stable language contract.
    /// </summary>
    /// <param name="language">The raw first info-string token.</param>
    /// <returns>A safe language descriptor for rendering and TextMate lookup.</returns>
    internal RazorDocsCodeLanguage Normalize(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return Plain("plaintext", "plaintext", "Plain text", isKnown: true);
        }

        var trimmed = language.Trim();
        if (KnownLanguages.TryGetValue(trimmed, out var known))
        {
            return known;
        }

        if (!ContainsOnlySafeLanguageCharacters(trimmed))
        {
            return Plain("unknown", "plaintext", "Unknown", isKnown: false);
        }

        var safeSlug = CreateSafeClassSlug(trimmed);
        if (string.IsNullOrEmpty(safeSlug))
        {
            return Plain("unknown", "plaintext", "Unknown", isKnown: false);
        }

        return Plain("unknown", safeSlug, safeSlug, isKnown: false);
    }

    /// <summary>
    /// Converts arbitrary language input into a CSS-safe lowercase ASCII slug.
    /// </summary>
    /// <param name="value">The value to slug.</param>
    /// <returns>A lowercase slug containing only ASCII letters, digits, and hyphens.</returns>
    internal static string CreateSafeClassSlug(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var previousWasHyphen = false;

        foreach (var ch in value.Trim())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                builder.Append(ch);
                previousWasHyphen = false;
                continue;
            }

            if (ch is >= 'A' and <= 'Z')
            {
                builder.Append(char.ToLowerInvariant(ch));
                previousWasHyphen = false;
                continue;
            }

            if ((ch == '-' || ch == '_' || ch == '.') && builder.Length > 0 && !previousWasHyphen)
            {
                builder.Append('-');
                previousWasHyphen = true;
            }
        }

        if (builder.Length > 0 && builder[^1] == '-')
        {
            builder.Length--;
        }

        return builder.ToString();
    }

    private static bool ContainsOnlySafeLanguageCharacters(string value)
    {
        foreach (var ch in value)
        {
            if (ch is >= 'a' and <= 'z'
                or >= 'A' and <= 'Z'
                or >= '0' and <= '9'
                or '-'
                or '_'
                or '.')
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private static RazorDocsCodeLanguage Known(
        string normalizedLanguage,
        string classLanguage,
        string label,
        string textMateLanguageId)
    {
        return new RazorDocsCodeLanguage(
            normalizedLanguage,
            classLanguage,
            label,
            textMateLanguageId,
            IsKnown: true,
            IsPlainText: false);
    }

    private static RazorDocsCodeLanguage Plain(
        string normalizedLanguage,
        string classLanguage,
        string label,
        bool isKnown)
    {
        return new RazorDocsCodeLanguage(
            normalizedLanguage,
            classLanguage,
            label,
            TextMateLanguageId: null,
            IsKnown: isKnown,
            IsPlainText: true);
    }
}
