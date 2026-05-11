namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Represents the authored Markdown code-fence input that RazorDocs can render as highlighted or plain code.
/// </summary>
/// <param name="Code">The literal code block body, or <c>null</c> when an upstream renderer provides no text.</param>
/// <param name="Language">The first whitespace-delimited Markdown info-string token, or <c>null</c> when omitted.</param>
internal sealed record RazorDocsCodeBlock(string? Code, string? Language);
