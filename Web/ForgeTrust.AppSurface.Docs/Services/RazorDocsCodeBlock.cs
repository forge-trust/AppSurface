namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Represents the authored Markdown code-fence input that RazorDocs can render as highlighted or plain code.
/// </summary>
/// <remarks>
/// <see cref="RazorDocsCodeBlock"/> stores only the first whitespace-delimited info-string token in
/// <see cref="Language"/> because the renderer treats that token as the language hint. It does not parse full
/// info-string attributes. <see cref="Language"/> may be null when the info string is omitted, and <see cref="Code"/>
/// may be null when an upstream renderer provides no text, so callers must handle both defensively.
/// </remarks>
/// <param name="Code">The literal code block body, or <c>null</c> when an upstream renderer provides no text.</param>
/// <param name="Language">The first whitespace-delimited Markdown info-string token, or <c>null</c> when omitted.</param>
internal sealed record RazorDocsCodeBlock(string? Code, string? Language);
