namespace ForgeTrust.RazorWire.Cli;

/// <summary>
/// Captures nearby <c>link</c> attributes used to classify a supported <c>href</c> reference.
/// </summary>
/// <param name="Rel">The original <c>rel</c> attribute value.</param>
/// <param name="As">The original <c>as</c> attribute value, when present.</param>
internal sealed record ExportReferenceLinkMetadata(string Rel, string? As)
{
    /// <summary>
    /// Gets a compact diagnostic description of the classification evidence.
    /// </summary>
    public string Display
        => string.IsNullOrWhiteSpace(As)
            ? $"rel '{Rel}'"
            : $"rel '{Rel}', as '{As}'";
}
