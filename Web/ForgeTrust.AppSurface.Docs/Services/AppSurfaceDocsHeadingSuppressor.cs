namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Applies page-shell heading rules to harvested documentation HTML at render time.
/// </summary>
internal static class AppSurfaceDocsHeadingSuppressor
{
    /// <summary>
    /// Removes the leading rendered Markdown <c>h1</c> element when the details page shell already renders the page H1.
    /// </summary>
    /// <param name="content">The sanitized harvested HTML body to render inside the details page content surface.</param>
    /// <param name="shellOwnsH1">
    /// <c>true</c> when the page shell renders the semantic H1 from <see cref="Models.DocDetailsViewModel.Title"/>;
    /// <c>false</c> when the harvested body remains responsible for its own top-level heading.
    /// </param>
    /// <returns>
    /// The original <paramref name="content"/> when the shell does not own the H1 or when the body does not begin with
    /// an <c>h1</c>; otherwise the body with that first rendered <c>h1</c> removed.
    /// </returns>
    /// <remarks>
    /// Only the leading H1 is suppressed. Later H1 elements remain visible because they are authored body structure, not
    /// duplicated page chrome.
    /// </remarks>
    internal static string SuppressLeadingMarkdownH1(string content, bool shellOwnsH1)
    {
        if (!shellOwnsH1 || string.IsNullOrEmpty(content))
        {
            return content;
        }

        var headingStart = GetFirstMeaningfulTokenIndex(content);
        if (headingStart < 0 || !StartsWithH1StartTag(content, headingStart))
        {
            return content;
        }

        var openTagEnd = content.IndexOf('>', headingStart);
        if (openTagEnd < 0)
        {
            return content;
        }

        if (!TryFindH1CloseTag(content, openTagEnd + 1, out _, out var closeTagEnd))
        {
            return content;
        }

        var contentStart = closeTagEnd + 1;
        while (contentStart < content.Length && char.IsWhiteSpace(content[contentStart]))
        {
            contentStart++;
        }

        return content[contentStart..];
    }

    /// <summary>
    /// Finds the first non-trivia token in harvested HTML.
    /// </summary>
    /// <param name="content">The harvested HTML body that may begin with whitespace, a BOM, comments, or content.</param>
    /// <returns>
    /// The index of the first non-trivia token, or <c>-1</c> when the body is empty or contains only ignorable trivia.
    /// </returns>
    /// <remarks>
    /// Only leading comments are skipped. Other elements, text, or malformed comments remain authored content and are
    /// preserved by the suppressor.
    /// </remarks>
    private static int GetFirstMeaningfulTokenIndex(string content)
    {
        var index = 0;
        while (index < content.Length)
        {
            var current = content[index];
            if (current == '\uFEFF' || char.IsWhiteSpace(current))
            {
                index++;
                continue;
            }

            if (content.AsSpan(index).StartsWith("<!--", StringComparison.Ordinal))
            {
                var commentEnd = content.IndexOf("-->", index + 4, StringComparison.Ordinal);
                if (commentEnd < 0)
                {
                    return index;
                }

                index = commentEnd + 3;
                continue;
            }

            return index;
        }

        return -1;
    }

    private static bool StartsWithH1StartTag(string content, int index)
    {
        var remaining = content.AsSpan(index);
        return remaining.StartsWith("<h1", StringComparison.OrdinalIgnoreCase)
            && remaining.Length > 3
            && IsTagNameBoundary(remaining[3]);
    }

    private static bool TryFindH1CloseTag(string content, int startIndex, out int closeTagStart, out int closeTagEnd)
    {
        var searchIndex = startIndex;
        while (searchIndex < content.Length)
        {
            var candidate = content.IndexOf("</h1", searchIndex, StringComparison.OrdinalIgnoreCase);
            if (candidate < 0)
            {
                break;
            }

            var boundaryIndex = candidate + 4;
            if (boundaryIndex < content.Length)
            {
                if (content[boundaryIndex] == '>')
                {
                    closeTagStart = candidate;
                    closeTagEnd = boundaryIndex;
                    return true;
                }

                if (char.IsWhiteSpace(content[boundaryIndex]))
                {
                    var tagEnd = boundaryIndex + 1;
                    while (tagEnd < content.Length && char.IsWhiteSpace(content[tagEnd]))
                    {
                        tagEnd++;
                    }

                    if (tagEnd < content.Length && content[tagEnd] == '>')
                    {
                        closeTagStart = candidate;
                        closeTagEnd = tagEnd;
                        return true;
                    }
                }
            }

            searchIndex = candidate + 4;
        }

        closeTagStart = -1;
        closeTagEnd = -1;
        return false;
    }

    private static bool IsTagNameBoundary(char value)
    {
        return char.IsWhiteSpace(value) || value is '>' or '/';
    }
}
