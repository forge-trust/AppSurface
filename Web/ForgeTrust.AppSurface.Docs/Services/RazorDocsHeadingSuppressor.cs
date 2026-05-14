using System.Text.RegularExpressions;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Applies page-shell heading rules to harvested documentation HTML at render time.
/// </summary>
internal static partial class RazorDocsHeadingSuppressor
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

        return LeadingH1Regex().Replace(content, string.Empty, count: 1);
    }

    [GeneratedRegex(@"\A(?:[\uFEFF\s]|<!--.*?-->)*<h1\b[^>]*>.*?</h1>\s*", RegexOptions.IgnoreCase | RegexOptions.Singleline, matchTimeoutMilliseconds: 100)]
    private static partial Regex LeadingH1Regex();
}
