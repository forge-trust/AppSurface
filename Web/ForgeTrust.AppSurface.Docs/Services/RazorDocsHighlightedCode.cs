namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Contains RazorDocs-owned HTML for a rendered Markdown code block.
/// </summary>
/// <param name="Html">The complete sanitized-shape HTML fragment for the code block.</param>
/// <param name="NormalizedLanguage">The normalized language identifier used by RazorDocs.</param>
/// <param name="IsHighlighted">Whether token spans were emitted for the code body.</param>
internal sealed record RazorDocsHighlightedCode(
    string Html,
    string NormalizedLanguage,
    bool IsHighlighted);
