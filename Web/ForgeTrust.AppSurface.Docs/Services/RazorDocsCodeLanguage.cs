namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Describes RazorDocs' normalized view of an authored code-fence language token.
/// </summary>
/// <param name="NormalizedLanguage">The stable RazorDocs language name.</param>
/// <param name="ClassLanguage">The safe suffix used by the conventional <c>language-*</c> class.</param>
/// <param name="Label">The reader-facing language label.</param>
/// <param name="TextMateLanguageId">The TextMateSharp language id, or <c>null</c> for plaintext fallback.</param>
/// <param name="IsKnown">Whether the authored token is part of RazorDocs' recognized language catalog.</param>
/// <param name="IsPlainText">Whether the language should render as escaped plaintext.</param>
internal sealed record RazorDocsCodeLanguage(
    string NormalizedLanguage,
    string ClassLanguage,
    string Label,
    string? TextMateLanguageId,
    bool IsKnown,
    bool IsPlainText);
